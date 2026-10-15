using System.Text.Json;
using System.Text.Json.Nodes;
using HanniGram.Core.Store;

namespace HanniGram.Mcp;

/// <summary>
/// The stdio protocol loop: newline-delimited JSON-RPC 2.0 over stdin/stdout.
/// <para>
/// <b>stdout carries protocol messages only.</b> Any other byte written to stdout —
/// a stray <c>Console.WriteLine</c>, a warning, a banner — desynchronizes the
/// client's framing and breaks the session. Every diagnostic therefore goes to the
/// stderr writer, which the transport spec explicitly allows.
/// </para>
/// <para>
/// The loop is tear-off testable: <see cref="Handle"/> takes one input line and
/// returns the one output line (or null for a message that must not be answered),
/// so the wire behaviour can be asserted without launching a process.
/// </para>
/// </summary>
public sealed class McpSession
{
    private static readonly string[] HandledMethods = { "initialize", "ping", "tools/list", "tools/call" };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly MemoryToolInvoker _tools;
    private readonly McpProjectResolver _projects;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly string _dbPath;

    public McpSession(HanniGramStore store, McpOptions options, TextReader input, TextWriter output, TextWriter log)
    {
        _projects = new McpProjectResolver(store, options.EnvCwd, options.CurrentDirectory);
        _tools = new MemoryToolInvoker(store, _projects);
        _input = input;
        _output = output;
        _log = log;
        _serverName = options.ServerName;
        _serverVersion = options.ServerVersion;
        _dbPath = options.DbPath;
    }

    /// <summary>
    /// Read messages until stdin reaches EOF, answering each one. Returns when the
    /// client closes the stream, which is how the stdio transport shuts a server
    /// down.
    /// </summary>
    public void Run()
    {
        _log.WriteLine($"{_serverName}: stdio server ready (protocol {McpProtocol.DefaultVersion}, db {_dbPath}, cwd {_projects.Cwd})");

        string? line;
        while ((line = _input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = Handle(line);
            if (response is null) continue;
            WriteMessage(response);
        }

        _log.WriteLine($"{_serverName}: stdin closed, shutting down");
    }

    /// <summary>
    /// Handle one newline-delimited message. Returns the response line, or null
    /// when the message must not be answered (a notification, or a malformed
    /// message that carries no id, as the JSON-RPC spec requires).
    /// </summary>
    public string? Handle(string line)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            // A parse error has no recoverable id, so the response carries a null id.
            _log.WriteLine($"{_serverName}: parse error: {ex.Message}");
            return ErrorLine(null, JsonRpcErrors.ParseError, "Parse error");
        }

        if (root is not JsonObject request)
        {
            _log.WriteLine($"{_serverName}: invalid request: the message is not a JSON object");
            return ErrorLine(null, JsonRpcErrors.InvalidRequest, "Invalid Request");
        }

        var id = request["id"];
        var isNotification = id is null;
        var method = AsString(request["method"]);

        if (method is null)
        {
            _log.WriteLine($"{_serverName}: invalid request: 'method' is missing or not a string");
            return isNotification
                ? null
                : ErrorLine(id, JsonRpcErrors.InvalidRequest, "Invalid Request: 'method' must be a string");
        }

        // Notifications are fire-and-forget: the spec forbids answering one.
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            _log.WriteLine($"{_serverName}: notification {method}");
            return null;
        }

        try
        {
            if (Array.IndexOf(HandledMethods, method) < 0)
            {
                _log.WriteLine($"{_serverName}: unknown method: {method}");
                return isNotification
                    ? null
                    : ErrorLine(id, JsonRpcErrors.MethodNotFound, $"Method not found: {method}");
            }

            var result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => CallTool(request["params"] as JsonObject),
                _ => new JsonObject(),
            };

            return isNotification ? null : ResultLine(id, result);
        }
        catch (McpInvalidParamsException ex)
        {
            _log.WriteLine($"{_serverName}: invalid params for {method}: {ex.Message}");
            return isNotification ? null : ErrorLine(id, JsonRpcErrors.InvalidParams, ex.Message);
        }
        catch (Exception ex)
        {
            // The message goes to stderr; the client gets a generic error so an
            // internal path or a SQL string never leaks into the protocol channel.
            _log.WriteLine($"{_serverName}: internal error handling {method}: {ex}");
            return isNotification ? null : ErrorLine(id, JsonRpcErrors.InternalError, "Internal error");
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters is null ? null : AsString(parameters["protocolVersion"]);
        var negotiated = McpProtocol.Negotiate(requested);
        var clientName = (parameters?["clientInfo"] as JsonObject) is { } clientInfo
            ? AsString(clientInfo["name"]) ?? "(unnamed)"
            : "(none)";
        _log.WriteLine(
            $"{_serverName}: initialize client={clientName} requested={requested ?? "(none)"} negotiated={negotiated}");

        // No `instructions`: this server is opt-in and must not push prose into a
        // model's context. Capabilities declare tools and nothing else.
        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _serverName,
                ["version"] = _serverVersion,
            },
        };
    }

    private static JsonObject ListTools() => new() { ["tools"] = MemoryToolCatalog.BuildList() };

    private JsonObject CallTool(JsonObject? parameters)
    {
        if (parameters is null)
        {
            throw new McpInvalidParamsException("Invalid params: tools/call requires a params object.");
        }

        var name = AsString(parameters["name"])
            ?? throw new McpInvalidParamsException("Invalid params: tools/call requires a string 'name'.");
        if (MemoryToolCatalog.Find(name) is null)
        {
            throw new McpInvalidParamsException($"Unknown tool: {name}");
        }

        // An absent `arguments` is an empty object: a tool whose params are all
        // optional is callable without one.
        var arguments = McpJson.ToDetachedElement(parameters["arguments"]);
        var (result, isError) = _tools.Call(name, arguments);

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = McpJson.Line(result),
                },
            },
            ["isError"] = isError,
        };
    }

    /// <summary>Write one message as a single line. Nothing else ever reaches stdout.</summary>
    private void WriteMessage(string message)
    {
        _output.Write(message);
        _output.Write('\n');
        _output.Flush();
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string ResultLine(JsonNode? id, JsonNode result) => McpJson.Line(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        // The id node belongs to the request tree, so it is cloned: a JsonNode can
        // only ever have one parent.
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    });

    private static string ErrorLine(JsonNode? id, int code, string message) => McpJson.Line(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    });
}
