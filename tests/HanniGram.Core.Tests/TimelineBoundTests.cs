using HanniGram.Core.Store;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// <c>GetByTopicKey</c> is the only read that returns a whole key's history, so it
/// is the only one that can grow unbounded. It carries its own hard cap: a caller
/// may ask for fewer rows, never for more. The rows are fabricated directly, since
/// the upsert deliberately prevents many rows sharing one key through the write
/// path.
/// </summary>
public sealed class TimelineBoundTests
{
    [Fact]
    public void GetByTopicKey_IsCapped_AtTheStoreLimit()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        SeedTimeline(db, project.Id, "history/many", HanniGramStore.MaxTimelineItems + 10);

        var rows = store.GetByTopicKey(project.Id, "history/many");

        Assert.Equal(HanniGramStore.MaxTimelineItems, rows.Count);
    }

    [Fact]
    public void GetByTopicKey_ClampsACallerLimit_SoItCannotBeRaised()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        SeedTimeline(db, project.Id, "history/clamp", HanniGramStore.MaxTimelineItems + 10);

        // Asking for more than the cap returns the cap; asking for fewer returns fewer.
        Assert.Equal(HanniGramStore.MaxTimelineItems, store.GetByTopicKey(project.Id, "history/clamp", 1000).Count);
        Assert.Equal(5, store.GetByTopicKey(project.Id, "history/clamp", 5).Count);
    }

    [Fact]
    public void GetByTopicKey_StillReturnsTheWholeHistoryForATypicalKey()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        SeedTimeline(db, project.Id, "history/small", 3);

        Assert.Equal(3, store.GetByTopicKey(project.Id, "history/small").Count);
    }

    private static void SeedTimeline(TempDatabase db, long projectId, string topicKey, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Execute(
                """
                INSERT INTO observations (project_id, title, content, topic_key, created_at_utc, updated_at_utc)
                VALUES ($p, $t, $c, $tk, $n, $n);
                """,
                ("$p", projectId), ("$t", $"Row {i}"), ("$c", $"body {i}"),
                ("$tk", topicKey), ("$n", DateTime.UtcNow.AddSeconds(i).ToString("O")));
        }
    }
}
