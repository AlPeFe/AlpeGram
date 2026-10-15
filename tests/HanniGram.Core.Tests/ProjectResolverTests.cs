using HanniGram.Core;
using HanniGram.Core.Store;
using Xunit;

namespace HanniGram.Core.Tests;

/// <summary>
/// A real directory (or git remote) must never resolve to the reserved global
/// sentinel, or a folder named <c>_global</c> would silently read and write the
/// global namespace as if it were an ordinary project.
/// </summary>
public sealed class ProjectResolverTests
{
    [Fact]
    public void Resolve_NeverReturnsTheGlobalSentinel_ForADirectoryNamedLikeIt()
    {
        using var dir = new TempDirectory(MemoryScopes.GlobalProjectName);

        var resolved = ProjectResolver.Resolve(dir.Path);

        Assert.NotEqual(MemoryScopes.GlobalProjectName, resolved);
    }

    [Fact]
    public void Resolve_ReturnsTheDirectoryName_ForANormalDirectory()
    {
        using var dir = new TempDirectory("hannigram-sample");

        var resolved = ProjectResolver.Resolve(dir.Path);

        Assert.Equal("hannigram-sample", resolved);
    }

    /// <summary>A throwaway directory that is cleaned up even if the test fails.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string leafName)
        {
            var parent = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hannigram-tests", Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(parent, leafName);
            Directory.CreateDirectory(Path);
            _parent = parent;
        }

        private readonly string _parent;
        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_parent, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
