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

    /// <summary>
    /// Run raw SQL against this test's database, outside the store. Used to
    /// fabricate fixtures (bulk rows, legacy schemas) without going through the
    /// write path the test is not exercising.
    /// </summary>
    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

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
