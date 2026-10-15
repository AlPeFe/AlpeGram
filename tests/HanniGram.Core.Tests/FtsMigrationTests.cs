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
