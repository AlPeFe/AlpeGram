namespace HanniGram.Core;

/// <summary>
/// The reserved names and scope tokens for HanniGram memory. Global memory is
/// not a second store: it lives in the same <c>projects</c> and
/// <c>observations</c> tables under a reserved sentinel project name, so it
/// shares the same FTS index and the same upsert-by-topic_key behaviour as a
/// per-project memory.
/// </summary>
public static class MemoryScopes
{
    /// <summary>
    /// The reserved project name that holds global (cross-project) memory. A
    /// real directory or git remote must never resolve to this name; see
    /// <see cref="Store.ProjectResolver"/>.
    /// </summary>
    public const string GlobalProjectName = "_global";

    /// <summary>Scope token: memory owned by a single project.</summary>
    public const string Project = "project";

    /// <summary>Scope token: the global, cross-project namespace.</summary>
    public const string Global = "global";

    /// <summary>Read selector token: the project namespace and the global namespace together.</summary>
    public const string All = "all";

    /// <summary>
    /// Interpret a caller-supplied scope token for a write. Recognition is
    /// case-insensitive; anything unrecognised falls back to
    /// <paramref name="defaultScope"/> so an arbitrary string never reaches the
    /// storage layer as a scope.
    /// </summary>
    public static string ResolveScope(string? scope, string defaultScope)
    {
        var value = scope?.Trim().ToLowerInvariant();
        return value == Project || value == Global ? value : defaultScope;
    }

    /// <summary>
    /// Interpret a scope selector for a read. Returns whether the project
    /// namespace and/or the global namespace should be included. Recognition is
    /// case-insensitive; anything unrecognised falls back to
    /// <paramref name="defaultScope"/> (which must be one of the three tokens,
    /// as every call site passes a constant).
    /// </summary>
    public static (bool IncludeProject, bool IncludeGlobal) Selection(string? scope, string defaultScope)
    {
        var value = scope?.Trim().ToLowerInvariant();
        if (value != Project && value != Global && value != All) value = defaultScope;
        return value switch
        {
            Project => (true, false),
            Global => (false, true),
            _ => (true, true), // All
        };
    }
}
