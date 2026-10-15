using System.Text.Json.Nodes;

namespace HanniGram.Mcp;

/// <summary>
/// One advertised MCP tool. The input schema is kept as a JSON string rather than
/// a live <see cref="JsonNode"/> because a node cannot be attached to two parents,
/// and a list payload is rebuilt on every <c>tools/list</c>.
/// </summary>
public sealed record McpToolDefinition(string Name, string Title, string Description, string InputSchemaJson);

/// <summary>
/// The tool surface: the memory API mirrored onto the existing Core store methods,
/// one tool per operation. Descriptions are deliberately short — this server is
/// opt-in and its tool list is only read when a harness asks for it, so nothing
/// here belongs in an ordinary prompt.
/// </summary>
public static class MemoryToolCatalog
{
    // The shared "which namespace" arguments. They are on every tool that touches
    // an observation, because MCP has no per-call cwd: `project` names the
    // namespace explicitly and otherwise falls back to HANNIGRAM_CWD / the process
    // working directory.
    private const string ProjectProperty =
        """
        "project": {
          "type": "string",
          "description": "Project name. Omit to resolve from HANNIGRAM_CWD, then the process working directory."
        }
        """;

    private const string ScopeWriteProperty =
        """
        "scope": {
          "type": "string",
          "enum": ["project", "global"],
          "description": "Namespace to write: the project itself (default) or the shared cross-project memory."
        }
        """;

    private const string ScopeAnyProperty =
        """
        "scope": {
          "type": "string",
          "enum": ["project", "global", "all"],
          "description": "Namespaces to read: project, global, or both (default). Every hit is labelled with its origin."
        }
        """;

    private const string ScopeByIdProperty =
        """
        "scope": {
          "type": "string",
          "enum": ["project", "global"],
          "description": "Namespace that owns the row: the project itself (default) or the shared cross-project memory."
        }
        """;

    private const string StateProperty =
        """
        "state": {
          "type": "string",
          "enum": ["active", "needs_review", "archived", "all"],
          "description": "Lifecycle filter. Defaults to active; archived is only returned when asked for."
        }
        """;

