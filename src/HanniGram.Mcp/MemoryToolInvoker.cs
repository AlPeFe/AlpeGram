using System.Text.Json;
using System.Text.Json.Nodes;
using HanniGram.Core;
using HanniGram.Core.Models;
using HanniGram.Core.Store;

namespace HanniGram.Mcp;

/// <summary>
/// Raised when a <c>tools/call</c> request is malformed: a missing or mistyped
/// argument, or a tool name that does not exist. These are protocol errors
/// (JSON-RPC -32602), not tool execution errors, because the model cannot fix them
/// by retrying with different values.
/// </summary>
public sealed class McpInvalidParamsException : Exception
{
    public McpInvalidParamsException(string message) : base(message) { }
}

/// <summary>
/// The tool surface, mapped onto the existing <see cref="HanniGramStore"/> methods.
/// <para>
/// Nothing is reimplemented here: every tool is argument validation, one store
/// call, and a bounded projection of the result. The projection is what keeps the
/// token budget: a row's body, a whole timeline and every list are capped by
/// <see cref="McpLimits"/> before they reach the wire.
/// </para>
/// </summary>
public sealed class MemoryToolInvoker
{
    private readonly HanniGramStore _store;
    private readonly McpProjectResolver _projects;

    public MemoryToolInvoker(HanniGramStore store, McpProjectResolver projects)
    {
        _store = store;
        _projects = projects;
    }

    /// <summary>
    /// Run one tool. Returns the bounded result plus whether it is a tool execution
    /// error (a not-found row, an unknown lifecycle state) — the model can act on
    /// those, so they stay inside the result object instead of becoming a
    /// protocol-level failure.
    /// </summary>
    public (JsonObject Result, bool IsError) Call(string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new McpInvalidParamsException("Invalid params: 'arguments' must be a JSON object.");
        }

