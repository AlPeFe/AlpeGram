using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The cheap deterministic review signal on save (T14). A keyed upsert reports the
/// row it overwrote; an unkeyed save reports a near-duplicate found in a bounded
/// recent window of the same namespace. The hint is a few tokens only and is
/// absent when nothing collided.
/// </summary>
public sealed class SaveReviewHintTests
{
    [Fact]
    public void FirstSave_ReportsNoReviewHint()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var result = store.SaveObservationWithReview(project.Id, "First note", "a first note body");

        Assert.True(result.Created);
        Assert.Null(result.Review);
    }

    [Fact]
    public void KeyedUpsert_ReportsTheOverwrittenRowAsATopicKeyCollision()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (first, firstCreated, firstReview) = store.SaveObservationWithReview(
            project.Id, "Architecture v1", "first body", topicKey: "architecture/store");
        Assert.True(firstCreated);
        Assert.Null(firstReview);

        var (second, created, review) = store.SaveObservationWithReview(
            project.Id, "Architecture v2", "second body", topicKey: "architecture/store");

        Assert.False(created);
        Assert.Equal(first.Id, second.Id);
        Assert.NotNull(review);
        Assert.Equal("topic_key", review!.Kind);
        Assert.Equal(first.Id, review.Id);
        Assert.Equal("Architecture v1", review.Title);
    }

    [Fact]
    public void UnkeyedNearDuplicate_ReportsTheDuplicate()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (original, _) = store.SaveObservation(
            project.Id, "Deployment rollback procedure", "rollback deployment procedure steps");

        var (_, created, review) = store.SaveObservationWithReview(
            project.Id, "Deployment rollback procedures", "rollback deployment procedure steps");

        Assert.True(created);
        Assert.NotNull(review);
        Assert.Equal("duplicate", review!.Kind);
        Assert.Equal(original.Id, review.Id);
    }

    [Fact]
    public void UnkeyedUnrelatedSave_ReportsNoHint()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Cooking pasta", "boil water add salt and noodles");

        var (_, _, review) = store.SaveObservationWithReview(
            project.Id, "Database index strategy", "btree versus hash for lookups");

        Assert.Null(review);
    }

    [Fact]
    public void DuplicateDetection_IsScopedToTheProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        store.SaveObservation(beta.Id, "Deployment rollback procedure", "rollback deployment procedure steps");

        // The same text in a DIFFERENT project is not a duplicate to report.
        var (_, _, review) = store.SaveObservationWithReview(
            alpha.Id, "Deployment rollback procedure", "rollback deployment procedure steps");

        Assert.Null(review);
    }

    [Fact]
    public void HintTitle_IsTruncated()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var longTitle = new string('t', 200) + " architecture note";
        store.SaveObservation(project.Id, longTitle, "body", topicKey: "architecture/long");

        var (_, _, review) = store.SaveObservationWithReview(
            project.Id, "short", "body", topicKey: "architecture/long");

        Assert.NotNull(review);
        Assert.True(review!.Title.Length <= 81, $"title preview was {review.Title.Length} chars");
    }

    // The similarity rule is a documented constant: identical sets are 1.0, an
    // over-threshold near match is above 0.8, and a signature below the token floor
    // can never match.
    [Fact]
    public void Dice_ImplementsTheDocumentedRule()
    {
        var a = MemoryDedup.Signature("deployment rollback procedure", "rollback deployment procedure steps");
        var b = MemoryDedup.Signature("deployment rollback procedures", "rollback deployment procedure steps");
        var unrelated = MemoryDedup.Signature("cooking pasta", "boil water add salt and noodles");
        var tooShort = MemoryDedup.Signature("short", "tiny");

        Assert.Equal(1.0, MemoryDedup.Dice(a, a), 3);
        Assert.True(MemoryDedup.Dice(a, b) >= MemoryDedup.NearDuplicateThreshold);
        Assert.Equal(0.0, MemoryDedup.Dice(a, unrelated), 3);
        Assert.Equal(0.0, MemoryDedup.Dice(tooShort, tooShort), 3);
    }
}
