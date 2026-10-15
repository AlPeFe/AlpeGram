namespace HanniGram.Core.Cli;

/// <summary>
/// The CLI's argument-derived rules for resolving a working directory. Kept in
/// Core (and not in the CLI's top-level statements) so the precedence can be
/// unit tested without launching the process.
/// </summary>
public static class CwdResolver
{
    /// <summary>
    /// Resolve the working directory for a command. Precedence: the
    /// <c>--cwd</c> flag, then the <c>HANNIGRAM_CWD</c> environment variable,
    /// then the process current directory. A blank env value is ignored.
    /// </summary>
    public static string Resolve(string[] args, string? envCwd, string currentDirectory)
    {
        var flag = Flag(args, "--cwd");
        if (flag is not null) return flag;
        return string.IsNullOrWhiteSpace(envCwd) ? currentDirectory : envCwd;
    }

    /// <summary>
    /// The positional cwd argument of <c>hannigram project [cwd]</c>: the first
    /// token that is neither a flag nor a flag's value. Returns <c>null</c> when
    /// the command carries no positional argument.
    /// </summary>
    public static string? Positional(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Skip the flag's value when it is a separate token, so a value
                // such as the path in `--cwd /some/path` is not mistaken for the
                // positional argument.
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }
            return arg;
        }
        return null;
    }

    /// <summary>
    /// Read the value of a <c>--flag value</c> pair from the argument list, or
    /// <c>null</c> when the flag is absent. Shared by the CLI so flag parsing has
    /// a single implementation.
    /// </summary>
    public static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
