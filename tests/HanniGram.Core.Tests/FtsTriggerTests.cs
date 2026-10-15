using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The FTS index is maintained by the external-content trigger triple on
/// observations. Search must reflect every insert, update and delete immediately,
/// with no corpus rebuild anywhere on the write path.
/// </summary>
public sealed class FtsTriggerTests
{
    [Fact]
    public void Search_ReflectsInsertImmediately()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (observation, created) = store.SaveObservation(project.Id, "Note", "insertedword body");

        Assert.True(created);
        var hit = Assert.Single(store.Search(project.Id, "insertedword"));
        Assert.Equal(observation.Id, hit.Id);
    }

    // The old and new words deliberately share no trigrams: when the strict AND
    // match is empty, Search retries with an OR of the query's trigrams, so two
    // words sharing a trigram would produce a false positive and mask real drift.
    [Fact]
    public void Search_AfterUpsert_FindsNewContentAndNotTheOld()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        store.SaveObservation(project.Id, "Note", "obsidianwalnut here", topicKey: "note/key");
        Assert.Single(store.Search(project.Id, "obsidianwalnut"));

        store.SaveObservation(project.Id, "Note", "geraniumposts here", topicKey: "note/key");

        Assert.Single(store.Search(project.Id, "geraniumposts"));
        Assert.Empty(store.Search(project.Id, "obsidianwalnut"));
    }

    [Fact]
    public void Search_AfterDelete_NoLongerFindsTheRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Note", "ephemeralword content");
        Assert.Single(store.Search(project.Id, "ephemeralword"));

        Assert.True(store.DeleteObservation(project.Id, observation.Id));

        Assert.Empty(store.Search(project.Id, "ephemeralword"));
    }

    // Every word here shares no trigram with another, so the trigram fallback can
    // never manufacture a hit for a term that was really removed from the index.
    [Fact]
    public void Search_StaysConsistentAcrossInsertUpdateAndDelete()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        store.SaveObservation(project.Id, "First", "alpenglow body", topicKey: "topic/a");
        var (second, _) = store.SaveObservation(project.Id, "Second", "betatron body", topicKey: "topic/b");
        Assert.Single(store.Search(project.Id, "alpenglow"));
        Assert.Single(store.Search(project.Id, "betatron"));

        store.SaveObservation(project.Id, "First", "gammaray body", topicKey: "topic/a");
        Assert.Empty(store.Search(project.Id, "alpenglow"));
        Assert.Single(store.Search(project.Id, "gammaray"));

        Assert.True(store.DeleteObservation(project.Id, second.Id));
        Assert.Empty(store.Search(project.Id, "betatron"));
        Assert.Single(store.Search(project.Id, "gammaray"));
        Assert.Single(store.ListObservations(project.Id));
    }

    /// <summary>
    /// The strongest proof that no rebuild runs on the write path: the second open
    /// takes the "already migrated" branch, so Initialize performs no rebuild at
    /// all, yet the write is still searchable.
    /// </summary>
    [Fact]
    public void WritesOnAnAlreadyMigratedDatabase_AreIndexedWithoutAnyRebuild()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
        }

        using (var store = db.OpenStore())
        {
            store.SaveObservation(projectId, "Note", "secondopen word");

            Assert.Single(store.Search(projectId, "secondopen"));
        }
    }

    /// <summary>
    /// Behavioural proof that a save no longer re-indexes the whole corpus: wipe the
    /// index behind the store's back, then save one row. A triggering write indexes
    /// only that row; a rebuild-on-write would have restored the wiped row too.
    /// </summary>
    [Fact]
    public void WritePath_DoesNotRebuildTheWholeIndex()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
            store.SaveObservation(projectId, "A", "alpenglow body");
            Assert.Single(store.Search(projectId, "alpenglow"));
        }

        // Valid FTS5 command for an external-content table (not a plain INSERT/DELETE).
        db.Execute("INSERT INTO observations_fts(observations_fts) VALUES('delete-all');");

        using (var store = db.OpenStore())
        {
            store.SaveObservation(projectId, "B", "betatron body");

            Assert.Single(store.Search(projectId, "betatron"));
            Assert.Empty(store.Search(projectId, "alpenglow"));
        }
    }
}
