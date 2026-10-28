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

    // The primary OR match on the misspelling "zephyruos" is empty (a quoted term
    // is a phrase over trigrams, so it needs the exact substring), so the trigram
    // fallback runs. A typo *inside* one token must still match the intended row.
    [Fact]
    public void Search_TypoWithinAToken_StillMatchesViaTheFallback()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Generic title", "zephyrous payload");

        var hit = Assert.Single(store.Search(project.Id, "zephyruos"));

        Assert.Contains("zephyrous", hit.Content);
    }

    // Relaxed (OR) primary path: "kwyjibo" is absent from every document, but the
    // query still names a term that IS present, so the row comes back instead of
    // the whole query collapsing to zero hits (the "AND trap" that made paraphrase
    // recall 0.0 in the run-1 benchmark). The absent term must not invent matches
    // of its own: asked alone, it returns nothing.
    [Fact]
    public void Search_TwoTokensWhereTheSecondIsAbsent_StillReturnsTheMatchingRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var (observation, _) = store.SaveObservation(project.Id, "Generic title", "zephyrous payload");

        var hit = Assert.Single(store.Search(project.Id, "zephyrous kwyjibo"));

        Assert.Equal(observation.Id, hit.Id);
        Assert.Empty(store.Search(project.Id, "kwyjibo"));
    }

    // With OR, two words that live in different documents each match their own row;
    // bm25 decides the order. What must NOT happen is a row matching a term it does
    // not contain, so each token alone still scopes to its own row.
    [Fact]
    public void Search_TwoUnrelatedTokensInDifferentDocuments_EachMapsToItsOwnRow()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "First", "obsidianwalnut");
        store.SaveObservation(project.Id, "Second", "geraniumposts");

        var hits = store.Search(project.Id, "obsidianwalnut geraniumposts");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Content.Contains("obsidianwalnut"));
        Assert.Contains(hits, h => h.Content.Contains("geraniumposts"));
        Assert.Single(store.Search(project.Id, "obsidianwalnut"));
        Assert.Single(store.Search(project.Id, "geraniumposts"));
    }
}
