using System.Text;

namespace HanniGram.Core;

/// <summary>
/// Deterministic, server-side near-duplicate detection for memory saves and the
/// review surface. No LLM, no embeddings, no full-corpus scan: each observation
/// becomes a normalized token set and two are compared with the Dice coefficient.
/// <para>
/// Rule (documented once, used everywhere): take the lowercased alphanumeric
/// tokens of length >= 2 across title and content; two observations are
/// near-duplicates when both signatures carry at least
/// <see cref="MinSignatureTokens"/> distinct tokens and their Dice similarity
/// <c>2|A∩B| / (|A|+|B|)</c> is >= <see cref="NearDuplicateThreshold"/>. A
/// signature below the token floor can never match, so a short two-word title
/// cannot collide by chance.
/// </para>
/// </summary>
public static class MemoryDedup
{
    /// <summary>Minimum Dice similarity for two observations to count as near-duplicates.</summary>
    public const double NearDuplicateThreshold = 0.8;

    /// <summary>
    /// How many of the most recently updated rows in a namespace are candidates
    /// for a duplicate check. Bounds the comparison window so a save never scans
    /// the whole corpus.
    /// </summary>
    public const int WindowSize = 50;

    /// <summary>
    /// The smallest signature that is allowed to match. Below this a handful of
    /// common words could push two unrelated notes over the threshold.
    /// </summary>
    public const int MinSignatureTokens = 3;

    /// <summary>Build the dedup signature of an observation from its title and content.</summary>
    public static HashSet<string> Signature(string? title, string? content) =>
        Tokens(string.Join(' ', title ?? string.Empty, content ?? string.Empty));

    /// <summary>
    /// Split text into lowercased alphanumeric tokens of length >= 2. Shorter
    /// fragments ("a", "to") carry no signal and would inflate the intersection.
    /// </summary>
    public static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush(current, set);
            }
        }
        Flush(current, set);
        return set;
    }

    private static void Flush(StringBuilder current, HashSet<string> set)
    {
        if (current.Length >= 2) set.Add(current.ToString());
        current.Clear();
    }

    /// <summary>
    /// Dice coefficient of two token sets: <c>2|A∩B| / (|A|+|B|)</c>. Returns 0
    /// when either signature is below <see cref="MinSignatureTokens"/> (no
    /// match), and 0 for two empty sets.
    /// </summary>
    public static double Dice(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count < MinSignatureTokens || b.Count < MinSignatureTokens) return 0;
        var small = a.Count <= b.Count ? a : b;
        var large = ReferenceEquals(small, a) ? b : a;
        var largeSet = large as HashSet<string> ?? new HashSet<string>(large, StringComparer.Ordinal);
        var intersection = 0;
        foreach (var token in small)
        {
            if (largeSet.Contains(token)) intersection++;
        }
        return 2.0 * intersection / (a.Count + b.Count);
    }
}
