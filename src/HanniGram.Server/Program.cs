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

// A mutation by id is scoped explicitly: "project" (default) is the caller's own
// namespace, "global" is the shared sentinel. Resolving the sentinel here is safe
// because the row being mutated already exists in it.
long ProjectIdForWrite(HttpRequest req, string? scope)
{
    var resolved = MemoryScopes.ResolveScope(scope, MemoryScopes.Project);
    return resolved == MemoryScopes.Global ? store.GetOrCreateGlobalProject().Id : ResolveProject(req).Id;
}

// Read a boolean request flag: `?name=true` (or `1`). Anything else is false.
bool Flag(HttpRequest req, string name)
{
    var value = req.Query[name].FirstOrDefault();
    return bool.TryParse(value, out var parsed) ? parsed : value == "1";
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

    var (obs, created, review) = store.SaveObservationWithReview(
        projectId, body.Title, body.Content ?? "",
        body.What, body.Why, body.Where, body.Learned, body.TopicKey, body.Type);
    obs.Scope = scope;
    // `review` is the compact deterministic hint (a few tokens) or null when
    // nothing collided. It is the entire review mechanism on the save hot path.
    return Results.Json(new { observation = obs, created, scope, review }, json);
});

// Change lifecycle state or pin by id (T15). Archive is the safe default over
// delete; mem_delete (DELETE /api/observations/{id}) stays for real deletion.
app.MapPost("/api/observations/{id:long}/state", (long id, HttpRequest req, ObservationStateBody body) =>
{
    if (!MemoryStates.IsKnownState(body.State))
        return Results.BadRequest(new { error = "invalid_state" });
    var projectId = ProjectIdForWrite(req, req.Query["scope"].FirstOrDefault());
    var obs = store.SetObservationState(projectId, id, body.State);
    return obs is null ? Results.NotFound() : Results.Json(obs, json);
});

app.MapPost("/api/observations/{id:long}/pin", (long id, HttpRequest req, ObservationPinBody body) =>
{
    var projectId = ProjectIdForWrite(req, req.Query["scope"].FirstOrDefault());
    var obs = store.SetObservationPinned(projectId, id, body.Pinned);
    return obs is null ? Results.NotFound() : Results.Json(obs, json);
});

// Update an observation by id (T20 / mem_update). Null fields are left unchanged;
// created_at_utc is preserved and updated_at_utc moves. Scoped by namespace, so a
// cross-project update is refused with 404.
app.MapPut("/api/observations/{id:long}", (long id, HttpRequest req, ObservationUpdateBody body) =>
{
    var projectId = ProjectIdForWrite(req, body.Scope ?? req.Query["scope"].FirstOrDefault());
    var obs = store.UpdateObservation(projectId, id,
        body.Title, body.Content, body.What, body.Why, body.Where, body.Learned, body.TopicKey, body.Type);
    return obs is null ? Results.NotFound() : Results.Json(obs, json);
});

app.MapGet("/api/observations/{id:long}", (long id, HttpRequest req) =>
{
    var p = ResolveProject(req);
    var obs = store.GetObservation(p.Id, id);
    return obs is null ? Results.NotFound() : Results.Json(obs, json);
});

app.MapDelete("/api/observations/{id:long}", (long id, HttpRequest req) =>
{
    // A delete may target the caller's project (default) or the shared global
    // namespace via ?scope=global. The global namespace is resolved read-only, so
    // a delete never materialises the sentinel just to report a miss.
    var scope = MemoryScopes.ResolveScope(req.Query["scope"].FirstOrDefault(), MemoryScopes.Project);
    var projectId = scope == MemoryScopes.Global ? store.GetGlobalProject()?.Id : ResolveProject(req).Id;
    if (projectId is null) return Results.NotFound();
    var deleted = store.DeleteObservation(projectId.Value, id);
    return deleted ? Results.Ok(new { deleted = true, id }) : Results.NotFound();
});

