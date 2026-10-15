using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// Lifecycle (T13): an observation carries a state (active | needs_review |
/// archived) and a pinned flag. Existing rows migrate to active / unpinned. Reads
/// return active rows only by default; archived is excluded unless explicitly
/// requested, and pinned floats to the front deterministically.
/// </summary>
public sealed class LifecycleTests
{
    [Fact]
    public void NewObservation_DefaultsToActiveAndUnpinned()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (observation, _) = store.SaveObservation(project.Id, "Note", "body");

        Assert.Equal(MemoryStates.Active, observation.State);
        Assert.False(observation.Pinned);
    }

    [Fact]
    public void Migration_AddsLifecycleColumnsAndDefaultsExistingRowsToActive()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
            store.SaveObservation(projectId, "Legacy note", "migratedword body");
        }

        // Rewind to a v3 database: the lifecycle columns did not exist then.
        db.Execute(@"
            DROP INDEX IF EXISTS idx_obs_project_state_updated;
            ALTER TABLE observations DROP COLUMN state;
            ALTER TABLE observations DROP COLUMN pinned;
            PRAGMA user_version=3;");

        using (var store = db.OpenStore())
        {
            var listed = Assert.Single(store.ListObservations(projectId));
            Assert.Equal("Legacy note", listed.Title);
            Assert.Equal(MemoryStates.Active, listed.State);
            Assert.False(listed.Pinned);

            // The one-off migration reindexed the corpus, so the row is still found.
            Assert.Single(store.Search(projectId, "migratedword"));
        }
    }

    [Fact]
    public void Search_ExcludesArchivedByDefault_ButReturnsItWhenAsked()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Note", "widowswalker body");

        Assert.Single(store.Search(project.Id, "widowswalker"));

        Assert.NotNull(store.SetObservationState(project.Id, observation.Id, MemoryStates.Archived));

        Assert.Empty(store.Search(project.Id, "widowswalker"));
        Assert.Single(store.Search(project.Id, "widowswalker", state: MemoryStates.Archived));
        Assert.Single(store.Search(project.Id, "widowswalker", state: MemoryStates.All));
    }

    [Fact]
    public void ListObservations_ExcludesArchivedByDefault_ButReturnsItWhenAsked()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (keep, _) = store.SaveObservation(project.Id, "Keep", "one body");
        var (forget, _) = store.SaveObservation(project.Id, "Forget", "two body");

        store.SetObservationState(project.Id, forget.Id, MemoryStates.Archived);

        Assert.Equal(new[] { keep.Id }, store.ListObservations(project.Id).Select(o => o.Id));
        Assert.Equal(new[] { forget.Id }, store.ListObservations(project.Id, state: MemoryStates.Archived).Select(o => o.Id));
        Assert.Equal(2, store.ListObservations(project.Id, state: MemoryStates.All).Count);
    }

    [Fact]
    public void ListObservations_WithNeedsReviewState_ReturnsOnlyFlaggedRows()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (flagged, _) = store.SaveObservation(project.Id, "Flagged", "one body");
        store.SaveObservation(project.Id, "Normal", "two body");

        store.SetObservationState(project.Id, flagged.Id, MemoryStates.NeedsReview);

        Assert.Equal(new[] { flagged.Id }, store.ListObservations(project.Id, state: MemoryStates.NeedsReview).Select(o => o.Id));
    }

    [Fact]
    public void ArchivedRow_IsNotUpsertTarget_SoASaveInsertsANewActiveRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (first, _) = store.SaveObservation(project.Id, "Topic v1", "first body", topicKey: "topic/evolving");

        store.SetObservationState(project.Id, first.Id, MemoryStates.Archived);

        var (second, created) = store.SaveObservation(project.Id, "Topic v2", "second body", topicKey: "topic/evolving");

        // A fresh teaching of a forgotten topic must not silently revive the
        // archived row: it creates a new active row and the archived row survives.
        Assert.True(created);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(MemoryStates.Active, second.State);
        Assert.Equal(2, store.GetByTopicKey(project.Id, "topic/evolving").Count);
        Assert.Equal(new[] { second.Id }, store.ListObservations(project.Id).Select(o => o.Id));
    }

    [Fact]
    public void SetObservationState_RejectsAnUnknownState()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Note", "body");

        Assert.Null(store.SetObservationState(project.Id, observation.Id, "exploded"));
        Assert.Equal(MemoryStates.Active, store.GetObservation(project.Id, observation.Id)!.State);
    }

    [Fact]
    public void SetObservationState_CannotReachIntoAnotherProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        var (inAlpha, _) = store.SaveObservation(alpha.Id, "Note", "body");

        Assert.Null(store.SetObservationState(beta.Id, inAlpha.Id, MemoryStates.Archived));
        Assert.Equal(MemoryStates.Active, store.GetObservation(alpha.Id, inAlpha.Id)!.State);
    }

    [Fact]
    public void SetObservationPinned_TogglesTheFlag()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Note", "body");

        Assert.True(store.SetObservationPinned(project.Id, observation.Id, true)!.Pinned);
        Assert.True(store.GetObservation(project.Id, observation.Id)!.Pinned);
        Assert.False(store.SetObservationPinned(project.Id, observation.Id, false)!.Pinned);
    }

    [Fact]
    public void PinnedRow_IsListedFirstDeterministically()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (older, _) = store.SaveObservation(project.Id, "Older", "one body");
        Thread.Sleep(20);
        store.SaveObservation(project.Id, "Newer", "two body");

        // Without a pin the newer row leads; pinning the older row floats it first.
        Assert.Equal("Newer", store.ListObservations(project.Id)[0].Title);
        store.SetObservationPinned(project.Id, older.Id, true);
        Assert.Equal(older.Id, store.ListObservations(project.Id)[0].Id);
    }
}
