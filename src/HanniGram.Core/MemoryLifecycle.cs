namespace HanniGram.Core;

/// <summary>
/// The lifecycle state of an observation. Memory that only grows is worse than no
/// memory: you cannot tell current truth from stale truth. <c>active</c> is the
/// default and the only state a search or a context read returns by default;
/// <c>needs_review</c> is quarantined to the review surface; <c>archived</c> is
/// the safe "forget" (the row survives) and is excluded unless explicitly asked.
/// </summary>
public static class MemoryStates
{
    /// <summary>Current truth: the default state and the default read filter.</summary>
    public const string Active = "active";

    /// <summary>Suspect memory the model or the user flagged for a look.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Forgotten but retained; never returned by a default query.</summary>
    public const string Archived = "archived";

    /// <summary>Read selector: every state, including archived.</summary>
    public const string All = "all";

    /// <summary>An unknown or absent state resolves to this on a read.</summary>
    public const string Default = Active;

    /// <summary>True for a concrete stored state (not the <c>all</c> selector).</summary>
    public static bool IsKnownState(string? state)
    {
        var value = state?.Trim().ToLowerInvariant();
        return value == Active || value == NeedsReview || value == Archived;
    }

    /// <summary>True when the token is the <c>all</c> selector (case-insensitive).</summary>
    public static bool IsAll(string? state) =>
        string.Equals(state?.Trim(), All, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Interpret a caller-supplied state token for a read. Recognition is
    /// case-insensitive; anything unrecognised falls back to
    /// <paramref name="fallback"/> so an arbitrary string never reaches SQL.
    /// </summary>
    public static string Resolve(string? state, string fallback = Default)
    {
        var value = state?.Trim().ToLowerInvariant();
        return IsKnownState(value) ? value! : fallback;
    }
}
