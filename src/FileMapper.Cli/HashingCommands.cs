using System.Globalization;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

namespace FileMapper.Cli;

internal static class HashingCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
            filemapper hashing start <snapshot> [--mode exact|probable|partial|all] [--scope <absolute-path>]
              [--minimum-bytes 0] [--max-bytes 1099511627776] [--workers 2] [--bytes-per-second 0]
              [--max-identities 1000000] [--max-paths 2000000] [--no-cache]
              [--sample-window 65536] [--chunk-minimum 65536] [--chunk-average 262144] [--chunk-maximum 1048576]
              [--max-chunks-per-file 65536] [--max-chunks 2000000] [--max-overlap-pairs 100000]
              [--max-pair-comparisons 2000000] [--max-chunk-fanout 256]
            filemapper hashing start --request <JSON-file>
            filemapper hashing jobs [--snapshot <id>] [--limit 100]
            filemapper hashing status|pause|cancel|resume <run-id>
            filemapper hashing groups <run-id> [--mode exact|probable] [--offset 0] [--limit 50]
            filemapper hashing members <run-id> --group <group-id> [--offset 0] [--limit 50]
            filemapper hashing overlaps <run-id> [--node <identity>] [--offset 0] [--limit 50]
            filemapper hashing evidence <run-id> [--node <identity>] [--offset 0] [--limit 50]
            filemapper hashing paths <run-id> --node <identity> [--offset 0] [--limit 50]
            filemapper hashing observation <evidence-id>
            filemapper hashing chunks <evidence-id> [--offset 0] [--limit 100]
            Global: --database <existing-map-database>. Output: typed JSON.
            Start/resume waits for the durable job. Ctrl+C pauses it; resume explicitly.
            Exact results use full unnamed-stream SHA-256. Samples are probable; chunk overlap is logical shared content.
            Read bytes count bytes returned by the file API, not physical device traffic. Limits and cache provenance remain visible.
            """);
            return 0;
        }
        var command = args[0];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(arg); continue; }
            var value = arg == "--no-cache" ? "true" : ++index < args.Length ? args[index] : throw new ArgumentException("Missing value for " + arg);
            if (!values.TryAdd(arg, value)) throw new ArgumentException("Repeated option: " + arg);
        }
        string? Option(string name) => values.GetValueOrDefault(name);
        string Required() => positionals.Count == 1 ? positionals[0] : throw new ArgumentException("Exactly one snapshot, run or evidence identifier is required.");
        long Number(string name, long fallback) => Option(name) is not { } value ? fallback
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException(name + " requires an integer.");
        int Int(string name, int fallback) => checked((int)Number(name, fallback));
        void Only(params string[] allowed)
        {
            foreach (var key in values.Keys) if (key != "--database" && !allowed.Contains(key, StringComparer.Ordinal)) throw new ArgumentException("Unsupported option for " + command + ": " + key);
        }
        void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Exporter.Json));
        var database = Option("--database") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "filemapper.db");
        if (!File.Exists(database)) throw new FileNotFoundException("A previously scanned map database is required.", database);
        var writing = command is "start" or "resume" or "pause" or "cancel";
        var store = new MapStore(database, readOnly: !writing);
        if (writing)
        {
            await using var coordinator = new HashingCoordinator(store);
            if (command is "pause" or "cancel")
            {
                Only();
                var requested = command == "pause" ? coordinator.Pause(Required()) : coordinator.Cancel(Required());
                Print(new { requested, action = command });
                return requested ? 0 : 1;
            }
            HashingRun run;
            if (command == "resume") { Only(); run = coordinator.Resume(Required()); }
            else
            {
                HashingRequest request;
                if (Option("--request") is { } input)
                {
                    Only("--request");
                    if (positionals.Count != 0) throw new ArgumentException("Use a request file or a positional snapshot, not both.");
                    await using var stream = File.OpenRead(input);
                    request = await NativeRequestJson.ReadAsync<HashingRequest>(stream, 64 * 1024, cancellationToken);
                }
                else
                {
                    Only("--mode", "--scope", "--minimum-bytes", "--max-bytes", "--workers", "--bytes-per-second", "--max-identities", "--max-paths", "--no-cache",
                        "--sample-window", "--chunk-minimum", "--chunk-average", "--chunk-maximum", "--max-chunks-per-file", "--max-chunks", "--max-overlap-pairs", "--max-pair-comparisons", "--max-chunk-fanout");
                    request = new(Required(), new HashingOptions(Mode: Option("--mode") ?? "exact", ScopePath: Option("--scope") is { } scope ? Path.GetFullPath(scope) : null,
                        MinimumBytes: Number("--minimum-bytes", 0), MaxIdentities: Int("--max-identities", 1_000_000), MaxPaths: Int("--max-paths", 2_000_000),
                        MaxBytesRead: Number("--max-bytes", 1_099_511_627_776), Workers: Int("--workers", 2), BytesPerSecond: Number("--bytes-per-second", 0),
                        SampleWindowBytes: Int("--sample-window", 65_536), ChunkMinimumBytes: Int("--chunk-minimum", 65_536), ChunkAverageBytes: Int("--chunk-average", 262_144),
                        ChunkMaximumBytes: Int("--chunk-maximum", 1_048_576), MaxChunksPerFile: Int("--max-chunks-per-file", 65_536), MaxChunkRecords: Number("--max-chunks", 2_000_000),
                        MaxOverlapPairs: Int("--max-overlap-pairs", 100_000), MaxChunkFanout: Int("--max-chunk-fanout", 256), MaxPairComparisons: Number("--max-pair-comparisons", 2_000_000), ReuseCache: !values.ContainsKey("--no-cache")));
                }
                run = coordinator.Start(request);
            }
            try { run = await coordinator.WaitAsync(run.Id, cancellationToken); }
            catch (OperationCanceledException)
            {
                coordinator.Pause(run.Id);
                Print(await coordinator.WaitAsync(run.Id));
                return 130;
            }
            Print(run);
            return run.Status == "completed" ? 0 : 1;
        }
        var offset = Int("--offset", 0);
        var limit = Int("--limit", command is "chunks" or "jobs" ? 100 : 50);
        switch (command)
        {
            case "jobs":
                Only("--snapshot", "--limit");
                if (positionals.Count != 0) throw new ArgumentException("Jobs has no positional arguments.");
                Print(store.ListHashingRuns(Option("--snapshot"), limit));
                break;
            case "status": Only(); Print(Require(store, Required())); break;
            case "groups":
                Only("--mode", "--offset", "--limit");
                var groupsRun = Require(store, Required(), true);
                Print(new { run = groupsRun, page = store.GetHashingGroups(groupsRun.Id, Option("--mode") ?? "exact", offset, limit) });
                break;
            case "members":
                Only("--group", "--offset", "--limit");
                var membersRun = Require(store, Required(), true);
                Print(new { run = membersRun, page = store.GetHashingMembers(membersRun.Id, Option("--group") ?? throw new ArgumentException("--group required."), offset, limit) });
                break;
            case "overlaps":
                Only("--node", "--offset", "--limit");
                var overlapRun = Require(store, Required(), true);
                Print(new { run = overlapRun, page = store.GetHashingOverlaps(overlapRun.Id, Option("--node"), offset, limit) });
                break;
            case "paths":
                Only("--node", "--offset", "--limit");
                var pathsRun = Require(store, Required());
                Print(new { run = pathsRun, page = store.GetHashingPaths(pathsRun.Id,
                    Option("--node") ?? throw new ArgumentException("--node required."), limit, offset) });
                break;
            case "evidence":
                Only("--node", "--offset", "--limit");
                var evidenceRun = Require(store, Required());
                Print(new { run = evidenceRun, page = store.GetHashingEvidencePage(evidenceRun.Id, Option("--node"), offset, limit) });
                break;
            case "observation": Only(); Print(store.GetHashingEvidence(Required()) ?? throw new KeyNotFoundException("Content evidence not found.")); break;
            case "chunks":
                Only("--offset", "--limit");
                var evidence = store.GetHashingEvidence(Required()) ?? throw new KeyNotFoundException("Content evidence not found.");
                Print(new { evidence, page = store.GetHashingChunks(evidence.Id, offset, limit) });
                break;
            default: throw new ArgumentException("Unknown hashing command. Use filemapper hashing --help.");
        }
        return 0;
    }
    private static HashingRun Require(MapStore store, string id, bool sealedResult = false)
    {
        var run = store.GetHashingRun(id) ?? throw new KeyNotFoundException("Content analysis not found.");
        if (sealedResult && run.ResultHash is null) throw new InvalidOperationException("Content analysis has no sealed result yet.");
        return run;
    }
}
