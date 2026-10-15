using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// mem_update (T20): update an observation by id. Null fields are left unchanged,
/// created_at_utc is preserved, updated_at_utc moves, topic_key follows the same
/// one-owner-per-namespace rule as a save, and a cross-project id is refused.
/// </summary>
public sealed class UpdateObservationTests
{
    [Fact]
    public void Update_ChangesFields_PreservesCreatedAt_AndMovesUpdatedAt()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (original, _) = store.SaveObservation(
            project.Id, "Old title", "old content", what: "ow", why: "oy", where: "owh", learned: "ol", type: "idea");
        Thread.Sleep(20);

        var updated = store.UpdateObservation(project.Id, original.Id,
            title: "New title", content: "new content", what: "nw", why: "ny", where: "nwh", learned: "nl", type: "bugfix");

        Assert.NotNull(updated);
        Assert.Equal("New title", updated!.Title);
        Assert.Equal("new content", updated.Content);
        Assert.Equal("nw", updated.What);
        Assert.Equal("ny", updated.Why);
        Assert.Equal("nwh", updated.Where);
        Assert.Equal("nl", updated.Learned);
        Assert.Equal("bugfix", updated.Type);
        Assert.Equal(original.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.True(updated.UpdatedAtUtc > original.UpdatedAtUtc);
    }

    [Fact]
    public void Update_LeavesNullFieldsUnchanged()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (original, _) = store.SaveObservation(
            project.Id, "Title", "content", what: "w", why: "y", where: "wh", learned: "l", topicKey: "topic/a", type: "idea");

        var updated = store.UpdateObservation(project.Id, original.Id, title: "Title v2");

        Assert.NotNull(updated);
        Assert.Equal("Title v2", updated!.Title);
        Assert.Equal("content", updated.Content);
        Assert.Equal("w", updated.What);
        Assert.Equal("topic/a", updated.TopicKey);
        Assert.Equal("idea", updated.Type);
    }

    [Fact]
    public void Update_EmptyStringClearsAField()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (original, _) = store.SaveObservation(project.Id, "Title", "content", learned: "lesson");

        var updated = store.UpdateObservation(project.Id, original.Id, learned: "");

        Assert.NotNull(updated);
        Assert.Equal(string.Empty, updated!.Learned);
    }

    [Fact]
    public void Update_TopicKeyCollision_UpdatedRowWinsAndTheOtherRowIsUnkeyed()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (first, _) = store.SaveObservation(project.Id, "First", "first body", topicKey: "topic/a");
        var (second, _) = store.SaveObservation(project.Id, "Second", "second body", topicKey: "topic/b");

        var updated = store.UpdateObservation(project.Id, second.Id, topicKey: "topic/a");

        Assert.NotNull(updated);
        Assert.Equal("topic/a", updated!.TopicKey);
        // The key is single-valued: the previous owner was unkeyed, nothing deleted.
        Assert.Null(store.GetObservation(project.Id, first.Id)!.TopicKey);
        Assert.Single(store.GetByTopicKey(project.Id, "topic/a"));
        Assert.Equal(2, store.ListObservations(project.Id).Count);
    }

    [Fact]
    public void Update_CrossProject_IsRefused()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        var (inAlpha, _) = store.SaveObservation(alpha.Id, "Title", "content");

        Assert.Null(store.UpdateObservation(beta.Id, inAlpha.Id, title: "Hijacked"));
        Assert.Equal("Title", store.GetObservation(alpha.Id, inAlpha.Id)!.Title);
    }

    [Fact]
    public void Update_UnknownId_ReturnsNull()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        Assert.Null(store.UpdateObservation(project.Id, 4242, title: "Nope"));
    }

    [Fact]
    public void Update_ReindexesTheNewContentInFts()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (original, _) = store.SaveObservation(project.Id, "Note", "alpenglowold body");
        Assert.Single(store.Search(project.Id, "alpenglowold"));

        store.UpdateObservation(project.Id, original.Id, content: "betatronnew body");

        Assert.Single(store.Search(project.Id, "betatronnew"));
        Assert.Empty(store.Search(project.Id, "alpenglowold"));
    }
}
