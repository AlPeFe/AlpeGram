using HanniGram.Core.Cli;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// The CLI's documented cwd resolution was never implemented: it advertised
/// HANNIGRAM_CWD but only read --cwd, and `project [cwd]` ignored its positional
/// argument. The rule now lives in CwdResolver so its precedence is testable.
/// </summary>
public sealed class CwdResolverTests
{
    [Fact]
    public void Resolve_FlagBeatsEnvAndCurrentDirectory()
    {
        var cwd = CwdResolver.Resolve(new[] { "--cwd", "/flag" }, "/env", "/cur");

        Assert.Equal("/flag", cwd);
    }

    [Fact]
    public void Resolve_EnvBeatsCurrentDirectory()
    {
        var cwd = CwdResolver.Resolve(new[] { "search", "query" }, "/env", "/cur");

        Assert.Equal("/env", cwd);
    }

    [Fact]
    public void Resolve_FallsBackToCurrentDirectory()
    {
        var cwd = CwdResolver.Resolve(Array.Empty<string>(), null, "/cur");

        Assert.Equal("/cur", cwd);
    }

    [Fact]
    public void Resolve_IgnoresABlankEnvValue()
    {
        var cwd = CwdResolver.Resolve(Array.Empty<string>(), "   ", "/cur");

        Assert.Equal("/cur", cwd);
    }

    [Fact]
    public void Positional_ReturnsTheFirstNonFlagArgument()
    {
        var positional = CwdResolver.Positional(new[] { "/pos" });

        Assert.Equal("/pos", positional);
    }

    [Fact]
    public void Positional_SkipsAFlagValue()
    {
        var positional = CwdResolver.Positional(new[] { "--cwd", "/flagvalue" });

        Assert.Null(positional);
    }

    [Fact]
    public void Positional_ReturnsTheArgumentAfterAFlagPair()
    {
        var positional = CwdResolver.Positional(new[] { "--project", "name", "/pos" });

        Assert.Equal("/pos", positional);
    }

    [Fact]
    public void Positional_WinsOverTheCwdFlagValue()
    {
        var positional = CwdResolver.Positional(new[] { "--cwd", "/flagvalue", "/pos" });

        Assert.Equal("/pos", positional);
    }

    [Fact]
    public void Positional_ReturnsNullWhenAbsent()
    {
        Assert.Null(CwdResolver.Positional(Array.Empty<string>()));
    }
}
