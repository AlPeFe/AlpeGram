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
    private const int SchemaVersion = 2;

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
                updated_at_utc TEXT NOT NULL
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
            Exec($"PRAGMA user_version={SchemaVersion};");
        }
        else
        {
            // Already migrated. Re-assert the triggers: they are the only thing that
            // keeps the index in sync, and a database written by an intermediate
            // build can carry the table without them.
            CreateFtsSyncTriggers();
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

            tx.Commit();
            return project;
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
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var existing = string.IsNullOrEmpty(topicKey) ? null : FindLatestByTopicKey(projectId, topicKey, tx);
            long id;
            var created = existing is null;
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
            }
            // Read back on the same transaction rather than through the public
            // GetObservation: that method would be correct too (it opens no
            // transaction of its own), but binding the read to this transaction
            // keeps the readback on the connection's active transaction.
            var observation = ReadObservationById(projectId, id, tx)!;
            tx.Commit();
            return (observation, created);
        }
    }

    private Observation? FindLatestByTopicKey(long projectId, string topicKey, SqliteTransaction? transaction)
    {
        // Order by updated_at_utc, not created_at_utc: an upsert preserves the
        // original created_at, so created_at ordering would keep returning the
        // stalest duplicate of a topic key.
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p AND topic_key = $tk ORDER BY updated_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$tk", topicKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObservation(r) : null;
    }

    public Observation? GetObservation(long projectId, long id)
    {
        lock (_gate)
        {
            return ReadObservationById(projectId, id, null);
        }
    }

    private Observation? ReadObservationById(long projectId, long id, SqliteTransaction? transaction)
    {
        using var cmd = CreateCommand(transaction);
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE id = $id AND project_id = $p";
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
            using var cmd = CreateCommand(tx);
            cmd.CommandText = "DELETE FROM observations WHERE id = $id AND project_id = $p";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$p", projectId);
            var deleted = cmd.ExecuteNonQuery() > 0;
            // The AFTER DELETE trigger drops the row from the external-content
            // index inside this same transaction; no corpus rebuild is needed.
            tx.Commit();
            return deleted;
        }
    }

    public IReadOnlyList<Observation> ListObservations(long projectId, int limit = 50)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
            var list = new List<Observation>();
            using var cmd = CreateCommand();
            // updated_at_utc is the real recency signal: a topic-key upsert keeps
            // created_at_utc, so ordering by created_at would bury a memory that
            // just evolved at its original position.
            cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p ORDER BY updated_at_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadObservation(r));
            return list;
        }
    }

    public IReadOnlyList<Observation> GetByTopicKey(long projectId, string topicKey)
    {
        lock (_gate)
        {
            var list = new List<Observation>();
            using var cmd = CreateCommand();
            cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p AND topic_key = $tk ORDER BY created_at_utc DESC";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$tk", topicKey);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadObservation(r));
            return list;
        }
    }

    public IReadOnlyList<SearchHit> Search(long projectId, string query, int limit = 10, string? type = null, string? topicKey = null)
    {
        lock (_gate)
        {
            limit = ClampLimit(limit);
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

            var hits = SearchCore(projectId, ftsQuery, limit, type, topicKey);
            // Trigram tokenizer matches substrings, not fuzzy typos. When a strict
            // AND match finds nothing, retry with OR of the query's trigrams so a
            // typo'd word (1-3 chars off in a long word) still matches documents
            // sharing enough trigrams, ranked by bm25.
            if (hits.Count == 0 && query.Where(char.IsLetterOrDigit).Count() >= 6)
            {
                var fallback = FtsTrigramFallback(query);
                if (fallback.Length != 0)
                {
                    hits = SearchCore(projectId, fallback, limit, type, topicKey);
                }
            }
            return hits;
        }
    }

    private List<SearchHit> SearchCore(long projectId, string ftsQuery, int limit, string? type = null, string? topicKey = null)
    {
        var list = new List<SearchHit>();
        // Hard guarantee for every caller, including the trigram fallback: an FTS5
        // MATCH is never issued with an empty expression.
        if (string.IsNullOrEmpty(ftsQuery))
        {
            return list;
        }
        using var cmd = CreateCommand();
        var sql = @"
            SELECT o.id, o.title, o.content, o.topic_key, o.type, o.created_at_utc, bm25(observations_fts) AS rank
            FROM observations_fts
            JOIN observations o ON o.id = observations_fts.rowid
            WHERE observations_fts MATCH $q AND o.project_id = $p";
        if (type is not null) sql += " AND o.type = $ty";
        if (topicKey is not null) sql += " AND o.topic_key = $tk";
        sql += "\n            ORDER BY rank\n            LIMIT $l;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$p", projectId);
        if (type is not null) cmd.Parameters.AddWithValue("$ty", type);
        if (topicKey is not null) cmd.Parameters.AddWithValue("$tk", topicKey);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SearchHit
            {
                Id = r.GetInt64(0),
                Title = r.GetString(1),
                Content = r.GetString(2),
                TopicKey = r.IsDBNull(3) ? null : r.GetString(3),
                Type = r.IsDBNull(4) ? null : r.GetString(4),
                CreatedAtUtc = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Rank = r.IsDBNull(6) ? 0 : r.GetDouble(6),
            });
        }
        return list;
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

    public Session StartSession(long projectId, string sessionId)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = CreateCommand(tx);
            cmd.CommandText = "INSERT INTO sessions (project_id, session_id, started_at_utc) VALUES ($p, $s, $n); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$n", Now());
            var id = (long)cmd.ExecuteScalar()!;
            tx.Commit();
            return new Session { Id = id, ProjectId = projectId, SessionId = sessionId, StartedAtUtc = DateTime.UtcNow };
        }
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
                list.Add(new Session
                {
                    Id = r.GetInt64(0),
                    ProjectId = r.GetInt64(1),
                    SessionId = r.GetString(2),
                    StartedAtUtc = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    EndedAtUtc = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Summary = r.IsDBNull(5) ? null : r.GetString(5),
                    Goal = r.IsDBNull(6) ? null : r.GetString(6),
                    NextSteps = r.IsDBNull(7) ? null : r.GetString(7),
                });
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

    private static string FtsQuery(string raw)
    {
        // Escape FTS5 special chars and AND the terms so multi-word queries
        // require all terms (closer to a focused search). Trigram tokenizer
        // matches substrings of >=3 chars; drop 1-2-char terms (they would
        // match nothing / could error) so a typo'd fragment still finds data.
        // Split on any whitespace so a whitespace-only query (tabs, newlines)
        // also collapses to "no terms" instead of becoming a MATCH expression.
        var terms = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"");
        return string.Join(" AND ", terms);
    }

    /// <summary>
    /// OR of the query's trigrams: enables fuzzy/typo-tolerant matching. With a
    /// trigram tokenizer, "rozado" and "rosado" share enough trigrams that OR-ing
    /// them lets bm25 rank the typo'd document. Cap the OR list so query size
    /// stays bounded (trigram only needs >=3 chars, and we only enter this path
    /// for queries of >=6 letters).
    /// </summary>
    private static string FtsTrigramFallback(string raw)
    {
        var grams = new List<string>();
        var norm = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        for (var i = 0; i + 3 <= norm.Length && grams.Count < 40; i++)
        {
            var g = norm.Substring(i, 3);
            if (!grams.Contains(g)) grams.Add(g);
        }
        return string.Join(" OR ", grams.Select(g => "\"" + g + "\"").Take(20));
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
    };

    public void Dispose() => _conn.Dispose();
}
