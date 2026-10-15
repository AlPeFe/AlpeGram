using System.Text;
using HanniGram.Core.Store;

namespace HanniGram.Mcp;

/// <summary>
/// Entry point of the HanniGram stdio MCP server.
/// <para>
/// The server owns the SQLite store directly (no daemon, no HTTP): the daemon and
/// this process may hold the same database open at the same time because the store
/// runs WAL with a busy timeout, which is exactly what that design is for.
/// </para>
/// <para>
/// stdout is the protocol channel and stderr is the diagnostic channel. This file
/// therefore never writes to <c>Console.Out</c>: both streams are created
/// explicitly as BOM-less UTF-8 writers so nothing but JSON-RPC lines can reach
/// stdout.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

        var options = McpOptions.FromEnvironment();
        try
        {
            using var store = new HanniGramStore(options.DbPath);
            new McpSession(store, options, stdin, stdout, stderr).Run();
            return 0;
        }
        catch (Exception ex)
        {
            // A failed open (bad HANNIGRAM_DB, an unreadable file) is reported on
            // stderr and exits non-zero; stdout stays untouched.
            stderr.WriteLine($"hannigram-mcp: fatal: {ex.Message}");
            return 1;
        }
    }
}
