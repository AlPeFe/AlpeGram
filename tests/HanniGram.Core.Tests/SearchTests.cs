using Xunit;

namespace HanniGram.Core.Tests;

public sealed class SearchTests
{
    [Fact]
    public void Search_FindsTermInContent()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Unrelated title", "the quick brown fox jumps over the lazy dog");

        var hits = store.Search(project.Id, "quick");

        Assert.Single(hits);
    }

    [Fact]
    public void Search_FindsTermInTitle()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Razor pages tutorial", "body");

        var hits = store.Search(project.Id, "razor");

        Assert.Single(hits);
        Assert.Equal("Razor pages tutorial", hits[0].Title);
    }

    [Fact]
    public void Search_IsScopedToProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        store.SaveObservation(alpha.Id, "Alpha note", "quicksilver benchmark");

        Assert.Single(store.Search(alpha.Id, "quicksilver"));
        Assert.Empty(store.Search(beta.Id, "quicksilver"));
    }

    [Fact]
    public void Search_RespectsTypeFilter()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Architecture note", "dependency inversion", type: "architecture");
        store.SaveObservation(project.Id, "Bugfix note", "dependency inversion", type: "bugfix");

        var hits = store.Search(project.Id, "dependency", type: "architecture");

        Assert.Single(hits);
        Assert.Equal("architecture", hits[0].Type);
    }

    // Regression: FtsQuery drops terms shorter than 3 chars, so these queries used
    // to escape to an empty expression and blow up on `MATCH ''` with
    // `fts5: syntax error near ""` (HTTP 500 on /api/search).
    [Theory]
    [InlineData("ab")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\t\t")]
    public void Search_QueryWithoutSearchableTerm_ReturnsEmptyWithoutThrowing(string query)
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Some title", "some reasonably long content about things");

        var hits = store.Search(project.Id, query);

        Assert.Empty(hits);
    }

    [Fact]
    public void Search_AfterShortQuery_StillWorksForValidQuery()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Some title", "searchable substring here");

        Assert.Empty(store.Search(project.Id, "ab"));
        Assert.Single(store.Search(project.Id, "substring"));
    }

    // Regression: observations_fts used to index only title/content/topic_key/type,
    // so the richest part of a memory (what/why/where/learned) was unretrievable.
    [Fact]
    public void Search_IndexesStructuredFields()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "One", "plain body", what: "zephyrous");
        store.SaveObservation(project.Id, "Two", "plain body", why: "kangarooish");
        store.SaveObservation(project.Id, "Three", "plain body", where: "vancouverly");
        store.SaveObservation(project.Id, "Four", "plain body", learned: "aardwolfish");

        Assert.Single(store.Search(project.Id, "zephyrous"));
        Assert.Single(store.Search(project.Id, "kangarooish"));
        Assert.Single(store.Search(project.Id, "vancouverly"));
        Assert.Single(store.Search(project.Id, "aardwolfish"));
    }

    [Fact]
    public void Search_FindsTermStoredOnlyInWhat()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(
            project.Id, "Generic title", "generic body", what: "zephyrous payload handling");

        var hit = Assert.Single(store.Search(project.Id, "zephyrous"));

        Assert.Equal(observation.Id, hit.Id);
    }
}
