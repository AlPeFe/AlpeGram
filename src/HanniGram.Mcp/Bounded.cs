namespace HanniGram.Mcp;

/// <summary>
/// The token budget for every MCP tool result. The store already bounds the
/// surfaces it owns (review, automatic context); a tool result adds its own caps so
/// that no tool — including the ones that read a whole row or a whole timeline —
/// can hand a model an unbounded body or an unbounded list. These are the single
/// place to change the MCP budget.
/// </summary>
public static class McpLimits
{
    /// <summary>Hard cap on how many items any tool returns.</summary>
    public const int MaxItems = 50;

    /// <summary>Cap on an echoed title.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>Cap on a single text field (content / what / why / where / learned / summary).</summary>
    public const int MaxFieldChars = 2000;

    /// <summary>Cap on a short identifier echoed back (topic key, type, session id).</summary>
    public const int MaxTokenChars = 200;
}

/// <summary>
/// Truncation helpers. Every string that leaves a tool goes through one of these,
/// so "is this bounded?" has a single answer.
/// </summary>
internal static class Bounded
{
    private const char Ellipsis = '\u2026';

    /// <summary>Collapse whitespace and truncate to <paramref name="max"/> characters.</summary>
    public static string Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..max] + Ellipsis;
    }

    /// <summary>
    /// As <see cref="Text"/>, but keeps an absent/blank value absent so the caller
    /// can omit the field entirely instead of paying for a <c>null</c> token.
    /// </summary>
    public static string? Optional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Text(value, max);

    /// <summary>Cap a list at the tool budget.</summary>
    public static IEnumerable<T> Cap<T>(IEnumerable<T> items, int limit)
    {
        var cap = Math.Clamp(limit, 1, McpLimits.MaxItems);
        return items.Take(cap);
    }
}
