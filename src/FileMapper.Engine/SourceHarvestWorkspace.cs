using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record SourceExtractionEntry(string EntryId, string? DocumentId, string? SourceSha256,
    string CaptureStatus, string LexicalStatus, string? OutputSha256, string? CanonicalNdjsonSha256,
    long Occurrences, long WordOccurrences, bool Complete);
public sealed record SourceExtractionCoverage(long Entries, long CapturedDocuments, long CompleteDocuments,
    long IncompleteDocuments, long UnavailableEntries, long Occurrences, long WordOccurrences, long UniqueLexicons);
public sealed record SourceExtractionManifest(string Schema, string CaptureId, SourceLexiconPolicy Policy,
    SourceBlobDescriptor Entries, SourceExtractionCoverage Coverage, string State,
    string Scope = "Lexical source occurrences only; no parser binding, translation equivalence, runtime invocation or learning-label inference.");
public sealed record SourceExtractionReceipt(string ExtractionId, string ManifestPath, SourceExtractionManifest Manifest);

/// <summary>Additive immutable source corpus, independent from historical graph snapshots and learner weights.</summary>
public sealed class SourceHarvestWorkspace
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string RootDirectory { get; }
    public string IndexPath => Path.Combine(RootDirectory, "source-harvest.db");
    private readonly bool readOnly;
    public SourceHarvestWorkspace(string rootDirectory, bool readOnly = false)
    {
        RootDirectory = Path.GetFullPath(rootDirectory); this.readOnly = readOnly;
        CheckPath(RootDirectory);
        if (readOnly && !Directory.Exists(RootDirectory)) throw new DirectoryNotFoundException("An existing source harvest workspace is required.");
        if (!readOnly) Directory.CreateDirectory(RootDirectory);
    }
    private SourceHarvestIndex Writer()
    {
        if (readOnly) throw new InvalidOperationException("This source harvest workspace is read-only.");
        var index = new SourceHarvestIndex(IndexPath); index.Initialize(); return index;
    }
    public SourceHarvestIndex Reader() => new(IndexPath, readOnly: true);

    public async Task<SourceCaptureReceipt> CaptureAsync(GitHarvestRequest request, IProgress<object>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = Lease(); var index = Writer();
        var capture = new GitSourceCapture(RootDirectory);
        var receipt = await capture.CaptureAsync(request, progress, cancellationToken);
        await RegisterAsync(index, capture, receipt, cancellationToken);
        return receipt;
    }

    public Task<SourceExtractionReceipt> ExtractAsync(string captureId, SourceLexiconOptions? options = null,
        IProgress<object>? progress = null, CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(captureId, false, options, progress, cancellationToken);

    /// <summary>Seals the same lexical corpus without opening or changing the ingestion query index.
    /// The caller must explicitly build/select a query projection for these retained receipts.</summary>
    public Task<SourceExtractionReceipt> ExtractCorpusAsync(string captureId, SourceLexiconOptions? options = null,
        IProgress<object>? progress = null, CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(captureId, true, options, progress, cancellationToken);

    private async Task<SourceExtractionReceipt> ExtractCoreAsync(string captureId, bool deferIndex, SourceLexiconOptions? options,
        IProgress<object>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = Lease(); var index = deferIndex ? null : Writer();
        var capture = new GitSourceCapture(RootDirectory); var receipt = capture.Read(captureId);
        if (index is not null) await RegisterAsync(index, capture, receipt, cancellationToken);
        var policy = SourceLexicon.Describe(options);
        var directory = Path.Combine(RootDirectory, "lexicons", policy.PolicyHash); CheckPath(directory); Directory.CreateDirectory(directory);
        var attempt = Path.Combine(RootDirectory, "attempts", "extract-" + Guid.NewGuid().ToString("N")); CheckPath(attempt); Directory.CreateDirectory(attempt);
        var entriesPath = Path.Combine(attempt, "extraction-entries.ndjson");
        var cache = new Dictionary<string, SourceLexiconResult>(StringComparer.Ordinal);
        var associations = new List<SourceDocumentAssociation>(128);
        long entries = 0, captured = 0, complete = 0, incomplete = 0, unavailable = 0, units = 0, words = 0;
        try
        {
            await using (var output = new FileStream(entriesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true))
            {
                await foreach (var entry in capture.EntriesAsync(captureId, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested(); entries++;
                    SourceLexiconResult? lexicon = null;
                    if (entry.Blob is { } blob && entry.Status == "captured")
                    {
                        captured++;
                        if (!cache.TryGetValue(blob.Sha256, out lexicon))
                        {
                            var sourceProof = await capture.Blobs.VerifyAsync(blob.Sha256, cancellationToken);
                            if (sourceProof.Status != "verified" || sourceProof.BytesRead != blob.ByteLength)
                                throw new IOException("Retained raw source is missing or corrupt: " + blob.Sha256);
                            var generated = false;
                            lexicon = index?.TryGetLexicon(blob.Sha256, policy.PolicyHash, cancellationToken);
                            if (lexicon is null)
                            {
                                var metadataPath = Path.Combine(directory, blob.Sha256 + ".json"); CheckPath(metadataPath);
                                if (File.Exists(metadataPath))
                                {
                                    if (new FileInfo(metadataPath).Length > 65536) throw new IOException("Lexicon metadata exceeds its size bound.");
                                    lexicon = JsonSerializer.Deserialize<SourceLexiconResult>(await File.ReadAllBytesAsync(metadataPath, cancellationToken), Json)
                                        ?? throw new IOException("Invalid retained lexical metadata.");
                                    if (lexicon.SourceSha256 != blob.Sha256 || lexicon.Policy.PolicyHash != policy.PolicyHash)
                                        throw new IOException("Lexicon cache identity conflict.");
                                }
                                else
                                {
                                    var staging = Path.Combine(attempt, blob.Sha256 + ".ndjson.gz");
                                    lexicon = await SourceLexicon.ExtractAsync(capture.Blobs, blob, staging, policy.Options, cancellationToken);
                                    generated = true;
                                    if (deferIndex && lexicon.Status is "source-length-mismatch" or "source-hash-mismatch")
                                        throw new IOException("Retained raw source changed during lexical extraction.");
                                    if (lexicon.OutputPath is { } source)
                                    {
                                        var target = Path.Combine(directory, blob.Sha256 + ".ndjson.gz"); CheckPath(target);
                                        if (File.Exists(target))
                                        {
                                            if (await FileHashAsync(target, cancellationToken) != lexicon.OutputSha256)
                                                throw new IOException("Existing unindexed lexical output conflicts with deterministic replay.");
                                            File.Delete(source);
                                        }
                                        else File.Move(source, target, overwrite: false);
                                        lexicon = lexicon with { OutputPath = target };
                                    }
                                    await WriteJsonAsync(metadataPath, lexicon, cancellationToken);
                                }
                                if (index is not null) await index.ImportLexiconAsync(lexicon, capture.Blobs, cancellationToken);
                                else if (!generated) await VerifyCorpusCacheAsync(capture, blob, lexicon, policy, directory, attempt, cancellationToken);
                            }
                            else
                            {
                                if (lexicon.OutputPath is { } saved && await FileHashAsync(saved, cancellationToken) != lexicon.OutputSha256)
                                    throw new IOException("Indexed lexical bytes are missing or corrupt.");
                                await index!.ImportLexiconAsync(lexicon, capture.Blobs, cancellationToken);
                            }
                            cache.Add(blob.Sha256, lexicon);
                        }
                        if (lexicon.Complete) complete++; else incomplete++;
                        units += lexicon.Occurrences; words += lexicon.WordOccurrences;
                    }
                    else unavailable++;
                    if (index is not null)
                    {
                        associations.Add(new(entry, lexicon));
                        if (associations.Count == 128) { index.AssociateDocuments(captureId, associations, cancellationToken); associations.Clear(); }
                    }
                    var record = new SourceExtractionEntry(entry.Entry.EntryId, entry.DocumentId, entry.Blob?.Sha256, entry.Status,
                        lexicon?.Status ?? "not-captured", lexicon?.OutputSha256, lexicon?.CanonicalNdjsonSha256,
                        lexicon?.Occurrences ?? 0, lexicon?.WordOccurrences ?? 0, lexicon?.Complete ?? false);
                    await output.WriteAsync(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(record) + "\n"), cancellationToken);
                    if (entries % 100 == 0) progress?.Report(new { phase = "extracting", captureId, entries, captured, complete, incomplete, unavailable, words, uniqueLexicons = cache.Count });
                }
                if (associations.Count != 0) index!.AssociateDocuments(captureId, associations, cancellationToken);
                await output.FlushAsync(cancellationToken); output.Flush(true);
            }
            SourceBlobDescriptor entriesBlob;
            await using (var stream = File.OpenRead(entriesPath))
                entriesBlob = (await capture.Blobs.PutAsync(stream, expectedLength: stream.Length, cancellationToken: cancellationToken)).Blob
                    ?? throw new IOException("Extraction entries manifest could not be retained.");
            var coverage = new SourceExtractionCoverage(entries, captured, complete, incomplete, unavailable, units, words, cache.Count);
            var manifest = new SourceExtractionManifest("filemapper.source-extraction/1", captureId, policy, entriesBlob, coverage,
                incomplete == 0 && receipt.Manifest.State == "sealed-complete-for-policy" ? "sealed-complete-for-contract" : "sealed-partial");
            var extractionId = CanonicalJson.Sha256(manifest);
            var manifests = Path.Combine(RootDirectory, "extractions"); CheckPath(manifests); Directory.CreateDirectory(manifests);
            var path = Path.Combine(manifests, extractionId + ".json");
            if (File.Exists(path))
            { if (await File.ReadAllTextAsync(path, cancellationToken) != CanonicalJson.Serialize(manifest)) throw new IOException("Extraction commitment conflicts with existing manifest."); }
            else await WriteJsonAsync(path, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            index?.SealExtraction(captureId, policy.PolicyHash, extractionId);
            var result = new SourceExtractionReceipt(extractionId, path, manifest);
            await WriteJsonAsync(Path.Combine(attempt, "sealed.json"), result, cancellationToken);
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await WriteJsonAsync(Path.Combine(attempt, "failed.json"), new { state = cancellationToken.IsCancellationRequested ? "canceled" : "failed",
                captureId, policy.PolicyHash, entries, captured, complete, incomplete, unavailable, words, error = error.GetType().Name, detail = error.Message }, CancellationToken.None);
            throw;
        }
    }

    private static async Task VerifyCorpusCacheAsync(GitSourceCapture capture, SourceBlobDescriptor blob,
        SourceLexiconResult cached, SourceLexiconPolicy policy, string directory, string attempt, CancellationToken ct)
    {
        if (cached.SourceSha256 != blob.Sha256 || cached.SourceBytes != blob.ByteLength ||
            CanonicalJson.Serialize(cached.Policy) != CanonicalJson.Serialize(policy))
            throw new IOException("Retained lexical cache source or policy differs from the requested corpus.");
        var target = Path.Combine(directory, blob.Sha256 + ".ndjson.gz"); CheckPath(target);
        if (cached.OutputPath is not null && !string.Equals(Path.GetFullPath(cached.OutputPath), target,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Retained lexical cache refers outside its owned corpus location.");
        // Re-extract cached content instead of trusting metadata or an indexed prefix as an oracle.
        // This keeps all decoder/fragment/coordinate validation in the pinned lexical implementation.
        var fresh = await SourceLexicon.ExtractAsync(capture.Blobs, blob,
            Path.Combine(attempt, blob.Sha256 + ".cache-proof.ndjson.gz"), policy.Options, ct);
        if ((cached.OutputPath is null) != (fresh.OutputPath is null) ||
            CanonicalJson.Serialize(cached with { OutputPath = null }) != CanonicalJson.Serialize(fresh with { OutputPath = null }))
            throw new IOException("Retained lexical cache differs from fresh source replay.");
        if (fresh.OutputPath is { } proof)
        {
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(proof).Length ||
                await FileHashAsync(target, ct) != fresh.OutputSha256)
                throw new IOException("Retained lexical artifact is missing or differs from fresh source replay.");
            CheckPath(proof); File.Delete(proof);
        }
    }

    public async Task<object> VerifyAsync(string captureId, CancellationToken cancellationToken = default)
    {
        var capture = new GitSourceCapture(RootDirectory, readOnly: true); var receipt = capture.Read(captureId);
        var hashes = new Dictionary<string, SourceBlobDescriptor>(StringComparer.Ordinal)
            { [receipt.Manifest.RawTree.Sha256] = receipt.Manifest.RawTree, [receipt.Manifest.Entries.Sha256] = receipt.Manifest.Entries };
        if (receipt.Manifest.MaterializationEvidence is { } evidence) hashes.TryAdd(evidence.Sha256, evidence);
        long entries = 0;
        await foreach (var entry in capture.EntriesAsync(captureId, cancellationToken))
        { entries++; if (entry.Blob is { } blob) hashes.TryAdd(blob.Sha256, blob); }
        var failures = new List<object>();
        foreach (var blob in hashes.Values)
        {
            var result = await capture.Blobs.VerifyAsync(blob.Sha256, cancellationToken);
            if (result.Status != "verified" || result.BytesRead != blob.ByteLength) failures.Add(new { blob.Sha256, result.Status, result.Detail });
        }
        return new { captureId, verified = failures.Count == 0 && entries == receipt.Manifest.Coverage.TreeEntries,
            checkedEntries = entries, expectedEntries = receipt.Manifest.Coverage.TreeEntries, checkedObjects = hashes.Count, failures,
            scope = "Capture manifest and referenced raw source, entry, tree and materialization evidence bytes. Extract resumes the index; replay independently reruns the lexer from retained source." };
    }

    public async Task<object> ReplayAsync(string extractionId, CancellationToken cancellationToken = default)
    {
        if (extractionId.Length != 64 || extractionId.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A lowercase extraction SHA-256 is required.");
        var path = Path.Combine(RootDirectory, "extractions", extractionId + ".json"); CheckPath(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Extraction manifest exceeds its size bound.");
        var manifest = JsonSerializer.Deserialize<SourceExtractionManifest>(await File.ReadAllBytesAsync(path, cancellationToken), Json)
            ?? throw new IOException("Invalid extraction manifest.");
        if (CanonicalJson.Sha256(manifest) != extractionId) throw new IOException("Extraction manifest hash mismatch.");
        if (SourceLexicon.Describe(manifest.Policy.Options).PolicyHash != manifest.Policy.PolicyHash)
            throw new InvalidOperationException("Replay requires the exact saved lexer, decoder, options and runtime contract.");
        var capture = new GitSourceCapture(RootDirectory, readOnly: true);
        var entriesProof = await capture.Blobs.VerifyAsync(manifest.Entries.Sha256, cancellationToken);
        if (entriesProof.Status != "verified" || entriesProof.BytesRead != manifest.Entries.ByteLength)
            throw new IOException("Extraction entries are missing or corrupt.");
        var attempt = Path.Combine(RootDirectory, "verification-attempts", Guid.NewGuid().ToString("N")); CheckPath(attempt); Directory.CreateDirectory(attempt);
        var reconstructed = new Dictionary<string, SourceLexiconResult>(StringComparer.Ordinal);
        using var expectedReader = new StreamReader(capture.Blobs.OpenRead(manifest.Entries.Sha256), new UTF8Encoding(false, true));
        long entries = 0, words = 0, units = 0;
        await foreach (var entry in capture.EntriesAsync(manifest.CaptureId, cancellationToken))
        {
            var line = await expectedReader.ReadLineAsync(cancellationToken) ?? throw new IOException("Extraction entry stream ended early.");
            var expected = JsonSerializer.Deserialize<SourceExtractionEntry>(line, Json) ?? throw new IOException("Invalid extraction entry.");
            if (expected.EntryId != entry.Entry.EntryId || expected.DocumentId != entry.DocumentId || expected.SourceSha256 != entry.Blob?.Sha256 || expected.CaptureStatus != entry.Status)
                throw new IOException("Extraction entry does not match its pinned source occurrence.");
            entries++;
            if (entry.Blob is { } blob && entry.Status == "captured")
            {
                if (!reconstructed.TryGetValue(blob.Sha256, out var replay))
                {
                    var raw = await capture.Blobs.VerifyAsync(blob.Sha256, cancellationToken);
                    if (raw.Status != "verified" || raw.BytesRead != blob.ByteLength) throw new IOException("Raw source is missing or corrupt during replay.");
                    replay = await SourceLexicon.ExtractAsync(capture.Blobs, blob, Path.Combine(attempt, blob.Sha256 + ".ndjson.gz"), manifest.Policy.Options, cancellationToken);
                    reconstructed.Add(blob.Sha256, replay);
                }
                if (expected.OutputSha256 != replay.OutputSha256 || expected.CanonicalNdjsonSha256 != replay.CanonicalNdjsonSha256 ||
                    expected.Occurrences != replay.Occurrences || expected.WordOccurrences != replay.WordOccurrences ||
                    expected.LexicalStatus != replay.Status || expected.Complete != replay.Complete)
                    throw new IOException("Fresh lexical replay differs from the sealed extraction for " + entry.DocumentId);
                words += replay.WordOccurrences; units += replay.Occurrences;
            }
            else if (expected.LexicalStatus != "not-captured" || expected.Occurrences != 0 || expected.WordOccurrences != 0 || expected.Complete)
                throw new IOException("Uncaptured entry claims lexical output.");
        }
        if (await expectedReader.ReadLineAsync(cancellationToken) is not null || entries != manifest.Coverage.Entries ||
            words != manifest.Coverage.WordOccurrences || units != manifest.Coverage.Occurrences || reconstructed.Count != manifest.Coverage.UniqueLexicons)
            throw new IOException("Replay coverage does not reconcile with its manifest.");
        var result = new { extractionId, manifest.CaptureId, replayVerified = true, checkedEntries = entries, checkedUniqueLexicons = reconstructed.Count,
            wordOccurrences = words, occurrences = units, policyHash = manifest.Policy.PolicyHash, evidenceDirectory = attempt,
            scope = "Fresh lexical extraction from immutable raw CAS bytes, compared to sealed per-document commitments; no index or learner mutations." };
        await WriteJsonAsync(Path.Combine(attempt, "replay.json"), result, cancellationToken); return result;
    }

    private static async Task RegisterAsync(SourceHarvestIndex index, GitSourceCapture capture, SourceCaptureReceipt receipt, CancellationToken ct)
    {
        index.RegisterCapture(receipt);
        // Import once. Existing entries retain their nonnull lexical association on exact retries.
        if (index.Counts(receipt.CaptureId, ct).AssociatedEntries == receipt.Manifest.Coverage.TreeEntries) return;
        var batch = new List<SourceDocumentAssociation>(256);
        await foreach (var entry in capture.EntriesAsync(receipt.CaptureId, ct))
        {
            batch.Add(new(entry, null));
            if (batch.Count == 256) { index.AssociateDocuments(receipt.CaptureId, batch, ct); batch.Clear(); }
        }
        if (batch.Count != 0) index.AssociateDocuments(receipt.CaptureId, batch, ct);
    }
    private FileStream Lease()
    {
        if (readOnly) throw new InvalidOperationException("This source harvest workspace is read-only.");
        var path = Path.Combine(RootDirectory, ".writer.lock"); CheckPath(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Source harvest paths cannot traverse a reparse point.");
    }
    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    { CheckPath(path); await using var source = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct)); }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        CheckPath(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value)), ct); await stream.FlushAsync(ct); stream.Flush(true); }
        CheckPath(path); File.Move(temporary, path, overwrite: false);
    }
}
