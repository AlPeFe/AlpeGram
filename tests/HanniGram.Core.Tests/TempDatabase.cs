using HanniGram.Core.Store;
using Microsoft.Data.Sqlite;

namespace HanniGram.Core.Tests;

/// <summary>
/// Owns an isolated SQLite file for a single test, so no test ever opens the
/// user's real database. The default <see cref="HanniGramStore"/> path resolves
/// to ~/.hannigram/hannigram.db; tests must always pass an explicit temp path.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hannigram-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DbPath = Path.Combine(_directory, "hannigram.db");
    }

    public string DbPath { get; }

    /// <summary>Open a store bound to this test's own temp database file.</summary>
    public HanniGramStore OpenStore() => new(DbPath);

    public void Dispose()
    {
        // Drop idle pooled handles so the WAL/SHM files can be removed. Rented
        // connections (a store that a test forgot to dispose) are unaffected.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless and never part of the repo.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
