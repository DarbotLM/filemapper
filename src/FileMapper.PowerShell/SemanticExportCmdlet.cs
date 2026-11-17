using System.IO.Compression;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileMapper.PowerShell;

[Cmdlet(VerbsData.Export, "FileMapperSemanticSnapshot", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperSemanticExport))]
public sealed class ExportFileMapperSemanticSnapshotCommand : SemanticSnapshotCmdlet
{
    [Parameter(Mandatory = true, Position = 1)] [ValidateNotNullOrEmpty] public string Path { get; set; } = "";
    [Parameter] [ValidateSet("none", "gzip")] public string Compression { get; set; } = "none";
    [Parameter] [ValidateRange(1, 1_073_741_824)] public int MaximumRecordBytes { get; set; } = 64 * 1024 * 1024;
    [Parameter] [ValidateRange(1L, long.MaxValue)] public long MaximumDownloadBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    [Parameter] [ValidateRange(1L, long.MaxValue)] public long MaximumExpandedBytes { get; set; } = 256L * 1024 * 1024 * 1024;
    protected override void ProcessRecord()
    {
        var target = Protect(() =>
        {
            var resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Path, out var provider, out _);
            if (provider.Name != "FileSystem") throw new ArgumentException("Semantic export requires a FileSystem destination.");
            return resolved;
        }, Path, "FileMapper.Semantic.ExportPath");
        if (!ShouldProcess(target, $"Export and verify snapshot {SnapshotId} ({Compression}); create new file without overwriting")) return;
        WriteObject(Protect(() => Export(target), target, "FileMapper.Semantic.Export"));
    }

    private FileMapperSemanticExport Export(string target)
    {
        if (File.Exists(target) || Directory.Exists(target)) throw new FileMapperExportDestinationExistsException();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, ".filemapper-semantic-" + Guid.NewGuid().ToString("N") + ".partial");
        var owned = false;
        try
        {
            using var deadline = Deadline();
            using var client = CreateClient();
            using var response = client.GetAsync(SnapshotRoute + "/semantic-export?compression=" + Compression.ToLowerInvariant(),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
            EnsureSuccess(response, deadline.Token);
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("Semantic export must be a raw file payload without HTTP Content-Encoding.");
            if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
                throw new InvalidDataException("Export exceeds MaximumDownloadBytes.");
            long fileBytes;
            FileMapperSemanticExportResult result;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                owned = true;
                using var source = response.Content.ReadAsStream(deadline.Token);
                fileBytes = CopyBounded(source, file, MaximumDownloadBytes, deadline.Token);
                if (response.Content.Headers.ContentLength is long expectedLength && expectedLength != fileBytes)
                    throw new InvalidDataException("Export Content-Length does not match the downloaded payload.");
                file.Position = 0;
                if (Compression.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    var trailer = SemanticGzipFraming.Validate(file, MaximumExpandedBytes, deadline.Token);
                    file.Position = 0;
                    var checksum = new SemanticGzipChecksum();
                    using var decoded = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
                    result = SemanticExportVerifier.Verify(decoded, SnapshotId, MaximumRecordBytes, MaximumExpandedBytes, deadline.Token, checksum.Append);
                    if (trailer.Crc32 != checksum.Crc32 || trailer.Size != unchecked((uint)checksum.Bytes) || trailer.ExpandedBytes != checksum.Bytes)
                        throw new InvalidDataException("Gzip trailer CRC32 or expanded size does not match the complete export.");
                }
                else result = SemanticExportVerifier.Verify(file, SnapshotId, MaximumRecordBytes, MaximumExpandedBytes, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            deadline.Token.ThrowIfCancellationRequested();
            // Same-directory rename is atomic, and overwrite:false closes the destination-exists race.
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { throw new FileMapperExportDestinationExistsException(); }
            owned = false;
            return new(target, Compression.ToLowerInvariant(), fileBytes, result);
        }
        finally
        {
            if (owned)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { WriteWarning("Could not remove the incomplete export temporary file: " + temporary); }
            }
        }
    }
}

internal sealed class FileMapperExportDestinationExistsException : IOException
{
    public FileMapperExportDestinationExistsException() : base("Export destination already exists; choose a new path.") { }
}

// One record is held at a time; the digest consumes ORIGINAL UTF-8 bytes, never reserialized JSON.
internal static class SemanticExportVerifier
{
    private const string Schema = "filemapper.semantic-export/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 64, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };

