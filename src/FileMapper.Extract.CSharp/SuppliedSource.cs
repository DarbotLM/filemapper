using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using FileMapper.Domain;

namespace FileMapper.Extract.CSharp;

public sealed record SuppliedSourceDocument(string LogicalPath, ReadOnlyMemory<byte> Bytes, string ExpectedSha256, long ExpectedByteLength);
public sealed record SuppliedSourceOptions(int MaxDocuments = 256, long MaxDocumentBytes = 2 * 1024 * 1024, long MaxTotalBytes = 32 * 1024 * 1024,
    int MaxGraphRecords = 500_000);
/// <summary>Native aggregate graph admission failed before adding another node, edge, observation or finding.</summary>
public sealed class SourceGraphLimitException(int maximumGraphRecords, string recordKind)
    : InvalidOperationException($"Supplied source graph record limit of {maximumGraphRecords} exceeded while adding a new {recordKind}; no completed analysis is returned.")
{
    public int MaximumGraphRecords { get; } = maximumGraphRecords;
    public string RecordKind { get; } = recordKind;
}
public sealed record SuppliedSourceDocumentStatus(string LogicalPath, string ExpectedSha256, long ExpectedByteLength,
    long ActualBytes, string Status, string? Encoding, int BomByteCount, long DecodedUtf16Units, string? Detail = null);
/// <summary>Complete describes input admission only, not parser correctness, semantic completeness or runtime behavior.</summary>
public sealed record SuppliedSourceCoverage(int ProvidedDocuments, int AdmittedDocuments, int RejectedDocuments,
    long ProvidedBytes, long AdmittedBytes, bool Complete);
public sealed record SuppliedSourceAnalysis(AnalysisResult Analysis, string ContextId, string SourceScope,
    SuppliedSourceCoverage Coverage, IReadOnlyList<SuppliedSourceDocumentStatus> Documents)
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public DateTimeOffset? SuppliedObservedAt { get; init; }
}
public sealed record SuppliedSourceDecodeResult(string Status, DecodedSuppliedSource? Decoded, string? Detail = null);

/// <summary>Strictly decoded text with an O(n) exact boundary map. Parser coordinates exclude the leading BOM.</summary>
public sealed class DecodedSuppliedSource
{
    private readonly int[] offsets;
    public string Text { get; }
    public string Encoding { get; }
    public int BomByteCount { get; }
    public long SourceByteLength { get; }
    public string SourceSha256 { get; }
    internal DecodedSuppliedSource(string text, string encoding, int bom, int sourceBytes, string hash, int[] boundaries)
    { Text = text; Encoding = encoding; BomByteCount = bom; SourceByteLength = sourceBytes; SourceSha256 = hash; offsets = boundaries; }
    public bool TryMapUtf16Span(int start, int length, out long byteStart, out long byteLength)
    {
        byteStart = byteLength = 0;
        if (start < 0 || length < 0 || start > Text.Length || length > Text.Length - start) return false;
        var first = offsets[start]; var last = offsets[start + length];
        if (first < 0 || last < 0) return false;
        byteStart = first; byteLength = last - first; return true;
    }
}

public static class SuppliedSourceDecoder
{
    public const string ContractId = "filemapper.supplied-source-decoder/1";
    public const long MaximumDocumentBytes = 16L * 1024 * 1024;
    public const int MaximumDecodedUtf16Units = 16 * 1024 * 1024;
    public static SuppliedSourceDecodeResult Decode(ReadOnlyMemory<byte> bytes, string expectedSha256, long expectedByteLength,
        long maxDocumentBytes = 2 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ValidateCommitment(expectedSha256, expectedByteLength);
        if (maxDocumentBytes is < 1 or > MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maxDocumentBytes));
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length != expectedByteLength) return new("length-mismatch", null, "Supplied bytes differ from the declared source length.");
        if (bytes.Length > maxDocumentBytes) return new("document-byte-budget", null, "Source exceeds the explicit per-document byte limit.");
        // ReadOnlyMemory does not prevent its owner from mutating the backing array. Hash and decode our own bounded copy.
        var copy = bytes.ToArray(); cancellationToken.ThrowIfCancellationRequested();
        var hash = Convert.ToHexStringLower(SHA256.HashData(copy)); cancellationToken.ThrowIfCancellationRequested();
        if (hash != expectedSha256) return new("hash-mismatch", null, "Supplied bytes differ from the declared source SHA-256.");
        var raw = copy.AsSpan(); string encoding = "utf-8"; int bom = 0;
        if (raw.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || raw.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            return new("unsupported-encoding", null, "UTF-32 is unsupported; only strict UTF-8 and BOM-marked UTF-16 are admitted.");
        if (raw.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (raw.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = "utf-16le"; bom = 2; }
        else if (raw.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = "utf-16be"; bom = 2; }
        string text;
        try
        {
            var decoder = encoding switch
            {
                "utf-16le" => (Encoding)new UnicodeEncoding(false, false, true),
                "utf-16be" => new UnicodeEncoding(true, false, true),
                _ => new UTF8Encoding(false, true)
            };
            text = decoder.GetString(raw[bom..]);
        }
        catch (DecoderFallbackException) { return new("invalid-encoding", null, "Source contains an invalid byte sequence; replacement decoding is forbidden."); }
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > MaximumDecodedUtf16Units) return new("decoded-unit-budget", null, "Decoded UTF-16 units exceed the hard decoder limit.");
        if (text.Contains('\0')) return new("binary-input", null, "Decoded NUL is classified as binary source; no text prefix is admitted.");
        var offsets = new int[text.Length + 1]; int index = 0, position = bom;
        while (index < text.Length)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            offsets[index] = position;
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
                throw new InvalidDataException("Strict decoder returned an invalid UTF-16 scalar.");
            if (consumed == 2) offsets[index + 1] = -1;
            position += encoding == "utf-8" ? rune.Utf8SequenceLength : consumed * 2;
            index += consumed;
        }
        offsets[text.Length] = position;
        if (position != copy.Length) throw new InvalidDataException("Decoded source boundaries do not cover the exact source bytes.");
        cancellationToken.ThrowIfCancellationRequested();
        return new("admitted", new(text, encoding, bom, copy.Length, hash, offsets));
    }
    internal static void ValidateCommitment(string sha256, long length)
    {
        if (sha256 is null || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A lowercase source SHA-256 is required.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    }
}

