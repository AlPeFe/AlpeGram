using HanniGram.Core;
using HanniGram.Core.Models;
using HanniGram.Core.Store;

namespace HanniGram.Mcp;

/// <summary>
/// Project and scope resolution for a transport that has no per-call cwd. Mirrors
/// the daemon's rules: an explicit <c>project</c> name wins; otherwise the project
/// is resolved from <c>HANNIGRAM_CWD</c> (or the process working directory) through
/// <see cref="ProjectResolver"/>, so a git remote still yields the same project
/// name the daemon and the CLI would produce.
/// <para>
/// The reserved global namespace is reached through the store: a scoped <b>read</b>
/// never materialises the sentinel (absent means "no global memory yet"), and only
/// a scoped <b>write</b> creates it. That preserves the store's read-path invariant.
/// </para>
/// </summary>
public sealed class McpProjectResolver
{
    private readonly HanniGramStore _store;
    private readonly string? _envCwd;
    private readonly string _currentDirectory;

    public McpProjectResolver(HanniGramStore store, string? envCwd, string currentDirectory)
    {
        _store = store;
        _envCwd = string.IsNullOrWhiteSpace(envCwd) ? null : envCwd;
        _currentDirectory = currentDirectory;
    }

    /// <summary>The working directory this process resolves projects against.</summary>
    public string Cwd => _envCwd ?? _currentDirectory;

    /// <summary>Resolve (creating if needed) the caller's project namespace.</summary>
    public Project ResolveProject(string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            // An explicit name has no directory to record: the daemon behaves the
            // same way when only ?project= is supplied.
            return _store.GetOrCreateProject(project.Trim(), null);
        }

        var cwd = Cwd;
        return _store.GetOrCreateProject(ProjectResolver.Resolve(cwd), cwd);
    }

    /// <summary>
    /// Namespace id for a by-id read: <c>global</c> resolves the sentinel
    /// read-only (null when it does not exist yet), <c>project</c> is the caller's
    /// own namespace.
    /// </summary>
    public long? ProjectIdForScopedRead(string? project, string? scope)
    {
        var resolved = MemoryScopes.ResolveScope(scope, MemoryScopes.Project);
        return resolved == MemoryScopes.Global ? _store.GetGlobalProject()?.Id : ResolveProject(project).Id;
    }

    /// <summary>
    /// Namespace id for a by-id mutation: <c>global</c> is the shared sentinel,
    /// created on demand because the row being mutated has to live there.
    /// </summary>
    public long ProjectIdForScopedWrite(string? project, string? scope)
    {
        var resolved = MemoryScopes.ResolveScope(scope, MemoryScopes.Project);
        return resolved == MemoryScopes.Global ? _store.GetOrCreateGlobalProject().Id : ResolveProject(project).Id;
    }

    /// <summary>
    /// Namespace id for a read with a scope selector (<c>project|global|all</c>).
    /// A selector that excludes the project namespace returns null, so a
    /// global-only read never creates a project row.
    /// </summary>
    public long? ProjectIdForScopeSelector(string? project, string? scope, string defaultScope)
    {
        var (includeProject, _) = MemoryScopes.Selection(scope, defaultScope);
        return includeProject ? ResolveProject(project).Id : null;
    }
}