    public static IReadOnlyList<McpToolDefinition> Tools { get; } = new[]
    {
        new McpToolDefinition(
            "mem_current_project",
            "HanniGram: current project",
            "Confirm the memory namespace this server resolves for the current directory.",
            """
            {
              "type": "object",
              "properties": { %PROJECT% },
              "required": [],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_save",
            "HanniGram: save memory",
            "Save a durable observation. A save with a topicKey updates the existing row for that key instead of duplicating it.",
            """
            {
              "type": "object",
              "properties": {
                "title": { "type": "string", "description": "Short, searchable title." },
                "content": { "type": "string", "description": "The observation body." },
                "what": { "type": "string" },
                "why": { "type": "string" },
                "where": { "type": "string" },
                "learned": { "type": "string" },
                "topicKey": { "type": "string", "description": "Stable key, e.g. 'architecture/auth-model'. Reuse it to update an evolving topic." },
                "type": { "type": "string", "description": "Observation type, e.g. 'adr', 'decision', 'discovery', 'bugfix'." },
                %SCOPE_WRITE%,
                %PROJECT%
              },
              "required": ["title"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_search",
            "HanniGram: search memory",
            "Full-text (FTS5, typo-tolerant) search across the selected namespaces. Returns bounded previews, not full bodies.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Search terms; all terms must match." },
                "limit": { "type": "integer", "description": "Max hits (default 10, hard cap 50)." },
                "type": { "type": "string", "description": "Filter by observation type." },
                "topicKey": { "type": "string", "description": "Restrict to one topic key." },
                %SCOPE_ANY%,
                %STATE%,
                %PROJECT%
              },
              "required": ["query"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_context",
            "HanniGram: bounded context",
            "The bounded context surface: pinned global heuristics plus recent project rows, previews only. The caps are fixed, so this can never become a corpus dump.",
            """
            {
              "type": "object",
              "properties": { %PROJECT% },
              "required": [],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_get_observation",
            "HanniGram: get observation",
            "Read one observation by id. Text fields are truncated to the tool budget.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id." },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_timeline",
            "HanniGram: timeline by topic",
            "Every observation for one topic key, newest first, truncated to the tool budget.",
            """
            {
              "type": "object",
              "properties": {
                "topicKey": { "type": "string" },
                "limit": { "type": "integer", "description": "Max rows (default 20, hard cap 50)." },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["topicKey"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_suggest_topic_key",
            "HanniGram: suggest topic keys",
            "List the topic keys that already exist in the project, to reuse one instead of inventing a near-duplicate.",
            """
            {
              "type": "object",
              "properties": {
                "limit": { "type": "integer", "description": "Max keys (default 20, hard cap 50)." },
                %PROJECT%
              },
              "required": [],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_delete",
            "HanniGram: delete memory",
            "Delete an observation by id. Archive (mem_state) is the non-destructive alternative.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id." },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_session_summary",
            "HanniGram: session summary",
            "Record a session handoff (goal, summary, next steps) for the project so the next session can recover context.",
            """
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string", "description": "What was accomplished." },
                "goal": { "type": "string" },
                "nextSteps": { "type": "string" },
                "sessionId": { "type": "string", "description": "Session id; a new one is created when omitted." },
                %PROJECT%
              },
              "required": ["summary"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_update",
            "HanniGram: update memory",
            "Edit an observation by id. created_at is preserved. An omitted field is left unchanged; an empty string clears it.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id." },
                "title": { "type": "string" },
                "content": { "type": "string" },
                "what": { "type": "string" },
                "why": { "type": "string" },
                "where": { "type": "string" },
                "learned": { "type": "string" },
                "topicKey": { "type": "string" },
                "type": { "type": "string" },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_review",
            "HanniGram: review memory",
            "Bounded review listing: rows flagged needs_review plus near-duplicate candidates, previews only.",
            """
            {
              "type": "object",
              "properties": {
                "limit": { "type": "integer", "description": "Max items (default 20, hard cap 50)." },
                %SCOPE_ANY%,
                %PROJECT%
              },
              "required": [],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_promote",
            "HanniGram: promote memory",
            "Promote a project observation into the shared global scope. When the topic key already exists globally, the promoted content wins and the global row is updated in place.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id in the project namespace." },
                %PROJECT%
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_state",
            "HanniGram: set lifecycle state",
            "Set an observation's lifecycle state. Archive is the safe forget; it is excluded from default reads but kept.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id." },
                "state": {
                  "type": "string",
                  "enum": ["active", "needs_review", "archived"],
                  "description": "The new lifecycle state."
                },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["id", "state"],
              "additionalProperties": false
            }
            """),

        new McpToolDefinition(
            "mem_pin",
            "HanniGram: pin memory",
            "Pin or unpin an observation. Pinned global heuristics are the only global memory eligible for the automatic context surface.",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "description": "Observation id." },
                "pinned": { "type": "boolean", "description": "true to pin, false to unpin." },
                %SCOPE_BY_ID%,
                %PROJECT%
              },
              "required": ["id", "pinned"],
              "additionalProperties": false
            }
            """),
    };

    /// <summary>Find a tool by exact name, or null when the name is unknown.</summary>
    public static McpToolDefinition? Find(string name)
    {
        foreach (var tool in Tools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal)) return tool;
        }
        return null;
    }

    /// <summary>
    /// Build the <c>tools/list</c> payload. Fresh nodes on every call: a
    /// <see cref="JsonNode"/> cannot be attached to two parents, so a cached tree
    /// would throw on the second listing.
    /// </summary>
    public static JsonArray BuildList()
    {
        var array = new JsonArray();
        foreach (var tool in Tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(Expand(tool.InputSchemaJson)),
            });
        }
        return array;
    }

    /// <summary>
    /// Expand the shared property snippets. The schemas are written with %NAME%
    /// placeholders so the namespace/scope/state arguments have one definition
    /// each instead of fifteen copies that can drift apart.
    /// </summary>
    private static string Expand(string schema) => schema
        .Replace("%PROJECT%", ProjectProperty, StringComparison.Ordinal)
        .Replace("%SCOPE_WRITE%", ScopeWriteProperty, StringComparison.Ordinal)
        .Replace("%SCOPE_ANY%", ScopeAnyProperty, StringComparison.Ordinal)
        .Replace("%SCOPE_BY_ID%", ScopeByIdProperty, StringComparison.Ordinal)
        .Replace("%STATE%", StateProperty, StringComparison.Ordinal);
}
