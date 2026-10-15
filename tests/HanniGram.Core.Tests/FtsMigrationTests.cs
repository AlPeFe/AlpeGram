using Microsoft.Data.Sqlite;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The SchemaVersion bump must migrate an existing database in place: a v1 index
/// (title/content/topic_key/type only) has to come back as a v2 index that also
/// covers what/why/where_/learned, without touching the observations rows.
/// </summary>
public sealed class FtsMigrationTests
{
    [Fact]
    public void ReopeningAnOlderSchema_ReindexesStructuredFieldsInPlace()
    {
        using var db = new TempDatabase();

        long projectId;
        long observationId;
        using (var store = db.OpenStore())
        {
            var project = store.GetOrCreateProject("alpha");
            projectId = project.Id;
            var (observation, _) = store.SaveObservation(
                project.Id, "Generic title", "generic body", what: "zephyrous payload");
            observationId = observation.Id;
        }

        SimulateVersion1FtsTable(db.DbPath);

        using (var store = db.OpenStore())
        {
            var hit = Assert.Single(store.Search(projectId, "zephyrous"));
            Assert.Equal(observationId, hit.Id);
            Assert.Single(store.ListObservations(projectId));
        }
    }

    /// <summary>
    /// A database whose FTS table was dropped while the sync triggers survived is
    /// exactly the state the migration leaves behind if it drops the table before
    /// the triggers. That state is fatal for writes: SQLite answers the next INSERT
    /// with "no such table: main.observations_fts". Reopening must repair it by
    /// dropping the dangling triggers, recreating the table, recreating the
    /// triggers, and rebuilding — then the write must work.
    /// </summary>
    [Fact]
    public void ReopeningWithDanglingTriggers_LeavesWritesWorking()
    {
        using var db = new TempDatabase();

        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
            store.SaveObservation(projectId, "Existing", "preexisting content");
        }

        // Drop ONLY the index and rewind the version: the triggers that reference it
        // are left behind, pointing at a table that no longer exists.
        db.Execute("DROP TABLE IF EXISTS observations_fts;");
        db.Execute("PRAGMA user_version=1;");

        using (var store = db.OpenStore())
        {
            // Would fail with "no such table: main.observations_fts" if a dangling
            // trigger survived into this write.
            var (observation, created) = store.SaveObservation(projectId, "After migration", "postmigration content");

            Assert.True(created);
            var hit = Assert.Single(store.Search(projectId, "postmigration"));
            Assert.Equal(observation.Id, hit.Id);
            Assert.Single(store.Search(projectId, "preexisting"));
        }
    }

    /// <summary>
    /// The migration must DROP the sync triggers before recreating them, not just
    /// CREATE TRIGGER IF NOT EXISTS: a stale trigger body with the right name would
    /// otherwise survive forever and silently stop indexing writes. This database
    /// carries such a trigger, and the version is rewound so the migration runs.
    /// </summary>
    [Fact]
    public void ReopeningWithAStaleTrigger_ReinstallsTheCorrectTrigger()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
        }

        // Correct name, wrong body: an AFTER INSERT trigger that indexes nothing.
        db.Execute(@"
            DROP TRIGGER IF EXISTS observations_fts_ai;
            CREATE TRIGGER observations_fts_ai AFTER INSERT ON observations BEGIN SELECT 1; END;
            PRAGMA user_version=1;");

        using (var store = db.OpenStore())
        {
            store.SaveObservation(projectId, "Note", "triggerrepairword body");

            Assert.Single(store.Search(projectId, "triggerrepairword"));
        }
    }

    /// <summary>Rewrite the database to look like it was written by SchemaVersion 1.</summary>
    private static void SimulateVersion1FtsTable(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        conn.Open();

        Execute(conn, "DROP TABLE IF EXISTS observations_fts;");
        Execute(conn, @"
            CREATE VIRTUAL TABLE observations_fts USING fts5(
                title, content, topic_key, type,
                tokenize='trigram',
                content='observations', content_rowid='id'
            );");
        Execute(conn, "INSERT INTO observations_fts(observations_fts) VALUES('rebuild');");
        Execute(conn, "PRAGMA user_version=1;");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
