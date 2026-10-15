using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// Promotion moves a project memory into the global namespace. Without a
/// collision the row is moved; when a global memory already owns the topic_key
/// the promoted content wins, the surviving row takes the source's lifecycle
/// state, and the redundant project row is removed. An archived source is refused
/// by default, so retired content can never overwrite live global truth. Either
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
        var promoted = result!.Observation!;
        Assert.Equal(PromotionOutcomes.Moved, result.Outcome);
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

        var promoted = result!.Observation!;
        Assert.Equal(PromotionOutcomes.Merged, result.Outcome);
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

        var result = store.PromoteObservation(project.Id, source.Id)!;

        Assert.Equal(PromotionOutcomes.Moved, result.Outcome);
        Assert.Equal(source.Id, result.Observation!.Id);
        Assert.Null(result.Observation.TopicKey);
        Assert.Single(store.ListObservations(store.GetGlobalProject()!.Id));
    }

    // ------------------------------------------------------------------
    // Lifecycle: retired content must never destroy (or impersonate) live truth.
    // ------------------------------------------------------------------

    [Fact]
    public void Promote_ArchivedSource_IsRefusedAndLeavesTheLiveGlobalRowUntouched()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();

        // Live global truth under the key.
        var (live, _) = store.SaveObservation(
            global.Id, "LIVE-GLOBAL", "live global truth", topicKey: "lifecycle/clobber");
        // A stale project row, then archived.
        var (stale, _) = store.SaveObservation(
            project.Id, "RETIRED-PROJECT", "retired stale content", topicKey: "lifecycle/clobber");
        store.SetObservationState(project.Id, stale.Id, MemoryStates.Archived);

        var result = store.PromoteObservation(project.Id, stale.Id);

        Assert.NotNull(result);
        Assert.Equal(PromotionOutcomes.Refused, result!.Outcome);
        Assert.Equal(PromotionOutcomes.ReasonSourceArchived, result.Reason);
        Assert.Null(result.Observation);

        // The live global row is byte-for-byte what it was.
        var unchanged = store.GetObservation(global.Id, live.Id)!;
        Assert.Equal("LIVE-GLOBAL", unchanged.Title);
        Assert.Equal("live global truth", unchanged.Content);
        Assert.Equal(MemoryStates.Active, unchanged.State);

        // The archived source is still there (nothing was written) and stays archived.
        var source = store.GetObservation(project.Id, stale.Id)!;
        Assert.Equal(MemoryStates.Archived, source.State);
    }

    [Fact]
    public void Promote_ArchivedSource_RefusedByDefault_WritesNothingAndCreatesNoSentinel()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (source, _) = store.SaveObservation(project.Id, "Only row", "body", topicKey: "lifecycle/only");
        store.SetObservationState(project.Id, source.Id, MemoryStates.Archived);

        var result = store.PromoteObservation(project.Id, source.Id);

        Assert.Equal(PromotionOutcomes.Refused, result!.Outcome);
        // A refusal must not materialise the global namespace on its way out.
        Assert.Null(store.GetGlobalProject());
        Assert.Equal(MemoryStates.Archived, store.GetObservation(project.Id, source.Id)!.State);
    }

    [Fact]
    public void Promote_ArchivedSource_WithOptIn_LandsArchived_AndNeverActive()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();

        var (live, _) = store.SaveObservation(
            global.Id, "LIVE-GLOBAL", "live global truth", topicKey: "lifecycle/clobber");
        var (stale, _) = store.SaveObservation(
            project.Id, "RETIRED-PROJECT", "retired stale content", topicKey: "lifecycle/clobber");
        store.SetObservationState(project.Id, stale.Id, MemoryStates.Archived);

        var result = store.PromoteObservation(project.Id, stale.Id, allowArchived: true);

        Assert.Equal(PromotionOutcomes.Merged, result!.Outcome);
        // The surviving row now carries the retired content, but its state is the
        // source's: archived, never active.
        var merged = store.GetObservation(global.Id, live.Id)!;
        Assert.Equal("RETIRED-PROJECT", merged.Title);
        Assert.Equal(MemoryStates.Archived, merged.State);

        // And it is invisible to the default (active-only) reads.
        Assert.Empty(store.ListObservations(global.Id));
        Assert.Single(store.ListObservations(global.Id, state: MemoryStates.All));
    }

    [Fact]
    public void Promote_ArchivedSource_WithOptIn_AndNoCollision_MovesWhileStayingArchived()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (source, _) = store.SaveObservation(project.Id, "Retired lesson", "body", topicKey: "lifecycle/retired");
        store.SetObservationState(project.Id, source.Id, MemoryStates.Archived);

        var result = store.PromoteObservation(project.Id, source.Id, allowArchived: true);

        Assert.Equal(PromotionOutcomes.Moved, result!.Outcome);
        var global = store.GetGlobalProject()!;
        Assert.Equal(MemoryStates.Archived, store.GetObservation(global.Id, source.Id)!.State);
    }

    [Fact]
    public void Promote_ActiveSource_OverArchivedGlobalTarget_RevivesItToTheSourceState()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();

        var (target, _) = store.SaveObservation(
            global.Id, "Old global", "alpenglow old body", topicKey: "lifecycle/revive");
        store.SetObservationState(global.Id, target.Id, MemoryStates.Archived);

        var (source, _) = store.SaveObservation(
            project.Id, "New teaching", "betatron new body", topicKey: "lifecycle/revive");

        var result = store.PromoteObservation(project.Id, source.Id);

        Assert.Equal(PromotionOutcomes.Merged, result!.Outcome);
        var revived = store.GetObservation(global.Id, target.Id)!;
        Assert.Equal("New teaching", revived.Title);
        // The new teaching revives the archived target to the source's state.
        Assert.Equal(MemoryStates.Active, revived.State);
        Assert.Single(store.ListObservations(global.Id));
    }
}