        return name switch
        {
            "mem_current_project" => CurrentProject(arguments),
            "mem_save" => Save(arguments),
            "mem_search" => Search(arguments),
            "mem_context" => Context(arguments),
            "mem_get_observation" => GetObservation(arguments),
            "mem_timeline" => Timeline(arguments),
            "mem_suggest_topic_key" => SuggestTopicKeys(arguments),
            "mem_delete" => Delete(arguments),
            "mem_session_summary" => SessionSummary(arguments),
            "mem_update" => Update(arguments),
            "mem_review" => Review(arguments),
            "mem_promote" => Promote(arguments),
            "mem_state" => SetState(arguments),
            "mem_pin" => SetPinned(arguments),
            _ => throw new McpInvalidParamsException($"Unknown tool: {name}"),
        };
    }

    // =====================================================================
    // Tools
    // =====================================================================

    private (JsonObject, bool) CurrentProject(JsonElement args)
    {
        var project = _projects.ResolveProject(OptionalString(args, "project"));
        return (new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["id"] = project.Id,
                ["name"] = project.Name,
                ["rootPath"] = project.RootPath,
                ["scope"] = MemoryScopes.Project,
            },
            ["cwd"] = _projects.Cwd,
        }, false);
    }

    private (JsonObject, bool) Save(JsonElement args)
    {
        var title = RequiredString(args, "title");
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = scope == MemoryScopes.Global
            ? _store.GetOrCreateGlobalProject().Id
            : _projects.ResolveProject(OptionalString(args, "project")).Id;

        var (observation, created, review) = _store.SaveObservationWithReview(
            projectId,
            title,
            OptionalString(args, "content") ?? string.Empty,
            OptionalString(args, "what"),
            OptionalString(args, "why"),
            OptionalString(args, "where"),
            OptionalString(args, "learned"),
            OptionalString(args, "topicKey"),
            OptionalString(args, "type"));

        var result = new JsonObject
        {
            ["observation"] = ObservationNode(observation, scope),
            ["created"] = created,
            ["scope"] = scope,
        };
        // The compact deterministic hint (T14) is the only review signal on the hot
        // path; it is a few tokens and never a body.
        if (review is not null)
        {
            result["review"] = new JsonObject
            {
                ["kind"] = review.Kind,
                ["id"] = review.Id,
                ["title"] = Bounded.Text(review.Title, McpLimits.MaxTitleChars),
            };
        }
        return (result, false);
    }

    private (JsonObject, bool) Search(JsonElement args)
    {
        var query = RequiredString(args, "query");
        var scope = OptionalString(args, "scope");
        var limit = Limit(args, "limit", 10);
        var projectId = _projects.ProjectIdForScopeSelector(
            OptionalString(args, "project"), scope, MemoryScopes.All);
        var namespaces = _store.NamespacesFor(projectId, scope, MemoryScopes.All);

        var hits = _store.Search(
            namespaces, query, limit,
            OptionalString(args, "type"),
            OptionalString(args, "topicKey"),
            OptionalString(args, "state"));

        var array = new JsonArray();
        foreach (var hit in Bounded.Cap(hits, limit))
        {
            var node = new JsonObject
            {
                ["id"] = hit.Id,
                ["scope"] = hit.Scope ?? MemoryScopes.Project,
                ["title"] = Bounded.Text(hit.Title, McpLimits.MaxTitleChars),
                ["content"] = Bounded.Text(hit.Content, McpLimits.MaxFieldChars),
                ["createdAtUtc"] = Utc(hit.CreatedAtUtc),
                ["rank"] = Math.Round(hit.Rank, 4),
            };
            Put(node, "topicKey", Bounded.Optional(hit.TopicKey, McpLimits.MaxTokenChars));
            Put(node, "type", Bounded.Optional(hit.Type, McpLimits.MaxTokenChars));
            Put(node, "state", Bounded.Optional(hit.State, McpLimits.MaxTokenChars));
            array.Add(node);
        }

        return (new JsonObject { ["count"] = array.Count, ["hits"] = array }, false);
    }

    private (JsonObject, bool) Context(JsonElement args)
    {
        var project = _projects.ResolveProject(OptionalString(args, "project"));
        var surface = _store.GetContextSurface(project.Id);

        var pinned = new JsonArray();
        foreach (var item in Bounded.Cap(surface.PinnedGlobal, McpLimits.MaxItems)) pinned.Add(ContextItemNode(item));
        var recent = new JsonArray();
        foreach (var item in Bounded.Cap(surface.RecentProject, McpLimits.MaxItems)) recent.Add(ContextItemNode(item));

        return (new JsonObject
        {
            ["pinnedGlobal"] = pinned,
            ["recentProject"] = recent,
            ["count"] = pinned.Count + recent.Count,
        }, false);
    }

    private (JsonObject, bool) GetObservation(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = _projects.ProjectIdForScopedRead(OptionalString(args, "project"), scope);
        var observation = projectId is null ? null : _store.GetObservation(projectId.Value, id);
        if (observation is null) return NotFound(id, scope);

        return (new JsonObject { ["observation"] = ObservationNode(observation, scope) }, false);
    }

    private (JsonObject, bool) Timeline(JsonElement args)
    {
        var topicKey = RequiredString(args, "topicKey");
        var limit = Limit(args, "limit", 20);
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = _projects.ProjectIdForScopedRead(OptionalString(args, "project"), scope);

        var array = new JsonArray();
        if (projectId is not null)
        {
            // GetByTopicKey has its own hard cap; passing the tool limit keeps the
            // SQL LIMIT at the source and the Cap is the belt-and-braces bound.
            foreach (var observation in Bounded.Cap(_store.GetByTopicKey(projectId.Value, topicKey, limit), limit))
            {
                array.Add(ObservationNode(observation, scope));
            }
        }

        return (new JsonObject
        {
            ["topicKey"] = Bounded.Text(topicKey, McpLimits.MaxTokenChars),
            ["scope"] = scope,
            ["count"] = array.Count,
            ["observations"] = array,
        }, false);
    }

    private (JsonObject, bool) SuggestTopicKeys(JsonElement args)
    {
        var limit = Limit(args, "limit", 20);
        var project = _projects.ResolveProject(OptionalString(args, "project"));

        var array = new JsonArray();
        foreach (var key in Bounded.Cap(_store.SuggestTopicKeys(project.Id, limit), limit))
        {
            array.Add(Bounded.Text(key, McpLimits.MaxTokenChars));
        }
        return (new JsonObject { ["count"] = array.Count, ["topicKeys"] = array }, false);
    }

    private (JsonObject, bool) Delete(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        // The read-only resolution is deliberate: a delete must not create the
        // global sentinel just to discover that the row was never there.
        var projectId = _projects.ProjectIdForScopedRead(OptionalString(args, "project"), scope);
        if (projectId is null) return NotFound(id, scope);

        var deleted = _store.DeleteObservation(projectId.Value, id);
        if (!deleted) return NotFound(id, scope);
        return (new JsonObject { ["deleted"] = true, ["id"] = id, ["scope"] = scope }, false);
    }

    private (JsonObject, bool) SessionSummary(JsonElement args)
    {
        var summary = RequiredString(args, "summary");
        var project = _projects.ResolveProject(OptionalString(args, "project"));
        var sessionId = OptionalString(args, "sessionId") ?? Guid.NewGuid().ToString("N");

        // StartSession is idempotent on (project, session), so re-summarising a live
        // session updates one row instead of accumulating duplicates.
        _store.StartSession(project.Id, sessionId);
        _store.EndSession(project.Id, sessionId, summary,
            OptionalString(args, "goal"), OptionalString(args, "nextSteps"));

        Session? session = null;
        foreach (var candidate in _store.ListSessions(project.Id, McpLimits.MaxItems))
        {
            if (string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal)) { session = candidate; break; }
        }

        var node = new JsonObject { ["sessionId"] = sessionId };
        if (session is not null)
        {
            node["id"] = session.Id;
            node["startedAtUtc"] = Utc(session.StartedAtUtc);
            if (session.EndedAtUtc is not null) node["endedAtUtc"] = Utc(session.EndedAtUtc.Value);
        }
        Put(node, "summary", Bounded.Optional(summary, McpLimits.MaxFieldChars));
        Put(node, "goal", Bounded.Optional(OptionalString(args, "goal"), McpLimits.MaxFieldChars));
        Put(node, "nextSteps", Bounded.Optional(OptionalString(args, "nextSteps"), McpLimits.MaxFieldChars));
        return (new JsonObject { ["session"] = node }, false);
    }

    private (JsonObject, bool) Update(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = _projects.ProjectIdForScopedWrite(OptionalString(args, "project"), scope);

        var observation = _store.UpdateObservation(
            projectId, id,
            OptionalString(args, "title"),
            OptionalString(args, "content"),
            OptionalString(args, "what"),
            OptionalString(args, "why"),
            OptionalString(args, "where"),
            OptionalString(args, "learned"),
            OptionalString(args, "topicKey"),
            OptionalString(args, "type"));

        if (observation is null) return NotFound(id, scope);
        return (new JsonObject { ["observation"] = ObservationNode(observation, scope) }, false);
    }

    private (JsonObject, bool) Review(JsonElement args)
    {
        var limit = Limit(args, "limit", 20);
        var scope = OptionalString(args, "scope");
        var projectId = _projects.ProjectIdForScopeSelector(
            OptionalString(args, "project"), scope, MemoryScopes.All);
        var namespaces = _store.NamespacesFor(projectId, scope, MemoryScopes.All);

        var array = new JsonArray();
        foreach (var item in Bounded.Cap(_store.ReviewObservations(namespaces, limit), limit))
        {
            var node = new JsonObject
            {
                ["kind"] = item.Kind,
                ["id"] = item.Id,
                ["scope"] = item.Scope ?? MemoryScopes.Project,
                ["title"] = Bounded.Text(item.Title, McpLimits.MaxTitleChars),
                ["preview"] = Bounded.Text(item.Preview, McpLimits.MaxFieldChars),
            };
            Put(node, "state", Bounded.Optional(item.State, McpLimits.MaxTokenChars));
            if (item.DuplicateOfId is not null) node["duplicateOfId"] = item.DuplicateOfId.Value;
            array.Add(node);
        }
        return (new JsonObject { ["count"] = array.Count, ["items"] = array }, false);
    }

    private (JsonObject, bool) Promote(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var allowArchived = OptionalBool(args, "allowArchived") ?? false;
        var project = _projects.ResolveProject(OptionalString(args, "project"));
        var result = _store.PromoteObservation(project.Id, id, allowArchived);
        if (result is null) return NotFound(id, MemoryScopes.Project);
        if (result.Outcome == PromotionOutcomes.Refused)
        {
            // A refusal is a value the model can act on (retry with allowArchived),
            // so it stays inside the result instead of becoming a protocol error.
            return (new JsonObject
            {
                ["outcome"] = result.Outcome,
                ["reason"] = result.Reason,
                ["error"] = $"Refused: the observation is '{MemoryStates.Archived}'. Pass allowArchived=true to promote it anyway; the promoted row will stay archived.",
            }, true);
        }

        return (new JsonObject
        {
            ["observation"] = ObservationNode(result.Observation!, MemoryScopes.Global),
            ["merged"] = result.Outcome == PromotionOutcomes.Merged,
            ["outcome"] = result.Outcome,
        }, false);
    }

    private (JsonObject, bool) SetState(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var state = RequiredString(args, "state");
        if (!MemoryStates.IsKnownState(state))
        {
            // A value the model chose badly: report it inside the result so it can
            // self-correct, like the daemon's 400 does for a caller.
            return (new JsonObject
            {
                ["error"] = $"Unknown state '{Bounded.Text(state, McpLimits.MaxTokenChars)}'. Use one of: {MemoryStates.Active}, {MemoryStates.NeedsReview}, {MemoryStates.Archived}.",
            }, true);
        }

        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = _projects.ProjectIdForScopedWrite(OptionalString(args, "project"), scope);
        var observation = _store.SetObservationState(projectId, id, state);
        if (observation is null) return NotFound(id, scope);

        return (new JsonObject { ["observation"] = ObservationNode(observation, scope) }, false);
    }

    private (JsonObject, bool) SetPinned(JsonElement args)
    {
        var id = RequiredLong(args, "id");
        var pinned = RequiredBool(args, "pinned");
        var scope = MemoryScopes.ResolveScope(OptionalString(args, "scope"), MemoryScopes.Project);
        var projectId = _projects.ProjectIdForScopedWrite(OptionalString(args, "project"), scope);
        var observation = _store.SetObservationPinned(projectId, id, pinned);
        if (observation is null) return NotFound(id, scope);

        return (new JsonObject { ["observation"] = ObservationNode(observation, scope) }, false);
    }

    // =====================================================================
    // Bounded projections
    // =====================================================================

    /// <summary>
    /// Project one observation. Every text field passes through the tool budget, so
    /// even the by-id read cannot return an unbounded body. <c>projectId</c> is
    /// omitted on purpose: the caller's own namespace is carried by <c>scope</c>.
    /// </summary>
    private static JsonObject ObservationNode(Observation observation, string scope)
    {
        var node = new JsonObject
        {
            ["id"] = observation.Id,
            ["scope"] = scope,
            ["state"] = observation.State,
            ["pinned"] = observation.Pinned,
            ["title"] = Bounded.Text(observation.Title, McpLimits.MaxTitleChars),
            ["content"] = Bounded.Text(observation.Content, McpLimits.MaxFieldChars),
            ["createdAtUtc"] = Utc(observation.CreatedAtUtc),
            ["updatedAtUtc"] = Utc(observation.UpdatedAtUtc),
        };
        Put(node, "what", Bounded.Optional(observation.What, McpLimits.MaxFieldChars));
        Put(node, "why", Bounded.Optional(observation.Why, McpLimits.MaxFieldChars));
        Put(node, "where", Bounded.Optional(observation.Where, McpLimits.MaxFieldChars));
        Put(node, "learned", Bounded.Optional(observation.Learned, McpLimits.MaxFieldChars));
        Put(node, "topicKey", Bounded.Optional(observation.TopicKey, McpLimits.MaxTokenChars));
        Put(node, "type", Bounded.Optional(observation.Type, McpLimits.MaxTokenChars));
        return node;
    }

    private static JsonObject ContextItemNode(ContextItem item)
    {
        var node = new JsonObject
        {
            ["id"] = item.Id,
            ["scope"] = item.Scope ?? MemoryScopes.Project,
            ["pinned"] = item.Pinned,
            ["title"] = Bounded.Text(item.Title, McpLimits.MaxTitleChars),
            ["preview"] = Bounded.Text(item.Preview, McpLimits.MaxFieldChars),
        };
        Put(node, "topicKey", Bounded.Optional(item.TopicKey, McpLimits.MaxTokenChars));
        Put(node, "type", Bounded.Optional(item.Type, McpLimits.MaxTokenChars));
        return node;
    }

    private static (JsonObject, bool) NotFound(long id, string scope) => (new JsonObject
    {
        ["error"] = $"Observation #{id} not found in the '{scope}' scope.",
    }, true);

    // =====================================================================
    // Arguments
    // =====================================================================

    private static string RequiredString(JsonElement args, string name)
    {
        var value = OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' is required and must be a non-empty string.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' must be a string.");
        }
        return value.GetString();
    }

    private static long RequiredLong(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' is required and must be an integer.");
        }
        return number;
    }

    private static bool RequiredBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' is required and must be a boolean.");
        }
        return value.GetBoolean();
    }

    /// <summary>An optional boolean argument; null when absent, so the caller picks the default.</summary>
    private static bool? OptionalBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' must be a boolean.");
        }
        return value.GetBoolean();
    }

    /// <summary>
    /// A caller-supplied list size, clamped into the tool budget. Clamping (rather
    /// than rejecting) matches the store, whose own caller-supplied limits are
    /// clamped too — the hard cap is the guarantee, and it holds either way.
    /// </summary>
    private static int Limit(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new McpInvalidParamsException($"Invalid params: '{name}' must be an integer.");
        }
        return (int)Math.Clamp(number, 1, McpLimits.MaxItems);
    }

    /// <summary>Set a string field, omitting it when absent (saves tokens on the wire).</summary>
    private static void Put(JsonObject node, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) node[name] = value;
    }

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O");
}