    internal static FileMapperSemanticExportResult Verify(Stream stream, string snapshotId, int maximumRecordBytes,
        long maximumExpandedBytes, CancellationToken token, Action<ReadOnlyMemory<byte>>? onBytes = null)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var line = new MemoryStream();
        var buffer = new byte[65536]; long expanded = 0, contentBytes = 0, lineNumber = 0;
        long objects = 0, edges = 0, observations = 0, locations = 0, findings = 0;
        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        FileMapperSemanticExportResult? result = null;
        while (true)
        {
            var count = stream.ReadAsync(buffer, token).AsTask().GetAwaiter().GetResult();
            if (count == 0) break;
            if (count > maximumExpandedBytes - expanded) throw Invalid("Decoded export exceeds MaximumExpandedBytes.");
            expanded += count;
            onBytes?.Invoke(buffer.AsMemory(0, count));
            var position = 0;
            while (position < count)
            {
                token.ThrowIfCancellationRequested();
                if (result is not null) throw Invalid("Data follows the completed footer.");
                var newline = Array.IndexOf(buffer, (byte)'\n', position, count - position);
                var end = newline < 0 ? count : newline + 1;
                if (end - position > maximumRecordBytes - line.Length) throw Invalid("Export record exceeds MaximumRecordBytes (including LF).");
                line.Write(buffer, position, end - position);
                position = end;
                if (newline < 0) continue;
                var memory = line.GetBuffer().AsMemory(0, checked((int)line.Length));
                if (memory.Length <= 1) throw Invalid("Empty NDJSON records are invalid.");
                using var document = JsonDocument.Parse(memory[..^1], new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                RejectDuplicateProperties(root, token);
                var type = RequiredString(root, "type");
                if (lineNumber++ == 0)
                {
                    if (type != "header" || RequiredString(root, "schema") != Schema ||
                        RequiredString(root.GetProperty("snapshot"), "id") != snapshotId)
                        throw Invalid("Export header schema or snapshot does not match the request.");
                }
                else
                {
                    switch (type)
                    {
                        case "object":
                            var item = root.GetProperty("item");
                            if (RequiredString(item.GetProperty("sourceReference"), "snapshotId") != snapshotId ||
                                RequiredString(item.GetProperty("snapshotWindow"), "snapshotId") != snapshotId)
                                throw Invalid("Semantic object belongs to another snapshot.");
                            var kind = RequiredString(item, "some_type");
                            kinds[kind] = checked(kinds.GetValueOrDefault(kind) + 1); objects++; break;
                        case "edge": RequireObject(root, "item"); edges++; break;
                        case "observation":
                            if (RequiredString(root.GetProperty("item"), "snapshotId") != snapshotId) throw Invalid("Observation belongs to another snapshot.");
                            observations++; break;
                        case "location":
                            if (RequiredString(root.GetProperty("item"), "snapshotId") != snapshotId) throw Invalid("Location belongs to another snapshot.");
                            locations++; break;
                        case "finding": RequireObject(root, "item"); findings++; break;
                        case "footer":
                            if (!root.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
                                throw Invalid("Export footer is not complete.");
                            result = root.GetProperty("result").Deserialize<FileMapperSemanticExportResult>(Json) ?? throw Invalid("Missing export result.");
                            var actualHash = Convert.ToHexStringLower(digest.GetHashAndReset());
                            if (result.Schema != Schema || result.SnapshotId != snapshotId || result.ContentSha256 != actualHash ||
                                result.ContentBytes != contentBytes || result.Objects != objects || result.Edges != edges ||
                                result.Observations != observations || result.Locations != locations || result.Findings != findings ||
                                result.ObjectKinds.Count != kinds.Count || kinds.Any(pair => !result.ObjectKinds.TryGetValue(pair.Key, out var expected) || expected != pair.Value))
                                throw Invalid("Export footer digest, counts, schema or snapshot does not match the received content.");
                            break;
                        default: throw Invalid("Unexpected semantic export record type: " + type);
                    }
                }
                if (result is null) { digest.AppendData(memory.Span); contentBytes = checked(contentBytes + memory.Length); }
                line.SetLength(0);
            }
        }
        token.ThrowIfCancellationRequested();
        if (line.Length != 0 || result is null) throw Invalid("Export ended without a complete, LF-terminated final footer.");
        return result;
    }

    private static void RejectDuplicateProperties(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Duplicate JSON property in semantic export.");
                RejectDuplicateProperties(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item, token);
    }
    private static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString())) throw Invalid("Missing string field: " + name);
        return property.GetString()!;
    }
    private static void RequireObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) throw Invalid("Missing object field: " + name);
    }
    private static InvalidDataException Invalid(string message) => new(message);
}

internal sealed class SemanticGzipChecksum
{
    private static readonly uint[] Table = CreateTable();
    private uint crc = uint.MaxValue;
    internal long Bytes { get; private set; }
    internal uint Crc32 => ~crc;
    internal void AppendByte(byte value) { crc = Table[(crc ^ value) & 255] ^ (crc >> 8); Bytes++; }
    internal void Append(ReadOnlyMemory<byte> memory)
    {
        foreach (var value in memory.Span) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
        Bytes = checked(Bytes + memory.Length);
    }
    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320 : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
