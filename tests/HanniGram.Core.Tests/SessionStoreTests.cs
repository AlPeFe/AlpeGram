using Xunit;

namespace HanniGram.Core.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public void StartSession_ReturnsPersistedSession()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var session = store.StartSession(project.Id, "session-1");

        Assert.True(session.Id > 0);
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal(project.Id, session.ProjectId);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public void EndSession_PersistsSummaryGoalAndNextSteps()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.StartSession(project.Id, "session-1");

        store.EndSession(project.Id, "session-1", "wrapped up", "ship slice 1", "slice 2 next");

        var session = Assert.Single(store.ListSessions(project.Id));
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("wrapped up", session.Summary);
        Assert.Equal("ship slice 1", session.Goal);
        Assert.Equal("slice 2 next", session.NextSteps);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void ListSessions_IsScopedToProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        store.StartSession(alpha.Id, "alpha-session");
        store.StartSession(beta.Id, "beta-session");

        var sessions = store.ListSessions(alpha.Id);

        var session = Assert.Single(sessions);
        Assert.Equal("alpha-session", session.SessionId);
    }

    // Regression: StartSession used to INSERT unconditionally, so starting the
    // same (project, session_id) twice left two rows and EndSession then UPDATEd
    // every matching row. There is now a unique index on (project_id, session_id)
    // and StartSession is idempotent.
    [Fact]
    public void StartSession_IsIdempotentForTheSameSessionId()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var first = store.StartSession(project.Id, "session-1");
        var second = store.StartSession(project.Id, "session-1");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.ListSessions(project.Id));
    }

    // mem_session_summary calls start then end. Repeated summaries for one session
    // id must collapse to a single row whose summary is the most recent one.
    [Fact]
    public void TwoSessionSummaries_ProduceOneRowAndTheLatestSummaryWins()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        store.StartSession(project.Id, "session-1");
        store.EndSession(project.Id, "session-1", "first summary", "goal one", "next one");
        store.StartSession(project.Id, "session-1");
        store.EndSession(project.Id, "session-1", "second summary", "goal two", "next two");

        var session = Assert.Single(store.ListSessions(project.Id));
        Assert.Equal("second summary", session.Summary);
        Assert.Equal("goal two", session.Goal);
        Assert.Equal("next two", session.NextSteps);
    }

    [Fact]
    public void StartingTwiceThenEnding_UpdatesTheSingleRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        store.StartSession(project.Id, "session-1");
        store.StartSession(project.Id, "session-1");
        store.EndSession(project.Id, "session-1", "wrapped up", null, null);

        var session = Assert.Single(store.ListSessions(project.Id));
        Assert.Equal("wrapped up", session.Summary);
        Assert.NotNull(session.EndedAtUtc);
    }

    // A pre-v3 database can already carry duplicate (project, session_id) rows, so
    // CREATE UNIQUE INDEX would fail outright. The migration must deduplicate first,
    // preferring the row that carries a summary (even over a more recently started
    // one), and only then create the index.
    [Fact]
    public void ReopeningWithDuplicateSessions_DeduplicatesAndPrefersTheSummaryRow()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
        }

        // Recreate the legacy state: no unique index, three rows for one session.
        db.Execute("DROP INDEX IF EXISTS uq_sessions_project_session;");
        InsertRawSession(db, projectId, "session-1", "2026-03-01T00:00:00.0000000Z", null);
        InsertRawSession(db, projectId, "session-1", "2026-01-01T00:00:00.0000000Z", "kept summary");
        InsertRawSession(db, projectId, "session-1", "2026-02-01T00:00:00.0000000Z", null);
        db.Execute("PRAGMA user_version=2;");

        using (var store = db.OpenStore())
        {
            var session = Assert.Single(store.ListSessions(projectId));
            Assert.Equal("kept summary", session.Summary);
            Assert.Equal(
                DateTime.Parse("2026-01-01T00:00:00.0000000Z", null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                session.StartedAtUtc);
        }
    }

    // The unique index is also re-asserted on the already-migrated path, the way
    // the FTS trigger triple is: a database written by an intermediate build can
    // carry the current version without the index.
    [Fact]
    public void ReopeningOnTheCurrentVersion_ReassertsTheSessionsUniqueIndex()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
        }

        // Same version (no rewind), but the index is gone and duplicates exist.
        db.Execute("DROP INDEX IF EXISTS uq_sessions_project_session;");
        InsertRawSession(db, projectId, "session-1", "2026-01-01T00:00:00.0000000Z", null);
        InsertRawSession(db, projectId, "session-1", "2026-02-01T00:00:00.0000000Z", null);

        using (var store = db.OpenStore())
        {
            Assert.Single(store.ListSessions(projectId));
            store.StartSession(projectId, "session-1");
            Assert.Single(store.ListSessions(projectId));
        }
    }

    private static void InsertRawSession(
        TempDatabase db, long projectId, string sessionId, string startedAt, string? summary)
    {
        db.Execute(
            @"INSERT INTO sessions (project_id, session_id, started_at_utc, summary)
              VALUES ($p, $s, $started, $summary);",
            ("$p", projectId),
            ("$s", sessionId),
            ("$started", startedAt),
            ("$summary", summary));
    }
}