// Promote a project memory to the global scope: the "I have taught this 100
// times" path. When a global memory already owns the topic_key, the promoted
// content wins and the redundant project row is removed; the response says which
// of the two happened. An archived source is REFUSED by default (409) because it
// would overwrite live global truth with retired content; pass allowArchived=true
// to opt in, and the surviving row then keeps the source's state.
app.MapPost("/api/observations/{id:long}/promote", (long id, HttpRequest req) =>
{
    var p = ResolveProject(req);
    var allowArchived = Flag(req, "allowArchived");
    var result = store.PromoteObservation(p.Id, id, allowArchived);
    if (result is null) return Results.NotFound();
    if (result.Outcome == PromotionOutcomes.Refused)
    {
        // 409 Conflict: the request conflicts with the source's lifecycle state.
        return Results.Json(
            new { outcome = result.Outcome, reason = result.Reason, error = result.Reason },
            json, statusCode: StatusCodes.Status409Conflict);
    }
    return Results.Json(new
    {
        observation = result.Observation,
        merged = result.Outcome == PromotionOutcomes.Merged,
        outcome = result.Outcome,
    }, json);
});

app.MapGet("/api/observations", (HttpRequest req, int limit = 50, string? scope = null, string? state = null) =>
{
    var namespaces = store.NamespacesFor(ProjectIdForRead(req, scope, MemoryScopes.All), scope, MemoryScopes.All);
    return Results.Json(store.ListObservations(namespaces, limit, state), json);
});

// Bounded review listing (T15): needs_review rows plus duplicate candidates, with
// truncated previews and a hard cap. Never returns an observation body.
app.MapGet("/api/review", (HttpRequest req, int limit = 20, string? scope = null) =>
{
    var namespaces = store.NamespacesFor(ProjectIdForRead(req, scope, MemoryScopes.All), scope, MemoryScopes.All);
    return Results.Json(store.ReviewObservations(namespaces, limit), json);
});

// Bounded global injection surface (T21): pinned global heuristics plus recent
// project rows, previews only, fixed caps. There is no limit parameter by design.
app.MapGet("/api/context", (HttpRequest req) =>
{
    var p = ResolveProject(req);
    return Results.Json(store.GetContextSurface(p.Id), json);
});

// Topic-key lookup. Slash keys ("architecture/auth-model", "adr/<slug>") are the
// documented shape, so the canonical form is a QUERY parameter: `?topicKey=svc/auth`
// survives URL-encoding exactly and cannot silently mismatch. A path form is kept
// for plain keys and for a raw slash path (catch-all); a still-percent-encoded
// `%2F` is decoded here, so the earlier `encodeURIComponent` callers stop getting
// a silent empty list.
IResult TopicTimeline(HttpRequest req, string topicKey)
{
    var p = ResolveProject(req);
    return Results.Json(store.GetByTopicKey(p.Id, topicKey), json);
}

app.MapGet("/api/observations/topic", (HttpRequest req) =>
{
    var topicKey = req.Query["topicKey"].FirstOrDefault();
    if (string.IsNullOrEmpty(topicKey))
        return Results.BadRequest(new { error = "topicKey_required" });
    return TopicTimeline(req, topicKey);
});

app.MapGet("/api/observations/topic/{**topicKey}", (string topicKey, HttpRequest req) =>
    TopicTimeline(req, Uri.UnescapeDataString(topicKey)));

// ---- Search (FTS5) ----
app.MapGet("/api/search", (HttpRequest req, string q, int limit = 10, string? type = null, string? topicKey = null, string? scope = null, string? state = null) =>
{
    var namespaces = store.NamespacesFor(ProjectIdForRead(req, scope, MemoryScopes.All), scope, MemoryScopes.All);
    return Results.Json(store.Search(namespaces, q, limit, type, topicKey, state), json);
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

/// <summary>Body for <c>PUT /api/observations/{id}</c> (T20). Null fields are left unchanged.</summary>
public sealed class ObservationUpdateBody
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? What { get; set; }
    public string? Why { get; set; }
    public string? Where { get; set; }
    public string? Learned { get; set; }
    public string? TopicKey { get; set; }
    public string? Type { get; set; }
    /// <summary>Namespace of the row: "project" (default) or "global".</summary>
    public string? Scope { get; set; }
}

/// <summary>Body for <c>POST /api/observations/{id}/state</c> (T13/T15).</summary>
public sealed class ObservationStateBody
{
    /// <summary>"active", "needs_review" or "archived".</summary>
    public required string State { get; set; }
}

/// <summary>Body for <c>POST /api/observations/{id}/pin</c> (T15/T21).</summary>
public sealed class ObservationPinBody
{
    public bool Pinned { get; set; }
}

/// <summary>
/// Exposes the auto-generated entry point type so integration tests can host the
/// daemon in-process (<c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
public partial class Program;
