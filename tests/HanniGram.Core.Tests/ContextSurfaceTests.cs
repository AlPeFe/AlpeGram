using HanniGram.Core;
using HanniGram.Core.Store;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The bounded global injection surface (T21): at most a small fixed number of
/// pinned global heuristics plus a bounded number of recent project rows, previews
/// only. The caps are store constants; a caller cannot raise them, and no call
/// returns a memory body.
/// </summary>
public sealed class ContextSurfaceTests
{
    [Fact]
    public void ContextSurface_ReturnsOnlyPinnedGlobalHeuristics()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        var (pinned, _) = store.SaveObservation(global.Id, "Console layout", "one project per concern");
        store.SaveObservation(global.Id, "Unpinned heuristic", "never injected automatically");
        store.SetObservationPinned(global.Id, pinned.Id, true);

        var surface = store.GetContextSurface(project.Id);

        var item = Assert.Single(surface.PinnedGlobal);
        Assert.Equal(pinned.Id, item.Id);
        Assert.Equal(MemoryScopes.Global, item.Scope);
        Assert.True(item.Pinned);
    }

    [Fact]
    public void ContextSurface_WithNoGlobalSentinel_ReturnsNoPinnedGlobal()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var surface = store.GetContextSurface(project.Id);

        Assert.Empty(surface.PinnedGlobal);
        Assert.Null(store.GetGlobalProject());
    }

    [Fact]
    public void ContextSurface_PinnedGlobal_IsCappedByTheStoreConstant()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        for (var i = 0; i < HanniGramStore.MaxPinnedGlobalHeuristics + 3; i++)
        {
            var (observation, _) = store.SaveObservation(global.Id, $"Heuristic {i}", $"body {i}");
            store.SetObservationPinned(global.Id, observation.Id, true);
        }

        var surface = store.GetContextSurface(project.Id);

        Assert.Equal(HanniGramStore.MaxPinnedGlobalHeuristics, surface.PinnedGlobal.Count);
    }

    [Fact]
    public void ContextSurface_RecentProjectRows_AreActiveOnlyAndCapped()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        for (var i = 0; i < HanniGramStore.MaxRecentProjectRows + 4; i++)
        {
            store.SaveObservation(project.Id, $"Project row {i}", $"body {i}");
        }
        var (archived, _) = store.SaveObservation(project.Id, "Archived row", "archived body");
        store.SetObservationState(project.Id, archived.Id, MemoryStates.Archived);

        var surface = store.GetContextSurface(project.Id);

        Assert.Equal(HanniGramStore.MaxRecentProjectRows, surface.RecentProject.Count);
        Assert.DoesNotContain(surface.RecentProject, item => item.Id == archived.Id);
    }

    [Fact]
    public void ContextSurface_ExcludesArchivedPinnedGlobal()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        var (heuristic, _) = store.SaveObservation(global.Id, "Old heuristic", "forgotten body");
        store.SetObservationPinned(global.Id, heuristic.Id, true);
        store.SetObservationState(global.Id, heuristic.Id, MemoryStates.Archived);

        var surface = store.GetContextSurface(project.Id);

        Assert.Empty(surface.PinnedGlobal);
    }

    [Fact]
    public void ContextSurface_PreviewsAreTruncated()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        var global = store.GetOrCreateGlobalProject();
        var longContent = new string('y', 400) + " context_tail_never_sent";
        var (heuristic, _) = store.SaveObservation(global.Id, "Long heuristic", longContent);
        store.SetObservationPinned(global.Id, heuristic.Id, true);

        var surface = store.GetContextSurface(project.Id);
        var item = Assert.Single(surface.PinnedGlobal);

        Assert.True(item.Preview.Length <= 161, $"preview was {item.Preview.Length} chars");
        Assert.DoesNotContain("context_tail_never_sent", item.Preview);
    }
}
