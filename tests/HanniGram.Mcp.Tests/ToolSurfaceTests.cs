using System.Text.Json.Nodes;
using HanniGram.Core;
using HanniGram.Core.Store;
using Xunit;

namespace HanniGram.Mcp.Tests;

/// <summary>
/// The advertised tool surface and its mapping onto the real store: each tool is
/// argument validation plus one existing <see cref="HanniGramStore"/> call, and the
/// JSON-RPC envelope around a result is the one the MCP spec describes.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] ExpectedTools =
    {
        "mem_context",
        "mem_current_project",
        "mem_delete",
        "mem_get_observation",
        "mem_pin",
        "mem_promote",
        "mem_review",
        "mem_save",
        "mem_search",
        "mem_session_summary",
        "mem_state",
        "mem_suggest_topic_key",
        "mem_timeline",
        "mem_update",
    };

    [Fact]
    public void ToolsList_AdvertisesTheWholeMemorySurface()
    {
        using var harness = new McpHarness();

        var response = harness.Send(Messages.ToolsList(1))!;
        var tools = response["result"]!.AsObject()["tools"]!.AsArray();

        var names = tools.Select(tool => tool!.AsObject()["name"]!.GetValue<string>()).OrderBy(name => name).ToArray();
        Assert.Equal(ExpectedTools, names);

        foreach (var tool in tools)
        {
            var definition = tool!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(definition["title"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(definition["description"]!.GetValue<string>()));
            Assert.Equal("object", definition["inputSchema"]!.AsObject()["type"]!.GetValue<string>());
            Assert.NotNull(definition["inputSchema"]!.AsObject()["properties"]);
        }
    }

    [Fact]
    public void ToolsList_DescriptionsStayShort()
    {
        using var harness = new McpHarness();

        var tools = harness.Send(Messages.ToolsList(1))!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();

        // The tool list is only read on demand, but this server is opt-in and its
        // text must never grow into a prompt tax: every description stays a sentence.
        foreach (var tool in tools)
        {
            var description = tool!.AsObject()["description"]!.GetValue<string>();
            Assert.True(description.Length <= 300, $"{tool!.AsObject()["name"]}: {description.Length} chars");
        }
    }

    [Fact]
    public void ToolsList_SaveAndSearchDeclareTheirScopeEnums()
    {
        using var harness = new McpHarness();
        var tools = harness.Send(Messages.ToolsList(1))!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();

        var save = Schema(tools, "mem_save");
        Assert.Equal(new[] { "title" }, save["required"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "project", "global" }, Enum(save, "scope"));

        var search = Schema(tools, "mem_search");
        Assert.Equal(new[] { "query" }, search["required"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "project", "global", "all" }, Enum(search, "scope"));

        var state = Schema(tools, "mem_state");
        Assert.Equal(new[] { "active", "needs_review", "archived" }, Enum(state, "state"));
    }

    [Fact]
    public void CurrentProject_UsesTheHannigramCwdWhenNoProjectIsGiven()
    {
        using var harness = new McpHarness();

        var response = harness.CallTool(1, "mem_current_project", "{}")!;
        var project = Wire.Payload(response)["project"]!.AsObject();

        // The temp directory is not a git repository, so the resolver falls back to
        // the directory name — which is what HANNIGRAM_CWD points at here.
        Assert.Equal(Path.GetFileName(harness.Options.EnvCwd), project["name"]!.GetValue<string>());
        Assert.Equal(harness.Options.EnvCwd, project["rootPath"]!.GetValue<string>());
    }

    [Fact]
    public void CurrentProject_HonoursAnExplicitProjectName()
    {
        using var harness = new McpHarness();

        var project = Wire.Payload(harness.CallTool(1, "mem_current_project", """{"project":"explicit-name"}""")!)["project"]!.AsObject();

        Assert.Equal("explicit-name", project["name"]!.GetValue<string>());
        Assert.Null(project["rootPath"]);
    }

    [Fact]
    public void Save_ThenSearch_FindsTheMemoryInTheProjectScope()
    {
        using var harness = new McpHarness();

        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"FTS trigger design","content":"alpenglowsync marker","topicKey":"architecture/fts","type":"adr","project":"alpha"}""")!);
        var observation = saved["observation"]!.AsObject();
        Assert.True(saved["created"]!.GetValue<bool>());
        Assert.Equal("project", observation["scope"]!.GetValue<string>());
        Assert.Equal("architecture/fts", observation["topicKey"]!.GetValue<string>());

        var found = Wire.Payload(harness.CallTool(2, "mem_search",
            """{"query":"alpenglowsync","scope":"project","project":"alpha"}""")!);
        Assert.Equal(1, found["count"]!.GetValue<int>());
        var hit = found["hits"]!.AsArray()[0]!.AsObject();
        Assert.Equal(observation["id"]!.GetValue<long>(), hit["id"]!.GetValue<long>());
        Assert.Equal("project", hit["scope"]!.GetValue<string>());
    }

    [Fact]
    public void Save_WithTopickey_UpsertsAndReturnsAReviewHint()
    {
        using var harness = new McpHarness();

        harness.CallTool(1, "mem_save", """{"title":"v1","content":"first","topicKey":"adr/style","project":"alpha"}""");
        var second = Wire.Payload(harness.CallTool(2, "mem_save",
            """{"title":"v2","content":"second","topicKey":"adr/style","project":"alpha"}""")!);

        Assert.False(second["created"]!.GetValue<bool>());
        Assert.Equal("v2", second["observation"]!.AsObject()["title"]!.GetValue<string>());
        // The compact deterministic review hint is the whole review mechanism on the
        // hot path: it names the collision, never the body.
        Assert.Equal("topic_key", second["review"]!.AsObject()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Save_WithGlobalScope_IsVisibleFromAnotherProjectAndLabelledGlobal()
    {
        using var harness = new McpHarness();

        harness.CallTool(1, "mem_save",
            """{"title":"Console app layout","content":"betatronlayout marker","scope":"global","project":"alpha"}""");

        var fromOtherProject = Wire.Payload(harness.CallTool(2, "mem_search",
            """{"query":"betatronlayout","project":"beta"}""")!);
        // No scope argument: the default is `all`, which is what makes a global
        // heuristic reachable from a project that never taught it.
        Assert.Equal(1, fromOtherProject["count"]!.GetValue<int>());
        Assert.Equal("global", fromOtherProject["hits"]!.AsArray()[0]!.AsObject()["scope"]!.GetValue<string>());

        var projectOnly = Wire.Payload(harness.CallTool(3, "mem_search",
            """{"query":"betatronlayout","scope":"project","project":"beta"}""")!);
        Assert.Equal(0, projectOnly["count"]!.GetValue<int>());
    }

    [Fact]
    public void GetObservation_ReturnsTheRow_AndIsScoped()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Get me","content":"body","what":"w","why":"y","where":"wh","learned":"l","project":"alpha"}""")!);
        var id = saved["observation"]!.AsObject()["id"]!.GetValue<long>();

        var response = harness.CallTool(2, "mem_get_observation", $$"""{"id":{{id}},"project":"alpha"}""")!;
        var observation = Wire.Payload(response)["observation"]!.AsObject();

        Assert.False(Wire.IsError(response));
        Assert.Equal("Get me", observation["title"]!.GetValue<string>());
        Assert.Equal("w", observation["what"]!.GetValue<string>());
        Assert.Equal("l", observation["learned"]!.GetValue<string>());

        // Another project cannot reach it by id.
        var other = harness.CallTool(3, "mem_get_observation", $$"""{"id":{{id}},"project":"beta"}""")!;
        Assert.True(Wire.IsError(other));
        Assert.Contains("not found", Wire.Payload(other)["error"]!.GetValue<string>());
    }

    [Fact]
    public void GetObservation_GlobalScope_DoesNotCreateTheSentinel()
    {
        using var harness = new McpHarness();

        var response = harness.CallTool(1, "mem_get_observation", """{"id":1,"scope":"global","project":"alpha"}""")!;

        Assert.True(Wire.IsError(response));
        // A read must treat an absent global namespace as "no global memory yet",
        // never as an instruction to materialise the sentinel row.
        Assert.Null(harness.Store.GetGlobalProject());
    }

    [Fact]
    public void Update_PreservesCreatedAt_ClearsWithAnEmptyString_AndMovesUpdatedAt()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Before","content":"body","what":"keep me","learned":"clear me","project":"alpha"}""")!);
        var observation = saved["observation"]!.AsObject();
        var id = observation["id"]!.GetValue<long>();
        var createdAt = observation["createdAtUtc"]!.GetValue<string>();

        var updated = Wire.Payload(harness.CallTool(2, "mem_update",
            $$"""{"id":{{id}},"title":"After","learned":"","project":"alpha"}""")!)["observation"]!.AsObject();

        Assert.Equal("After", updated["title"]!.GetValue<string>());
        Assert.Equal("keep me", updated["what"]!.GetValue<string>());
        // An empty string clears; the cleared field is then absent from the payload.
        Assert.Null(updated["learned"]);
        Assert.Equal(createdAt, updated["createdAtUtc"]!.GetValue<string>());
        Assert.True(string.CompareOrdinal(updated["updatedAtUtc"]!.GetValue<string>(), createdAt) >= 0);
    }

    [Fact]
    public void Update_OfAnUnknownId_IsAToolExecutionError()
    {
        using var harness = new McpHarness();

        var response = harness.CallTool(1, "mem_update", """{"id":4242,"title":"nope","project":"alpha"}""")!;

        Assert.True(Wire.IsError(response));
        Assert.Contains("#4242", Wire.Payload(response)["error"]!.GetValue<string>());
    }

    [Fact]
    public void Delete_RemovesTheRow_AndThenReportsItMissing()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Delete me","content":"body","project":"alpha"}""")!);
        var id = saved["observation"]!.AsObject()["id"]!.GetValue<long>();

        var deleted = harness.CallTool(2, "mem_delete", $$"""{"id":{{id}},"project":"alpha"}""")!;
        Assert.False(Wire.IsError(deleted));
        Assert.True(Wire.Payload(deleted)["deleted"]!.GetValue<bool>());

        var again = harness.CallTool(3, "mem_delete", $$"""{"id":{{id}},"project":"alpha"}""")!;
        Assert.True(Wire.IsError(again));

        var search = Wire.Payload(harness.CallTool(4, "mem_search", """{"query":"Delete","scope":"all","project":"alpha"}""")!);
        Assert.Equal(0, search["count"]!.GetValue<int>());
    }

    [Fact]
    public void Timeline_ReturnsEveryRowForATopicKey()
    {
        using var harness = new McpHarness();
        var project = harness.Store.GetOrCreateProject("alpha");
        // Fabricated directly: the tool surface cannot create two rows with the same
        // topic key (the upsert is exactly what prevents that), and the timeline's
        // cap is what has to be asserted here.
        for (var i = 0; i < McpLimits.MaxItems + 10; i++)
        {
            harness.ExecuteSql(
                """
                INSERT INTO observations (project_id, title, content, topic_key, created_at_utc, updated_at_utc)
                VALUES ($p, $t, $c, 'adr/timeline', $n, $n);
                """,
                ("$p", project.Id), ("$t", $"Row {i}"), ("$c", $"body {i}"),
                ("$n", DateTime.UtcNow.AddSeconds(i).ToString("O")));
        }

        var payload = Wire.Payload(harness.CallTool(1, "mem_timeline",
            """{"topicKey":"adr/timeline","limit":1000,"project":"alpha"}""")!);

        Assert.Equal("adr/timeline", payload["topicKey"]!.GetValue<string>());
        // Both the store's by-key read and this tool cap bind the timeline.
        Assert.Equal(McpLimits.MaxItems, payload["count"]!.GetValue<int>());
        Assert.Equal(McpLimits.MaxItems, payload["observations"]!.AsArray().Count);
    }

    [Fact]
    public void SuggestTopicKey_ListsTheExistingKeys()
    {
        using var harness = new McpHarness();
        harness.CallTool(1, "mem_save", """{"title":"a","content":"x","topicKey":"adr/layout","project":"alpha"}""");
        harness.CallTool(2, "mem_save", """{"title":"b","content":"y","topicKey":"adr/colors","project":"alpha"}""");
        harness.CallTool(3, "mem_save", """{"title":"c","content":"z","project":"alpha"}""");

        var payload = Wire.Payload(harness.CallTool(4, "mem_suggest_topic_key", """{"project":"alpha"}""")!);

        Assert.Equal(2, payload["count"]!.GetValue<int>());
        var keys = payload["topicKeys"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "adr/colors", "adr/layout" }, keys);
    }

    [Fact]
    public void SessionSummary_IsIdempotent_AndTheLatestSummaryWins()
    {
        using var harness = new McpHarness();

        harness.CallTool(1, "mem_session_summary",
            """{"summary":"first summary","goal":"g","sessionId":"s-1","project":"alpha"}""");
        var second = Wire.Payload(harness.CallTool(2, "mem_session_summary",
            """{"summary":"second summary","nextSteps":"n","sessionId":"s-1","project":"alpha"}""")!)["session"]!.AsObject();

        Assert.Equal("s-1", second["sessionId"]!.GetValue<string>());
        Assert.Equal("second summary", second["summary"]!.GetValue<string>());
        Assert.Equal("n", second["nextSteps"]!.GetValue<string>());

        var sessions = harness.Store.ListSessions(harness.Store.GetOrCreateProject("alpha").Id, 50);
        var session = Assert.Single(sessions);
        Assert.Equal("second summary", session.Summary);
    }

    [Fact]
    public void SessionSummary_WithoutAnId_CreatesOne()
    {
        using var harness = new McpHarness();

        var session = Wire.Payload(harness.CallTool(1, "mem_session_summary",
            """{"summary":"handoff","project":"alpha"}""")!)["session"]!.AsObject();

        Assert.False(string.IsNullOrWhiteSpace(session["sessionId"]!.GetValue<string>()));
        Assert.Single(harness.Store.ListSessions(harness.Store.GetOrCreateProject("alpha").Id, 50));
    }

    [Fact]
    public void State_ArchivesTheRow_AndDefaultReadsHideIt()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Stale claim","content":"kestrelstale marker","project":"alpha"}""")!)["observation"]!.AsObject();
        var id = saved["id"]!.GetValue<long>();

        var archived = Wire.Payload(harness.CallTool(2, "mem_state",
            $$"""{"id":{{id}},"state":"archived","project":"alpha"}""")!)["observation"]!.AsObject();
        Assert.Equal("archived", archived["state"]!.GetValue<string>());

        var hidden = Wire.Payload(harness.CallTool(3, "mem_search",
            """{"query":"kestrelstale","scope":"project","project":"alpha"}""")!);
        Assert.Equal(0, hidden["count"]!.GetValue<int>());

        var asked = Wire.Payload(harness.CallTool(4, "mem_search",
            """{"query":"kestrelstale","scope":"project","state":"archived","project":"alpha"}""")!);
        Assert.Equal(1, asked["count"]!.GetValue<int>());
    }

    [Fact]
    public void State_WithAnUnknownValue_IsAToolExecutionError()
    {
        using var harness = new McpHarness();
        var id = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"t","content":"c","project":"alpha"}""")!)["observation"]!.AsObject()["id"]!.GetValue<long>();

        var response = harness.CallTool(2, "mem_state", $$"""{"id":{{id}},"state":"forgotten","project":"alpha"}""")!;

        // A value the model picked badly is reported inside the result so it can
        // retry, not as a protocol-level failure.
        Assert.True(Wire.IsError(response));
        Assert.Contains("Unknown state", Wire.Payload(response)["error"]!.GetValue<string>());
    }

    [Fact]
    public void Pin_ThenContext_ReturnsThePinnedGlobalHeuristic()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"One concern per project","content":"heuristic body","scope":"global","project":"alpha"}""")!)["observation"]!.AsObject();
        var id = saved["id"]!.GetValue<long>();
        harness.CallTool(2, "mem_save", """{"title":"Unpinned heuristic","content":"never injected","scope":"global","project":"alpha"}""");

        harness.CallTool(3, "mem_pin", $$"""{"id":{{id}},"pinned":true,"scope":"global","project":"alpha"}""");
        var surface = Wire.Payload(harness.CallTool(4, "mem_context", """{"project":"beta"}""")!);

        var pinned = surface["pinnedGlobal"]!.AsArray();
        var item = Assert.Single(pinned);
        Assert.Equal("One concern per project", item!.AsObject()["title"]!.GetValue<string>());
        Assert.True(item!.AsObject()["pinned"]!.GetValue<bool>());
        Assert.Equal("global", item!.AsObject()["scope"]!.GetValue<string>());

        var unpinned = harness.CallTool(5, "mem_pin", $$"""{"id":{{id}},"pinned":false,"scope":"global","project":"alpha"}""")!;
        Assert.False(Wire.Payload(unpinned)["observation"]!.AsObject()["pinned"]!.GetValue<bool>());
        Assert.Empty(Wire.Payload(harness.CallTool(6, "mem_context", """{"project":"beta"}""")!)["pinnedGlobal"]!.AsArray());
    }

    [Fact]
    public void Promote_MovesTheRowIntoTheGlobalScope()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Promotable","content":"quartzpromote marker","topicKey":"heuristics/promote","project":"alpha"}""")!)["observation"]!.AsObject();
        var id = saved["id"]!.GetValue<long>();

        var payload = Wire.Payload(harness.CallTool(2, "mem_promote", $$"""{"id":{{id}},"project":"alpha"}""")!);

        Assert.Equal("moved", payload["outcome"]!.GetValue<string>());
        Assert.False(payload["merged"]!.GetValue<bool>());
        Assert.Equal("global", payload["observation"]!.AsObject()["scope"]!.GetValue<string>());
        Assert.Equal(id, payload["observation"]!.AsObject()["id"]!.GetValue<long>());

        var reachable = Wire.Payload(harness.CallTool(3, "mem_search",
            """{"query":"quartzpromote","scope":"all","project":"beta"}""")!);
        Assert.Equal(1, reachable["count"]!.GetValue<int>());
    }

    [Fact]
    public void Promote_ArchivedSource_IsRefusedUnlessAllowArchivedIsSet()
    {
        using var harness = new McpHarness();
        // A live global row owns the key; the project row is stale and archived.
        Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"LIVE-GLOBAL","content":"live global truth","topicKey":"lifecycle/mcp","scope":"global","project":"alpha"}""")!);
        var saved = Wire.Payload(harness.CallTool(2, "mem_save",
            """{"title":"RETIRED-PROJECT","content":"retired stale content","topicKey":"lifecycle/mcp","project":"alpha"}""")!);
        var id = saved["observation"]!.AsObject()["id"]!.GetValue<long>();
        harness.CallTool(3, "mem_state", $$"""{"id":{{id}},"state":"archived","project":"alpha"}""");

        var refused = harness.CallTool(4, "mem_promote", $$"""{"id":{{id}},"project":"alpha"}""")!;
        Assert.True(Wire.IsError(refused));
        var refusedPayload = Wire.Payload(refused);
        Assert.Equal("refused", refusedPayload["outcome"]!.GetValue<string>());
        Assert.Equal("source_archived", refusedPayload["reason"]!.GetValue<string>());

        // The live global row is untouched, and the opt-in lands the content archived.
        var optIn = Wire.Payload(harness.CallTool(5, "mem_promote",
            $$"""{"id":{{id}},"allowArchived":true,"project":"alpha"}""")!);
        Assert.Equal("merged", optIn["outcome"]!.GetValue<string>());
        Assert.Equal("archived", optIn["observation"]!.AsObject()["state"]!.GetValue<string>());
        Assert.Equal("RETIRED-PROJECT", optIn["observation"]!.AsObject()["title"]!.GetValue<string>());
    }

    [Fact]
    public void Review_ReturnsFlaggedRowsWithTruncatedPreviews()
    {
        using var harness = new McpHarness();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save",
            """{"title":"Flagged","content":"needs a look","project":"alpha"}""")!)["observation"]!.AsObject();
        var id = saved["id"]!.GetValue<long>();
        harness.CallTool(2, "mem_state", $$"""{"id":{{id}},"state":"needs_review","project":"alpha"}""");

        var payload = Wire.Payload(harness.CallTool(3, "mem_review", """{"scope":"project","project":"alpha"}""")!);

        Assert.Equal(1, payload["count"]!.GetValue<int>());
        var item = payload["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal("needs_review", item["kind"]!.GetValue<string>());
        Assert.Equal("Flagged", item["title"]!.GetValue<string>());
        Assert.False(item.ContainsKey("content"));
    }

    private static JsonObject Schema(JsonArray tools, string name)
    {
        foreach (var tool in tools)
        {
            var definition = tool!.AsObject();
            if (definition["name"]!.GetValue<string>() == name)
            {
                return definition["inputSchema"]!.AsObject();
            }
        }
        throw new InvalidOperationException($"tool {name} was not advertised");
    }

    private static string[] Enum(JsonObject schema, string property) =>
        schema["properties"]!.AsObject()[property]!.AsObject()["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>()).ToArray();
}
