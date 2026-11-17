using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Engine;
using FileMapper.Harvest;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Cli;

internal static class HarvestCommands
{
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
            filemapper harvest capture --request <JSON-file> --workspace <directory> [--extract]
            filemapper harvest extract <capture-sha256> --workspace <directory> [--defer-index]
            filemapper harvest list [--offset 0] [--limit 100] --workspace <directory>
            filemapper harvest counts <capture-sha256> --workspace <directory>
            filemapper harvest terms <capture-sha256> [--search <text>] [--offset 0] [--limit 100] --workspace <directory>
            filemapper harvest documents <capture-sha256> [--supported-only] [--search <text>] [--offset 0] [--limit 100] --workspace <directory>
            filemapper harvest occurrences <capture-sha256> --term <term-id> [--offset 0] [--limit 100] --workspace <directory>
            filemapper harvest verify <capture-sha256> --workspace <directory>
            filemapper harvest replay <extraction-sha256> --workspace <directory>
            filemapper harvest compact --input <JSON-file> --destination <new-database> --workspace <directory>
            filemapper harvest semantics --input <JSON-file> --destination <new-directory> --workspace <existing-corpus>
            filemapper harvest connect --service-data <existing-directory> --workspace <existing-corpus> [--compact-index <sealed-database>]
            filemapper harvest connection --service-data <existing-directory>

            list/counts/terms/documents/occurrences accept --compact-index <database> for a sealed projection.
            documents reports capture metadata; --supported-only selects native C#/PowerShell candidates,
            whose decoding, extraction membership and analysis budgets are checked when analysis starts.
            Compact input is { inputs: [{ captureId, extractionId? }], options?: {...} }.
            It rebuilds only from retained source evidence, without changing the ingestion index.
            extract --defer-index seals the same corpus without opening/writing the ingestion index.
            Its queryIndex output describes this invocation; existing index coverage is not evaluated.
            connect atomically saves the selection for the next service start; it does not change a running service.
            connection inspects saved configuration only and creates nothing. Explicit startup options take precedence.
            semantics input is { captureId, extractionId, entryIds?: [...], options?: {...} }.
            It writes a separate immutable C#/PowerShell graph, native shapes and byte-span word joins.
            It does not execute source or modify the source corpus, query index or learner weights.

