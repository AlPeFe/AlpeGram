namespace HanniGram.Mcp;

/// <summary>
/// The MCP protocol surface this server speaks and the JSON-RPC error codes it
/// returns.
/// <para>
/// The revision declared here is the newest <b>handshake</b>-based MCP revision.
/// The 2026-07-28 revision dropped the <c>initialize</c> handshake in favour of
/// per-request <c>_meta</c> metadata, and this server deliberately implements the
/// handshake model (it is the one every broadly-deployed client still speaks, and
/// the one the tool surface is specified against). Declaring a modern revision
/// while serving a handshake would be a lie, so it declares the last revision
/// whose shape it actually implements.
/// </para>
/// <para>
/// A dual-era client probes with <c>server/discover</c>, gets a
/// <c>Method not found</c> (a non-modern error), and falls back to
/// <c>initialize</c> — which is exactly the documented outcome for a modern client
/// against a legacy-era server.
/// </para>
/// </summary>
public static class McpProtocol
{
    /// <summary>The revision the server declares when it cannot accept the client's.</summary>
    public const string DefaultVersion = "2025-11-25";

    /// <summary>
    /// Every handshake revision this server serves. A client that asks for one of
    /// these gets it echoed back (the negotiation rule); anything newer or unknown
    /// is answered with <see cref="DefaultVersion"/> rather than refused, so a
    /// client on a different revision is tolerated instead of broken.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedVersions = new[]
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        "2025-11-25",
    };

    /// <summary>
    /// The protocol version both ends will speak. Echoes the client's requested
    /// version when it is one we support, otherwise falls back to
    /// <see cref="DefaultVersion"/> (never an error: an unknown version must not
    /// stop a client from connecting).
    /// </summary>
    public static string Negotiate(string? requested)
    {
        var value = requested?.Trim();
        if (string.IsNullOrEmpty(value)) return DefaultVersion;
        foreach (var version in SupportedVersions)
        {
            if (string.Equals(version, value, StringComparison.Ordinal)) return version;
        }
        return DefaultVersion;
    }
}

/// <summary>The JSON-RPC 2.0 error codes used by the dispatcher.</summary>
public static class JsonRpcErrors
{
    /// <summary>The message was not valid JSON.</summary>
    public const int ParseError = -32700;
    /// <summary>The message was JSON but not a valid JSON-RPC request object.</summary>
    public const int InvalidRequest = -32600;
    /// <summary>The method is not implemented by this server.</summary>
    public const int MethodNotFound = -32601;
    /// <summary>The params are missing, mistyped, or name an unknown tool.</summary>
    public const int InvalidParams = -32602;
    /// <summary>The server failed while serving a well-formed request.</summary>
    public const int InternalError = -32603;
}
