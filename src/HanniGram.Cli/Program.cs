using HanniGram.Core;
using HanniGram.Core.Cli;
using HanniGram.Core.Store;
using static HanniGram.Core.Cli.CwdResolver;

// HanniGram CLI — use the engine standalone, without the daemon.
// Usage:
//   hannigram project [cwd]                 resolve project for a cwd
//   hannigram init [--cwd]                  init project memory (BD + .hannigram/memory.md local export)
//   hannigram save <title> [--content] [--what] [--why] [--where] [--learned] [--topic] [--type] [--scope] [--project] [--cwd]
//   hannigram search <query> [--limit] [--scope] [--project] [--cwd]
//   hannigram list [--limit] [--project] [--cwd]
//   hannigram topics [--project] [--cwd]
//   hannigram session-start [--id] [--project] [--cwd]
//   hannigram session-end <id> [--summary] [--goal] [--next] [--project] [--cwd]
//   hannigram projects

var dbPath = Env("HANNIGRAM_DB") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hannigram", "hannigram.db");
using var store = new HanniGramStore(dbPath);

var cliArgs = Args();
if (cliArgs.Length == 0) { Help(); return; }

var cmd = cliArgs[0];
var rest = cliArgs.Skip(1).ToArray();
var project = Flag(rest, "--project");
// Resolution order: --cwd, then HANNIGRAM_CWD, then the current directory.
var cwd = CwdResolver.Resolve(rest, Env("HANNIGRAM_CWD"), Directory.GetCurrentDirectory());
var resolvedProject = project ?? ProjectResolver.Resolve(cwd);

