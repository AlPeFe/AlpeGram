using HanniGram.Core;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The global scope is not a second store: it is the reserved sentinel project
/// row reusing the same observations table, the same FTS index and the same
/// upsert-by-topic_key path. These tests pin the two rules that make it safe:
/// reads never create the sentinel, and writes reuse the ordinary store.
/// </summary>
public sealed class GlobalScopeTests
{
    [Fact]
    public void GlobalNamespace_IsAbsentOnAFreshDatabase()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();

        Assert.Null(store.GetGlobalProject());
        Assert.Empty(store.ListProjects());
    }

    [Fact]
    public void GlobalNamespace_IsCreatedOnFirstWrite()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();

        var global = store.GetOrCreateGlobalProject();

        Assert.Equal(MemoryScopes.GlobalProjectName, global.Name);
        Assert.Single(store.ListProjects());
        Assert.Equal(global.Id, store.GetGlobalProject()!.Id);
    }

    [Fact]
    public void MemorySavedToGlobal_IsRetrievableFromTheGlobalScope()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var global = store.GetOrCreateGlobalProject();

        store.SaveObservation(global.Id, "Commit style", "imperative mood, conventional commits",
            topicKey: "conventions/commits");

        var hit = Assert.Single(store.Search(global.Id, "imperative"));
        Assert.Equal("Commit style", hit.Title);
        Assert.Single(store.ListObservations(global.Id));
    }

    [Fact]
    public void ProjectScope_DoesNotSeeGlobalMemory()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        store.SaveObservation(global.Id, "Universal heuristic", "globalonlymarker body");

        Assert.Empty(store.Search(project.Id, "globalonlymarker"));
        Assert.Empty(store.ListObservations(project.Id));
    }
}
