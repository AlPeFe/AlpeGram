using HanniGram.Core.Models;
using Microsoft.Data.Sqlite;

namespace HanniGram.Core.Store;

/// <summary>
/// SQLite + FTS5 store for HanniGram. One global database file (default
/// ~/.hannigram/hannigram.db) holding all projects, each with its own memory
/// namespace. FTS5 powers full-text search over observations.
/// </summary>
public sealed class HanniGramStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    // Serialize access to the single shared connection. Microsoft.Data.Sqlite does
    // NOT allow concurrent commands on one SqliteConnection (two overlapping
    // commands throw "Index was out of range" in RemoveCommand under load). lock{}
    // is reentrant, so a public method calling another public method is still safe.
    private readonly object _gate = new();

    public HanniGramStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hannigram", "hannigram.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        Initialize();
    }

    // Bump when the FTS tokenizer or an indexed schema changes, to force a one-off
    // re-index. Do NOT bump on every open — that re-indexed the whole corpus per start.
    // v2: observations_fts gained what/why/where_/learned.
    // v3: sessions gain a unique (project_id, session_id) index, so a session
    //     summary re-saved for the same session updates one row instead of
    //     accumulating duplicates. The v3 migration deduplicates first.
    // v4: observations gain a lifecycle state (active|needs_review|archived) and a
    //     pinned flag, so current truth can be told from stale truth and the global
    //     injection budget can select pinned heuristics. Existing rows migrate to
    //     active / unpinned. The FTS column set is unchanged, but the version bump
    //     runs the standard one-off FTS recreate (the migration shape is shared).
    private const int SchemaVersion = 4;

    private void Initialize()
    {
        // Connection pragmas, set once per open:
        //  - WAL: lets the daemon (single writer) coexist with the CLI and the
        //    harness's read-only node:sqlite readers without SQLITE_BUSY.
        //  - busy_timeout: wait instead of throwing when another writer holds the lock.
        //  - foreign_keys: enforce the ON DELETE CASCADE clauses (OFF by default in SQLite).
        // These pragmas return a value, so they must be run with ExecuteScalar (not
        // ExecuteNonQuery): leaving the result row unconsumed leaves the statement
        // half-finalized and later writes report "database disk image is malformed".
        Pragma("PRAGMA journal_mode=WAL;");
        Pragma("PRAGMA synchronous=NORMAL;");
        Pragma("PRAGMA busy_timeout=5000;");
        Pragma("PRAGMA foreign_keys=ON;");

        Exec(@"
            CREATE TABLE IF NOT EXISTS projects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                root_path TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                what TEXT, why TEXT, where_ TEXT, learned TEXT,
                topic_key TEXT,
                type TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'active',
                pinned INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                session_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT,
                summary TEXT, goal TEXT, next_steps TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_obs_project ON observations(project_id);
            CREATE INDEX IF NOT EXISTS idx_obs_topic ON observations(project_id, topic_key);
            CREATE INDEX IF NOT EXISTS idx_obs_project_updated ON observations(project_id, updated_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_sess_project ON sessions(project_id);
        ");

        // Lifecycle columns (v4). CREATE TABLE only covers a fresh database; an
        // existing observations table needs the columns added in place, so the
        // ensure step is idempotent and runs on BOTH the migrating and the
        // already-migrated path, exactly like the FTS triggers and the sessions
        // index. The state-aware ordering index is likewise re-asserted every open,
        // but only AFTER the columns exist: on an existing v3 table the index would
        // otherwise reference a column that is not there yet.
        EnsureObservationLifecycleColumns();
        Exec("CREATE INDEX IF NOT EXISTS idx_obs_project_state_updated ON observations(project_id, state, updated_at_utc DESC);");

        // FTS5 trigram index: typo-tolerant and substring/CJK search. Built/patched ONLY
        // when the schema version advances, not on every open. The drop+recreate is
        // external-content safe (observations rows are untouched; 'rebuild' re-indexes).
        // Column order is part of the contract: the FTS5 'delete' command used by the
        // sync triggers must pass OLD values back in exactly this order.
        if (GetUserVersion() < SchemaVersion)
        {
            // Trigger/table ordering matters. observations_fts is external-content and
            // the sync triggers reference it by name, so dropping the table while the
            // triggers still exist leaves them dangling and the NEXT write fails with
            // "no such table: observations_fts". Drop triggers, then the table, then
            // recreate both, then rebuild the index once from the content table.
            DropFtsSyncTriggers();
            Exec("DROP TABLE IF EXISTS observations_fts;");
            CreateFtsTable();
            CreateFtsSyncTriggers();
            RebuildFts();
            EnsureSessionsUniqueIndex();
            Exec($"PRAGMA user_version={SchemaVersion};");
        }
        else
        {
            // Already migrated. Re-assert the triggers: they are the only thing that
            // keeps the index in sync, and a database written by an intermediate
            // build can carry the table without them.
            CreateFtsSyncTriggers();
            // Re-assert the sessions unique index the same way: a build that wrote
            // SchemaVersion 3 without the index must still be repaired on open.
            EnsureSessionsUniqueIndex();
        }
    }

    /// <summary>Create the external-content FTS index over <c>observations</c>.</summary>
    private void CreateFtsTable() => Exec(@"
        CREATE VIRTUAL TABLE IF NOT EXISTS observations_fts USING fts5(
            title, content, what, why, where_, learned, topic_key, type,
            tokenize='trigram',
            content='observations', content_rowid='id'
        );
    ");

    /// <summary>
    /// The standard FTS5 external-content trigger triple. observations_fts is an
    /// EXTERNAL-CONTENT table: it must never be written with a plain INSERT/DELETE
    /// (that corrupts the index and SQLite then reports "database disk image is
    /// malformed" on the next write). The 'delete' command requires the OLD column
    /// values, in the FTS column order declared in <see cref="CreateFtsTable"/>.
    /// With these triggers, indexing is O(1) per write and runs inside the writer's
    /// transaction, instead of rebuilding the whole corpus.
    /// </summary>
    private void CreateFtsSyncTriggers() => Exec(@"
        CREATE TRIGGER IF NOT EXISTS observations_fts_ai AFTER INSERT ON observations BEGIN
            INSERT INTO observations_fts(rowid, title, content, what, why, where_, learned, topic_key, type)
            VALUES (new.id, new.title, new.content, new.what, new.why, new.where_, new.learned, new.topic_key, new.type);
        END;
        CREATE TRIGGER IF NOT EXISTS observations_fts_ad AFTER DELETE ON observations BEGIN
            INSERT INTO observations_fts(observations_fts, rowid, title, content, what, why, where_, learned, topic_key, type)
            VALUES ('delete', old.id, old.title, old.content, old.what, old.why, old.where_, old.learned, old.topic_key, old.type);
        END;
        CREATE TRIGGER IF NOT EXISTS observations_fts_au AFTER UPDATE ON observations BEGIN
            INSERT INTO observations_fts(observations_fts, rowid, title, content, what, why, where_, learned, topic_key, type)
            VALUES ('delete', old.id, old.title, old.content, old.what, old.why, old.where_, old.learned, old.topic_key, old.type);
            INSERT INTO observations_fts(rowid, title, content, what, why, where_, learned, topic_key, type)
            VALUES (new.id, new.title, new.content, new.what, new.why, new.where_, new.learned, new.topic_key, new.type);
        END;
    ");

    /// <summary>Remove the sync triggers before their table is dropped or recreated.</summary>
    private void DropFtsSyncTriggers() => Exec(@"
        DROP TRIGGER IF EXISTS observations_fts_ai;
        DROP TRIGGER IF EXISTS observations_fts_ad;
        DROP TRIGGER IF EXISTS observations_fts_au;
    ");

    // =====================================================================
    // Lifecycle schema (v4)
    // =====================================================================

    /// <summary>
    /// Ensure the <c>state</c> and <c>pinned</c> columns exist on
    /// <c>observations</c>. Idempotent: once present this is two cheap
    /// <c>PRAGMA table_info</c> reads. Existing rows land on the column defaults
    /// (<c>active</c> / <c>0</c>), which is the intended migration — memory written
    /// before lifecycle existed is current truth until someone says otherwise.
    /// </summary>
    private void EnsureObservationLifecycleColumns()
    {
        if (!ColumnExists("observations", "state"))
        {
            Exec("ALTER TABLE observations ADD COLUMN state TEXT NOT NULL DEFAULT 'active';");
        }
        if (!ColumnExists("observations", "pinned"))
        {
            Exec("ALTER TABLE observations ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>True when <paramref name="table"/> already has <paramref name="column"/>.</summary>
    private bool ColumnExists(string table, string column)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        // table_info columns: cid, name, type, notnull, dflt_value, pk.
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // =====================================================================
    // Sessions schema
    // =====================================================================

    // A session is identified by (project, session_id). Without uniqueness,
    // StartSession always INSERTed and EndSession UPDATEd every matching row, so
    // mem_session_summary (start + end) duplicated rows and then overwrote its own
    // summaries. The index is what makes both operations target exactly one row.
    private const string SessionsUniqueIndexName = "uq_sessions_project_session";

    /// <summary>
    /// Ensure the unique (project_id, session_id) index exists. On an existing
    /// database this is the migration step and it must be safe: the user's db may
    /// already carry duplicates from the pre-v3 write path, and CREATE UNIQUE INDEX
    /// fails outright on duplicate keys. Deduplicate first, then create.
    /// Idempotent: once the index exists this is a cheap existence check.
    /// </summary>
    private void EnsureSessionsUniqueIndex()
    {
        if (SessionsUniqueIndexExists()) return;

        DeduplicateSessions();
        Exec($"CREATE UNIQUE INDEX IF NOT EXISTS {SessionsUniqueIndexName} ON sessions(project_id, session_id);");
    }

    private bool SessionsUniqueIndexExists()
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $n";
        cmd.Parameters.AddWithValue("$n", SessionsUniqueIndexName);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Collapse duplicate (project_id, session_id) groups to a single row, keeping
    /// the most relevant one: a row carrying a summary first, then the latest
    /// started_at_utc, then the highest id (the most recently inserted). The rest
    /// are removed. Runs only when the unique index is being created.
    /// </summary>
    private void DeduplicateSessions() => Exec(@"
        DELETE FROM sessions
        WHERE id NOT IN (
            SELECT keep_id FROM (
                SELECT id AS keep_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY project_id, session_id
                           ORDER BY (COALESCE(summary, '') <> '') DESC,
                                    started_at_utc DESC,
                                    id DESC
                       ) AS rn
                FROM sessions
            )
            WHERE rn = 1
        );
    ");

    private int GetUserVersion()
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // =====================================================================
    // Projects
    // =====================================================================

    public Project? GetProjectByName(string name)
    {
        lock (_gate)
        {
            return ReadProjectByName(name, null);
        }
    }

    private Project? ReadProjectByName(string name, SqliteTransaction? transaction)
    {
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, name, root_path, created_at_utc, updated_at_utc FROM projects WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProject(r) : null;
    }

    public Project GetOrCreateProject(string name, string? rootPath = null)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var project = GetOrCreateProjectCore(name, rootPath, tx);
            tx.Commit();
            return project;
        }
    }

    /// <summary>
    /// Transaction-aware project create-or-read, so a caller that already holds
    /// a transaction (promotion is the reason it exists) does not open a nested
    /// one.
    /// </summary>
    private Project GetOrCreateProjectCore(string name, string? rootPath, SqliteTransaction tx)
    {
        var project = ReadProjectByName(name, tx);

        if (project is null)
        {
            using var insert = CreateCommand(tx);
            insert.CommandText = "INSERT INTO projects (name, root_path, created_at_utc, updated_at_utc) VALUES ($n, $r, $c, $u); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$r", (object?)rootPath ?? DBNull.Value);
            var now = DateTime.UtcNow;
            insert.Parameters.AddWithValue("$c", now.ToString("O"));
            insert.Parameters.AddWithValue("$u", now.ToString("O"));
            var id = (long)insert.ExecuteScalar()!;
            project = new Project { Id = id, Name = name, RootPath = rootPath, CreatedAtUtc = now, UpdatedAtUtc = now };
        }
        else if (rootPath != null && project.RootPath != rootPath)
        {
            using var update = CreateCommand(tx);
            update.CommandText = "UPDATE projects SET root_path = $r, updated_at_utc = $u WHERE id = $id";
            update.Parameters.AddWithValue("$r", rootPath);
            update.Parameters.AddWithValue("$u", Now());
            update.Parameters.AddWithValue("$id", project.Id);
            update.ExecuteNonQuery();
            project.RootPath = rootPath;
        }

        return project;
    }

    /// <summary>
    /// Look up the reserved global namespace WITHOUT creating it. Read paths use
    /// this: resolving global memory must treat "absent" as "no global memory
    /// yet", never as an instruction to insert the sentinel row.
    /// </summary>
    public Project? GetGlobalProject()
    {
        lock (_gate)
        {
            return ReadProjectByName(MemoryScopes.GlobalProjectName, null);
        }
    }

    /// <summary>
    /// Return the reserved global namespace, creating it on first write. Only
    /// the write paths (save-to-global, promotion) call this.
    /// </summary>
    public Project GetOrCreateGlobalProject()
    {
        lock (_gate)
        {
            return GetOrCreateProject(MemoryScopes.GlobalProjectName, null);
        }
    }

    public IReadOnlyList<Project> ListProjects()
    {
        lock (_gate)
        {
            var list = new List<Project>();
            using var cmd = CreateCommand();
            cmd.CommandText = "SELECT id, name, root_path, created_at_utc, updated_at_utc FROM projects ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadProject(r));
            return list;
        }
    }

    // =====================================================================
    // Observations
    // =====================================================================

    /// <summary>
    /// Save an observation scoped to a project. When <paramref name="topicKey"/> is
    /// provided, this is an UPSERT keyed on (project, topic_key): an existing
    /// observation with the same key is updated in place (its id and created_at are
    /// preserved) so a topic evolves instead of accumulating stale versions. Without
    /// a topic key, a new observation is always inserted.
    /// The write and its FTS index update (done by the sync triggers) share one
    /// transaction, so the content table and its index can never diverge.
    /// Returns the persisted observation and whether it was newly created.
    /// </summary>
    public (Observation Observation, bool Created) SaveObservation(long projectId, string title, string content,
        string? what = null, string? why = null, string? where = null, string? learned = null,
        string? topicKey = null, string? type = null)
    {
        var result = SaveObservationWithReview(projectId, title, content, what, why, where, learned, topicKey, type);
        return (result.Observation, result.Created);
    }

    /// <summary>
    /// Save, and additionally report the deterministic review hint (T14). The hint
    /// is compact and absent when nothing collided. Detection is server-side and
    /// bounded: a keyed save already read its exact <c>topic_key</c> match through
    /// the upsert lookup, and an unkeyed save is compared against a bounded recent
    /// window in the same namespace — never the whole corpus, never an LLM.
    /// </summary>
    public (Observation Observation, bool Created, SaveReviewHint? Review) SaveObservationWithReview(
        long projectId, string title, string content,
        string? what = null, string? why = null, string? where = null, string? learned = null,
        string? topicKey = null, string? type = null)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var keyed = !string.IsNullOrEmpty(topicKey);
            var existing = keyed ? FindLatestByTopicKey(projectId, topicKey!, tx) : null;
            long id;
            var created = existing is null;

            // The duplicate probe runs BEFORE the insert and only for an unkeyed,
            // brand-new row: an unkeyed save is the case that can silently duplicate
            // an existing memory. Comparing against the window before the write keeps
            // the new row out of its own candidate set.
            SaveReviewHint? review = null;
            if (created && !keyed)
            {
                review = FindNearDuplicateHint(projectId, title, content, tx);
            }

            if (created)
            {
                using var cmd = CreateCommand(tx);
                cmd.CommandText = @"
                    INSERT INTO observations (project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc)
                    VALUES ($p, $t, $c, $w, $y, $wh, $l, $tk, $ty, $n, $n);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", projectId);
                cmd.Parameters.AddWithValue("$t", title);
                cmd.Parameters.AddWithValue("$c", content);
                cmd.Parameters.AddWithValue("$w", (object?)what ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$y", (object?)why ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$wh", (object?)where ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", (object?)learned ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tk", (object?)topicKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ty", (object?)type ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", Now());
                id = (long)cmd.ExecuteScalar()!;
            }
            else
            {
                id = existing!.Id;
                using var cmd = CreateCommand(tx);
                cmd.CommandText = @"
                    UPDATE observations
                    SET title = $t, content = $c, what = $w, why = $y, where_ = $wh, learned = $l, type = $ty, updated_at_utc = $n
                    WHERE id = $id";
                cmd.Parameters.AddWithValue("$t", title);
                cmd.Parameters.AddWithValue("$c", content);
                cmd.Parameters.AddWithValue("$w", (object?)what ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$y", (object?)why ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$wh", (object?)where ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", (object?)learned ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ty", (object?)type ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", Now());
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();

                // The upsert path overwrote a keyed row: that is precisely the
                // "current truth replaced older truth" event worth a nudge, and it
                // is the only hint the hot path needs.
                review = new SaveReviewHint
                {
                    Kind = "topic_key",
                    Id = existing.Id,
                    Title = Preview(existing.Title, TitlePreviewChars),
                };
            }
            // Read back on the same transaction rather than through the public
            // GetObservation: that method would be correct too (it opens no
            // transaction of its own), but binding the read to this transaction
            // keeps the readback on the connection's active transaction.
            var observation = ReadObservationById(projectId, id, tx)!;
            tx.Commit();
            return (observation, created, review);
        }
    }

    /// <summary>
    /// Compare an unkeyed save against the bounded recent window in the same
    /// namespace and return the strongest near-duplicate, or null. The window and
    /// the similarity rule are constants on <see cref="MemoryDedup"/>.
    /// </summary>
    private SaveReviewHint? FindNearDuplicateHint(
        long projectId, string title, string content, SqliteTransaction tx)
    {
        var signature = MemoryDedup.Signature(title, content);
        if (signature.Count < MemoryDedup.MinSignatureTokens) return null;

        using var cmd = CreateCommand(tx);
        cmd.CommandText = "SELECT id, title, content FROM observations WHERE project_id = $p AND state <> $archived ORDER BY updated_at_utc DESC, id DESC LIMIT $w";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$archived", MemoryStates.Archived);
        cmd.Parameters.AddWithValue("$w", MemoryDedup.WindowSize);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var candidateId = r.GetInt64(0);
            var candidateTitle = r.GetString(1);
            var candidateContent = r.GetString(2);
            var similarity = MemoryDedup.Dice(signature, MemoryDedup.Signature(candidateTitle, candidateContent));
            if (similarity >= MemoryDedup.NearDuplicateThreshold)
            {
                return new SaveReviewHint
                {
                    Kind = "duplicate",
                    Id = candidateId,
                    Title = Preview(candidateTitle, TitlePreviewChars),
                };
            }
        }
        return null;
    }

    private Observation? FindLatestByTopicKey(long projectId, string topicKey, SqliteTransaction? transaction)
    {
        // Order by updated_at_utc, not created_at_utc: an upsert preserves the
        // original created_at, so created_at ordering would keep returning the
        // stalest duplicate of a topic key.
        // Archived rows are not upsert targets: a fresh teaching of a forgotten
        // topic inserts a new active row instead of reviving the archived one, so
        // the archived row survives as history.
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc, state, pinned FROM observations WHERE project_id = $p AND topic_key = $tk AND state <> $archived ORDER BY updated_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$tk", topicKey);
        cmd.Parameters.AddWithValue("$archived", MemoryStates.Archived);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObservation(r) : null;
    }

    public Observation? GetObservation(long projectId, long id)
    {
        lock (_gate)
        {
            var observation = ReadObservationById(projectId, id, null);
            if (observation is not null) observation.Scope = ScopeOfProject(projectId);
            return observation;
        }
    }

    /// <summary>
    /// The origin scope of a namespace identified only by id, derived from the
    /// project's name so an id-based caller cannot mislabel the global sentinel
    /// as a project (or a project as global).
    /// </summary>
    private string ScopeOfProject(long projectId)
    {
        lock (_gate)
        {
            using var cmd = CreateCommand();
            cmd.CommandText = "SELECT name FROM projects WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", projectId);
            return cmd.ExecuteScalar() as string == MemoryScopes.GlobalProjectName
                ? MemoryScopes.Global
                : MemoryScopes.Project;
        }
    }

    /// <summary>
    /// Build the namespace list for a read from a scope selector. The global
    /// namespace is looked up READ-ONLY, so a cross-scope read never creates the
    /// sentinel. A null <paramref name="projectId"/> omits the project namespace
    /// (used when the selector is global-only, so no project row is created).
    /// </summary>
    public IReadOnlyList<MemoryNamespace> NamespacesFor(long? projectId, string? scope, string defaultScope)
    {
        lock (_gate)
        {
            var (includeProject, includeGlobal) = MemoryScopes.Selection(scope, defaultScope);
            var list = new List<MemoryNamespace>();
            if (includeProject && projectId is not null)
            {
                // Derive the label from the namespace's identity so an explicit
                // ?project=_global cannot stamp global rows as "project".
                var label = ScopeOfProject(projectId.Value);
                list.Add(new MemoryNamespace(projectId.Value, label));
            }
            if (includeGlobal)
            {
                var global = ReadProjectByName(MemoryScopes.GlobalProjectName, null);
                if (global is not null && !list.Any(n => n.ProjectId == global.Id))
                    list.Add(new MemoryNamespace(global.Id, MemoryScopes.Global));
            }
            return list;
        }
    }

    private Observation? ReadObservationById(long projectId, long id, SqliteTransaction? transaction)
    {
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc, state, pinned FROM observations WHERE id = $id AND project_id = $p";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", projectId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObservation(r) : null;
    }

    /// <summary>Delete an observation (scoped to a project). Returns true if it existed.</summary>
    public bool DeleteObservation(long projectId, long id)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var deleted = DeleteObservationTx(projectId, id, tx);
            // The AFTER DELETE trigger drops the row from the external-content
            // index inside this same transaction; no corpus rebuild is needed.
            tx.Commit();
            return deleted;
        }
    }

    private bool DeleteObservationTx(long projectId, long id, SqliteTransaction tx)
    {
        using var cmd = CreateCommand(tx);
        cmd.CommandText = "DELETE FROM observations WHERE id = $id AND project_id = $p";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", projectId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Promote a project observation to the global namespace.
    /// <para>
    /// No collision: the row is MOVED (its project_id is reassigned), keeping its
    /// id and topic_key. Collision (a global memory already owns the topic_key):
    /// the promoted content WINS — the existing global row is updated in place and
    /// the now-redundant project row is deleted, so nothing is duplicated and the
    /// newer content is not discarded. The FTS sync triggers keep the index
    /// correct through the UPDATE and DELETE; no rebuild.
    /// </para>
    /// Returns null when the observation is not in the project; the bool is true
    /// when the promotion merged into an existing global row.
    /// </summary>
    public (Observation Observation, bool Merged)? PromoteObservation(long projectId, long id)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var source = ReadObservationById(projectId, id, tx);
            if (source is null)
            {
                tx.Commit();
                return null;
            }

            var global = GetOrCreateProjectCore(MemoryScopes.GlobalProjectName, null, tx);

            // A global row with the same topic key (and not the source itself,
            // which is only possible if the caller already passed the global
            // namespace) triggers the merge path.
            var existing = string.IsNullOrEmpty(source.TopicKey)
                ? null
                : FindLatestByTopicKey(global.Id, source.TopicKey, tx);
            if (existing is not null && existing.Id != source.Id)
            {
                using (var update = CreateCommand(tx))
                {
                    update.CommandText = @"
                        UPDATE observations
                        SET title = $t, content = $c, what = $w, why = $y, where_ = $wh, learned = $l, type = $ty, updated_at_utc = $n
                        WHERE id = $id";
                    update.Parameters.AddWithValue("$t", source.Title);
                    update.Parameters.AddWithValue("$c", source.Content);
                    update.Parameters.AddWithValue("$w", (object?)source.What ?? DBNull.Value);
                    update.Parameters.AddWithValue("$y", (object?)source.Why ?? DBNull.Value);
                    update.Parameters.AddWithValue("$wh", (object?)source.Where ?? DBNull.Value);
                    update.Parameters.AddWithValue("$l", (object?)source.Learned ?? DBNull.Value);
                    update.Parameters.AddWithValue("$ty", (object?)source.Type ?? DBNull.Value);
                    update.Parameters.AddWithValue("$n", Now());
                    update.Parameters.AddWithValue("$id", existing.Id);
                    update.ExecuteNonQuery();
                }

                // The project row is now redundant: the promoted content lives in
                // the surviving global row.
                DeleteObservationTx(projectId, source.Id, tx);

                var merged = ReadObservationById(global.Id, existing.Id, tx)!;
                merged.Scope = MemoryScopes.Global;
                tx.Commit();
                return (merged, true);
            }

            using (var move = CreateCommand(tx))
            {
                move.CommandText = "UPDATE observations SET project_id = $g, updated_at_utc = $n WHERE id = $id AND project_id = $p";
                move.Parameters.AddWithValue("$g", global.Id);
                move.Parameters.AddWithValue("$n", Now());
                move.Parameters.AddWithValue("$id", source.Id);
                move.Parameters.AddWithValue("$p", projectId);
                move.ExecuteNonQuery();
            }

            var moved = ReadObservationById(global.Id, source.Id, tx)!;
            moved.Scope = MemoryScopes.Global;
            tx.Commit();
            return (moved, false);
        }
    }

    public IReadOnlyList<Observation> ListObservations(long projectId, int limit = 50, string? state = null)
        => ListObservations(new[] { new MemoryNamespace(projectId, ScopeOfProject(projectId)) }, limit, state);

    /// <summary>
    /// List observations across every requested namespace, stamping each row
    /// with its origin scope. Bounded by <paramref name="limit"/> and ordered
    /// deterministically (pinned, then updated_at_utc, then id).
    /// <para>
    /// <paramref name="state"/> selects the lifecycle filter: absent or unknown →
    /// <c>active</c> only; a concrete state → that state only; <c>all</c> → every
    /// state. Archived rows are therefore never returned unless explicitly asked.
    /// </para>
    /// </summary>
    public IReadOnlyList<Observation> ListObservations(IReadOnlyList<MemoryNamespace> namespaces, int limit = 50, string? state = null)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
            var list = new List<Observation>();
            if (namespaces.Count == 0) return list;

            var ids = namespaces.Select(n => n.ProjectId).Distinct().ToArray();
            var scopeById = new Dictionary<long, string>();
            foreach (var n in namespaces) scopeById[n.ProjectId] = n.Scope;

            var filter = ReadState(state);
            using var cmd = CreateCommand();
            // pinned floats to the front deterministically, then updated_at_utc is the
            // real recency signal: a topic-key upsert keeps created_at_utc, so ordering
            // by created_at would bury a memory that just evolved at its original
            // position. id breaks ties deterministically.
            cmd.CommandText =
                "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc, state, pinned FROM observations WHERE project_id IN ("
                + string.Join(", ", ids.Select((_, i) => "$p" + i))
                + ")";
            if (!MemoryStates.IsAll(filter)) cmd.CommandText += " AND state = $st";
            cmd.CommandText += " ORDER BY pinned DESC, updated_at_utc DESC, id DESC LIMIT $l";
            for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("$p" + i, ids[i]);
            if (!MemoryStates.IsAll(filter)) cmd.Parameters.AddWithValue("$st", filter);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var observation = ReadObservation(r);
                observation.Scope = scopeById.TryGetValue(observation.ProjectId, out var scope)
                    ? scope
                    : MemoryScopes.Project;
                list.Add(observation);
            }
            return list;
        }
    }

    /// <summary>Resolve a read's state selector to a concrete state or the <c>all</c> token.</summary>
    private static string ReadState(string? state) =>
        MemoryStates.IsAll(state) ? MemoryStates.All : MemoryStates.Resolve(state, MemoryStates.Active);

    public IReadOnlyList<Observation> GetByTopicKey(long projectId, string topicKey)
    {
        lock (_gate)
        {
            var list = new List<Observation>();
            using var cmd = CreateCommand();
            // No state filter: a timeline by key is an exact lookup, so it shows the
            // full history including archived rows. The default state filter applies
            // to search and recency listing, not to a deliberate by-key read.
            cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc, state, pinned FROM observations WHERE project_id = $p AND topic_key = $tk ORDER BY created_at_utc DESC";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$tk", topicKey);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadObservation(r));
            return list;
        }
    }

    public IReadOnlyList<SearchHit> Search(long projectId, string query, int limit = 10, string? type = null, string? topicKey = null, string? state = null)
        => Search(new[] { new MemoryNamespace(projectId, ScopeOfProject(projectId)) }, query, limit, type, topicKey, state);

    /// <summary>
    /// Cross-scope search: one FTS pass over every requested namespace. Each hit
    /// carries its origin scope so a caller can tell a repo fact from a universal
    /// heuristic. Results stay bounded by <paramref name="limit"/> and ordered
    /// deterministically (bm25 rank, then id).
    /// <para>
    /// <paramref name="state"/> selects the lifecycle filter exactly like
    /// <see cref="ListObservations(IReadOnlyList{MemoryNamespace}, int, string?)"/>:
    /// absent or unknown → <c>active</c> only; a concrete state → that state only;
    /// <c>all</c> → every state. Archived rows are never returned by default.
    /// </para>
    /// </summary>
    public IReadOnlyList<SearchHit> Search(IReadOnlyList<MemoryNamespace> namespaces, string query, int limit = 10, string? type = null, string? topicKey = null, string? state = null)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
            if (namespaces.Count == 0)
            {
                return Array.Empty<SearchHit>();
            }

            // FtsQuery drops every term shorter than the trigram minimum, so "ab",
            // "a", "" and whitespace-only all escape to the empty string. FTS5
            // rejects MATCH '' with `fts5: syntax error near ""`, which used to
            // bubble up as an HTTP 500. A query with no searchable term has no hits
            // by definition, so return early instead of touching the index.
            var ftsQuery = FtsQuery(query);
            if (ftsQuery.Length == 0)
            {
                return Array.Empty<SearchHit>();
            }

            var filter = ReadState(state);
            var hits = SearchCore(namespaces, ftsQuery, limit, type, topicKey, filter);
            // Trigram tokenizer matches substrings, not fuzzy typos. When a strict
            // AND match finds nothing, retry with OR of the query's trigrams so a
            // typo'd word (1-3 chars off in a long word) still matches documents
            // sharing enough trigrams, ranked by bm25.
            if (hits.Count == 0 && query.Where(char.IsLetterOrDigit).Count() >= 6)
            {
                var fallback = FtsTrigramFallback(query);
                if (fallback.Length != 0)
                {
                    hits = SearchCore(namespaces, fallback, limit, type, topicKey, filter);
                }
            }
            return hits;
        }
    }

    private List<SearchHit> SearchCore(IReadOnlyList<MemoryNamespace> namespaces, string ftsQuery, int limit, string? type = null, string? topicKey = null, string state = MemoryStates.Active)
    {
        var list = new List<SearchHit>();
        // Hard guarantee for every caller, including the trigram fallback: an FTS5
        // MATCH is never issued with an empty expression.
        if (string.IsNullOrEmpty(ftsQuery) || namespaces.Count == 0)
        {
            return list;
        }

        var ids = namespaces.Select(n => n.ProjectId).Distinct().ToArray();
        var scopeById = new Dictionary<long, string>();
        foreach (var n in namespaces) scopeById[n.ProjectId] = n.Scope;

        using var cmd = CreateCommand();
        // One MATCH across every requested project_id keeps bm25 comparable
        // between scopes; ORDER BY (rank, id) is deterministic and LIMIT bounds
        // the merged result set.
        var sql = @"
            SELECT o.id, o.title, o.content, o.topic_key, o.type, o.created_at_utc, bm25(observations_fts) AS rank, o.project_id, o.state
            FROM observations_fts
            JOIN observations o ON o.id = observations_fts.rowid
            WHERE observations_fts MATCH $q AND o.project_id IN (";
        sql += string.Join(", ", ids.Select((_, i) => "$p" + i));
        sql += ")";
        if (type is not null) sql += " AND o.type = $ty";
        if (topicKey is not null) sql += " AND o.topic_key = $tk";
        if (!MemoryStates.IsAll(state)) sql += " AND o.state = $st";
        sql += "\n            ORDER BY rank, o.id\n            LIMIT $l;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("$p" + i, ids[i]);
        if (type is not null) cmd.Parameters.AddWithValue("$ty", type);
        if (topicKey is not null) cmd.Parameters.AddWithValue("$tk", topicKey);
        if (!MemoryStates.IsAll(state)) cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var projectId = r.GetInt64(7);
            list.Add(new SearchHit
            {
                Id = r.GetInt64(0),
                Title = r.GetString(1),
                Content = r.GetString(2),
                TopicKey = r.IsDBNull(3) ? null : r.GetString(3),
                Type = r.IsDBNull(4) ? null : r.GetString(4),
                CreatedAtUtc = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Rank = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                Scope = scopeById.TryGetValue(projectId, out var scope) ? scope : MemoryScopes.Project,
                State = r.IsDBNull(8) ? null : r.GetString(8),
            });
        }
        return list;
    }

    /// <summary>
    /// Change an observation's lifecycle state by id. Scoped to
    /// <paramref name="projectId"/>, so a caller cannot reach into another
    /// project's (or the global) namespace by id. Returns the updated row, or null
    /// when the id is unknown in that namespace or the state is not a known state.
    /// The recency stamp is deliberately NOT moved: state and pin are metadata, not
    /// a content edit, so they must not reshuffle a listing.
    /// </summary>
    public Observation? SetObservationState(long projectId, long id, string state)
    {
        lock (_gate)
        {
            var resolved = state?.Trim().ToLowerInvariant();
            if (!MemoryStates.IsKnownState(resolved)) return null;

            // Derive the origin label before the transaction: it needs its own command
            // and a command may not run beside an open transaction.
            var scope = ScopeOfProject(projectId);
            using var tx = _conn.BeginTransaction();
            if (ReadObservationById(projectId, id, tx) is null)
            {
                tx.Commit();
                return null;
            }

            using (var cmd = CreateCommand(tx))
            {
                cmd.CommandText = "UPDATE observations SET state = $s WHERE id = $id AND project_id = $p";
                cmd.Parameters.AddWithValue("$s", resolved!);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$p", projectId);
                cmd.ExecuteNonQuery();
            }

            var updated = ReadObservationById(projectId, id, tx)!;
            tx.Commit();
            updated.Scope = scope;
            return updated;
        }
    }

    /// <summary>
    /// Pin or unpin an observation by id, scoped to <paramref name="projectId"/>.
    /// Pinning is the hook the global injection budget uses: only pinned global
    /// heuristics are eligible for the automatic context path. Returns the updated
    /// row, or null when the id is unknown in that namespace.
    /// </summary>
    public Observation? SetObservationPinned(long projectId, long id, bool pinned)
    {
        lock (_gate)
        {
            var scope = ScopeOfProject(projectId);
            using var tx = _conn.BeginTransaction();
            if (ReadObservationById(projectId, id, tx) is null)
            {
                tx.Commit();
                return null;
            }

            using (var cmd = CreateCommand(tx))
            {
                cmd.CommandText = "UPDATE observations SET pinned = $pin WHERE id = $id AND project_id = $p";
                cmd.Parameters.AddWithValue("$pin", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$p", projectId);
                cmd.ExecuteNonQuery();
            }

            var updated = ReadObservationById(projectId, id, tx)!;
            tx.Commit();
            updated.Scope = scope;
            return updated;
        }
    }

    /// <summary>
    /// Bounded review listing: rows flagged <c>needs_review</c> plus duplicate
    /// candidates, with truncated previews and a hard cap. The response never
    /// carries an observation body.
    /// </summary>
    public IReadOnlyList<ReviewItem> ReviewObservations(IReadOnlyList<MemoryNamespace> namespaces, int limit = MaxReviewItems)
    {
        lock (_gate)
        {
            limit = Math.Clamp(limit, 1, MaxReviewItems);
            var items = new List<ReviewItem>();
            if (namespaces.Count == 0) return items;

            var scopeById = new Dictionary<long, string>();
            foreach (var n in namespaces) scopeById[n.ProjectId] = n.Scope;
            var ids = scopeById.Keys.ToArray();

            // Flagged rows first: they are the priority, and the same hard cap bounds
            // both this query and the whole listing.
            using (var cmd = CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, project_id, title, content, state FROM observations WHERE project_id IN ("
                    + string.Join(", ", ids.Select((_, i) => "$p" + i))
                    + ") AND state = $review ORDER BY updated_at_utc DESC, id DESC LIMIT $l";
                for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("$p" + i, ids[i]);
                cmd.Parameters.AddWithValue("$review", MemoryStates.NeedsReview);
                cmd.Parameters.AddWithValue("$l", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    items.Add(new ReviewItem
                    {
                        Kind = "needs_review",
                        Id = r.GetInt64(0),
                        Title = Preview(r.GetString(2), TitlePreviewChars),
                        Preview = Preview(r.GetString(3), ReviewPreviewChars),
                        State = r.GetString(4),
                        Scope = scopeById.TryGetValue(r.GetInt64(1), out var s) ? s : MemoryScopes.Project,
                    });
                }
            }

            // Duplicate candidates: pairwise within the bounded recent window of each
            // namespace. O(window^2) on small token sets — deterministic, no LLM —
            // and still bounded by the same limit.
            foreach (var ns in namespaces)
            {
                if (items.Count >= limit) break;
                ScanDuplicates(ns, items, limit);
            }
            return items;
        }
    }

    private void ScanDuplicates(MemoryNamespace ns, List<ReviewItem> items, int limit)
    {
        var rows = new List<(long Id, string Title, string Content)>();
        using (var cmd = CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, content FROM observations WHERE project_id = $p AND state <> $archived ORDER BY updated_at_utc DESC, id DESC LIMIT $w";
            cmd.Parameters.AddWithValue("$p", ns.ProjectId);
            cmd.Parameters.AddWithValue("$archived", MemoryStates.Archived);
            cmd.Parameters.AddWithValue("$w", MemoryDedup.WindowSize);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }

        var signatures = rows.Select(row => MemoryDedup.Signature(row.Title, row.Content)).ToArray();
        for (var i = 0; i < rows.Count; i++)
        {
            if (items.Count >= limit) return;
            // rows are newest-first: i is the newer row, j the older one it may
            // duplicate. One partner per row keeps the listing from ballooning.
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (MemoryDedup.Dice(signatures[i], signatures[j]) < MemoryDedup.NearDuplicateThreshold) continue;
                items.Add(new ReviewItem
                {
                    Kind = "duplicate",
                    Id = rows[i].Id,
                    Title = Preview(rows[i].Title, TitlePreviewChars),
                    Preview = Preview(rows[i].Content, ReviewPreviewChars),
                    Scope = ns.Scope,
                    DuplicateOfId = rows[j].Id,
                });
                break;
            }
        }
    }

    public IReadOnlyList<string> SuggestTopicKeys(long projectId, int limit = 20)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
            var list = new List<string>();
            using var cmd = CreateCommand();
            cmd.CommandText = "SELECT DISTINCT topic_key FROM observations WHERE project_id = $p AND topic_key IS NOT NULL ORDER BY topic_key LIMIT $l";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    // =====================================================================
    // Sessions
    // =====================================================================

    /// <summary>
    /// Start (or resume) a session. Idempotent on (project, session_id): starting
    /// a session that already exists does not insert a second row and does not
    /// throw — the existing session is returned unchanged. The INSERT ... ON
    /// CONFLICT DO NOTHING + read-back keeps this atomic even if two processes
    /// (daemon and CLI on the same db) race, instead of a SELECT-then-INSERT.
    /// </summary>
    public Session StartSession(long projectId, string sessionId)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = CreateCommand(tx);
            cmd.CommandText = @"
                INSERT INTO sessions (project_id, session_id, started_at_utc)
                VALUES ($p, $s, $n)
                ON CONFLICT(project_id, session_id) DO NOTHING;";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$n", Now());
            cmd.ExecuteNonQuery();

            var session = ReadSessionByKey(projectId, sessionId, tx)!;
            tx.Commit();
            return session;
        }
    }

    private Session? ReadSessionByKey(long projectId, string sessionId, SqliteTransaction? transaction)
    {
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, project_id, session_id, started_at_utc, ended_at_utc, summary, goal, next_steps FROM sessions WHERE project_id = $p AND session_id = $s";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public void EndSession(long projectId, string sessionId, string? summary, string? goal, string? nextSteps)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = CreateCommand(tx);
            cmd.CommandText = "UPDATE sessions SET ended_at_utc = $e, summary = $s, goal = $g, next_steps = $n WHERE project_id = $p AND session_id = $sid";
            cmd.Parameters.AddWithValue("$e", Now());
            cmd.Parameters.AddWithValue("$s", (object?)summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$g", (object?)goal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", (object?)nextSteps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public IReadOnlyList<Session> ListSessions(long projectId, int limit = 20)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
            var list = new List<Session>();
            using var cmd = CreateCommand();
            cmd.CommandText = "SELECT id, project_id, session_id, started_at_utc, ended_at_utc, summary, goal, next_steps FROM sessions WHERE project_id = $p ORDER BY started_at_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadSession(r));
            }
            return list;
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    // Every caller-supplied limit is clamped into this range before it reaches a
    // SQL LIMIT. SQLite reads LIMIT -1 as "no limit", so an unauthenticated local
    // caller passing -1 could otherwise dump the whole corpus in one request.
    private const int MaxLimit = 500;

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    // =====================================================================
    // Bounded surfaces (review / automatic context)
    // =====================================================================

    // The hard cap on the review listing. A caller may ask for fewer; nobody can
    // ask for more, so the review response can never become a corpus dump.
    public const int MaxReviewItems = 50;

    // Preview bounds. Every review row and every context row is truncated: the body
    // never leaves the store on a bounded surface.
    private const int TitlePreviewChars = 80;
    private const int ReviewPreviewChars = 200;

    /// <summary>
    /// Collapse whitespace and truncate to <paramref name="max"/> characters with a
    /// trailing ellipsis. Used by every bounded surface so it never returns a body.
    /// </summary>
    private static string Preview(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }

    /// <summary>
    /// Rebuild the FTS index from the content table. FTS5 EXTERNAL-CONTENT tables
    /// must NOT be written directly: a manual INSERT/DELETE against observations_fts
    /// corrupts the index ("database disk image is malformed" on the first write
    /// after a rebuild). 'rebuild' is the canonical external-content sync — it is
    /// O(N) but safe. It runs ONLY on schema migration (Initialize); the write path
    /// is served by the O(1) sync triggers, so no save/delete touches the corpus.
    /// </summary>
    private void RebuildFts()
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "INSERT INTO observations_fts(observations_fts) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    // Minimum trigger length of the trigram tokenizer. A term shorter than this
    // cannot be matched by grams and FtsQuery drops it too.
    private const int TrigramLength = 3;

    // One cap for the fallback, applied PER TOKEN. The old code collected up to 40
    // grams and then used only 20 — neither was a documented bound. Bounding each
    // token keeps the OR-group small no matter how many tokens the query has.
    private const int MaxTrigramsPerToken = 32;

    /// <summary>
    /// Split a query into terms on any whitespace, so a whitespace-only query
    /// (tabs, newlines) collapses to "no terms" instead of becoming a MATCH
    /// expression that FTS5 rejects with `syntax error near ""`.
    /// </summary>
    private static string[] QueryTokens(string raw) =>
        raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private static string FtsQuery(string raw)
    {
        // Escape FTS5 special chars and AND the terms so multi-word queries
        // require all terms (closer to a focused search). Trigram tokenizer
        // matches substrings of >=3 chars; drop 1-2-char terms (they would
        // match nothing / could error) so a typo'd fragment still finds data.
        var terms = QueryTokens(raw)
            .Where(t => t.Length >= TrigramLength)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"");
        return string.Join(" AND ", terms);
    }

    /// <summary>
    /// Typo-tolerant fallback used only when the strict AND match is empty. It
    /// keeps "all query terms must be present" semantics: each token contributes
    /// its own OR-group of trigrams, and the token groups are ANDed together. A
    /// typo inside one token still matches (its group shares enough grams), but a
    /// token entirely absent cannot be satisfied by another token's grams.
    /// Trigrams are built per token — never across token boundaries — so no gram
    /// spans a space, and a HashSet keeps the dedup linear instead of quadratic.
    /// </summary>
    private static string FtsTrigramFallback(string raw)
    {
        var tokenGroups = new List<string>();
        foreach (var token in QueryTokens(raw))
        {
            var norm = new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (norm.Length < TrigramLength) continue;

            var seen = new HashSet<string>();
            var grams = new List<string>();
            for (var i = 0; i + TrigramLength <= norm.Length && grams.Count < MaxTrigramsPerToken; i++)
            {
                var g = norm.Substring(i, TrigramLength);
                if (seen.Add(g)) grams.Add(g);
            }
            if (grams.Count == 0) continue;

            tokenGroups.Add("(" + string.Join(" OR ", grams.Select(g => "\"" + g + "\"")) + ")");
        }
        return string.Join(" AND ", tokenGroups);
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>
    /// Create a command on the shared connection. When a transaction is open the
    /// command must be bound to it, otherwise Microsoft.Data.Sqlite rejects the
    /// command for not matching the connection's active transaction.
    /// </summary>
    private SqliteCommand CreateCommand(SqliteTransaction? transaction = null)
    {
        var cmd = _conn.CreateCommand();
        if (transaction is not null)
        {
            cmd.Transaction = transaction;
        }
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Run a value-returning PRAGMA, consuming its result row.</summary>
    private void Pragma(string sql)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteScalar();
    }

    private static Project ReadProject(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        RootPath = r.IsDBNull(2) ? null : r.GetString(2),
        CreatedAtUtc = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        UpdatedAtUtc = DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static Session ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ProjectId = r.GetInt64(1),
        SessionId = r.GetString(2),
        StartedAtUtc = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        EndedAtUtc = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Summary = r.IsDBNull(5) ? null : r.GetString(5),
        Goal = r.IsDBNull(6) ? null : r.GetString(6),
        NextSteps = r.IsDBNull(7) ? null : r.GetString(7),
    };

    private static Observation ReadObservation(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ProjectId = r.GetInt64(1),
        Title = r.GetString(2),
        Content = r.GetString(3),
        What = r.IsDBNull(4) ? null : r.GetString(4),
        Why = r.IsDBNull(5) ? null : r.GetString(5),
        Where = r.IsDBNull(6) ? null : r.GetString(6),
        Learned = r.IsDBNull(7) ? null : r.GetString(7),
        TopicKey = r.IsDBNull(8) ? null : r.GetString(8),
        Type = r.IsDBNull(9) ? null : r.GetString(9),
        CreatedAtUtc = DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
        UpdatedAtUtc = DateTime.Parse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
        State = r.GetString(12),
        Pinned = r.GetInt64(13) != 0,
    };

    public void Dispose() => _conn.Dispose();
}