try
{
    switch (cmd)
    {
        case "project":
        {
            // `hannigram project [cwd]` honours its positional cwd argument; the
            // --cwd flag / HANNIGRAM_CWD / current directory apply when it is absent.
            var projectCwd = CwdResolver.Positional(rest) ?? cwd;
            Console.WriteLine(project ?? ProjectResolver.Resolve(projectCwd));
            break;
        }

        case "init":
        {
            // Inicializa la memoria del proyecto: crea el proyecto en la BD y
            // escribe la memoria local (fichero) para que otros harness puedan
            // leer las decisiones sin HanniGram.
            var p = store.GetOrCreateProject(resolvedProject, cwd);
            var dir = Path.Combine(cwd, ".hannigram");
            Directory.CreateDirectory(dir);
            var memoryPath = Path.Combine(dir, "memory.md");
            var obs = store.ListObservations(p.Id, 500);
            var lines = new List<string>
            {
                $"# Memoria del proyecto — {resolvedProject}",
                $"Generado por HanniGram ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC).",
                $"BD global: {dbPath}",
                $"Observaciones: {obs.Count}",
                "",
                "> HanniGram: memoria persistente con búsqueda FTS5 trigram (tolerante a typos).",
                "> Este fichero es una vista exportada para herramientas sin HanniGram; la fuente de verdad es la BD.",
                "",
            };
            foreach (var o in obs)
            {
                lines.Add($"## #{o.Id} {o.Title} {(o.TopicKey is null ? "" : $"`{o.TopicKey}`")}");
                if (!string.IsNullOrWhiteSpace(o.Content)) lines.Add(o.Content);
                lines.Add("");
            }
            File.WriteAllText(memoryPath, string.Join("\n", lines));
            Console.WriteLine($"init: proyecto '{p.Name}' en BD + memoria local {memoryPath} ({obs.Count} observaciones)");
            break;
        }

        case "save":
        {
            var title = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("title required");
            // --scope global writes to the shared sentinel namespace; the default
            // project scope writes to the caller's own namespace.
            var scope = MemoryScopes.ResolveScope(Flag(rest, "--scope"), MemoryScopes.Project);
            var p = scope == MemoryScopes.Global
                ? store.GetOrCreateGlobalProject()
                : store.GetOrCreateProject(resolvedProject, cwd);
            var (obs, created) = store.SaveObservation(p.Id, title,
                Flag(rest, "--content") ?? "",
                Flag(rest, "--what"), Flag(rest, "--why"), Flag(rest, "--where"),
                Flag(rest, "--learned"), Flag(rest, "--topic"), Flag(rest, "--type"));
            Console.WriteLine($"{(created ? "saved" : "updated")} #{obs.Id} [{p.Name}] ({scope}) {obs.Title}");
            break;
        }

        case "search":
        {
            var q = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("query required");
            // Cross-scope by default: project hits and global heuristics in one
            // result set, each tagged with its origin. A global search reads the
            // sentinel through NamespacesFor WITHOUT creating it.
            var scope = Flag(rest, "--scope");
            var limit = IntFlag(rest, "--limit", 10);
            var projectId = MemoryScopes.Selection(scope, MemoryScopes.All).IncludeProject
                ? store.GetOrCreateProject(resolvedProject, cwd).Id
                : (long?)null;
            var namespaces = store.NamespacesFor(projectId, scope, MemoryScopes.All);
            foreach (var hit in store.Search(namespaces, q, limit))
                Console.WriteLine($"[{hit.Rank:F2}] ({hit.Scope}) #{hit.Id} {hit.Title} — {Truncate(hit.Content, 80)}");
            break;
        }

        case "list":
        {
            var p = store.GetOrCreateProject(resolvedProject, cwd);
            var limit = IntFlag(rest, "--limit", 50);
            foreach (var obs in store.ListObservations(p.Id, limit))
                Console.WriteLine($"#{obs.Id} [{obs.TopicKey ?? "-"}] {obs.Title} ({obs.CreatedAtUtc:yyyy-MM-dd})");
            break;
        }

        case "topics":
        {
            var p = store.GetOrCreateProject(resolvedProject, cwd);
            foreach (var t in store.SuggestTopicKeys(p.Id))
                Console.WriteLine(t);
            break;
        }

        case "session-start":
        {
            var p = store.GetOrCreateProject(resolvedProject, cwd);
            var s = store.StartSession(p.Id, Flag(rest, "--id") ?? Guid.NewGuid().ToString("N"));
            Console.WriteLine($"session {s.SessionId} started [{p.Name}]");
            break;
        }

        case "session-end":
        {
            var id = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("session id required");
            var p = store.GetOrCreateProject(resolvedProject, cwd);
            store.EndSession(p.Id, id, Flag(rest, "--summary"), Flag(rest, "--goal"), Flag(rest, "--next"));
            Console.WriteLine($"session {id} ended");
            break;
        }

        case "projects":
            foreach (var p in store.ListProjects())
                Console.WriteLine($"{p.Name} ({p.RootPath ?? "-"})");
            break;

        default:
            Help();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Environment.ExitCode = 1;
}

static string? Env(string name) => Environment.GetEnvironmentVariable(name);
static string[] Args() => Environment.GetCommandLineArgs().Skip(1).ToArray();
// Flag(...) is CwdResolver.Flag, imported with `using static` above.
static int IntFlag(string[] a, string name, int def)
    => int.TryParse(Flag(a, name), out var v) ? v : def;
static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static void Help() => Console.WriteLine("""
    HanniGram — persistent project memory for AI coding agents.

    Usage:
      hannigram project [cwd]
      hannigram save <title> [--content] [--what] [--why] [--where] [--learned] [--topic] [--type] [--scope project|global] [--project] [--cwd]
      hannigram search <query> [--limit] [--scope project|global|all] [--project] [--cwd]
      hannigram list [--limit] [--project] [--cwd]
      hannigram topics [--project] [--cwd]
      hannigram session-start [--id] [--project] [--cwd]
      hannigram session-end <id> [--summary] [--goal] [--next] [--project] [--cwd]
      hannigram projects

    Scope: save defaults to the caller's project namespace; --scope global writes
    the shared cross-project memory. search defaults to --scope all (project and
    global hits, each tagged with its origin); use project or global to narrow it.

    Env: HANNIGRAM_DB (db path), HANNIGRAM_CWD (default cwd)
    """);
