namespace HanniGram.Core;

/// <summary>
/// The bounded set of outcomes a promotion can report. The set is closed on
/// purpose: a caller switches on these tokens instead of parsing prose, and a
/// refusal is a first-class outcome rather than a silent success.
/// <para>
/// <see cref="Refused"/> is returned (and nothing is written) when the source row
/// is <c>archived</c> and the caller did not opt in: promoting retired content by
/// default used to overwrite a live global row and leave it active, which turned
/// stale memory into current truth.
/// </para>
/// </summary>
public static class PromotionOutcomes
{
    /// <summary>No collision: the source row was moved into the global namespace (same id).</summary>
    public const string Moved = "moved";

    /// <summary>A global row already owned the topic key: the source content overwrote it in place.</summary>
    public const string Merged = "merged";

    /// <summary>The source row is archived and the caller did not opt in; nothing was written.</summary>
    public const string Refused = "refused";

    /// <summary>Machine-readable reason carried by a <see cref="Refused"/> outcome.</summary>
    public const string ReasonSourceArchived = "source_archived";
}
