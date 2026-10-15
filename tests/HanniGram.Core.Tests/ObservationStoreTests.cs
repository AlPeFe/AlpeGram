using Xunit;

namespace HanniGram.Core.Tests;

public sealed class ObservationStoreTests
{
    [Fact]
    public void SaveObservation_WithoutTopicKey_InsertsNewRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (observation, created) = store.SaveObservation(project.Id, "Title", "content body");

        Assert.True(created);
        Assert.True(observation.Id > 0);
        Assert.Equal(project.Id, observation.ProjectId);
        Assert.Equal("Title", observation.Title);
        Assert.Equal("content body", observation.Content);
    }

    [Fact]
    public void SaveObservation_WithTopicKey_UpsertsInPlaceAndPreservesCreatedAt()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (first, firstCreated) = store.SaveObservation(
            project.Id, "First", "v1", topicKey: "architecture/store", type: "architecture");
        var (second, secondCreated) = store.SaveObservation(
            project.Id, "Second", "v2", topicKey: "architecture/store", type: "architecture");

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Second", second.Title);
        Assert.Equal("v2", second.Content);
        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.Single(store.GetByTopicKey(project.Id, "architecture/store"));
    }

    [Fact]
    public void SaveObservation_WithoutTopicKey_NeverUpserts()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (first, _) = store.SaveObservation(project.Id, "Same", "same body");
        var (second, _) = store.SaveObservation(project.Id, "Same", "same body");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.ListObservations(project.Id).Count);
    }

    [Fact]
    public void SaveObservation_IsScopedByProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");

        var (inAlpha, _) = store.SaveObservation(alpha.Id, "T", "c", topicKey: "shared/topic");
        var (inBeta, _) = store.SaveObservation(beta.Id, "T", "c", topicKey: "shared/topic");

        Assert.NotEqual(inAlpha.Id, inBeta.Id);
        Assert.Null(store.GetObservation(beta.Id, inAlpha.Id));
        Assert.NotNull(store.GetObservation(alpha.Id, inAlpha.Id));
    }

    [Fact]
    public void SaveObservation_PersistsStructuredFields()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var (saved, _) = store.SaveObservation(
            project.Id, "Title", "content", what: "w", why: "y", where: "wh", learned: "l", type: "decision");

        var reloaded = store.GetObservation(project.Id, saved.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("w", reloaded!.What);
        Assert.Equal("y", reloaded.Why);
        Assert.Equal("wh", reloaded.Where);
        Assert.Equal("l", reloaded.Learned);
        Assert.Equal("decision", reloaded.Type);
    }

    [Fact]
    public void DeleteObservation_RemovesOnlyTargetRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (target, _) = store.SaveObservation(project.Id, "Target", "one");
        var (keeper, _) = store.SaveObservation(project.Id, "Keeper", "two");

        Assert.True(store.DeleteObservation(project.Id, target.Id));
        Assert.Null(store.GetObservation(project.Id, target.Id));
        Assert.NotNull(store.GetObservation(project.Id, keeper.Id));
        Assert.False(store.DeleteObservation(project.Id, target.Id));
    }

    [Fact]
    public void ListObservations_ReturnsAllRowsForProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var other = store.GetOrCreateProject("beta");
        store.SaveObservation(project.Id, "One", "first");
        store.SaveObservation(project.Id, "Two", "second");
        store.SaveObservation(other.Id, "Three", "third");

        var listed = store.ListObservations(project.Id);

        Assert.Equal(2, listed.Count);
        Assert.All(listed, o => Assert.Equal(project.Id, o.ProjectId));
    }

    [Fact]
    public void SuggestTopicKeys_ReturnsDistinctKeys()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "A", "a", topicKey: "topic/one");
        store.SaveObservation(project.Id, "B", "b", topicKey: "topic/one");
        store.SaveObservation(project.Id, "C", "c", topicKey: "topic/two");
        store.SaveObservation(project.Id, "D", "d");

        var keys = store.SuggestTopicKeys(project.Id);

        Assert.Equal(new[] { "topic/one", "topic/two" }, keys);
    }
}