            Capture requests pin GitHub repository numeric ID, canonical URI, bare path and exact commit.
            policy.materializeBlobs enables filtered Git fetch unless boundedMaterialization selects
            github-response-body with an explicit maximumResponseBodyBytes. Otherwise reads are local.
            Native Git plumbing never checks out or executes repository source, hooks, submodules or LFS.
            Every captured word has raw-byte/UTF-16 positions. Commands, translations and runtime I/O
            require separate typed adapters; lexical source observations do not train learner weights.
            Capture and lexical coverage are reported separately. Progress is JSON on stderr.
            """); return 0;
        }
        var options = ParseArguments(args);
        string? Option(string name) => options.GetValueOrDefault(name);
        string RequiredOption(string name) => Option(name) ?? throw new ArgumentException(name + " is required.");
        string CaptureId() => args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : throw new ArgumentException("Capture SHA-256 is required.");
        int Number(string name, int fallback) => Option(name) is { } value ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        if (args[0] is "connect" or "connection")
        {
            var store = new SourceHarvestConnectionStore(RequiredOption("--service-data"));
            var connection = args[0] == "connect"
                ? await store.ConnectAsync(RequiredOption("--workspace"), Option("--compact-index"), cancellationToken)
                : await store.LoadAsync(cancellationToken);
            Console.WriteLine(CanonicalJson.Serialize(new { scope = "saved-configuration", appliesOn = "service-startup",
                activeServiceStateEvaluated = false, connection }));
            return 0;
        }
        var writable = args[0] is "capture" or "extract";
        var workspacePath = args[0] == "semantics" ? SourceHarvestConnectionStore.NormalizePath(RequiredOption("--workspace")) : RequiredOption("--workspace");
        var workspace = new SourceHarvestWorkspace(workspacePath, readOnly: !writable);
        ISourceHarvestReader Reader() => Option("--compact-index") is { } compactPath ? new CompactSourceHarvestIndex(compactPath) : workspace.Reader();
        var progress = new InlineProgress(value => Console.Error.WriteLine(CanonicalJson.Serialize(value)));
        object result;
        switch (args[0])
        {
            case "capture":
                var path = Path.GetFullPath(RequiredOption("--request"));
                if (new FileInfo(path).Length > 65536) throw new ArgumentException("Capture request exceeds 64 KiB.");
                var request = JsonSerializer.Deserialize<GitHarvestRequest>(await File.ReadAllBytesAsync(path, cancellationToken), Input)
                    ?? throw new ArgumentException("Capture request must be an object.");
                var capture = await workspace.CaptureAsync(request, progress, cancellationToken);
                result = options.ContainsKey("--extract")
                    ? new { capture, extraction = await workspace.ExtractAsync(capture.CaptureId, progress: progress, cancellationToken: cancellationToken) }
                    : capture;
                break;
            case "extract":
                if (options.ContainsKey("--defer-index"))
                {
                    var extraction = await workspace.ExtractCorpusAsync(CaptureId(), progress: progress, cancellationToken: cancellationToken);
                    result = new { extraction.ExtractionId, extraction.ManifestPath, extraction.Manifest,
                        queryIndex = new { mode = "deferred", writesPerformed = false, existingCoverage = "not-evaluated",
                            nextStep = "Explicitly rebuild/select a compact projection using this capture and extraction ID." } };
                }
                else result = await workspace.ExtractAsync(CaptureId(), progress: progress, cancellationToken: cancellationToken);
                break;
            case "list": result = Reader().ListCaptures(Number("--offset", 0), Number("--limit", 100), cancellationToken); break;
            case "counts": result = Reader().Counts(CaptureId(), cancellationToken); break;
            case "terms": result = Reader().Terms(CaptureId(), Option("--search"), Number("--offset", 0), Number("--limit", 100), cancellationToken); break;
            case "documents": result = Reader().Documents(CaptureId(), Option("--search"), options.ContainsKey("--supported-only"), Number("--offset", 0), Number("--limit", 100), cancellationToken); break;
            case "occurrences": result = Reader().Occurrences(CaptureId(), RequiredOption("--term"), Number("--offset", 0), Number("--limit", 100), cancellationToken); break;
            case "verify": result = await workspace.VerifyAsync(CaptureId(), cancellationToken); break;
            case "replay": result = await workspace.ReplayAsync(CaptureId(), cancellationToken); break;
            case "compact":
                var inputPath = Path.GetFullPath(RequiredOption("--input"));
                if (new FileInfo(inputPath).Length > 1024 * 1024) throw new ArgumentException("Compact rebuild request exceeds 1 MiB.");
                var compact = JsonSerializer.Deserialize<CompactRequest>(await File.ReadAllBytesAsync(inputPath, cancellationToken), Input)
                    ?? throw new ArgumentException("Compact request must be an object.");
                if (compact.Inputs is null) throw new ArgumentException("Compact inputs are required.");
                result = await CompactSourceHarvestIndex.RebuildAsync(workspace.RootDirectory, RequiredOption("--destination"), compact.Inputs, compact.Options,
                    new CompactProgress(value => Console.Error.WriteLine(CanonicalJson.Serialize(value))), cancellationToken);
                break;
            case "semantics":
                var semanticInputPath = SourceHarvestConnectionStore.NormalizePath(RequiredOption("--input"));
                await using (var semanticInput = new FileStream(semanticInputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                {
                    var semanticRequest = await NativeRequestJson.ReadAsync<SourceSemanticRequest>(semanticInput, 1024 * 1024, cancellationToken);
                    result = await SourceSemanticWorkspace.AnalyzeAsync(workspace.RootDirectory, RequiredOption("--destination"),
                        semanticRequest, progress, cancellationToken);
                }
                break;
            default: throw new ArgumentException("Unknown harvest operation. Use harvest --help.");
        }
        Console.WriteLine(CanonicalJson.Serialize(result)); return 0;
    }
    private sealed class InlineProgress(Action<object> action) : IProgress<object>
    { public void Report(object value) => action(value); }
    private sealed record CompactRequest(IReadOnlyList<CompactSourceIndexInput> Inputs, CompactSourceIndexBuildOptions? Options = null);
    private sealed class CompactProgress(Action<CompactSourceIndexBuildProgress> action) : IProgress<CompactSourceIndexBuildProgress>
    { public void Report(CompactSourceIndexBuildProgress value) => action(value); }

    private static Dictionary<string, string?> ParseArguments(string[] args)
    {
        string[] extra = args[0] switch
        {
            "capture" => ["--request", "--extract"],
            "extract" => ["--defer-index"],
            "verify" or "replay" => [],
            "list" => ["--offset", "--limit", "--compact-index"],
            "counts" => ["--compact-index"],
            "terms" => ["--search", "--offset", "--limit", "--compact-index"],
            "documents" => ["--supported-only", "--search", "--offset", "--limit", "--compact-index"],
            "occurrences" => ["--term", "--offset", "--limit", "--compact-index"],
            "compact" or "semantics" => ["--input", "--destination"],
            "connect" => ["--service-data", "--compact-index"],
            "connection" => ["--service-data"],
            _ => throw new ArgumentException("Unknown harvest operation. Use harvest --help.")
        };
        var allowed = new HashSet<string>(extra, StringComparer.Ordinal);
        if (args[0] != "connection") allowed.Add("--workspace");
        var positional = args[0] is "extract" or "counts" or "terms" or "documents" or "occurrences" or "verify" or "replay";
        if (positional && (args.Length < 2 || args[1].Length != 64 || args[1].Any(c => !char.IsAsciiHexDigitLower(c))))
            throw new ArgumentException("A lowercase SHA-256 capture/extraction ID is required immediately after the operation.");
        var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = positional ? 2 : 1; i < args.Length; i++)
        {
            var name = args[i];
            if (!allowed.Contains(name)) throw new ArgumentException("Unknown or unsupported harvest option: " + name);
            if (parsed.ContainsKey(name)) throw new ArgumentException("Duplicate harvest option: " + name);
            if (name is "--extract" or "--defer-index" or "--supported-only") { parsed.Add(name, null); continue; }
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException(name + " requires a value.");
            parsed.Add(name, args[i]);
        }
        string[] required = args[0] switch
        {
            "capture" => ["--workspace", "--request"],
            "occurrences" => ["--workspace", "--term"],
            "compact" or "semantics" => ["--workspace", "--input", "--destination"],
            "connect" => ["--service-data", "--workspace"],
            "connection" => ["--service-data"],
            _ => ["--workspace"]
        };
        foreach (var name in required) if (!parsed.ContainsKey(name)) throw new ArgumentException(name + " is required.");
        foreach (var name in new[] { "--offset", "--limit" })
            if (parsed.TryGetValue(name, out var value) && (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number < (name == "--limit" ? 1 : 0) || name == "--limit" && number > 500))
                throw new ArgumentException(name + " must be a nonnegative integer; limit must be 1–500.");
        return parsed;
    }
}
