using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// SQLite reads LIMIT -1 as "unlimited", so an unvalidated caller-supplied limit
/// on the unauthenticated local API could dump the entire corpus in one request.
/// Every limit is clamped to [1, 500].
/// </summary>
public sealed class LimitClampTests
{
    [Fact]
    public void ListObservations_WithNegativeLimit_ReturnsAtMostOneRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        InsertBulkObservations(db, project.Id, 10);

        var listed = store.ListObservations(project.Id, -1);

        Assert.Single(listed);
    }

    [Fact]
    public void ListObservations_NeverExceedsTheCap()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        InsertBulkObservations(db, project.Id, 510);

        var capped = store.ListObservations(project.Id, int.MaxValue);
        var negative = store.ListObservations(project.Id, -1);

        Assert.Equal(500, capped.Count);
        Assert.Single(negative);
    }

    [Fact]
    public void Search_WithNegativeLimit_IsClampedInsteadOfUnlimited()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "One", "shared substring marker");
        store.SaveObservation(project.Id, "Two", "shared substring marker");

        var hits = store.Search(project.Id, "substring", -1);

        Assert.Single(hits);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public void SuggestTopicKeys_WithNonPositiveLimit_ReturnsAtMostOne(int limit)
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "A", "a", topicKey: "topic/one");
        store.SaveObservation(project.Id, "B", "b", topicKey: "topic/two");

        var keys = store.SuggestTopicKeys(project.Id, limit);

        Assert.Single(keys);
    }

    [Fact]
    public void ListSessions_WithNegativeLimit_ReturnsAtMostOne()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.StartSession(project.Id, "session-1");
        store.StartSession(project.Id, "session-2");

        var sessions = store.ListSessions(project.Id, -1);

        Assert.Single(sessions);
    }

    private static void InsertBulkObservations(TempDatabase db, long projectId, int count)
    {
        db.Execute(
            @"
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < $count)
            INSERT INTO observations (project_id, title, content, created_at_utc, updated_at_utc)
            SELECT $p, 'Row ' || n, 'bulk content',
                   printf('2026-01-01T00:00:%02d.0000000Z', n % 60),
                   printf('2026-01-01T00:00:%02d.0000000Z', n % 60)
            FROM seq;",
            ("$p", projectId),
            ("$count", count));
    }
}
