using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine($"""
    FileMapper {typeof(MapStore).Assembly.GetName().Version?.ToString(3)} — evidence-backed filesystem and code maps

    filemapper scan <directory> [--hash] [--analyze] [--reparse skip|within|all|reference] [--exclude <glob>]...
    filemapper status
    filemapper jobs
    filemapper pause <job-id>
    filemapper resume <job-id>
    filemapper cancel <job-id>
    filemapper backup --destination <new-file>
    filemapper restore --source <backup-file>  (replaces local database under writer lock)
    filemapper tree <snapshot> [--parent <path>] [--offset 0] [--limit 100]
    filemapper query <snapshot> [--search <text>] [--kind <kind>]
    filemapper rank <snapshot> [--by logical|allocated|age] [--limit 50]
    filemapper breakdown <snapshot> [--by extension|owner|age] [--path <directory>] [--offset 0] [--limit 100]
    filemapper duplicates <snapshot> [--offset 0] [--limit 100]
    filemapper hashing --help  (staged exact/probable/chunk-overlap analysis and durable jobs)
    filemapper duplicate-members <snapshot> --hash <sha256> --bytes <length> [--offset 0] [--limit 1000]
    filemapper analyze <snapshot> [--analyzer-report <PSScriptAnalyzer-json>]
    filemapper audit <snapshot>
    filemapper explain <snapshot> --finding <finding-id>
    filemapper route <snapshot> --from <node-id> --to <node-id>
    filemapper context <snapshot> --search <text> [--budget 16000]  (UTF-8 context bytes)
    filemapper diff <before> <after> [--offset 0] [--limit 1000]
    filemapper export <snapshot> [--format json|ndjson|csv|sarif]
    filemapper mcp  (read-only MCP over stdio)
    filemapper robocopy --help  (typed plans, list-only previews, explicit local approvals)
    filemapper semantic --help  (native SOM objects, comparisons, export and offline game replay)
    filemapper learning --help  (persistent vocabulary, supervised labels and frozen models)
    filemapper arena --help  (3D whole-shape Tetrid, saved challenges and native opponent)
    filemapper computer --help  (replayable game-policy episodes)
    filemapper harvest --help  (pinned repository source bytes, words and exact source positions)
    filemapper word-library --help  (public evidence-bound lexical editions)

    Global: --database <path>. Output: JSON (export can stream CSV/NDJSON).
    Hashing reads file content; analysis never runs repository code.
    Default reparse policy: skip. Unknown allocation stays null.
    """); return 0;
}
string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
(string Root, string? Database, string? Reparse, string[]? Excludes, bool Hash, bool Analyze, bool InstallerSession) ParseScan()
{
    var root = Required(1);
    var values = new List<string>();
    string[] scanOptions = ["--exclude", "--hash", "--analyze", "--reparse", "--database", "--installer-session"];
    string? database = null, reparse = null;
    bool hash = false, analyze = false, installerSession = false;
    for (var i = 2; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--exclude=", StringComparison.Ordinal))
        {
            var value = arg["--exclude=".Length..];
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--exclude requires a glob value.");
            values.Add(value);
        }
        else if (arg is "--exclude" or "--database" or "--reparse")
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || scanOptions.Contains(args[i + 1]) || (arg == "--exclude" && args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException($"{arg} requires a value.");
            var value = args[++i];
            if (arg == "--exclude") values.Add(value);
            else if (arg == "--database") database ??= value;
            else reparse ??= value;
        }
        else if (arg == "--hash") hash = true;
        else if (arg == "--analyze") analyze = true;
        else if (arg == "--installer-session") installerSession = true;
        else throw new ArgumentException($"Unknown scan argument: {arg}");
    }
    return (root, database, reparse, values.Count == 0 ? null : values.ToArray(), hash, analyze, installerSession);
}
int Number(string name, int fallback) => int.TryParse(Option(name), out var n) ? n : fallback;
string Required(int index) => args.Length > index && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : throw new ArgumentException("Missing positional argument. Use --help.");
void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Exporter.Json));
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args[0] == "robocopy") return await FileMapper.Cli.RobocopyCommands.RunAsync(args[1..], cancellation.Token);
    if (args[0] == "semantic") return await FileMapper.Cli.SemanticCommands.RunAsync(args[1..], cancellation.Token);
    if (args[0] is "learning" or "arena") return await FileMapper.Cli.LearningCommands.RunAsync(args, cancellation.Token);
    if (args[0] == "computer") return await FileMapper.Cli.ComputerCommands.RunAsync(args[1..], cancellation.Token);
    if (args[0] == "word-library") return await FileMapper.Cli.WordLibraryCommands.RunAsync(args[1..], cancellation.Token);
    if (args[0] == "harvest") return await FileMapper.Cli.HarvestCommands.RunAsync(args[1..], cancellation.Token);
    if (args[0] == "hashing") return await FileMapper.Cli.HashingCommands.RunAsync(args[1..], cancellation.Token);
    var scan = args[0] == "scan" ? ParseScan() : ((string Root, string? Database, string? Reparse, string[]? Excludes, bool Hash, bool Analyze, bool InstallerSession)?)null;
    var path = (scan is null ? Option("--database") : scan.Value.Database) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "filemapper.db");
    if (args[0] == "mcp")
    {
        if (args.Length != 1 && (args.Length != 3 || args[1] != "--database" || string.IsNullOrWhiteSpace(args[2])))
            throw new ArgumentException("Use mcp [--database <existing-file>]. No installer or writer options are accepted.");
        await new McpServer(new MapStore(path, readOnly: true)).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellation.Token);
        return 0;
    }
    if (args[0] == "context")
    {
        int contextBudget = 16000;
        if (Array.IndexOf(args, "--budget") >= 0 && !int.TryParse(Option("--budget"), out contextBudget))
            throw new ArgumentException("--budget requires an integer from 256 to 100000.");
        var contextQuery = new QueryEngine(new MapStore(path, readOnly: true));
        Print(contextQuery.Context(Required(1), Option("--search") ?? "", contextBudget, cancellation.Token));
        return 0;
    }
    var installerSession = scan?.InstallerSession ?? args.Contains("--installer-session");
    var installerToken = installerSession ? Environment.GetEnvironmentVariable("FILEMAPPER_INSTALLER_TOKEN") : null;
    if (installerSession && string.IsNullOrWhiteSpace(installerToken)) throw new InvalidOperationException("Installer session credentials are missing.");
    var store = new MapStore(path, installerToken);
    if (args[0] == "backup") { var destination = Option("--destination") ?? throw new ArgumentException("--destination required"); store.BackupTo(destination); Print(new { backedUp = true, destination = Path.GetFullPath(destination) }); return 0; }
    if (args[0] == "restore") { var source = Option("--source") ?? throw new ArgumentException("--source required"); store.RestoreFrom(source); Print(new { restored = true, database = store.DatabasePath }); return 0; }
    store.Initialize(); var query = new QueryEngine(store);
    await using var scans = new ScanCoordinator(store);
    switch (args[0])
    {
        case "scan":
            var job = scans.StartScan(new(scan!.Value.Root, scan.Value.Reparse ?? "skip", Excludes: scan.Value.Excludes, HashFiles: scan.Value.Hash));
            using (cancellation.Token.Register(() => scans.Cancel(job.Id))) job = await scans.WaitAsync(job.Id);
            if (job.Status != "completed") { Print(job); return 2; }
            if (scan.Value.Analyze) { var analysis = scans.StartAnalysis(job.SnapshotId); using (cancellation.Token.Register(() => scans.Cancel(analysis.Id))) { var result = await scans.WaitAsync(analysis.Id); if (result.Status != "completed") { Print(result); return 2; } } }
            Print(query.Require(job.SnapshotId)); break;
        case "status": Print(store.ListSnapshots()); break;
        case "jobs": Print(scans.Jobs); break;
        case "pause": Print(new { requested = scans.Pause(Required(1)) }); break;
        case "cancel": Print(new { requested = scans.Cancel(Required(1)) }); break;
        case "resume":
            var resumed = scans.Resume(Required(1));
            using (cancellation.Token.Register(() => scans.Pause(resumed.Id))) resumed = await scans.WaitAsync(resumed.Id);
            Print(resumed); if (resumed.Status != "completed") return 2; break;
        case "tree": Print(query.Hierarchy(Required(1), Option("--parent"), Math.Max(0, Number("--offset", 0)), Math.Clamp(Number("--limit", 100), 1, 1000))); break;
        case "query": Print(store.GetNodes(Required(1), Option("--kind"), Option("--search"), limit: Math.Clamp(Number("--limit", 100), 1, 1000))); break;
        case "rank": Print(query.Rank(Required(1), Option("--by") ?? "logical", Number("--limit", 50))); break;
        case "breakdown": Print(query.Breakdown(Required(1), Option("--by") ?? "extension", Option("--path"), Math.Max(0, Number("--offset", 0)), Math.Clamp(Number("--limit", 100), 1, 500))); break;
        case "duplicates": Print(query.Duplicates(Required(1), Math.Max(0, Number("--offset", 0)), Math.Clamp(Number("--limit", 100), 1, 500))); break;
        case "duplicate-members":
            if (!long.TryParse(Option("--bytes"), out var duplicateBytes) || duplicateBytes < 0) throw new ArgumentException("--bytes requires a nonnegative integer");
            Print(query.DuplicateMembers(Required(1), Option("--hash") ?? throw new ArgumentException("--hash required"), duplicateBytes, Math.Max(0, Number("--offset", 0)), Math.Clamp(Number("--limit", 1000), 1, 1000))); break;
        case "analyze":
            var aj = scans.StartAnalysis(Required(1), Option("--analyzer-report") is { } reportPath ? [reportPath] : null);
            using (cancellation.Token.Register(() => scans.Cancel(aj.Id))) aj = await scans.WaitAsync(aj.Id);
            Print(aj); if (aj.Status != "completed") return 2; break;
        case "audit": Print(new { snapshot = query.Require(Required(1)), findings = store.GetFindings(Required(1)) }); break;
        case "explain": Print(query.ExplainFinding(Required(1), Option("--finding") ?? throw new ArgumentException("--finding required"))); break;
        case "route": Print(query.Route(Required(1), Option("--from") ?? throw new ArgumentException("--from required"), Option("--to") ?? throw new ArgumentException("--to required"))); break;
        case "diff": Print(query.Diff(Required(1), Required(2), Math.Max(0, Number("--offset", 0)), Math.Clamp(Number("--limit", 1000), 1, 1000))); break;
        case "export": await Exporter.WriteAsync(store, Required(1), Option("--format") ?? "json", Console.OpenStandardOutput(), cancellation.Token); break;
        default: throw new ArgumentException("Unknown command. Use --help.");
    }
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.GetType().Name, detail = ex.Message }, Exporter.Json)); return 1;
}
