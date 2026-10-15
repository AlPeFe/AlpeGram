using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HanniGram.Mcp;

/// <summary>
/// The single JSON configuration of the protocol channel.
/// <para>
/// <c>WriteIndented</c> stays false because the stdio transport is
/// newline-delimited: a message MUST NOT contain an embedded newline, so the
/// entire message has to serialize onto one line. The encoder is the default one,
/// which escapes every non-ASCII character — stdout therefore stays pure ASCII and
/// cannot be mangled by a Windows console code page.
/// </para>
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>Serialize a message onto exactly one line (no trailing newline).</summary>
    public static string Line(JsonNode node) => node.ToJsonString(Options);

    /// <summary>
    /// Detach a parsed arguments object from its document, so the invoker can hold
    /// it after the owning <see cref="JsonDocument"/> is disposed.
    /// </summary>
    public static JsonElement ToDetachedElement(JsonNode? node)
    {
        var json = node?.ToJsonString(Options) ?? "{}";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