public sealed record PreparedSuppliedSource(string LogicalPath, DecodedSuppliedSource Decoded);
/// <summary>Shared bounded input admission; it never reads logical paths or resolves repository dependencies.</summary>
public sealed record SuppliedSourceBatch(IReadOnlyList<PreparedSuppliedSource> Admitted,
    IReadOnlyList<SuppliedSourceDocumentStatus> Documents, SuppliedSourceCoverage Coverage)
{
    public static SuppliedSourceBatch Prepare(string snapshotId, string contextId, IReadOnlyList<SuppliedSourceDocument> documents,
        SuppliedSourceOptions? options, IReadOnlyCollection<string> supportedExtensions, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(snapshotId, nameof(snapshotId), 4096); ValidateIdentity(contextId, nameof(contextId), 4096);
        ArgumentNullException.ThrowIfNull(documents); ArgumentNullException.ThrowIfNull(supportedExtensions); options ??= new();
        if (options.MaxDocuments is < 1 or > 2000 || options.MaxDocumentBytes is < 1 or > SuppliedSourceDecoder.MaximumDocumentBytes || options.MaxTotalBytes is < 1 or > 64L * 1024 * 1024 || options.MaxGraphRecords is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Input limits require 1–2,000 documents, 1–16 MiB/document, 1–64 MiB total bytes and 1–1,000,000 aggregate graph records.");
        cancellationToken.ThrowIfCancellationRequested();
        if (documents.Count > options.MaxDocuments) throw new ArgumentException("Supplied batch exceeds the explicit document count limit.");
        var inputs = documents.ToArray(); var identities = new HashSet<string>(StringComparer.Ordinal);
        // Validate all identities before hashing or parsing any document, including documents later rejected by a byte budget.
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(input);
            ValidateIdentity(input.LogicalPath, nameof(input.LogicalPath), 16384);
            if (!identities.Add(input.LogicalPath)) throw new ArgumentException("Duplicate or conflicting logical source identity.");
            SuppliedSourceDecoder.ValidateCommitment(input.ExpectedSha256, input.ExpectedByteLength);
            if (Uri.TryCreate(input.LogicalPath, UriKind.Absolute, out var uri) && !uri.IsFile && (uri.Query.Length != 0 || uri.Fragment.Length != 0))
                throw new ArgumentException("Logical source URIs must not contain a query or fragment.");
        }
        var accepted = new List<PreparedSuppliedSource>(); var statuses = new List<SuppliedSourceDocumentStatus>();
        long providedBytes = 0, admittedBytes = 0, examinedBytes = 0;
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested(); providedBytes = checked(providedBytes + input.Bytes.Length);
            SuppliedSourceDecodeResult result;
            if (!supportedExtensions.Contains(Extension(input.LogicalPath), StringComparer.OrdinalIgnoreCase))
                result = new("unsupported-language", null, "The source extension is unsupported by this language adapter.");
            else if (input.Bytes.Length > options.MaxDocumentBytes)
                result = new("document-byte-budget", null, "Source exceeds the explicit per-document byte limit.");
            else if (input.Bytes.Length > options.MaxTotalBytes - examinedBytes)
                result = new("total-byte-budget", null, "Source exceeds the remaining batch byte budget; no prefix is parsed.");
            else
            {
                examinedBytes += input.Bytes.Length;
                result = SuppliedSourceDecoder.Decode(input.Bytes, input.ExpectedSha256, input.ExpectedByteLength, options.MaxDocumentBytes, cancellationToken);
            }
            var decoded = result.Decoded;
            if (decoded is not null) { accepted.Add(new(input.LogicalPath, decoded)); admittedBytes += decoded.SourceByteLength; }
            statuses.Add(new(input.LogicalPath, input.ExpectedSha256, input.ExpectedByteLength, input.Bytes.Length,
                result.Status, decoded?.Encoding, decoded?.BomByteCount ?? 0, decoded?.Text.Length ?? 0, result.Detail));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(accepted, statuses, new(inputs.Length, accepted.Count, inputs.Length - accepted.Count, providedBytes, admittedBytes, accepted.Count == inputs.Length));
    }
    public static string Extension(string logicalPath)
    {
        if (Uri.TryCreate(logicalPath, UriKind.Absolute, out var uri) && !uri.IsFile)
            return Path.GetExtension(uri.GetComponents(UriComponents.Path, UriFormat.Unescaped));
        return Path.GetExtension(logicalPath);
    }
    private static void ValidateIdentity(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("A bounded, nonempty identity without control characters is required.", name);
        for (var i = 0; i < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(i), out _, out var consumed) != OperationStatus.Done)
                throw new ArgumentException("Source identities must contain valid Unicode scalar values.", name);
            i += consumed;
        }
    }
}
