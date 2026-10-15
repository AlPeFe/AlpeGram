using Microsoft.Data.Sqlite;
using Xunit;

namespace HanniGram.Mcp.Tests;

/// <summary>
/// Coexistence with the running HTTP daemon. The MCP server opens the same SQLite
/// file directly instead of proxying the daemon, so two processes hold one database
/// open at once. WAL plus a busy timeout is what makes that safe: concurrent
/// readers are never blocked, and a writer waits for a short lock instead of
/// failing. These tests reproduce that with two independent connections.
/// </summary>
public sealed class CoexistenceTests
{
    [Fact]
    public void TwoStoresOnOneDatabase_SeeEachOthersWrites()
    {
        using var harness = new McpHarness();
        // A second store on the same file, standing in for the daemon's connection.
        using var second = new HanniGram.Core.Store.HanniGramStore(harness.DbPath);

        // A write through the MCP session...
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"From MCP","content":"coexistmarker alpha","project":"alpha"}""")!);
        var id = saved["observation"]!.AsObject()["id"]!.GetValue<long>();

        // ...is visible to the other connection.
        var project = second.GetOrCreateProject("alpha");
        var seen = second.GetObservation(project.Id, id);
        Assert.NotNull(seen);
        Assert.Equal("From MCP", seen!.Title);

        // A write through the other connection...
        second.SaveObservation(project.Id, "From the other store", "coexistmarker beta");

        // ...is visible to the MCP session, with no error and a consistent count.
        var found = Wire.Payload(harness.CallTool(2, "mem_search",
            """{"query":"coexistmarker","scope":"project","project":"alpha"}""")!);
        Assert.Equal(2, found["count"]!.GetValue<int>());
    }

    [Fact]
    public void ASaveWaitsOutAWriteLockInsteadOfFailing()
    {
        using var harness = new McpHarness();
        // Create the project up front so the blocking write below is exactly the observation insert.
        var project = harness.Store.GetOrCreateProject("alpha");

        using var blocker = OpenRaw(harness.DbPath);
        // Hold the database's write lock from another connection.
        Execute(blocker, "BEGIN IMMEDIATE;");
        Execute(blocker, $"INSERT INTO projects (name, root_path, created_at_utc, updated_at_utc) VALUES ('blocker', NULL, '{Now()}', '{Now()}');");

        var committed = false;
        var release = Task.Run(async () =>
        {
            await Task.Delay(400);
            Execute(blocker, "COMMIT;");
            Volatile.Write(ref committed, true);
        });

        // The store's connection has busy_timeout=5000, so this write waits for the
        // lock instead of throwing "database is locked".
        harness.Store.SaveObservation(project.Id, "Waited", "lockwaitmarker");

        // The save could not have completed before the lock was released: the fact
        // that COMMIT has already run is the proof that it waited rather than
        // slipping past the lock or failing outright.
        Assert.True(Volatile.Read(ref committed), "the save returned before the write lock was released");
        Assert.Single(harness.Store.Search(project.Id, "lockwaitmarker"));
        release.GetAwaiter().GetResult();
    }

    [Fact]
    public void AReadSucceedsWhileAnotherConnectionHoldsTheWriteLock()
    {
        using var harness = new McpHarness();
        var project = harness.Store.GetOrCreateProject("alpha");
        var saved = harness.Store.SaveObservation(project.Id, "Readable", "walreadmarker").Observation;

        using var blocker = OpenRaw(harness.DbPath);
        Execute(blocker, "BEGIN IMMEDIATE;");
        Execute(blocker, $"INSERT INTO projects (name, root_path, created_at_utc, updated_at_utc) VALUES ('blocker', NULL, '{Now()}', '{Now()}');");

        try
        {
            // WAL lets a reader proceed while a writer holds the lock: the read is
            // not blocked and does not fail with SQLITE_BUSY.
            Assert.Single(harness.Store.Search(project.Id, "walreadmarker"));
            Assert.NotNull(harness.Store.GetObservation(project.Id, saved.Id));
        }
        finally
        {
            Execute(blocker, "COMMIT;");
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
