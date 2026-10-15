using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace HanniGram.Mcp.Tests;

/// <summary>
/// The token budget of every tool result. The store already bounds the surfaces it
/// owns, but a by-id read, a whole timeline and a caller-supplied limit all reach a
/// tool result, so the tool layer adds its own hard caps. These tests assert the
/// caps hold even when a caller asks for more and when the stored text is huge.
/// </summary>
public sealed class BoundedResultTests
{
    [Fact]
    public void GetObservation_TruncatesAnOversizedBody()
    {
        using var harness = new McpHarness();
        var body = LongBody();
        Assert.True(body.Length > McpLimits.MaxFieldChars, "the fixture must exceed the tool budget");

        var saveArgs = new JsonObject { ["title"] = "Oversized body", ["content"] = body, ["project"] = "alpha" }.ToJsonString();
        var saved = Wire.Payload(harness.CallTool(1, "mem_save", saveArgs)!);
        var id = saved["observation"]!.AsObject()["id"]!.GetValue<long>();

        // The save echoes the row, so its content is already capped.
        var savedContent = saved["observation"]!.AsObject()["content"]!.GetValue<string>();
        Assert.Equal(McpLimits.MaxFieldChars + 1, savedContent.Length);
        Assert.EndsWith("\u2026", savedContent);

        // And so is the by-id read: a whole body can never reach a model.
        var readArgs = new JsonObject { ["id"] = id, ["project"] = "alpha" }.ToJsonString();
        var read = Wire.Payload(harness.CallTool(2, "mem_get_observation", readArgs)!)["observation"]!.AsObject();
        var readContent = read["content"]!.GetValue<string>();
        Assert.True(readContent.Length <= McpLimits.MaxFieldChars + 1);
        Assert.EndsWith("\u2026", readContent);

        // The search preview is bounded by the same budget.
        var found = Wire.Payload(harness.CallTool(3, "mem_search",
            """{"query":"quartzlongmarker","scope":"project","project":"alpha"}""")!);
        Assert.Equal(1, found["count"]!.GetValue<int>());
        var hitContent = found["hits"]!.AsArray()[0]!.AsObject()["content"]!.GetValue<string>();
        Assert.True(hitContent.Length <= McpLimits.MaxFieldChars + 1);
    }

    [Fact]
    public void Search_ClampsAnOversizedLimitToTheHardCap()
    {
        using var harness = new McpHarness();
        var project = harness.Store.GetOrCreateProject("alpha");
        // Fabricated directly: the tool surface cannot create this many rows as
        // cheaply, and the cap — not the fixture — is what is under test.
        InsertObservations(harness, project.Id, McpLimits.MaxItems + 10, "clampsearchmarker", "active");

        var payload = Wire.Payload(harness.CallTool(1, "mem_search",
            """{"query":"clampsearchmarker","limit":1000,"scope":"project","project":"alpha"}""")!);

        // The caller asked for 1000; the tool returns at most the budget.
        Assert.Equal(McpLimits.MaxItems, payload["count"]!.GetValue<int>());
        Assert.Equal(McpLimits.MaxItems, payload["hits"]!.AsArray().Count);
    }

    [Fact]
    public void Review_ClampsTheListAndNeverReturnsABody()
    {
        using var harness = new McpHarness();
        var project = harness.Store.GetOrCreateProject("alpha");
        InsertObservations(harness, project.Id, McpLimits.MaxItems + 10, "flaggedmarker", "needs_review");

        var payload = Wire.Payload(harness.CallTool(1, "mem_review",
            """{"scope":"project","limit":1000,"project":"alpha"}""")!);

        Assert.Equal(McpLimits.MaxItems, payload["count"]!.GetValue<int>());
        var items = payload["items"]!.AsArray();
        Assert.Equal(McpLimits.MaxItems, items.Count);
        foreach (var item in items)
        {
            var node = item!.AsObject();
            // A review listing is previews only: it must never carry a body.
            Assert.False(node.ContainsKey("content"));
            Assert.True(node["preview"]!.GetValue<string>().Length <= McpLimits.MaxFieldChars);
        }
    }

    [Fact]
    public void Context_StaysWithinTheInjectionBudget_AndCarriesNoBodies()
    {
        using var harness = new McpHarness();
        for (var i = 0; i < McpLimits.MaxItems; i++)
        {
            harness.CallTool(1 + i, "mem_save",
                new JsonObject { ["title"] = $"Row {i}", ["content"] = $"body {i}", ["project"] = "alpha" }.ToJsonString());
        }

        var surface = Wire.Payload(harness.CallTool(100, "mem_context", """{"project":"alpha"}""")!);
        var recent = surface["recentProject"]!.AsArray();

        // Fifty rows were saved; the injection surface never returns them all.
        Assert.InRange(recent.Count, 1, McpLimits.MaxItems - 1);
        foreach (var item in recent)
        {
            var node = item!.AsObject();
            Assert.False(node.ContainsKey("content"));
            Assert.True(node["preview"]!.GetValue<string>().Length <= McpLimits.MaxFieldChars);
        }
    }

    /// <summary>A body a little over the tool budget, fronted with a unique marker term.</summary>
    private static string LongBody()
    {
        var builder = new StringBuilder("quartzlongmarker ");
        while (builder.Length <= McpLimits.MaxFieldChars + 500)
        {
            builder.Append("lorem ipsum dolor sit amet consectetur adipiscing elit ");
        }
        return builder.ToString();
    }

    /// <summary>Insert rows straight into the table (the FTS triggers keep the index in sync).</summary>
    private static void InsertObservations(McpHarness harness, long projectId, int count, string marker, string state)
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            harness.ExecuteSql(
                """
                INSERT INTO observations (project_id, title, content, state, created_at_utc, updated_at_utc)
                VALUES ($p, $t, $c, $state, $n, $n);
                """,
                ("$p", projectId),
                ("$t", $"{marker} row {i}"),
                ("$c", $"{marker} body {i} that must stay bounded"),
                ("$state", state),
                ("$n", now.AddSeconds(i).ToString("O")));
        }
    }
}
