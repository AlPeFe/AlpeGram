using Microsoft.Data.Sqlite;
using Xunit;

namespace HanniGram.Core.Tests;

public sealed class RecencyTests
{
    [Fact]
    public void ListObservations_PutsAnUpsertedObservationFirst()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (topic, _) = store.SaveObservation(project.Id, "Topic v1", "first", topicKey: "topic/evolving");
        Thread.Sleep(20);
        store.SaveObservation(project.Id, "Unrelated newest", "second");
        Thread.Sleep(20);
        var (updated, created) = store.SaveObservation(project.Id, "Topic v2", "third", topicKey: "topic/evolving");

        Assert.False(created);
        Assert.Equal(topic.Id, updated.Id);

        var listed = store.ListObservations(project.Id);
        Assert.Equal(2, listed.Count);
        Assert.Equal(topic.Id, listed[0].Id);
    }

    // A topic key can legitimately have more than one row (legacy data, or rows
    // written before upsert existed). The upsert must resolve to the row that was
    // touched most recently, not the one that happens to be newest by creation.
    [Fact]
    public void SaveObservation_TargetsTheMostRecentlyUpdatedDuplicate()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        InsertRawObservation(db.DbPath, project.Id, "stale duplicate", "k",
            createdAt: "2026-01-01T00:00:00.0000000Z", updatedAt: "2026-01-01T00:00:00.0000000Z");
        var freshId = InsertRawObservation(db.DbPath, project.Id, "fresh duplicate", "k",
            createdAt: "2025-01-01T00:00:00.0000000Z", updatedAt: "2026-06-01T00:00:00.0000000Z");

        var (updated, created) = store.SaveObservation(project.Id, "upserted", "body", topicKey: "k");

        Assert.False(created);
        Assert.Equal(freshId, updated.Id);
        Assert.Equal("upserted", updated.Title);
    }

    private static long InsertRawObservation(
        string dbPath, long projectId, string title, string topicKey, string createdAt, string updatedAt)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO observations (project_id, title, content, topic_key, created_at_utc, updated_at_utc)
            VALUES ($p, $t, $c, $k, $created, $updated);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", title);
        cmd.Parameters.AddWithValue("$k", topicKey);
        cmd.Parameters.AddWithValue("$created", createdAt);
        cmd.Parameters.AddWithValue("$updated", updatedAt);
        return (long)cmd.ExecuteScalar()!;
    }
}
