using System.Text.Json.Nodes;
using HanniGram.Core.Store;
using Microsoft.Data.Sqlite;

namespace HanniGram.Mcp.Tests;

/// <summary>
/// Drives an <see cref="McpSession"/> over in-memory streams against a temp
/// database, so a test can assert the exact wire behaviour — framing, stdout
/// purity, stderr diagnostics, error codes, bounded payloads — without launching a
/// process. The default store path is the user's real database, so a harness always
/// passes an explicit temp path.
/// </summary>
public sealed class McpHarness : IDisposable
{
    private readonly string _directory;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public McpHarness()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hannigram-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DbPath = Path.Combine(_directory, "hannigram.db");
        Store = new HanniGramStore(DbPath);
        Options = new McpOptions
        {
            DbPath = DbPath,
            // A temp directory is not a git repo, so ProjectResolver falls back to
            // the directory name: deterministic and never the user's project.
            EnvCwd = _directory,
            CurrentDirectory = _directory,
        };
    }

    public string DbPath { get; }
    public McpOptions Options { get; }
    public HanniGramStore Store { get; }

    /// <summary>Everything the server wrote to the protocol channel.</summary>
    public string Stdout => _stdout.ToString();

    /// <summary>Everything the server wrote to the diagnostic channel.</summary>
    public string Stderr => _stderr.ToString();

    /// <summary>A fresh session over this harness's streams and store.</summary>
    public McpSession NewSession(string stdin = "") => new(Store, Options, new StringReader(stdin), _stdout, _stderr);

    /// <summary>Run the full loop over a canned stdin script (exercises framing, not just Handle).</summary>
    public void Run(string stdin) => NewSession(stdin).Run();

    /// <summary>Send one request and parse the single response line (null when nothing is answered).</summary>
    public JsonObject? Send(string request)
    {
        var line = NewSession().Handle(request);
        return line is null ? null : (JsonObject)JsonNode.Parse(line)!;
    }

    /// <summary>Call a tool and return the whole JSON-RPC response object.</summary>
    public JsonObject? CallTool(long id, string tool, string argumentsJson) =>
        Send(Messages.Call(id, tool, argumentsJson));

    /// <summary>Fabricate fixtures the tool surface cannot create (legacy/duplicate rows).</summary>
    public void ExecuteSql(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Store.Dispose();
        // Release pooled handles so the WAL/SHM side files can be removed.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless and never part of the repo.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>JSON-RPC request builders, so a test reads as the conversation it sends.</summary>
public static class Messages
{
    // Raw strings here are NOT interpolated on purpose: a JSON envelope ends in
    // runs of closing braces ({"a":{"b":1}}), and an interpolated raw literal
    // rejects a brace run that reaches its `$` count. The tokens are substituted
    // instead, so the request stays visible as the exact bytes on the wire.
    public static string Initialize(long id, string requestedVersion) =>
        """{"jsonrpc":"2.0","id":__ID__,"method":"initialize","params":{"protocolVersion":"__VERSION__","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
            .Replace("__ID__", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__VERSION__", requestedVersion, StringComparison.Ordinal);

    public static string InitializedNotification() =>
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    public static string ToolsList(long id) =>
        """{"jsonrpc":"2.0","id":__ID__,"method":"tools/list"}"""
            .Replace("__ID__", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static string Call(long id, string tool, string argumentsJson) =>
        """{"jsonrpc":"2.0","id":__ID__,"method":"tools/call","params":{"name":"__TOOL__","arguments":__ARGS__}}"""
            .Replace("__ID__", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__TOOL__", tool, StringComparison.Ordinal)
            .Replace("__ARGS__", argumentsJson, StringComparison.Ordinal);

    public static string Ping(long id) =>
        """{"jsonrpc":"2.0","id":__ID__,"method":"ping"}"""
            .Replace("__ID__", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static string UnknownMethod(long id, string method) =>
        """{"jsonrpc":"2.0","id":__ID__,"method":"__METHOD__"}"""
            .Replace("__ID__", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__METHOD__", method, StringComparison.Ordinal);

    public static string UnknownNotification(string method) =>
        """{"jsonrpc":"2.0","method":"__METHOD__"}"""
            .Replace("__METHOD__", method, StringComparison.Ordinal);
}

/// <summary>Response readers, so the assertions stay about behaviour.</summary>
public static class Wire
{
    /// <summary>The text block of a tool result: parse it back into JSON.</summary>
    public static JsonObject Payload(JsonObject response) =>
        (JsonObject)JsonNode.Parse(
            response["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>())!;

    public static bool IsError(JsonObject response) =>
        response["result"]!.AsObject()["isError"]!.GetValue<bool>();

    public static int ErrorCode(JsonObject response) =>
        response["error"]!.AsObject()["code"]!.GetValue<int>();

    public static string ErrorMessage(JsonObject response) =>
        response["error"]!.AsObject()["message"]!.GetValue<string>();

    public static int RequiredId(JsonObject response) => (int)response["id"]!.GetValue<long>();

    /// <summary>The single response line's JSON object, asserting there was one.</summary>
    public static JsonObject Single(JsonObject? response) => response!;
}
