using HanniGram.Core;
using HanniGram.Core.Models;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// Cross-scope search returns project hits and global hits in one result set,
/// each stamped with its origin so a caller can tell a repo fact from a
/// universal heuristic. The global namespace is resolved read-only, so a
/// cross-scope read never creates the sentinel.
/// </summary>
public sealed class CrossScopeSearchTests
{
    // The token is shared by both rows so a single query matches each namespace;
    // the origin label (not the query) is what distinguishes them.
    private const string Shared = "sharedmarker";

    [Fact]
    public void AllScope_ReturnsProjectAndGlobalHits_WithTheirOrigin()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(project.Id, "Repo fact", Shared + " repo body");
        store.SaveObservation(global.Id, "Universal heuristic", Shared + " global body");

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var hits = store.Search(namespaces, Shared);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Scope == MemoryScopes.Project && h.Title == "Repo fact");
        Assert.Contains(hits, h => h.Scope == MemoryScopes.Global && h.Title == "Universal heuristic");
    }

    [Fact]
    public void ProjectScope_ExcludesGlobalHits()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(project.Id, "Repo fact", Shared + " repo body");
        store.SaveObservation(global.Id, "Universal heuristic", Shared + " global body");

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.Project, MemoryScopes.All);
        var hits = store.Search(namespaces, Shared);

        var hit = Assert.Single(hits);
        Assert.Equal(MemoryScopes.Project, hit.Scope);
    }

    // Core acceptance: a global heuristic written while working in one project is
    // found from a DIFFERENT project through cross-scope search.
    [Fact]
    public void GlobalScope_IsFoundFromADifferentProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(global.Id, "Commit style", Shared + " imperative mood");

        var beta = store.GetOrCreateProject("beta");
        var namespaces = store.NamespacesFor(beta.Id, MemoryScopes.All, MemoryScopes.All);
        var hits = store.Search(namespaces, Shared);

        var hit = Assert.Single(hits);
        Assert.Equal(MemoryScopes.Global, hit.Scope);
        Assert.Equal("Commit style", hit.Title);
    }

    [Fact]
    public void GlobalScope_OnAnEmptyDatabase_ReturnsNothingWithoutCreatingTheSentinel()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.SaveObservation(project.Id, "Repo fact", Shared + " repo body");

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var hits = store.Search(namespaces, Shared);

        Assert.Single(hits);
        // The read resolved global memory and found none; it must not have
        // inserted the sentinel row.
        Assert.Null(store.GetGlobalProject());
        Assert.Single(store.ListProjects());
    }

    [Fact]
    public void CrossScopeList_CarriesTheOriginScope()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(project.Id, "Repo fact", "repo body");
        store.SaveObservation(global.Id, "Universal heuristic", "global body");

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var listed = store.ListObservations(namespaces);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, o => o.Scope == MemoryScopes.Project);
        Assert.Contains(listed, o => o.Scope == MemoryScopes.Global);
    }

    // An id-based read of the global namespace must be labelled global, not
    // assumed to be a project: GetGlobalProject() returns a Project a caller can
    // pass straight to Search/ListObservations.
    [Fact]
    public void IdBasedGlobalRead_IsLabelledGlobal()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(global.Id, "Universal heuristic", Shared + " global body");

        var hit = Assert.Single(store.Search(global.Id, Shared));
        var listed = Assert.Single(store.ListObservations(global.Id));

        Assert.Equal(MemoryScopes.Global, hit.Scope);
        Assert.Equal(MemoryScopes.Global, listed.Scope);
    }

    // Even when the sentinel is addressed explicitly as a project, the origin
    // label reflects reality rather than the caller's declared scope.
    [Fact]
    public void SentinelAddressedAsAProject_IsStillLabelledGlobal()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(global.Id, "Universal heuristic", Shared + " global body");

        var namespaces = store.NamespacesFor(global.Id, MemoryScopes.Project, MemoryScopes.All);
        var hit = Assert.Single(store.Search(namespaces, Shared));

        Assert.Equal(MemoryScopes.Global, hit.Scope);
    }

    [Fact]
    public void CrossScopeSearch_IsBoundedByTheLimit()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        for (var i = 0; i < 3; i++)
        {
            store.SaveObservation(project.Id, $"Repo {i}", "boundedmarker repo body");
            store.SaveObservation(global.Id, $"Global {i}", "boundedmarker global body");
        }

        var namespaces = store.NamespacesFor(project.Id, MemoryScopes.All, MemoryScopes.All);
        var hits = store.Search(namespaces, "boundedmarker", limit: 2);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Contains(h.Scope, new[] { MemoryScopes.Project, MemoryScopes.Global }));
    }

    [Theory]
    [InlineData(null, MemoryScopes.All, true, true)]
    [InlineData("project", MemoryScopes.All, true, false)]
    [InlineData("global", MemoryScopes.All, false, true)]
    [InlineData("all", MemoryScopes.Project, true, true)]
    [InlineData("GLOBAL", MemoryScopes.All, false, true)]
    [InlineData("nonsense", MemoryScopes.All, true, true)]
    public void Selection_InterpretsScopeTokens(string? scope, string defaultScope, bool project, bool global)
    {
        var (includeProject, includeGlobal) = MemoryScopes.Selection(scope, defaultScope);

        Assert.Equal(project, includeProject);
        Assert.Equal(global, includeGlobal);
    }
}
