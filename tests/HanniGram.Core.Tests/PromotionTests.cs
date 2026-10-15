using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// Promotion moves a project memory into the global namespace. Without a
/// collision the row is moved; when a global memory already owns the topic_key
/// the promoted content wins and the redundant project row is removed. Either
/// way the FTS index must stay correct with no rebuild.
/// </summary>
public sealed class PromotionTests
{
    [Fact]
    public void Promote_MovesTheRowToGlobal_KeepingItsTopicKey()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (source, _) = store.SaveObservation(
            project.Id, "Console app layout", "layered, one project per concern",
            topicKey: "heuristics/console-layout");

        var result = store.PromoteObservation(project.Id, source.Id);

        Assert.NotNull(result);
        var (promoted, merged) = result!.Value;
        Assert.False(merged);
        Assert.Equal(source.Id, promoted.Id);
        Assert.Equal(MemoryScopes.Global, promoted.Scope);
        Assert.Equal("heuristics/console-layout", promoted.TopicKey);

        // Gone from the project, present in global (same row id).
        Assert.Null(store.GetObservation(project.Id, source.Id));
        var global = store.GetGlobalProject();
        Assert.NotNull(global);
        Assert.Single(store.GetByTopicKey(global!.Id, "heuristics/console-layout"));
        Assert.Single(store.ListObservations(global.Id));
    }

    [Fact]
    public void Promote_WithoutCollision_StaysSearchableInGlobal_AndLeavesTheProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (source, _) = store.SaveObservation(project.Id, "Note", "obsidianwalnut body");

        store.PromoteObservation(project.Id, source.Id);

        Assert.Empty(store.Search(project.Id, "obsidianwalnut"));
        var global = store.GetGlobalProject()!;
        var hit = Assert.Single(store.Search(global.Id, "obsidianwalnut"));
        Assert.Equal(MemoryScopes.Global, hit.Scope);
    }

    [Fact]
    public void Promote_WithExistingGlobalTopicKey_PromotedContentWinsAndProjectRowIsRemoved()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();

        // The global scope already owns the topic_key with older content.
        var (existingGlobal, _) = store.SaveObservation(
            global.Id, "Commit style v1", "alpenglow old heuristic", topicKey: "conventions/commits");
        var (source, _) = store.SaveObservation(
            project.Id, "Commit style v2", "betatron new heuristic", topicKey: "conventions/commits");

        var result = store.PromoteObservation(project.Id, source.Id);

        var (promoted, merged) = result!.Value;
        Assert.True(merged);
        // The surviving row is the pre-existing global row, updated in place.
        Assert.Equal(existingGlobal.Id, promoted.Id);
        Assert.Equal(MemoryScopes.Global, promoted.Scope);
        Assert.Equal("Commit style v2", promoted.Title);
        Assert.Contains("betatron", promoted.Content);

        // Exactly one global row for the key; the project row is gone; no duplicate.
        Assert.Single(store.GetByTopicKey(global.Id, "conventions/commits"));
        Assert.Null(store.GetObservation(project.Id, source.Id));
        Assert.Single(store.ListObservations(global.Id));
        Assert.Empty(store.ListObservations(project.Id));

        // Index follows the merge: new term findable, old term gone.
        Assert.Single(store.Search(global.Id, "betatron"));
        Assert.Empty(store.Search(global.Id, "alpenglow"));
    }

    [Fact]
    public void Promote_IsScopedToTheCallersProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        var (inAlpha, _) = store.SaveObservation(alpha.Id, "Note", "body");

        // beta cannot promote alpha's observation by id.
        Assert.Null(store.PromoteObservation(beta.Id, inAlpha.Id));
        Assert.NotNull(store.GetObservation(alpha.Id, inAlpha.Id));
    }

    [Fact]
    public void Promote_UnknownId_ReturnsNullAndCreatesNoSentinel()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        Assert.Null(store.PromoteObservation(project.Id, 4242));
        Assert.Null(store.GetGlobalProject());
    }

    [Fact]
    public void Promote_WithoutTopicKey_StillMovesToGlobal()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (source, _) = store.SaveObservation(project.Id, "Untopiced note", "geraniumposts body");

        var (promoted, merged) = store.PromoteObservation(project.Id, source.Id)!.Value;

        Assert.False(merged);
        Assert.Equal(source.Id, promoted.Id);
        Assert.Null(promoted.TopicKey);
        Assert.Single(store.ListObservations(store.GetGlobalProject()!.Id));
    }
}
