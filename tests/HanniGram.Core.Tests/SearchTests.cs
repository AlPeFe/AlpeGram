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
}
