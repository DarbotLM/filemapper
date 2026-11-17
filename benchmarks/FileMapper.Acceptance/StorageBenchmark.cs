using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

namespace FileMapper.Acceptance;

internal static class StorageBenchmark
{
    internal static readonly string[] PhaseNames = ["root-aggregate", "root-hierarchy", "logical-ranking", "duplicates", "owner-breakdown"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static long NodeCount(long files) => files + (files + 999) / 1000 + 1;
    public static async Task Run(string directory, long files, int samples, AcceptanceReport report, CancellationToken token,
        string? existingDatabase = null, string[]? phases = null, int warmups = 1, bool allowCacheWrites = false, int memoryIntervalMs = 1000, string? sourceRoot = null)
    {
        phases ??= PhaseNames;
        var root = Path.Combine(directory, "synthetic-scope");
        var database = existingDatabase is null ? Path.Combine(directory, "storage.db") : Path.GetFullPath(existingDatabase);
        if (existingDatabase is not null && !File.Exists(database)) throw new FileNotFoundException("Existing benchmark database not found.", database);
        var readOnly = existingDatabase is not null && !allowCacheWrites;
        var store = new MapStore(database, readOnly: readOnly);
        using var events = NewLog(directory, "storage-events.ndjson");
        using var sampleLog = NewLog(directory, "storage-samples.ndjson");
        await using var memory = new MemoryTimeline(directory, memoryIntervalMs);
        report.Measurements["storageProtocol"] = new { version = "storage-benchmark/2", phases, samples, warmups, readOnly, allowCacheWrites,
            expectedNodes = NodeCount(files), coldCacheClaim = false,
            timing = "First invocation separate from warmups/measured samples. No OS cache eviction. Population/fingerprints and prior phases may warm caches.",
            cache = readOnly ? "No persistent cache writes requested; existing caches may be read." : "Public query APIs may initialize/populate directory caches.",
            percentile = "Nearest-rank p95; even median is mean of central samples." };
        report.Measurements["provenance"] = Provenance(sourceRoot);
        memory.Phase = "database-before-fingerprint";
        var before = DatabaseState(database);
        report.Measurements["databaseBefore"] = before;
        Record(events, new { kind = "start", at = DateTimeOffset.UtcNow, files, samples, warmups, phases, readOnly });
        try
        {
        memory.Phase = "population";
        if (!readOnly) store.Initialize();
        Snapshot snapshot;
        var populate = Stopwatch.StartNew();
        if (existingDatabase is not null)
        {
            snapshot = store.ListSnapshots().Single(s => s.Status == "completed" && s.FileCount == files);
            if (!store.GetObservations(snapshot.Id, snapshot.Id).Any(e => e.Predicate == "dataset.generator" && e.SourceKind == "synthetic-benchmark" && e.Value.StartsWith("Deterministic synthetic storage-v1;", StringComparison.Ordinal)))
                throw new ArgumentException("Existing snapshot is not an identified storage-v1 synthetic benchmark dataset.");
            root = snapshot.Root;
            Console.WriteLine($"Reusing completed synthetic snapshot {snapshot.Id} ({files:N0} files); population skipped.");
            report.Limitations.Add("Existing synthetic snapshot reused. Population timing is unavailable; persistent directory caches may already be warm. Initial invocation is not OS-cold.");
        }
        else
        {
        Directory.CreateDirectory(root);
        Console.WriteLine($"Population starting: {files:N0} synthetic files.");
        using (store.AcquireWriter())
        {
            snapshot = store.CreateSnapshot(root);
            var nodes = new List<MapNode>(512); var entries = new List<FileEntry>(512);
            void Add(string path, string? parent, string kind, string id, long bytes, string? hash)
            {
                nodes.Add(new(id, kind, Path.GetFileName(path), path, new() { ["synthetic"] = true, ["generatorVersion"] = "storage-v1" }));
                entries.Add(new(snapshot.Id, path, parent, id, Path.GetFileName(path), kind, bytes, bytes,
                    DateTimeOffset.UnixEpoch.AddDays(bytes % 365), "Synthetic", 1, "synthetic-owner-" + bytes % 7, hash));
            }
            void Flush()
            {
                if (entries.Count == 0) return;
                store.Ingest(snapshot.Id, new(nodes.ToArray(), [], [], entries.ToArray())); nodes.Clear(); entries.Clear();
            }
            store.Ingest(snapshot.Id, new([], [], [new Evidence(Identity.For("synthetic", snapshot.Id), snapshot.Id, snapshot.Id,
                "dataset.generator", "Deterministic synthetic storage-v1; values are not measured filesystem facts.", "synthetic-benchmark", root,
                DateTimeOffset.UtcNow)], []));
            Add(root, null, "Directory", "synthetic:root", 0, null);
            const int groupSize = 1000;
            for (long index = 0; index < files; index++)
            {
                token.ThrowIfCancellationRequested();
                var group = index / groupSize;
                var parent = Path.Combine(root, "directory-" + group.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                if (index % groupSize == 0) Add(parent, root, "Directory", "synthetic:directory:" + group, 0, null);
                var bytes = 1L + index % 4096;
                var path = Path.Combine(parent, "file-" + index.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".bin");
                // Only one percent receive duplicate candidate hashes, keeping the projection bounded
                // in group count while exercising full-data grouping across distant directory pages.
                var hash = index % 100 == 0 ? Identity.For("hash", (index % 4096).ToString(System.Globalization.CultureInfo.InvariantCulture))[5..] : null;
                Add(path, parent, "File", "synthetic:file:" + index, bytes, hash);
                if (entries.Count >= 512) Flush();
                if ((index + 1) % 100_000 == 0 || index + 1 == files)
                {
                    Flush();
                    Record(events, new { kind = "population-progress", at = DateTimeOffset.UtcNow, committedFiles = index + 1, requestedFiles = files,
                        elapsedMs = populate.Elapsed.TotalMilliseconds, database = DatabaseSizes(database) });
                    Console.WriteLine($"Committed {index + 1:N0}/{files:N0} files in {populate.Elapsed.TotalSeconds:N1}s.");
                }
            }
            Flush(); store.FinishSnapshot(snapshot.Id);
        }
        }
        populate.Stop(); snapshot = store.GetSnapshot(snapshot.Id)!;
        var expected = files / 4096 * (4096L * 4097 / 2) + files % 4096 * (files % 4096 + 1) / 2;
        report.Check("synthetic-population", snapshot.FileCount == files && snapshot.LogicalBytes == expected && snapshot.Completeness == "complete",
            $"snapshot={snapshot.Id}; files={snapshot.FileCount}; logical={snapshot.LogicalBytes}; expected={expected}");
        report.Measurements["population"] = new { files, snapshot.EntryCount, snapshot.LogicalBytes, reused = existingDatabase is not null, elapsedMs = existingDatabase is null ? (double?)populate.Elapsed.TotalMilliseconds : null };
        report.Measurements["snapshotId"] = snapshot.Id;
        memory.Phase = "node-count-validation";
        var actualNodes = store.GetNodes(snapshot.Id, limit: 1).Total;
        report.Measurements["actualGraphNodes"] = actualNodes;
        report.Check("actual-node-count", actualNodes == NodeCount(files), $"actual={actualNodes}; expected={NodeCount(files)}");
        Record(events, new { kind = "population-ready", at = DateTimeOffset.UtcNow, snapshot.Id, files, actualNodes });
        Console.WriteLine($"Snapshot ready: {snapshot.EntryCount:N0} entries / {actualNodes:N0} nodes.");
        var query = new QueryEngine(store);
        var expectedRank = new List<(string Id, long Bytes)>();
        for (var bytes = 4096; bytes > 0 && expectedRank.Count < Math.Min(50, files); bytes--)
            for (long index = bytes - 1; index < files && expectedRank.Count < Math.Min(50, files); index += 4096)
                expectedRank.Add(("synthetic:file:" + index, bytes));
        var results = new Dictionary<string, object>();
        report.Measurements["queries"] = results;
        void Measure(string name, Func<object> operation)
        {
            memory.Phase = name;
            var timings = new double[samples];
            double Invoke(string kind, int index)
            {
                token.ThrowIfCancellationRequested();
                var started = DateTimeOffset.UtcNow;
                var watch = Stopwatch.StartNew();
                object value;
                try { value = operation(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    watch.Stop(); Record(sampleLog, new { phase = name, kind, index, startedAt = started, elapsedMs = watch.Elapsed.TotalMilliseconds, status = "failed", error = ex.GetType().Name + ": " + ex.Message }); throw;
                }
                watch.Stop();
                // Validation/serialization is excluded from query latency; its effects on later caches/GC are disclosed.
                string hash;
                try
                {
                    var json = JsonSerializer.SerializeToElement(value, Json);
                    if (name is "root-aggregate" or "root-hierarchy")
                    {
                        var scope = name == "root-aggregate" ? json : json.GetProperty("scope");
                        if (scope.GetProperty("recursiveLogicalBytes").GetInt64() != expected || scope.GetProperty("uniqueFileCount").GetInt64() != files)
                            throw new InvalidOperationException("Aggregate differs from independently computed generated truth.");
                        if (kind == "first-invocation") report.Check("aggregate-total-" + name, true, $"logical={expected}; identities={files}");
                    }
                    if (name == "logical-ranking")
                    {
                        var items = json.GetProperty("items").EnumerateArray().ToArray();
                        if (items.Length != expectedRank.Count || items.Where((item, i) => item.GetProperty("item").GetProperty("nodeId").GetString() != expectedRank[i].Id || item.GetProperty("item").GetProperty("logicalBytes").GetInt64() != expectedRank[i].Bytes).Any())
                            throw new InvalidOperationException("Ranking differs from deterministic top-file identities/bytes/order.");
                        if (kind == "first-invocation") report.Check("rank-generated-truth", true, $"{items.Length} ordered identities and logical sizes.");
                    }
                    hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json.GetRawText()))).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Record(sampleLog, new { phase = name, kind, index, startedAt = started, elapsedMs = watch.Elapsed.TotalMilliseconds, status = "validation-failed", error = ex.GetType().Name + ": " + ex.Message }); throw;
                }
                Record(sampleLog, new { phase = name, kind, index, startedAt = started, elapsedMs = watch.Elapsed.TotalMilliseconds, status = "completed", resultSha256 = hash });
                Console.WriteLine($"Query {name} {kind} {index}: {watch.Elapsed.TotalMilliseconds:N2} ms.");
                return watch.Elapsed.TotalMilliseconds;
            }
            var first = Invoke("first-invocation", 0);
            if (name == "root-aggregate") { results[name] = new { firstInvocationMs = first, measuredSamples = 0, note = "One aggregate only; no p95 inferred." }; return; }
            for (var i = 0; i < warmups; i++) Invoke("warmup", i + 1);
            for (var i = 0; i < samples; i++) timings[i] = Invoke("measured", i + 1);
            var sorted = timings.Order().ToArray();
            var p95 = sorted[(int)Math.Ceiling(samples * .95) - 1];
            results[name] = new { firstInvocationMs = first, warmups, measuredSamples = samples, chronologicalMs = timings, minMs = sorted[0],
                medianMs = samples % 2 == 0 ? (sorted[samples / 2 - 1] + sorted[samples / 2]) / 2 : sorted[samples / 2], p95Ms = p95, maxMs = sorted[^1],
                observedTargetUnder300ms = name is "root-hierarchy" or "logical-ranking" ? (bool?)(p95 < 300) : null,
                atLeastTenMillionNodes = actualNodes >= 10_000_000, atLeastThirtyMeasuredSamples = samples >= 30, performanceGateAccepted = false };
            report.Check("phase-completed-" + name, true, $"{samples} measured samples; first invocation/warmups excluded from distribution.");
        }
        foreach (var phase in phases)
        {
            switch (phase)
            {
                case "root-aggregate": Measure(phase, () => store.GetDirectoryMetrics(snapshot.Id, root)); break;
                case "root-hierarchy": Measure(phase, () => query.Hierarchy(snapshot.Id, root, limit: 50)); break;
                case "logical-ranking": Measure(phase, () => query.Rank(snapshot.Id, "logical", 50)); break;
                case "duplicates": Measure(phase, () => query.Duplicates(snapshot.Id, limit: 10)); break;
                case "owner-breakdown": Measure(phase, () => query.Breakdown(snapshot.Id, "owner", limit: 10)); break;
                default: throw new ArgumentException("Unknown phase: " + phase);
            }
        }
        Console.WriteLine("Query phases complete.");
        report.Check("selected-phases-completed", true, "Only selected phases measured; omitted phases remain unmeasured.");
        Record(events, new { kind = "queries-completed", at = DateTimeOffset.UtcNow, phases });
        report.Limitations.Add("Synthetic data is generated through public ingestion APIs, not collected from a real filesystem. It cannot pass scan throughput, native correctness, or real-volume boss gates.");
        report.Limitations.Add("First invocation is not OS-cold. Fingerprints/population/earlier phases warm caches. Validation and result hashing are outside query timing but can affect later GC/cache state. Reference certification, controlled background load, native scan memory and incremental refresh require separate runs.");
        report.Limitations.Add("Pass/fail checks above cover population and aggregation correctness only. Performance gates remain unproven until actual node count, sample distribution and hardware conditions satisfy the canonical specification.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(events, new { kind = "failed-or-cancelled", at = DateTimeOffset.UtcNow, phase = memory.Phase, error = ex.GetType().Name + ": " + ex.Message }); throw;
        }
        finally
        {
            memory.Phase = "database-after-fingerprint";
            var after = DatabaseState(database);
            report.Measurements["databaseAfter"] = after;
            if (readOnly) report.Check("retained-database-content-unchanged", JsonSerializer.Serialize(before, Json) == JsonSerializer.Serialize(after, Json), "Main DB/WAL length and SHA256 compared. SHM coordination bytes excluded.");
            await memory.Stop();
            report.Measurements["memoryTimeline"] = memory.Summary;
            report.Limitations.Add("Memory is benchmark-process memory, not native scan memory. SQLite read-only mode can coordinate via SHM. Cancellation is checked between synchronous public calls; no forced SQL interruption. Flushed logs survive partial failure, but a killed process may not publish report.json.");
            Record(events, new { kind = "finalized", at = DateTimeOffset.UtcNow, databaseAfter = after, memory = memory.Summary });
        }
    }

    private static string Hash(string path)
    {
        // SQLite can retain an idle pooled writer handle; this is a quiescent-run fingerprint,
        // not an atomic snapshot or permission to benchmark a concurrently changing database.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    private static StreamWriter NewLog(string directory, string name) => new(new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    private static void Record(StreamWriter writer, object value) { writer.WriteLine(JsonSerializer.Serialize(value, Json)); writer.Flush(); }
    private static object[] DatabaseState(string database) => new[] { database, database + "-wal" }.Select(path => File.Exists(path)
        ? (object)new { file = Path.GetFileName(path), exists = true, length = new FileInfo(path).Length, sha256 = Hash(path) }
        : new { file = Path.GetFileName(path), exists = false, length = 0L, sha256 = (string?)null }).ToArray();
    private static object[] DatabaseSizes(string database) => new[] { database, database + "-wal", database + "-shm" }
        .Select(path => (object)new { file = Path.GetFileName(path), exists = File.Exists(path), length = File.Exists(path) ? new FileInfo(path).Length : 0 }).ToArray();

    private static object Provenance(string? sourceRoot)
    {
        var binaries = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Where(p => Path.GetFileName(p).StartsWith("FileMapper.", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("SQLitePCLRaw", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { name = Path.GetFileName(p), sha256 = Hash(p), bytes = new FileInfo(p).Length, informationalVersion = FileVersionInfo.GetVersionInfo(p).ProductVersion }).ToArray();
        var native = Directory.EnumerateFiles(AppContext.BaseDirectory, "*e_sqlite3*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => new { path = Path.GetRelativePath(AppContext.BaseDirectory, p), sha256 = Hash(p), bytes = new FileInfo(p).Length }).ToArray();
        object? source = null;
        if (sourceRoot is not null)
        {
            var resolvedSourceRoot = Path.GetFullPath(sourceRoot);
            string Git(params string[] arguments)
            {
                var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("-C"); start.ArgumentList.Add(resolvedSourceRoot);
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot read Git provenance.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10_000)) { process.Kill(); throw new TimeoutException("Git provenance read timed out."); }
                var text = output.GetAwaiter().GetResult(); var detail = error.GetAwaiter().GetResult();
                if (process.ExitCode != 0 || text.Length > 1_048_576) throw new InvalidOperationException("Git provenance failed: " + detail);
                return text.TrimEnd();
            }
            string[] paths = ["benchmarks/FileMapper.Acceptance/StorageBenchmark.cs", "benchmarks/FileMapper.Acceptance/Program.cs", "benchmarks/README.md"];
            var status = Git("status", "--porcelain", "--untracked-files=normal");
            source = new { revision = Git("rev-parse", "HEAD"), dirty = status.Length != 0, statusPorcelain = status,
                benchmarkFiles = paths.Select(p => new { path = p, sha256 = Hash(Path.Combine(resolvedSourceRoot, p)) }).ToArray(),
                interpretation = "Revision identifies checkout; dirty status includes benchmark overlays and any product edits. DLL hashes identify executed bytes. No clean-product or reproducible-build claim is inferred." };
        }
        return new { binaries, native, source, sourceNotProvided = sourceRoot is null, processExecutableSha256 = Environment.ProcessPath is string executable ? Hash(executable) : null };
    }

    private sealed class MemoryTimeline : IAsyncDisposable
    {
        private readonly StreamWriter writer;
        private readonly CancellationTokenSource stop = new();
        private readonly Task task;
        private readonly int interval;
        private long count, maxWorkingSet, maxPrivate, osPeak, maxHeap;
        private bool stopped;
        private string phase = "startup";
        public string Phase { get => Volatile.Read(ref phase); set => Volatile.Write(ref phase, value); }
        public MemoryTimeline(string directory, int intervalMs)
        {
            interval = intervalMs; writer = NewLog(directory, "storage-memory.ndjson");
            task = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    while (true)
                    {
                        using var process = Process.GetCurrentProcess(); process.Refresh();
                        var heap = GC.GetTotalMemory(false);
                        maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64); maxPrivate = Math.Max(maxPrivate, process.PrivateMemorySize64);
                        osPeak = Math.Max(osPeak, process.PeakWorkingSet64); maxHeap = Math.Max(maxHeap, heap); count++;
                        Record(writer, new { at = DateTimeOffset.UtcNow, elapsedMs = clock.Elapsed.TotalMilliseconds, phase = Phase, workingSetBytes = process.WorkingSet64,
                            privateBytes = process.PrivateMemorySize64, osPeakWorkingSetBytes = process.PeakWorkingSet64, managedHeapBytes = heap, cpuMs = process.TotalProcessorTime.TotalMilliseconds });
                        await Task.Delay(interval, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
        }
        public object Summary => new { file = "storage-memory.ndjson", samples = count, requestedIntervalMs = interval, sampledPeakWorkingSetBytes = maxWorkingSet,
            sampledPeakPrivateBytes = maxPrivate, osReportedPeakWorkingSetBytes = osPeak, sampledPeakManagedHeapBytes = maxHeap,
            scope = "Whole benchmark process, including setup/fingerprints/ingestion/queries. Sampling may miss peaks; OS peak is separate. Not native scan memory." };
        public async Task Stop() { if (stopped) return; stopped = true; stop.Cancel(); try { await task; } finally { writer.Dispose(); } }
        public async ValueTask DisposeAsync() { await Stop(); stop.Dispose(); }
    }
}
