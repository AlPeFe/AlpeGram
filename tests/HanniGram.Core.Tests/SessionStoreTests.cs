using Xunit;

namespace HanniGram.Core.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public void StartSession_ReturnsPersistedSession()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");

        var session = store.StartSession(project.Id, "session-1");

        Assert.True(session.Id > 0);
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal(project.Id, session.ProjectId);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public void EndSession_PersistsSummaryGoalAndNextSteps()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var project = store.GetOrCreateProject("alpha");
        store.StartSession(project.Id, "session-1");

        store.EndSession(project.Id, "session-1", "wrapped up", "ship slice 1", "slice 2 next");

        var session = Assert.Single(store.ListSessions(project.Id));
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("wrapped up", session.Summary);
        Assert.Equal("ship slice 1", session.Goal);
        Assert.Equal("slice 2 next", session.NextSteps);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void ListSessions_IsScopedToProject()
    {
        using var db = new TempDatabase();
        using var store = db.OpenStore();
        var alpha = store.GetOrCreateProject("alpha");
        var beta = store.GetOrCreateProject("beta");
        store.StartSession(alpha.Id, "alpha-session");
        store.StartSession(beta.Id, "beta-session");

        var sessions = store.ListSessions(alpha.Id);

        var session = Assert.Single(sessions);
        Assert.Equal("alpha-session", session.SessionId);
    }
}
