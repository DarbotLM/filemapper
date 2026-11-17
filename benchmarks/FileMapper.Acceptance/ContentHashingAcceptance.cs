using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

namespace FileMapper.Acceptance;

/// <summary>Real local files, native collection and guarded reads; no synthesized content receipts.</summary>
internal static class ContentHashingAcceptance
{
    public static async Task Run(string directory, AcceptanceReport report, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Guarded content reads require Windows.");
        var filesystem = new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat;
        var root = Path.Combine(directory, "corpus"); Directory.CreateDirectory(root);
        byte[] RandomBytes(int count, int seed) { var bytes = new byte[count]; new Random(seed).NextBytes(bytes); return bytes; }
        var original = RandomBytes(1_048_576, 1907);
        var collision = original.ToArray(); collision[131_072] ^= 0x5a; // Outside first/middle/last windows.
        var shifted = new byte[original.Length + 137]; RandomBytes(137, 101).CopyTo(shifted, 0); original.CopyTo(shifted, 137);
        var fixtures = new Dictionary<string, byte[]>
        {
            ["duplicate-a.bin"] = original, ["duplicate-b.bin"] = original,
            ["sample-collision.bin"] = collision, ["shifted.bin"] = shifted,
            ["unique.bin"] = RandomBytes(600_001, 599), ["empty-a.bin"] = [], ["empty-b.bin"] = [],
            ["repeat-short.bin"] = new byte[524_288], ["repeat-long.bin"] = new byte[1_572_864]
        };
        var ntfs = filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        if (ntfs)
        {
            fixtures["ads-a.bin"] = fixtures["ads-b.bin"] = RandomBytes(32_769, 411);
            fixtures["outside-a.bin"] = fixtures["outside-b.bin"] = RandomBytes(65_537, 412);
        }
        foreach (var (name, content) in fixtures) await File.WriteAllBytesAsync(Path.Combine(root, name), content, token);
        if (ntfs &&
            !CreateHardLinkW(Path.Combine(root, "duplicate-a-alias.bin"), Path.Combine(root, "duplicate-a.bin"), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ntfs)
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "ads-a.bin") + ":metadata", "named-stream-evidence"u8.ToArray(), token);
            if (!CreateHardLinkW(Path.Combine(directory, "outside-scope-alias.bin"), Path.Combine(root, "outside-a.bin"), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var store = new MapStore(Path.Combine(directory, "hashing.db")); store.Initialize();
        await using var scans = new ScanCoordinator(store);
        var scan = scans.StartScan(new(root, HashFiles: false));
        scan = await scans.WaitAsync(scan.Id).WaitAsync(TimeSpan.FromMinutes(2), token);
        if (scan.Status != "completed") throw new InvalidOperationException("Fixture collection failed: " + scan.Error);
        var snapshot = store.GetSnapshot(scan.SnapshotId)!;
        var baseline = JsonSerializer.Serialize(store.StreamEntries(snapshot.Id).OrderBy(e => e.Path).ToArray());
        var files = store.StreamEntries(snapshot.Id).Where(e => e.Kind == "File").GroupBy(e => e.NodeId).Select(g => g.First()).ToArray();
        var truth = new Dictionary<string, string>();
        var baselineClock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file.Path);
            truth[file.NodeId] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        }
        baselineClock.Stop();
        string Node(string name) => files.Single(e => Path.GetFileName(e.Path) == name ||
            name == "duplicate-a.bin" && Path.GetFileName(e.Path) == "duplicate-a-alias.bin").NodeId;
        report.Measurements["filesystem"] = filesystem;
        report.Measurements["baseline"] = snapshot;
        report.Measurements["independentFullRead"] = new { bytesRead = fixtures.Values.Sum(b => (long)b.Length), elapsedMilliseconds = baselineClock.Elapsed.TotalMilliseconds,
            hashes = truth, scope = "One warm-cache-sensitive direct SHA256 read per physical identity; not a throughput certification." };
        report.Limitations.Add("Bounded generated fixture on the observed volume; no whole-drive performance, atomic snapshot, model bakeoff or production gate is established.");
        await using var coordinator = new HashingCoordinator(store);
        var runs = new List<HashingRun>();
        async Task<HashingRun> Run(HashingOptions options)
        {
            var started = coordinator.Start(new(snapshot.Id, options));
            var run = await coordinator.WaitAsync(started.Id, token).WaitAsync(TimeSpan.FromMinutes(2), token);
            runs.Add(run);
            report.Check("terminal-" + options.Mode + "-" + runs.Count, run.Status == "completed" && run.ResultHash is not null,
                $"run={run.Id}; status={run.Status}; completeness={run.Completeness}; error={run.Error}");
            return run;
        }
        HashingEvidence[] Evidence(string id) => store.GetHashingEvidencePage(id, limit: 200).Items.ToArray();
        HashingGroup[] Groups(string id, string mode) => store.GetHashingGroups(id, mode, limit: 200).Items.ToArray();
        FileContentChunk[] Chunks(string id)
        {
            var list = new List<FileContentChunk>();
            while (true) { var page = store.GetHashingChunks(id, list.Count, 200); list.AddRange(page.Items); if (list.Count >= page.Total) return list.ToArray(); }
        }
        var options = new HashingOptions(Workers: 2, ChunkMinimumBytes: 4096, ChunkAverageBytes: 8192, ChunkMaximumBytes: 32768, ReuseCache: false);
        var probable = await Run(options with { Mode = "probable" });
        var probableGroups = Groups(probable.Id, "probable");
        report.Check("sample-collision-remains-probable", probableGroups.Any(g => g.LogicalBytes == original.Length && g.IdentityCount == 3)
            && probableGroups.All(g => g.ReclaimableBytes is null) && Groups(probable.Id, "exact").Length == 0,
            JsonSerializer.Serialize(probableGroups));
        var exact = await Run(options);
        var exactGroups = Groups(exact.Id, "exact");
        var expectedGroups = truth.GroupBy(p => p.Value).Where(g => g.Count() > 1).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()).ToArray();
        var actualGroups = exactGroups.OrderBy(g => g.Digest).Select(g => g.Digest + ":" + g.IdentityCount).ToArray();
        report.Check("exact-rejects-sample-collision", actualGroups.SequenceEqual(expectedGroups), JsonSerializer.Serialize(new { expectedGroups, actualGroups }));
        var expectedFullCandidates = files.GroupBy(file => file.LogicalBytes).Where(group => group.Count() > 1).Sum(group => group.LongCount());
        report.Check("exact-stage-reduction", exact.Coverage.BytesRead < fixtures.Values.Sum(b => (long)b.Length) && exact.Coverage.FullyHashedIdentities == expectedFullCandidates,
            JsonSerializer.Serialize(exact.Coverage));
        report.Check("aliases-count-once", exactGroups.Single(g => g.LogicalBytes == original.Length).IdentityCount == 2,
            JsonSerializer.Serialize(exactGroups.Single(g => g.LogicalBytes == original.Length)));
        var all = await Run(options with { Mode = "all", ReuseCache = true });
        var full = Evidence(all.Id).Where(e => e.Receipt.Stage == "full" && e.Receipt.Status == "complete").ToArray();
        report.Check("all-full-digests-match-independent-read", full.Length == files.Length && full.All(e => truth[e.NodeId] == e.Receipt.Digest), $"actual={full.Length}; expected={files.Length}");
        var chunkChecks = new List<object>(); var chunkProof = true;
        foreach (var item in full.Where(e => e.Receipt.ChunkCount > 0).OrderBy(e => e.NodeId, StringComparer.Ordinal))
        {
            var retained = Chunks(item.Id); var file = files.Single(file => file.NodeId == item.NodeId);
            foreach (var index in new[] { 0, retained.Length / 2, retained.Length - 1 }.Distinct())
            {
                if (chunkChecks.Count == 8) break;
                var chunk = retained[index]; var validRange = chunk.Ordinal == index && chunk.Offset >= 0 && chunk.Length > 0 && chunk.Offset + chunk.Length <= file.LogicalBytes;
                string? actualSha256 = null;
                if (validRange)
                {
                    await using var direct = File.OpenRead(file.Path); direct.Position = chunk.Offset;
                    var content = new byte[chunk.Length]; await direct.ReadExactlyAsync(content, token);
                    actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
                }
                chunkProof &= validRange && actualSha256 == chunk.Sha256;
                chunkChecks.Add(new { item.Id, item.NodeId, chunk.Ordinal, chunk.Offset, chunk.Length, expectedSha256 = chunk.Sha256, actualSha256 });
            }
            if (chunkChecks.Count == 8) break;
        }
        report.Check("emitted-chunks-match-direct-file-intervals", chunkChecks.Count == 8 && chunkProof,
            "Eight retained first/middle/tail chunk intervals across physical identities were sought and read directly, then independently SHA256 hashed.");
        report.Measurements["directChunkIntervalProof"] = chunkChecks;
        if (ntfs)
        {
            var adsId = Node("ads-a.bin"); var adsEvidence = full.Single(e => e.NodeId == adsId);
            var adsGroup = Groups(all.Id, "exact").Single(g => g.Digest == truth[adsId]);
            report.Check("actual-ads-forbids-reclaimability", adsEvidence.Receipt.Before?.HasAlternateStreams == true &&
                adsEvidence.Receipt.After?.HasAlternateStreams == true && adsGroup.IdentityCount == 2 && adsGroup.ReclaimableBytes is null,
                JsonSerializer.Serialize(new { adsGroup, adsEvidence.Receipt.Before, adsEvidence.Receipt.After }));
            var externalId = Node("outside-a.bin"); var externalEvidence = full.Single(e => e.NodeId == externalId);
            var externalGroup = Groups(all.Id, "exact").Single(g => g.Digest == truth[externalId]);
            report.Check("outside-scope-hardlink-forbids-reclaimability", externalEvidence.Receipt.Before is { LinkCount: 2, ValidatedPathCount: 1, PathsComplete: false, HasAlternateStreams: false } &&
                externalEvidence.Receipt.After is { PathsComplete: false } && externalGroup.IdentityCount == 2 && externalGroup.ReclaimableBytes is null,
                JsonSerializer.Serialize(new { externalGroup, externalEvidence.Receipt.Before, externalEvidence.Receipt.After }));
        }
        else report.Limitations.Add("ADS and outside-scope hardlink savings fixtures are NTFS-only and were not asserted on this volume.");
        var overlaps = store.GetHashingOverlaps(all.Id, limit: 200).Items;
        var shiftedPair = overlaps.SingleOrDefault(p => (p.LeftId == Node("duplicate-a.bin") && p.RightId == Node("shifted.bin")) ||
            (p.RightId == Node("duplicate-a.bin") && p.LeftId == Node("shifted.bin")));
        report.Check("cdc-recovers-shifted-content", shiftedPair is not null && shiftedPair.SharedBytes > original.Length / 2,
            JsonSerializer.Serialize(shiftedPair));
        var conserved = true;
        foreach (var overlap in overlaps)
        {
            var left = Chunks(full.Single(e => e.NodeId == overlap.LeftId).Id).GroupBy(c => (c.Sha256, c.Length)).ToDictionary(g => g.Key, g => g.LongCount());
            var right = Chunks(full.Single(e => e.NodeId == overlap.RightId).Id).GroupBy(c => (c.Sha256, c.Length)).ToDictionary(g => g.Key, g => g.LongCount());
            var shared = left.Sum(p => Math.Min(p.Value, right.GetValueOrDefault(p.Key)) * p.Key.Length);
            conserved &= shared == overlap.SharedBytes && shared <= Math.Min(overlap.LeftBytes, overlap.RightBytes) && overlap.ReclaimableBytes is null;
        }
        report.Check("cdc-multiset-byte-conservation", overlaps.Count > 0 && conserved, $"pairs={overlaps.Count}; independently recalculated multiset intersections");
        var repeated = overlaps.SingleOrDefault(p => new[] { p.LeftId, p.RightId }.Contains(Node("repeat-short.bin")) && new[] { p.LeftId, p.RightId }.Contains(Node("repeat-long.bin")));
        report.Check("repeated-chunks-never-multiply-savings", repeated is not null && repeated.SharedBytes == 524_288 && repeated.ReclaimableBytes is null, JsonSerializer.Serialize(repeated));
        var cached = await Run(options with { Mode = "all", ReuseCache = true });
        var eligible = full.Count(e => e.Receipt.After?.CacheEligible == true);
        report.Check("cache-obeys-observed-volume-capability", eligible == files.Length
            ? cached.Coverage.BytesRead == 0 && cached.Coverage.CacheHits >= files.Length
            : cached.Coverage.CacheHits == 0 && cached.Coverage.BytesRead > 0,
            $"filesystem={filesystem}; eligibleFull={eligible}/{files.Length}; bytes={cached.Coverage.BytesRead}; hits={cached.Coverage.CacheHits}");
        report.Check("cache-preserves-original-read-provenance", Evidence(cached.Id).Where(e => e.CacheHit).All(e =>
        {
            var source = store.GetHashingEvidence(e.CacheSourceEvidenceId!);
            return source is { CacheHit: false } && source.Receipt.Digest == e.Receipt.Digest && e.Receipt.BytesRead == 0 && source.Receipt.FinishedAt <= e.Receipt.StartedAt;
        }), "Every cache hit links directly to an earlier non-cache receipt.");
        var spatial = new HashingSpatialProjection(store).Objects(cached.Id, limit: 24);
        report.Check("spatial-projection-pages-committed-evidence", spatial.Page.Total == Evidence(cached.Id).Length && spatial.Page.Items.Count > 0 && spatial.RunId == cached.Id,
            $"objects={spatial.Page.Items.Count}/{spatial.Page.Total}; model={spatial.ProjectionModel}; scope={spatial.Scope}");
        var projectedEvidence = Evidence(cached.Id).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var nativeJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        report.Check("spatial-native-identities-and-session-scope", spatial.SnapshotId == snapshot.Id && spatial.Page.Items.All(obj =>
        {
            var properties = obj.RawSourceNode.GetProperty("properties");
            var evidenceId = properties.GetProperty("hashingEvidenceId").GetString()!;
            var recorded = projectedEvidence[evidenceId];
            var encoded = properties.GetProperty("hashingEvidence").Deserialize<HashingEvidence>(nativeJson)!;
            return obj.SnapshotWindow.SnapshotId == cached.Id && obj.SourceReference.SnapshotId == cached.Id &&
                properties.GetProperty("baselineSnapshotId").GetString() == snapshot.Id &&
                properties.GetProperty("hashingRunId").GetString() == cached.Id &&
                obj.SourceReference.NodeId == Identity.For("hashing-evidence-node", recorded.Id) &&
                obj.EvidenceRefs.Any(reference => reference.Id == recorded.Id) &&
                JsonSerializer.Serialize(encoded) == JsonSerializer.Serialize(recorded);
        }), "Each native SOM retains the exact committed evidence payload; run session and baseline snapshot remain separate identities.");
        var cachedObjects = spatial.Page.Items.Where(obj => obj.RawSourceNode.GetProperty("properties").GetProperty("hashingEvidence").GetProperty("cacheHit").GetBoolean()).ToArray();
        report.Check("spatial-original-read-window-survives-cache", (eligible != files.Length || cachedObjects.Length > 0) && spatial.Page.Items.All(obj =>
        {
            var properties = obj.RawSourceNode.GetProperty("properties");
            var recorded = projectedEvidence[properties.GetProperty("hashingEvidenceId").GetString()!];
            var source = recorded.CacheHit ? store.GetHashingEvidence(recorded.CacheSourceEvidenceId!)! : recorded;
            var sourcePayload = properties.GetProperty("cacheSourceEvidence");
            return obj.ObservedWindow.From == source.Receipt.StartedAt && obj.ObservedWindow.To == source.Receipt.FinishedAt &&
                obj.SnapshotWindow.StartedAt == cached.StartedAt && obj.SnapshotWindow.FinishedAt == cached.FinishedAt &&
                (recorded.CacheHit ? sourcePayload.GetProperty("id").GetString() == source.Id &&
                    obj.EvidenceRefs.Any(reference => reference.Id == source.Id) &&
                    JsonSerializer.Serialize(sourcePayload.Deserialize<HashingEvidence>(nativeJson)) == JsonSerializer.Serialize(source)
                    : sourcePayload.ValueKind == JsonValueKind.Null);
        }), $"Native observation windows use original read receipts; current run/cache-validation intervals remain in raw evidence. Cached objects actually checked: {cachedObjects.Length}.");
        var changedPath = Path.Combine(root, "duplicate-b.bin"); var modified = File.GetLastWriteTimeUtc(changedPath);
        var changedBytes = original.ToArray(); changedBytes[131_073] ^= 0x6b;
        await File.WriteAllBytesAsync(changedPath, changedBytes, token); File.SetLastWriteTimeUtc(changedPath, modified);
        var changed = await Run(options with { Mode = "all", ReuseCache = true });
        var changedEvidence = Evidence(changed.Id).Single(e => e.NodeId == Node("duplicate-b.bin") && e.Receipt.Stage == "full");
        report.Check("restored-mtime-cannot-reuse-stale-content", !changedEvidence.CacheHit && changedEvidence.Receipt.Digest == Convert.ToHexStringLower(SHA256.HashData(changedBytes)) &&
            !Groups(changed.Id, "exact").Any(g => g.LogicalBytes == original.Length), JsonSerializer.Serialize(changedEvidence));
        var limited = await Run(options with { Mode = "partial", MaxBytesRead = 1 });
        report.Check("aggregate-byte-ceiling-is-enforced", limited.Coverage.ByteBudgetReached && limited.Coverage.BytesRead <= 1 && limited.Coverage.BudgetChargedBytes <= 1 && limited.Completeness == "limited", JsonSerializer.Serialize(limited.Coverage));
        var chunkLimited = await Run(options with { Mode = "partial", MaxChunksPerFile = 1 });
        report.Check("chunk-cap-retains-full-hashes", chunkLimited.Coverage.ChunkBudgetReached && chunkLimited.Coverage.FullyHashedIdentities == files.Length &&
            chunkLimited.Coverage.InvalidatedIdentities == 0 && Evidence(chunkLimited.Id).All(e => e.Receipt.ChunkCount <= 1), JsonSerializer.Serialize(chunkLimited.Coverage));
        if (filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            var started = coordinator.Start(new(snapshot.Id, options with { Mode = "partial", BytesPerSecond = 2_097_152, Workers = 1 }));
            await Task.Delay(200, token); var accepted = coordinator.Pause(started.Id);
            var paused = await coordinator.WaitAsync(started.Id, token);
            report.Check("pause-retains-durable-progress", accepted && paused.Status == "paused" && paused.ResultHash is null, JsonSerializer.Serialize(paused));
            coordinator.Resume(paused.Id);
            var resumed = await coordinator.WaitAsync(paused.Id, token).WaitAsync(TimeSpan.FromMinutes(1), token); runs.Add(resumed);
            report.Check("resume-closes-same-run", resumed.Status == "completed" && resumed.Id == paused.Id && resumed.ResultHash is not null && resumed.Coverage.FullyHashedIdentities == files.Length,
                JsonSerializer.Serialize(resumed));
        }
        report.Check("baseline-entries-immutable", baseline == JsonSerializer.Serialize(store.StreamEntries(snapshot.Id).OrderBy(e => e.Path).ToArray()), "Entries including original null hashes remain byte-for-byte equal after analyses.");
        report.Measurements["runs"] = runs;
        report.Measurements["spatialPage"] = spatial;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
