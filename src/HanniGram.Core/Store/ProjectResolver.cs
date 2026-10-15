namespace HanniGram.Core.Store;

/// <summary>
/// Resolves a working directory to a stable project name, mirroring engram's
/// project-aware reads. Uses the git remote (owner/repo) when available, else
/// the directory name. Falls back to a normalized absolute path for non-git dirs.
/// </summary>
public static class ProjectResolver
{
    public static string Resolve(string cwd)
    {
        var gitRemote = TryGetGitRemote(cwd);
        if (gitRemote != null) return GuardAgainstSentinel(gitRemote, cwd);

        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
        if (!string.IsNullOrWhiteSpace(dirName)) return GuardAgainstSentinel(dirName, cwd);

        return GuardAgainstSentinel(NormalizePath(cwd), cwd);
    }

    /// <summary>
    /// A real directory or git remote must NEVER resolve to the reserved global
    /// sentinel: a folder (or remote) literally named <c>_global</c> would
    /// otherwise silently read and write the global namespace as if it were a
    /// project. On a collision the normalized absolute path is returned, which
    /// is unambiguous. The <c>-local</c> suffix covers the pathless edge case
    /// (a bare relative cwd equal to the sentinel).
    /// </summary>
    private static string GuardAgainstSentinel(string name, string cwd)
    {
        if (!string.Equals(name, MemoryScopes.GlobalProjectName, StringComparison.Ordinal)) return name;

        var path = NormalizePath(cwd);
        return string.Equals(path, MemoryScopes.GlobalProjectName, StringComparison.Ordinal)
            ? path + "-local"
            : path;
    }

    private static string NormalizePath(string cwd) => cwd.Replace('\\', '/').Trim('/').Replace('/', '-');

    private static string? TryGetGitRemote(string cwd)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "config --get remote.origin.url")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var url = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            if (p.ExitCode != 0 || string.IsNullOrEmpty(url)) return null;

            // Normalize: https://github.com/owner/repo.git | git@github.com:owner/repo.git
            url = url.TrimEnd('/');
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
            var parts = url.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var owner = parts[^2];
                var repo = parts[^1];
                return $"{owner}/{repo}";
            }
            return url;
        }
        catch
        {
            return null;
        }
    }
}
