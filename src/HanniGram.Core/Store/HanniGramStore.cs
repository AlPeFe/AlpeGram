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

    private void Initialize()
    {
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
            CREATE INDEX IF NOT EXISTS idx_sess_project ON sessions(project_id);
            -- FTS5 with trigram tokenizer: typo-tolerant and substring/CJK search.
            -- Drop+recreate forces the new tokenizer on existing DBs (external-content
            -- safe: observations rows are untouched; 'rebuild' below re-indexes them).
            DROP TABLE IF EXISTS observations_fts;
            CREATE VIRTUAL TABLE observations_fts USING fts5(
                title, content, topic_key, type,
                tokenize='trigram',
                content='observations', content_rowid='id'
            );
        ");
        // Rebuild FTS if the external-content table is out of sync (e.g. after schema change).
        Exec("INSERT INTO observations_fts(observations_fts) VALUES('rebuild');");
    }

    // =====================================================================
    // Projects
    // =====================================================================

    public Project? GetProjectByName(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, root_path, created_at_utc, updated_at_utc FROM projects WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProject(r) : null;
    }

    public Project GetOrCreateProject(string name, string? rootPath = null)
    {
        var existing = GetProjectByName(name);
        if (existing != null)
        {
            if (rootPath != null && existing.RootPath != rootPath)
            {
                using var up = _conn.CreateCommand();
                up.CommandText = "UPDATE projects SET root_path = $r, updated_at_utc = $u WHERE id = $id";
                up.Parameters.AddWithValue("$r", rootPath);
                up.Parameters.AddWithValue("$u", Now());
                up.Parameters.AddWithValue("$id", existing.Id);
                up.ExecuteNonQuery();
                existing.RootPath = rootPath;
            }
            return existing;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO projects (name, root_path, created_at_utc, updated_at_utc) VALUES ($n, $r, $c, $u); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$r", (object?)rootPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$u", Now());
        var id = (long)cmd.ExecuteScalar()!;
        return new Project { Id = id, Name = name, RootPath = rootPath, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
    }

    public IReadOnlyList<Project> ListProjects()
    {
        var list = new List<Project>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, root_path, created_at_utc, updated_at_utc FROM projects ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadProject(r));
        return list;
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
    /// Returns the persisted observation and whether it was newly created.
    /// </summary>
    public (Observation Observation, bool Created) SaveObservation(long projectId, string title, string content,
        string? what = null, string? why = null, string? where = null, string? learned = null,
        string? topicKey = null, string? type = null)
    {
        var existing = string.IsNullOrEmpty(topicKey) ? null : FindLatestByTopicKey(projectId, topicKey);
        long id;
        var created = existing is null;
        if (created)
        {
            using var cmd = _conn.CreateCommand();
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
            using var cmd = _conn.CreateCommand();
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
        SyncFtsRow(id);
        return (GetObservation(projectId, id)!, created);
    }

    private Observation? FindLatestByTopicKey(long projectId, string topicKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p AND topic_key = $tk ORDER BY created_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$tk", topicKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObservation(r) : null;
    }

	public Observation? GetObservation(long projectId, long id)
	{
		using var cmd = _conn.CreateCommand();
		cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE id = $id AND project_id = $p";
		cmd.Parameters.AddWithValue("$id", id);
		cmd.Parameters.AddWithValue("$p", projectId);
		using var r = cmd.ExecuteReader();
		return r.Read() ? ReadObservation(r) : null;
	}

	/// <summary>Delete an observation (scoped to a project) and its FTS row. Returns true if it existed.</summary>
	public bool DeleteObservation(long projectId, long id)
	{
		using var cmd = _conn.CreateCommand();
		cmd.CommandText = "DELETE FROM observations WHERE id = $id AND project_id = $p";
		cmd.Parameters.AddWithValue("$id", id);
		cmd.Parameters.AddWithValue("$p", projectId);
		var deleted = cmd.ExecuteNonQuery() > 0;
		if (deleted)
		{
			using var fts = _conn.CreateCommand();
			fts.CommandText = "DELETE FROM observations_fts WHERE rowid = $id";
			fts.Parameters.AddWithValue("$id", id);
			fts.ExecuteNonQuery();
		}
		return deleted;
	}

    public IReadOnlyList<Observation> ListObservations(long projectId, int limit = 50)
    {
        var list = new List<Observation>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p ORDER BY created_at_utc DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadObservation(r));
        return list;
    }

    public IReadOnlyList<Observation> GetByTopicKey(long projectId, string topicKey)
    {
        var list = new List<Observation>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, title, content, what, why, where_, learned, topic_key, type, created_at_utc, updated_at_utc FROM observations WHERE project_id = $p AND topic_key = $tk ORDER BY created_at_utc DESC";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$tk", topicKey);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadObservation(r));
        return list;
    }

    public IReadOnlyList<SearchHit> Search(long projectId, string query, int limit = 10, string? type = null, string? topicKey = null)
    {
        var hits = SearchCore(projectId, FtsQuery(query), limit, type, topicKey);
        // Trigram tokenizer matches substrings, not fuzzy typos. When a strict
        // AND match finds nothing, retry with OR of the query's trigrams so a
        // typo'd word (1-3 chars off in a long word) still matches documents
        // sharing enough trigrams, ranked by bm25.
        if (hits.Count == 0 && query.Where(char.IsLetterOrDigit).Count() >= 6)
        {
            hits = SearchCore(projectId, FtsTrigramFallback(query), limit, type, topicKey);
        }
        return hits;
    }

    private List<SearchHit> SearchCore(long projectId, string ftsQuery, int limit, string? type = null, string? topicKey = null)
    {
        var list = new List<SearchHit>();
        using var cmd = _conn.CreateCommand();
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
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT topic_key FROM observations WHERE project_id = $p AND topic_key IS NOT NULL ORDER BY topic_key LIMIT $l";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // =====================================================================
    // Sessions
    // =====================================================================

    public Session StartSession(long projectId, string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (project_id, session_id, started_at_utc) VALUES ($p, $s, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$n", Now());
        var id = (long)cmd.ExecuteScalar()!;
        return new Session { Id = id, ProjectId = projectId, SessionId = sessionId, StartedAtUtc = DateTime.UtcNow };
    }

    public void EndSession(long projectId, string sessionId, string? summary, string? goal, string? nextSteps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET ended_at_utc = $e, summary = $s, goal = $g, next_steps = $n WHERE project_id = $p AND session_id = $sid";
        cmd.Parameters.AddWithValue("$e", Now());
        cmd.Parameters.AddWithValue("$s", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)goal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)nextSteps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Session> ListSessions(long projectId, int limit = 20)
    {
        var list = new List<Session>();
        using var cmd = _conn.CreateCommand();
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

    // =====================================================================
    // Helpers
    // =====================================================================

    private void SyncFtsRow(long id)
    {
        // External-content FTS5: delete any existing index entry for this rowid,
        // then re-insert. This makes the sync idempotent for the upsert path
        // (update re-indexes the row instead of leaving a stale/duplicated entry).
        using (var del = _conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM observations_fts WHERE rowid = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO observations_fts(rowid, title, content, topic_key, type) SELECT id, title, content, topic_key, type FROM observations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static string FtsQuery(string raw)
    {
        // Escape FTS5 special chars and AND the terms so multi-word queries
        // require all terms (closer to a focused search). Trigram tokenizer
        // matches substrings of >=3 chars; drop 1-2-char terms (they would
        // match nothing / could error) so a typo'd fragment still finds data.
        var terms = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
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

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
