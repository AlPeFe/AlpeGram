using HanniGram.Core;
using HanniGram.Core.Models;
using HanniGram.Core.Store;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The bounded review surface (T15): needs_review rows and duplicate candidates,
/// with truncated previews and a hard cap. The listing is bounded by a store
/// constant, so asking for more can never turn it into a corpus dump.
/// </summary>
public sealed class ReviewSurfaceTests
{
    [Fact]
    public void Review_ListsNeedsReviewRows_WithStateAndScope()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (flagged, _) = store.SaveObservation(project.Id, "Flagged", "needs a look body");
        store.SetObservationState(project.Id, flagged.Id, MemoryStates.NeedsReview);

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var items = store.ReviewObservations(namespaces);

        var item = Assert.Single(items);
        Assert.Equal("needs_review", item.Kind);
        Assert.Equal(flagged.Id, item.Id);
        Assert.Equal(MemoryStates.NeedsReview, item.State);
        Assert.Equal(MemoryScopes.Project, item.Scope);
    }

    [Fact]
    public void Review_ReportsDuplicateCandidates()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (first, _) = store.SaveObservation(
            project.Id, "Deployment rollback procedure", "rollback deployment procedure steps");
        var (second, _) = store.SaveObservation(
            project.Id, "Deployment rollback procedure", "rollback deployment procedure steps");

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var items = store.ReviewObservations(namespaces);

        var duplicate = Assert.Single(items, i => i.Kind == "duplicate");
        Assert.Equal(second.Id, duplicate.Id);
        Assert.Equal(first.Id, duplicate.DuplicateOfId);
    }

    [Fact]
    public void Review_NeverExceedsTheHardCap()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        for (var i = 0; i < HanniGramStore.MaxReviewItems + 10; i++)
        {
            var (observation, _) = store.SaveObservation(project.Id, $"Row {i}", $"unique body {i}");
            store.SetObservationState(project.Id, observation.Id, MemoryStates.NeedsReview);
        }

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var items = store.ReviewObservations(namespaces, int.MaxValue);

        Assert.Equal(HanniGramStore.MaxReviewItems, items.Count);
    }

    [Fact]
    public void Review_PreviewsAreTruncated()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var longContent = new string('x', 400) + " tailmarker_should_never_appear";
        var (observation, _) = store.SaveObservation(project.Id, "Long note", longContent);
        store.SetObservationState(project.Id, observation.Id, MemoryStates.NeedsReview);

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var item = Assert.Single(store.ReviewObservations(namespaces));

        Assert.True(item.Preview.Length <= 201, $"preview was {item.Preview.Length} chars");
        Assert.DoesNotContain("tailmarker_should_never_appear", item.Preview);
    }

    [Fact]
    public void ArchiveThenRestore_ChangesDefaultVisibility()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Note", "body");

        store.SetObservationState(project.Id, observation.Id, MemoryStates.Archived);
        Assert.Empty(store.ListObservations(project.Id));

        store.SetObservationState(project.Id, observation.Id, MemoryStates.Active);
        Assert.Single(store.ListObservations(project.Id));
    }
}
