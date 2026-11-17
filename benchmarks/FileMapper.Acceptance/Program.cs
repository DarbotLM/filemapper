using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FileMapper.Acceptance;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("""
                FileMapper acceptance runner
                  filesystem --output <new-directory>
                  semantic --output <new-directory>
                  hashing --output <new-directory>
                  storage --output <new-directory> [--files 100000 | --nodes 10000000] [--samples 30] [--warmups 1]
                    [--phases all|none|root-aggregate,root-hierarchy,logical-ranking,duplicates,owner-breakdown]
                    [--existing-database <synthetic-storage.db>] [--allow-cache-writes]
                    [--memory-interval-ms 1000] [--source-root <checkout>]

                Fixtures are created only in a new output directory and retained with the report.
                Filesystem mode requires Windows/NTFS. Storage mode emits a synthetic collector
                dataset through the public store API; it does not establish real-filesystem gates.
                Reports distinguish observed measurements from acceptance targets.
                Existing storage databases are read-only unless --allow-cache-writes is explicit.
                --phases none populates and verifies counts without expensive query phases.
                """);
            return 0;
        }
        string? Option(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        if (args[0] == "storage")
        {
            string[] allowed = ["--output", "--files", "--nodes", "--samples", "--warmups", "--phases", "--existing-database", "--allow-cache-writes", "--memory-interval-ms", "--source-root"];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var key = args[i];
                if (!allowed.Contains(key, StringComparer.Ordinal) || !seen.Add(key)) throw new ArgumentException("Unknown or duplicate storage option: " + key);
                if (key == "--allow-cache-writes") continue;
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + key);
            }
        }
        var directory = Path.GetFullPath(Option("--output") ?? throw new ArgumentException("--output is required."));
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new ArgumentException("Output directory must be new or empty.");
        Directory.CreateDirectory(directory);
        var report = new AcceptanceReport(args[0], directory);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args[0] == "filesystem") await WindowsCorpus.Run(directory, report, cancellation.Token);
            else if (args[0] == "semantic") await SemanticIntegration.Run(directory, report, cancellation.Token);
            else if (args[0] == "hashing") await ContentHashingAcceptance.Run(directory, report, cancellation.Token);
            else if (args[0] == "storage")
            {
                if (Option("--files") is not null && Option("--nodes") is not null) throw new ArgumentException("Choose --files or --nodes, not both.");
                var files = long.Parse(Option("--files") ?? "100000", System.Globalization.CultureInfo.InvariantCulture);
                if (Option("--nodes") is string nodesText)
                {
                    var nodes = long.Parse(nodesText, System.Globalization.CultureInfo.InvariantCulture);
                    long low = 100, high = 20_000_000;
                    while (low < high) { var mid = (low + high) / 2; if (StorageBenchmark.NodeCount(mid) < nodes) low = mid + 1; else high = mid; }
                    files = low;
                    if (StorageBenchmark.NodeCount(files) != nodes) throw new ArgumentException("Requested node count cannot be represented by storage-v1's root plus one directory per 1,000 files.");
                }
                var samples = int.Parse(Option("--samples") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
                var warmups = int.Parse(Option("--warmups") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                var interval = int.Parse(Option("--memory-interval-ms") ?? "1000", System.Globalization.CultureInfo.InvariantCulture);
                var phaseText = Option("--phases") ?? "all";
                string[] phases = phaseText == "all" ? StorageBenchmark.PhaseNames : phaseText == "none" ? [] : phaseText.Split(',', StringSplitOptions.TrimEntries);
                if (phases.Distinct(StringComparer.Ordinal).Count() != phases.Length || phases.Any(p => !StorageBenchmark.PhaseNames.Contains(p, StringComparer.Ordinal))) throw new ArgumentException("Unknown or duplicate storage query phase.");
                var allowCache = args.Contains("--allow-cache-writes", StringComparer.Ordinal);
                if (allowCache && Option("--existing-database") is null) throw new ArgumentException("--allow-cache-writes requires --existing-database.");
                if (files is < 100 or > 20_000_000 || samples is < 1 or > 1000 || warmups is < 0 or > 100 || interval is < 100 or > 5000)
                    throw new ArgumentException("Files must be 100–20,000,000; samples 1–1,000; warmups 0–100; memory interval 100–5,000 ms.");
                await StorageBenchmark.Run(directory, files, samples, report, cancellation.Token, Option("--existing-database"), phases, warmups, allowCache, interval, Option("--source-root"));
            }
            else throw new ArgumentException("Unknown suite.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report.Check("suite-completed", false, ex.GetType().Name + ": " + ex.Message);
        }
        report.FinishedAt = DateTimeOffset.UtcNow;
        report.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), json);
        Console.WriteLine(json);
        return report.Checks.All(c => c.Passed) ? 0 : 1;
    }
}

internal sealed class AcceptanceReport(string suite, string directory)
{
    public int SchemaVersion => 1;
    public string Suite { get; } = suite;
    public string Directory { get; } = directory;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public object Machine { get; } = new { os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
        availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, referenceHardwareCertified = false };
    public long PeakWorkingSetBytes { get; set; }
    public List<CheckResult> Checks { get; } = [];
    public Dictionary<string, object?> Measurements { get; } = [];
    public List<string> Limitations { get; } = [];
    public void Check(string name, bool passed, string evidence) => Checks.Add(new(name, passed, evidence));
}
internal sealed record CheckResult(string Name, bool Passed, string Evidence);
