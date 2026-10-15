using System.Text.Json.Nodes;
using Xunit;

namespace HanniGram.Mcp.Tests;

/// <summary>
/// The JSON-RPC 2.0 and MCP handshake behaviour of the stdio server: version
/// negotiation, method dispatch, error codes, notification silence, and the
/// framing rules that keep stdout a protocol channel and nothing else.
/// </summary>
public sealed class ProtocolTests
{
    [Fact]
    public void Initialize_EchoesASupportedClientVersion()
    {
        using var harness = new McpHarness();

        var response = harness.Send(Messages.Initialize(1, "2025-06-18"))!;
        var result = response["result"]!.AsObject();

        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("hannigram", result["serverInfo"]!.AsObject()["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!.AsObject()["tools"]);
    }

    [Fact]
    public void Initialize_ToleratesAnUnknownClientVersion()
    {
        using var harness = new McpHarness();

        // A client asking for a version this server does not serve (a future one,
        // or nonsense) is answered with the declared version instead of an error:
        // the handshake must not be what stops a client from connecting.
        var future = harness.Send(Messages.Initialize(1, "1900-01-01"))!;
        Assert.Equal(McpProtocol.DefaultVersion, future["result"]!.AsObject()["protocolVersion"]!.GetValue<string>());
        Assert.Null(future["error"]);

        var missing = harness.Send("""{"jsonrpc":"2.0","id":2,"method":"initialize"}""")!;
        Assert.Equal(McpProtocol.DefaultVersion, missing["result"]!.AsObject()["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_DeclaresNoInstructions()
    {
        using var harness = new McpHarness();

        var result = harness.Send(Messages.Initialize(1, "2025-11-25"))!.AsObject()["result"]!.AsObject();

        // This server is opt-in and must push nothing into a model's context, so it
        // never sends the optional `instructions` field.
        Assert.False(result.ContainsKey("instructions"));
    }

    [Fact]
    public void InitializedNotification_IsNeverAnswered()
    {
        using var harness = new McpHarness();

        Assert.Null(harness.Send(Messages.InitializedNotification()));
        Assert.Contains("notifications/initialized", harness.Stderr);
    }

    [Fact]
    public void AnUnknownNotification_IsNeverAnswered()
    {
        using var harness = new McpHarness();

        Assert.Null(harness.Send(Messages.UnknownNotification("notifications/whatever")));
    }

    [Fact]
    public void ANonObjectMessage_IsAnInvalidRequest()
    {
        using var harness = new McpHarness();

        var response = harness.Send("[1,2,3]")!;

        Assert.Equal(JsonRpcErrors.InvalidRequest, Wire.ErrorCode(response));
        Assert.Null(response["id"]);
    }

    [Fact]
    public void MalformedJson_IsAParseErrorWithANullId()
    {
        using var harness = new McpHarness();

        var response = harness.Send("{this is not json")!;

        Assert.Equal(JsonRpcErrors.ParseError, Wire.ErrorCode(response));
        Assert.Null(response["id"]);
        Assert.Contains("parse error", harness.Stderr);
    }

    [Fact]
    public void ARequestWithoutAMethod_IsAnInvalidRequest()
    {
        using var harness = new McpHarness();

        var response = harness.Send("""{"jsonrpc":"2.0","id":7}""")!;

        Assert.Equal(JsonRpcErrors.InvalidRequest, Wire.ErrorCode(response));
        Assert.Equal(7, Wire.RequiredId(response));
    }

    [Fact]
    public void UnknownMethod_IsMethodNotFound()
    {
        using var harness = new McpHarness();

        var response = harness.Send(Messages.UnknownMethod(4, "does/not/exist"))!;

        Assert.Equal(JsonRpcErrors.MethodNotFound, Wire.ErrorCode(response));
        Assert.Equal(4, Wire.RequiredId(response));
        Assert.Contains("does/not/exist", Wire.ErrorMessage(response));
        Assert.Contains("unknown method: does/not/exist", harness.Stderr);
    }

    [Fact]
    public void Ping_ReturnsAnEmptyResult()
    {
        using var harness = new McpHarness();

        var response = harness.Send(Messages.Ping(9))!;

        Assert.Equal(9, Wire.RequiredId(response));
        Assert.Empty(response["result"]!.AsObject());
    }

    [Fact]
    public void NonStringMethod_IsAnInvalidRequest()
    {
        using var harness = new McpHarness();

        var response = harness.Send("""{"jsonrpc":"2.0","id":1,"method":42}""")!;

        Assert.Equal(JsonRpcErrors.InvalidRequest, Wire.ErrorCode(response));
    }

    [Fact]
    public void ToolsCall_WithUnknownTool_IsInvalidParams()
    {
        using var harness = new McpHarness();

        var response = harness.CallTool(1, "mem_nope", "{}")!;

        Assert.Equal(JsonRpcErrors.InvalidParams, Wire.ErrorCode(response));
        Assert.Contains("Unknown tool: mem_nope", Wire.ErrorMessage(response));
    }

    [Fact]
    public void ToolsCall_WithAMissingRequiredArgument_IsInvalidParams()
    {
        using var harness = new McpHarness();

        // mem_save without its required title.
        var response = harness.CallTool(1, "mem_save", """{"content":"no title"}""")!;

        Assert.Equal(JsonRpcErrors.InvalidParams, Wire.ErrorCode(response));
        Assert.Contains("'title'", Wire.ErrorMessage(response));
    }

    [Fact]
    public void ToolsCall_WithAMistypedArgument_IsInvalidParams()
    {
        using var harness = new McpHarness();

        var response = harness.CallTool(1, "mem_get_observation", """{"id":"one"}""")!;

        Assert.Equal(JsonRpcErrors.InvalidParams, Wire.ErrorCode(response));
        Assert.Contains("'id'", Wire.ErrorMessage(response));
    }

    [Fact]
    public void ToolsCall_WithoutParams_IsInvalidParams()
    {
        using var harness = new McpHarness();

        var response = harness.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""")!;

        Assert.Equal(JsonRpcErrors.InvalidParams, Wire.ErrorCode(response));
    }

    [Fact]
    public void ToolsCall_WithANonObjectArguments_IsInvalidParams()
    {
        using var harness = new McpHarness();

        var response = harness.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mem_search","arguments":"oops"}}""")!;

        Assert.Equal(JsonRpcErrors.InvalidParams, Wire.ErrorCode(response));
    }

    [Fact]
    public void ToolsCall_WithNoArgumentsObject_IsStillCallable()
    {
        using var harness = new McpHarness();

        // mem_current_project has no required arguments, so `arguments` may be
        // omitted entirely instead of being sent as {}.
        var response = harness.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mem_current_project"}}""")!;

        Assert.False(Wire.IsError(response));
        Assert.NotNull(Wire.Payload(response)["project"]);
    }

    [Fact]
    public void StdoutCarriesOnlyProtocolMessages_AndDiagnosticsGoToStderr()
    {
        using var harness = new McpHarness();
        var stdin = string.Join('\n', new[]
        {
            Messages.Initialize(1, "2025-11-25"),
            Messages.InitializedNotification(),
            Messages.ToolsList(2),
            Messages.Call(3, "mem_save", """{"title":"Stdout purity","content":"body","project":"alpha"}"""),
            "{not json",
            Messages.UnknownMethod(4, "does/not/exist"),
        });

        harness.Run(stdin);

        // stdout: one JSON-RPC message per line, nothing else. The notification
        // produced no line, so there are exactly five responses.
        var lines = harness.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        var ids = new List<long?>();
        foreach (var line in lines)
        {
            var message = (JsonObject)JsonNode.Parse(line)!;
            Assert.Equal("2.0", message["jsonrpc"]!.GetValue<string>());
            // A null id is the parse-error response, which has no recoverable id.
            ids.Add(message["id"] is null ? null : message["id"]!.GetValue<long>());
        }
        Assert.Equal(new long?[] { 1, 2, 3, null, 4 }, ids.ToArray());

        // No diagnostic prose ever reached the protocol channel.
        Assert.DoesNotContain("stdio server ready", harness.Stdout);
        Assert.DoesNotContain("initialize client=", harness.Stdout);
        Assert.DoesNotContain("parse error", harness.Stdout);

        // The diagnostics did happen, on stderr.
        Assert.Contains("stdio server ready", harness.Stderr);
        Assert.Contains("initialize client=", harness.Stderr);
        Assert.Contains("parse error", harness.Stderr);
        Assert.Contains("stdin closed", harness.Stderr);
    }

    [Fact]
    public void Framing_WritesOneMessagePerLine_WithNoEmbeddedNewlines()
    {
        using var harness = new McpHarness();
        harness.Run(string.Join('\n', new[]
        {
            Messages.Initialize(1, "2025-11-25"),
            Messages.ToolsList(2),
        }));

        var lines = harness.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Two requests, two lines. An indented (pretty-printed) writer would break
        // the count and, worse, put raw newlines inside a message.
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain("  ", line);
            Assert.EndsWith("}", line);
        }
        Assert.Contains("\"tools\":", lines[1]);
    }

    [Fact]
    public void TheStreamIsAnsweredPerLine_NotPerChunk()
    {
        using var harness = new McpHarness();
        var builder = new System.Text.StringBuilder();
        for (long id = 1; id <= 25; id++) builder.Append(Messages.Ping(id)).Append('\n');

        harness.Run(builder.ToString());

        var lines = harness.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(25, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(i + 1, ((JsonObject)JsonNode.Parse(lines[i])!)["id"]!.GetValue<long>());
        }
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2026-07-28", "2025-11-25")]
    [InlineData(" 2025-06-18 ", "2025-06-18")]
    [InlineData("nonsense", "2025-11-25")]
    [InlineData("", "2025-11-25")]
    public void Negotiate_EchoesSupportedVersions_AndFallsBackOtherwise(string requested, string expected)
    {
        Assert.Equal(expected, McpProtocol.Negotiate(requested));
    }

    [Fact]
    public void Negotiate_WithNoRequestedVersion_UsesTheDeclaredOne()
    {
        Assert.Equal(McpProtocol.DefaultVersion, McpProtocol.Negotiate(null));
        Assert.Contains(McpProtocol.DefaultVersion, McpProtocol.SupportedVersions);
    }
}
