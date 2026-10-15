using System.Text.Json;
using System.Text.Json.Serialization;
using HanniGram.Core;
using HanniGram.Core.Models;
using HanniGram.Core.Store;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Bind only to loopback by default; override with --urls or HANNIGRAM_URLS.
var urls = Environment.GetEnvironmentVariable("HANNIGRAM_URLS") ?? "http://127.0.0.1:8765";
builder.WebHost.UseUrls(urls);

var dbPath = Environment.GetEnvironmentVariable("HANNIGRAM_DB") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hannigram", "hannigram.db");

var store = new HanniGramStore(dbPath);
builder.Services.AddSingleton(store);

var app = builder.Build();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// An unhandled exception used to surface as a bare, bodiless 500, which the CLI
// and the harness could not parse. Return a small JSON envelope instead and log
// the exception server-side: stack traces and messages never leave the process.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("HanniGram.Server");
        logger.LogError(feature?.Error, "Unhandled exception while handling {Method} {Path}",
            context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.Serialize(
            new { error = "internal_error", message = "The daemon failed to handle the request." },
            json);
        await context.Response.WriteAsync(payload);
    });
});

// Resolve a project from a cwd (or explicit name). Mirrors engram's resolution:
// explicit project > HANNIGRAM_CWD > cwd detection.
Project ResolveProject(HttpRequest req)
{
    var name = req.Query["project"].FirstOrDefault();
    var cwd = req.Query["cwd"].FirstOrDefault();
    if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(cwd))
        cwd = Environment.GetEnvironmentVariable("HANNIGRAM_CWD") ?? Directory.GetCurrentDirectory();
    if (string.IsNullOrEmpty(name))
        name = ProjectResolver.Resolve(cwd);
    return store.GetOrCreateProject(name, string.IsNullOrEmpty(cwd) ? null : cwd);
}

// The global namespace is shared by every project. Read paths resolve it
// READ-ONLY through the store (NamespacesFor), so a project that never wrote
// global memory does not spawn the sentinel; only writes create it.
long? ProjectIdForRead(HttpRequest req, string? scope, string defaultScope)
{
    var (includeProject, _) = MemoryScopes.Selection(scope, defaultScope);
    return includeProject ? ResolveProject(req).Id : null;
}

// ---- Health ----
app.MapGet("/health", () => Results.Json(new { status = "ok", db = dbPath }, json));

// ---- Projects ----
app.MapGet("/api/projects", () => Results.Json(store.ListProjects(), json));

app.MapGet("/api/projects/current", (HttpRequest req) =>
{
    var p = ResolveProject(req);
    return Results.Json(p, json);
});

// ---- Observations ----
app.MapPost("/api/observations", (HttpRequest req, ObservationBody body) =>
{
    // A memory is written to a scope explicitly. "project" (the default) uses
    // the caller's own namespace; "global" uses the shared sentinel and creates
    // it on first write.
    var scope = MemoryScopes.ResolveScope(body.Scope, MemoryScopes.Project);
    var projectId = scope == MemoryScopes.Global
        ? store.GetOrCreateGlobalProject().Id
        : ResolveProject(req).Id;

    var (obs, created) = store.SaveObservation(
        projectId, body.Title, body.Content ?? "",
        body.What, body.Why, body.Where, body.Learned, body.TopicKey, body.Type);
    obs.Scope = scope;
    return Results.Json(new { observation = obs, created, scope }, json);
});

app.MapGet("/api/observations/{id:long}", (long id, HttpRequest req) =>
{
    var p = ResolveProject(req);
    var obs = store.GetObservation(p.Id, id);
    return obs is null ? Results.NotFound() : Results.Json(obs, json);
});

app.MapDelete("/api/observations/{id:long}", (long id, HttpRequest req) =>
{
    var p = ResolveProject(req);
    var deleted = store.DeleteObservation(p.Id, id);
    return deleted ? Results.Ok(new { deleted = true, id }) : Results.NotFound();
});

// Promote a project memory to the global scope: the "I have taught this 100
// times" path. When a global memory already owns the topic_key, the promoted
// content wins and the redundant project row is removed; the response says which
// of the two happened.
app.MapPost("/api/observations/{id:long}/promote", (long id, HttpRequest req) =>
{
    var p = ResolveProject(req);
    var result = store.PromoteObservation(p.Id, id);
    if (result is null) return Results.NotFound();
    var (observation, merged) = result.Value;
    return Results.Json(new { observation, merged, outcome = merged ? "merged" : "moved" }, json);
});

app.MapGet("/api/observations", (HttpRequest req, int limit = 50, string? scope = null) =>
{
    var namespaces = store.NamespacesFor(ProjectIdForRead(req, scope, MemoryScopes.All), scope, MemoryScopes.All);
    return Results.Json(store.ListObservations(namespaces, limit), json);
});

app.MapGet("/api/observations/topic/{topicKey}", (string topicKey, HttpRequest req) =>
{
    var p = ResolveProject(req);
    return Results.Json(store.GetByTopicKey(p.Id, topicKey), json);
});

// ---- Search (FTS5) ----
app.MapGet("/api/search", (HttpRequest req, string q, int limit = 10, string? type = null, string? topicKey = null, string? scope = null) =>
{
    var namespaces = store.NamespacesFor(ProjectIdForRead(req, scope, MemoryScopes.All), scope, MemoryScopes.All);
    return Results.Json(store.Search(namespaces, q, limit, type, topicKey), json);
});

// ---- Topic keys ----
app.MapGet("/api/topics", (HttpRequest req, int limit = 20) =>
{
    var p = ResolveProject(req);
    return Results.Json(store.SuggestTopicKeys(p.Id, limit), json);
});

// ---- Sessions ----
app.MapPost("/api/sessions/start", (HttpRequest req, SessionStartBody body) =>
{
    var p = ResolveProject(req);
    var sessionId = string.IsNullOrEmpty(body.SessionId) ? Guid.NewGuid().ToString("N") : body.SessionId;
    var s = store.StartSession(p.Id, sessionId);
    return Results.Json(s, json);
});

app.MapPost("/api/sessions/end", (HttpRequest req, SessionEndBody body) =>
{
    var p = ResolveProject(req);
    store.EndSession(p.Id, body.SessionId, body.Summary, body.Goal, body.NextSteps);
    return Results.Json(new { ok = true }, json);
});

app.MapGet("/api/sessions", (HttpRequest req, int limit = 20) =>
{
    var p = ResolveProject(req);
    return Results.Json(store.ListSessions(p.Id, limit), json);
});

app.Run();

// ---- Request bodies ----
public sealed class ObservationBody
{
    public required string Title { get; set; }
    public string? Content { get; set; }
    public string? What { get; set; }
    public string? Why { get; set; }
    public string? Where { get; set; }
    public string? Learned { get; set; }
    public string? TopicKey { get; set; }
    public string? Type { get; set; }
    /// <summary>Write scope: "project" (default) or "global".</summary>
    public string? Scope { get; set; }
}

public sealed class SessionStartBody
{
    public string? SessionId { get; set; }
}

public sealed class SessionEndBody
{
    public required string SessionId { get; set; }
    public string? Summary { get; set; }
    public string? Goal { get; set; }
    public string? NextSteps { get; set; }
}
