using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The dual FTS index (A0): <c>observations_fts</c> stays trigram-only, and
/// <c>observations_fts_words</c> adds word boundaries plus porter stemming. Search
/// routes by query shape and falls through to the other index when the routed one
/// finds nothing. These tests pin the two behaviours that routing exists for: a
/// natural-language query must be able to match a different inflection of a word,
/// and a code-shaped query must keep matching identifiers/substrings.
/// </summary>
public sealed class FtsDualIndexTests
{
    /// <summary>
    /// "pooled" and "pooling" are different tokens with different trigrams (they only
    /// share "poo"/"ool"), so the trigram index cannot answer this query at all. The
    /// words index stems both to "pool", which is exactly the gap A0 fills.
    /// </summary>
    [Fact]
    public void NaturalLanguageQuery_MatchesAcrossStems()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Database access", "connection pooling strategies for the daemon");

        var hit = Assert.Single(store.Search(project.Id, "pooled"));
        Assert.Contains("pooling", hit.Content);
    }

    /// <summary>
    /// A path/identifier query still goes to the trigram index, where substrings match:
    /// the words index would store "auth storage ts" as three separate tokens and a
    /// quoted whole-token query would be a phrase.
    /// </summary>
    [Fact]
    public void CodeShapedQuery_StillMatchesIdentifiers()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Credentials", "auth-storage.ts wrote the credentials file with mode 0666");

        var hit = Assert.Single(store.Search(project.Id, "auth-storage.ts"));
        Assert.Contains("auth-storage.ts", hit.Content);
    }

    /// <summary>
    /// Prose-shaped query, but only the trigram index can see it: "compiled" is a
    /// substring of "precompiled" and not a token of its own, so the words index finds
    /// nothing and the search must fall through to the trigram index instead of
    /// returning zero hits.
    /// </summary>
    [Fact]
    public void WordsIndexEmpty_FallsThroughToTheTrigramIndex()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Build layout", "precompiled artifacts live in the cache directory");

        var hit = Assert.Single(store.Search(project.Id, "compiled art"));
        Assert.Contains("precompiled", hit.Content);
    }

    /// <summary>
    /// Both indexes must answer immediately after a write, with no rebuild: the two
    /// trigger triples are installed together by the migration.
    /// </summary>
    [Fact]
    public void BothIndexes_AreKeptInSyncByTheWriteTriggers()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Note", "the pooled connection was reused");

        // Trigram path (substring of a token) and words path (stem) on the same row.
        Assert.Single(store.Search(project.Id, "pool"));
        Assert.Single(store.Search(project.Id, "pooling"));
        Assert.Single(store.Search(project.Id, "reusing"));
    }

    /// <summary>
    /// A database that carries SchemaVersion 5 but lost the words index (written by an
    /// intermediate build) is repaired on open: the table is recreated and re-indexed,
    /// so the stem query works again without anyone bumping the version.
    /// </summary>
    [Fact]
    public void ReopeningWithTheWordsIndexMissing_RepairsAndRebuildsIt()
    {
        using var db = new TempDatabase();
        long projectId;
        using (var store = db.OpenStore())
        {
            projectId = store.GetOrCreateProject("alpha").Id;
            store.SaveObservation(projectId, "Database access", "connection pooling strategies");
            Assert.Single(store.Search(projectId, "pooled"));
        }

        db.Execute(@"
            DROP TRIGGER IF EXISTS observations_fts_words_ai;
            DROP TRIGGER IF EXISTS observations_fts_words_ad;
            DROP TRIGGER IF EXISTS observations_fts_words_au;
            DROP TABLE IF EXISTS observations_fts_words;");

        using (var store = db.OpenStore())
        {
            var hit = Assert.Single(store.Search(projectId, "pooled"));
            Assert.Contains("pooling", hit.Content);
            // The write path must still work: the dangling triggers were re-pointed at
            // a table that exists again.
            store.SaveObservation(projectId, "Second", "a freshly pooled handle");
            Assert.Equal(2, store.Search(projectId, "pooled").Count);
        }
    }
}
