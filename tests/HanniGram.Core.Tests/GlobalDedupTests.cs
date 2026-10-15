using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The global scope reuses the same upsert-by-topic_key path as a project, so a
/// heuristic taught twice updates the same row in place instead of accumulating
/// duplicates. This is the property that would break if global memory got its own
/// parallel storage path.
/// </summary>
public sealed class GlobalDedupTests
{
    [Fact]
    public void GlobalSave_SameTopicKey_UpdatesInPlaceInsteadOfAccumulating()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var global = store.GetOrCreateGlobalProject();

        var (first, firstCreated) = store.SaveObservation(
            global.Id, "Commit style v1", "imperative mood", topicKey: "conventions/commits");
        var (second, secondCreated) = store.SaveObservation(
            global.Id, "Commit style v2", "imperative mood, conventional commits", topicKey: "conventions/commits");

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Commit style v2", second.Title);
        Assert.Single(store.GetByTopicKey(global.Id, "conventions/commits"));
        Assert.Single(store.ListObservations(global.Id));
    }

    // The same topic key in a project scope is a DIFFERENT memory: global dedup
    // must not collapse a project row into the global one.
    [Fact]
    public void SameTopicKey_IsNotDedupedAcrossScopes()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();

        store.SaveObservation(project.Id, "Local commit style", "project body", topicKey: "conventions/commits");
        store.SaveObservation(global.Id, "Global commit style", "global body", topicKey: "conventions/commits");

        Assert.Single(store.GetByTopicKey(project.Id, "conventions/commits"));
        Assert.Single(store.GetByTopicKey(global.Id, "conventions/commits"));
    }

    // The upserted global row must re-index: the new content is searchable and the
    // old content is gone, with no rebuild on the write path.
    [Fact]
    public void GlobalUpsert_ReindexesInPlace()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var global = store.GetOrCreateGlobalProject();

        store.SaveObservation(global.Id, "Commit style", "obsidianwalnut", topicKey: "conventions/commits");
        Assert.Single(store.Search(global.Id, "obsidianwalnut"));

        store.SaveObservation(global.Id, "Commit style", "geraniumposts", topicKey: "conventions/commits");

        Assert.Single(store.Search(global.Id, "geraniumposts"));
        Assert.Empty(store.Search(global.Id, "obsidianwalnut"));
    }
}
