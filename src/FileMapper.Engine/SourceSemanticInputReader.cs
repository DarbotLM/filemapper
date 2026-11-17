using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;

namespace FileMapper.Engine;

internal static class SourceSemanticInputReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task<SourceSemanticInput> ReadAsync(string corpus, SourceSemanticRequest request,
        SourceSemanticOptions options, Func<SourceSemanticDocument, CancellationToken, Task> recordDocument, CancellationToken token)
    {
        Hash(request.CaptureId); Hash(request.ExtractionId);
        if (request.EntryIds is { Count: > 2000 } || request.EntryIds is { Count: 0 }) throw new ArgumentException("Select 1–2,000 entry IDs, or omit entryIds for the whole capture denominator.");
        HashSet<string>? requested = request.EntryIds is null ? null : new(StringComparer.Ordinal);
        if (requested is not null) foreach (var id in request.EntryIds!)
            if (id is null || !id.StartsWith("entry:", StringComparison.Ordinal) || id.Length != 70 || !id[6..].All(char.IsAsciiHexDigitLower) || !requested.Add(id))
                throw new ArgumentException("Entry IDs must be unique native entry commitments.");
        var captureStore = new GitSourceCapture(corpus, readOnly: true);
        var capturePath = Ordinary(Path.Combine(corpus, "captures", request.CaptureId + ".json"));
        await using var captureInput = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var capture = await NativeRequestJson.ReadAsync<SourceCaptureManifest>(captureInput, 4 * 1024 * 1024, token);
        if (CanonicalJson.Sha256(capture) != request.CaptureId) throw new InvalidDataException("Capture manifest differs from its requested commitment.");
        if (capture.Schema != "filemapper.source-capture/1" || capture.ObjectFormat is not ("sha1" or "sha256"))
            throw new InvalidDataException("Unsupported source capture schema or Git object format.");
        var extractionPath = Ordinary(Path.Combine(corpus, "extractions", request.ExtractionId + ".json"));
        await using var extractionInput = new FileStream(extractionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var extraction = await NativeRequestJson.ReadAsync<SourceExtractionManifest>(extractionInput, 1024 * 1024, token);
        if (extraction.Schema != "filemapper.source-extraction/1") throw new InvalidDataException("Unsupported source extraction schema.");
        if (CanonicalJson.Sha256(extraction) != request.ExtractionId || extraction.CaptureId != request.CaptureId)
            throw new InvalidDataException("Extraction does not match its selected capture commitment.");
        if (CanonicalJson.Serialize(SourceLexicon.Describe(extraction.Policy.Options)) != CanonicalJson.Serialize(extraction.Policy))
            throw new InvalidOperationException("Semantic word joins require the exact saved lexical runtime and policy.");
        await using var capturedStream = captureStore.Blobs.OpenRead(capture.Entries.Sha256);
        await using var extractionStream = captureStore.Blobs.OpenRead(extraction.Entries.Sha256);
        await using var rawTree = captureStore.Blobs.OpenRead(capture.RawTree.Sha256);
        // Hash and enumerate the same held handles. A verified path is not a lease on a later reopen.
        await VerifyStreamAsync(rawTree, capture.RawTree, captureStore.Blobs.MaxDocumentBytes, token);
        await VerifyStreamAsync(capturedStream, capture.Entries, captureStore.Blobs.MaxDocumentBytes, token);
        await VerifyStreamAsync(extractionStream, extraction.Entries, captureStore.Blobs.MaxDocumentBytes, token);
        await using var recorded = Lines(extractionStream, token).GetAsyncEnumerator(token);
        await using var tree = TreeRecords(rawTree, token).GetAsyncEnumerator(token);
        var selected = new List<SourceSemanticSelectedDocument>();
        var statuses = new Dictionary<string, long>(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        long total = 0, chosen = 0, supported = 0, unsupported = 0, admittedBytes = 0;
        long captured = 0, complete = 0, incomplete = 0, units = 0, words = 0, associatedBytes = 0;
        var unique = new Dictionary<string, long>(StringComparer.Ordinal);
        using var inputDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var semanticDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        inputDigest.AppendData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new { Schema = "filemapper.semantic-input-set/1", request.CaptureId, request.ExtractionId, request.EntryIds, Options = options, Order = "capture tree order; parser inputs sorted by logical URI" }) + "\n"));
        semanticDigest.AppendData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new { Schema = "filemapper.semantic-context-inputs/1",
            capture.RepositoryId, capture.Commit, Order = "selected capture tree order; parser inputs sorted by logical URI" }) + "\n"));
        await foreach (var line in Lines(capturedStream, token))
        {
            if (++total > 2_000_000) throw new InvalidDataException("Capture entry count exceeds its admission bound.");
            var entry = JsonSerializer.Deserialize<CapturedSourceEntry>(line, Json) ?? throw new InvalidDataException("Invalid captured entry.");
            ValidateEntry(capture, entry);
            if (!allIds.Add(entry.Entry.EntryId)) throw new InvalidDataException("Duplicate capture entry identity.");
            if (!await tree.MoveNextAsync() || !TreeMatches(tree.Current, entry.Entry)) throw new InvalidDataException("Capture entry differs from its retained raw tree record.");
            if (!await recorded.MoveNextAsync()) throw new InvalidDataException("Extraction associations ended early.");
            var lexical = JsonSerializer.Deserialize<SourceExtractionEntry>(recorded.Current, Json) ?? throw new InvalidDataException("Invalid lexical association.");
            if (lexical.EntryId != entry.Entry.EntryId || lexical.DocumentId != entry.DocumentId || lexical.SourceSha256 != entry.Blob?.Sha256 || lexical.CaptureStatus != entry.Status)
                throw new InvalidDataException("Lexical association differs from the capture occurrence.");
            if (lexical.Occurrences < 0 || lexical.WordOccurrences < 0 || lexical.WordOccurrences > lexical.Occurrences) throw new InvalidDataException("Invalid saved lexical counts.");
            if (entry.Status == "captured")
            {
                captured++; associatedBytes = checked(associatedBytes + entry.Blob!.ByteLength);
                if (unique.TryGetValue(entry.Blob.Sha256, out var oldSize) && oldSize != entry.Blob.ByteLength) throw new InvalidDataException("Conflicting raw object sizes.");
                unique[entry.Blob.Sha256] = entry.Blob.ByteLength;
                if (lexical.Complete) complete++; else incomplete++;
                units = checked(units + lexical.Occurrences); words = checked(words + lexical.WordOccurrences);
            }
            var include = requested is null || requested.Contains(entry.Entry.EntryId);
            inputDigest.AppendData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new { Entry = entry, Extraction = lexical, Selected = include }) + "\n"));
            if (!include) continue;
            chosen++; found.Add(entry.Entry.EntryId);
            var extension = Path.GetExtension(entry.Entry.RepositoryPath).ToLowerInvariant();
            var language = extension == ".cs" ? "C#" : extension is ".ps1" or ".psm1" or ".psd1" ? "PowerShell" : null;
            string? uri = null, detail = null; string status;
            if (entry.Status != "captured") status = "source-unavailable";
            else if (entry.Entry.Mode is not ("100644" or "100755")) status = "unsupported-entry-kind";
            else if (entry.Entry.PathEncoding != "utf-8" || entry.Entry.RepositoryPath.Length > 4096) status = "unsupported-source-path";
            else if (language is null) status = "unsupported-language";
            else
            {
                supported++;
                uri = LogicalUri(capture, entry.Entry);
                if (selected.Count >= options.MaximumDocuments) status = "document-count-budget";
                else if (entry.Blob!.ByteLength > options.MaximumDocumentBytes) status = "document-byte-budget";
                else if (entry.Blob.ByteLength > options.MaximumTotalBytes - admittedBytes) status = "total-byte-budget";
                else
                {
                    await using var source = captureStore.Blobs.OpenRead(entry.Blob.Sha256);
                    if (source.Length != entry.Blob.ByteLength) throw new InvalidDataException("Selected source length differs from its commitment.");
                    var bytes = new byte[checked((int)source.Length)]; await source.ReadExactlyAsync(bytes, token);
                    if (source.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.Blob.Sha256)
                        throw new InvalidDataException("Selected source bytes differ from their commitment.");
                    VerifyGitBlob(entry.Entry.GitObjectId, bytes, capture.ObjectFormat);
                    selected.Add(new(entry, lexical, uri, language, bytes)); admittedBytes += bytes.Length;
                    status = "admitted-for-parser";
                }
            }
            if (status.StartsWith("unsupported-", StringComparison.Ordinal)) unsupported++;
            statuses[status] = statuses.GetValueOrDefault(status) + 1;
            var document = new SourceSemanticDocument(entry.Entry.EntryId, entry.DocumentId, entry.Entry.RepositoryPath, uri,
                entry.Blob?.Sha256, entry.Blob?.ByteLength, language, status, lexical.LexicalStatus, detail);
            // Observation-run commitments belong in InputSetSha256; content/model context must survive an identical re-capture.
            semanticDigest.AppendData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(document) + "\n"));
            await recordDocument(document, token);
        }
        if (await recorded.MoveNextAsync() || await tree.MoveNextAsync()) throw new InvalidDataException("Raw tree or lexical associations contain extra entries.");
        if (requested is not null && found.Count != requested.Count) throw new ArgumentException("A selected entry is absent from this capture.");
        var coverage = extraction.Coverage;
        if (total != capture.Coverage.TreeEntries || captured != capture.Coverage.CapturedEntries || associatedBytes != capture.Coverage.AssociatedSourceBytes ||
            unique.Count != capture.Coverage.UniqueRetainedBlobs || unique.Values.Sum() != capture.Coverage.UniqueRetainedBytes ||
            total != coverage.Entries || captured != coverage.CapturedDocuments || complete != coverage.CompleteDocuments || incomplete != coverage.IncompleteDocuments ||
            units != coverage.Occurrences || words != coverage.WordOccurrences || unique.Count != coverage.UniqueLexicons || total - captured != coverage.UnavailableEntries)
            throw new InvalidDataException("Capture/extraction denominators do not match their retained associations.");
        return new(capture, extraction, statuses, selected.OrderBy(d => d.Uri, StringComparer.Ordinal).ToArray(),
            total, chosen, supported, unsupported, Convert.ToHexStringLower(inputDigest.GetHashAndReset()),
            Convert.ToHexStringLower(semanticDigest.GetHashAndReset()));
    }

    internal static string Ordinary(string path) => SourceHarvestConnectionStore.NormalizePath(path);
    internal static void Hash(string value)
    { if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigitLower)) throw new ArgumentException("A lowercase SHA-256 is required."); }
    private static async Task VerifyStreamAsync(Stream stream, SourceBlobDescriptor blob, long maximumBytes, CancellationToken token)
    {
        Hash(blob.Sha256);
        if (blob.ByteLength < 0 || blob.ByteLength > maximumBytes) throw new InvalidDataException("Retained metadata object exceeds the capture store's explicit byte limit.");
        if (stream.Length != blob.ByteLength || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != blob.Sha256)
            throw new InvalidDataException("Retained input object is missing or corrupt.");
        stream.Position = 0;
    }
    private static string LogicalUri(SourceCaptureManifest capture, SourceTreeEntry entry) =>
        "filemapper-source://repo-" + CanonicalJson.HashUtf8(capture.RepositoryId) + "/" + capture.Commit + "/" +
        string.Join('/', entry.RepositoryPath.Split('/').Select(Uri.EscapeDataString));
    private static void VerifyGitBlob(string expected, byte[] bytes, string format)
    {
        using var hash = IncrementalHash.CreateHash(format == "sha1" ? HashAlgorithmName.SHA1 : format == "sha256" ? HashAlgorithmName.SHA256 : throw new InvalidDataException("Unsupported Git object format."));
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0")); hash.AppendData(bytes);
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected) throw new InvalidDataException("Selected raw bytes do not match the declared Git blob object.");
    }
    private static void ValidateEntry(SourceCaptureManifest capture, CapturedSourceEntry value)
    {
        var e = value.Entry; var raw = Convert.FromBase64String(e.RepositoryPathBytes);
        if (raw.Length is 0 or > 1024 * 1024 || raw.Contains((byte)0) || raw[0] == '/') throw new InvalidDataException("Invalid repository path bytes.");
        if (e.GitObjectId.Length != (capture.ObjectFormat == "sha1" ? 40 : 64) || !e.GitObjectId.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("Malformed Git object identity.");
        string display, encoding;
        try { display = new UTF8Encoding(false, true).GetString(raw); encoding = "utf-8"; }
        catch (DecoderFallbackException) { display = Encoding.UTF8.GetString(raw); encoding = "opaque-bytes"; }
        if (display != e.RepositoryPath || encoding != e.PathEncoding || display.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Ambiguous or inconsistent repository path.");
        if (e.EntryId != "entry:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-tree-entry/1", capture.RepositoryId, Revision = capture.Commit, PathBytes = e.RepositoryPathBytes, e.Mode, ObjectId = e.GitObjectId })) throw new InvalidDataException("Invalid native tree entry identity.");
        if (value.Status == "captured")
        {
            if (value.Blob is null || value.Blob.ByteLength < 0 || value.ReportedBlobBytes != value.Blob.ByteLength || e.ObjectType != "blob") throw new InvalidDataException("Invalid retained document descriptor.");
            Hash(value.Blob.Sha256);
            if (value.DocumentId != "document:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-document-occurrence/1", capture.RepositoryId, Revision = capture.Commit, e.RepositoryPathBytes, e.Mode, BlobSha256 = value.Blob.Sha256 })) throw new InvalidDataException("Invalid native document occurrence identity.");
        }
        else if (value.Blob is not null || value.DocumentId is not null) throw new InvalidDataException("Unavailable source claims retained bytes.");
    }
    private static bool TreeMatches(byte[] raw, SourceTreeEntry entry)
    {
        var tab = Array.IndexOf(raw, (byte)'\t'); if (tab < 0) return false;
        var prefix = Encoding.ASCII.GetBytes(entry.Mode + " " + entry.ObjectType + " " + entry.GitObjectId);
        return raw.AsSpan(0, tab).SequenceEqual(prefix) && raw.AsSpan(tab + 1).SequenceEqual(Convert.FromBase64String(entry.RepositoryPathBytes));
    }
    private static async IAsyncEnumerable<byte[]> TreeRecords(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var record = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0) for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (buffer[i] == 0) { if (record.Length == 0) throw new InvalidDataException("Empty raw tree record."); yield return record.ToArray(); record.SetLength(0); }
            else { if (record.Length > 1024 * 1024 + 128) throw new InvalidDataException("Raw tree record exceeds its bound."); record.WriteByte(buffer[i]); }
        }
        if (record.Length != 0) throw new InvalidDataException("Raw tree ended without its NUL delimiter.");
    }
    internal static async IAsyncEnumerable<string> Lines(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 8192, leaveOpen: true);
        var buffer = new char[8192]; var line = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0) for (var i = 0; i < count; i++)
        {
            if (buffer[i] == '\n') { if (line.Length == 0) throw new InvalidDataException("Empty input record."); yield return line.ToString(); line.Clear(); }
            else { if (line.Length >= 16 * 1024 * 1024) throw new InvalidDataException("Input record exceeds its admission bound."); line.Append(buffer[i]); }
        }
        if (line.Length != 0) throw new InvalidDataException("Input record ended without a line delimiter.");
    }
}
