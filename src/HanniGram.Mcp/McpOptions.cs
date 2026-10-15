using System.Reflection;

namespace HanniGram.Mcp;

/// <summary>
/// Process configuration. MCP has no per-call working directory, so the project is
/// resolved from an optional <c>project</c> argument, then <c>HANNIGRAM_CWD</c>,
/// then the process working directory; the database comes from <c>HANNIGRAM_DB</c>
/// with the same default as the daemon and the CLI.
/// </summary>
public sealed class McpOptions
{
    /// <summary>SQLite file the server opens itself (no daemon required).</summary>
    public required string DbPath { get; init; }

    /// <summary>Value of <c>HANNIGRAM_CWD</c>, or null when it is unset or blank.</summary>
    public string? EnvCwd { get; init; }

    /// <summary>Process working directory, used when <c>HANNIGRAM_CWD</c> is absent.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>Server name reported by <c>initialize</c>.</summary>
    public string ServerName { get; init; } = "hannigram";

    /// <summary>Server version reported by <c>initialize</c>.</summary>
    public string ServerVersion { get; init; } = DefaultVersion();

    /// <summary>
    /// The default database path, identical to the daemon's and the CLI's:
    /// <c>~/.hannigram/hannigram.db</c>.
    /// </summary>
    public static string DefaultDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".hannigram", "hannigram.db");

    public static McpOptions FromEnvironment(string? currentDirectory = null) => new()
    {
        DbPath = FirstNonBlank(Environment.GetEnvironmentVariable("HANNIGRAM_DB")) ?? DefaultDbPath(),
        EnvCwd = FirstNonBlank(Environment.GetEnvironmentVariable("HANNIGRAM_CWD")),
        CurrentDirectory = currentDirectory ?? Directory.GetCurrentDirectory(),
    };

    /// <summary>A blank environment variable means "not set", never an empty path.</summary>
    private static string? FirstNonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DefaultVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }
}
