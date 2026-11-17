using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Semantics;

namespace FileMapper.Harvest;

/// <summary>Lossless lexical positions over retained bytes; no parser, translation, binding, or execution claim.</summary>
public static class SourceLexicon
{
    public const string ContractId = "filemapper.source-lexicon/1";
    public const string DecoderContractId = "filemapper.strict-source-decoder/1";
    public const string TermContractId = "filemapper.source-term/nfkc-lower/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SourceLexiconPolicy Describe(SourceLexiconOptions? options = null)
    {
        options ??= new();
        if (options.MaxDocumentBytes < 0 || options.MaxTokenUtf16Length is < 2 or > 65536 || options.ReadBufferBytes is < 4096 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(options), "Document size must be nonnegative, token bound 2–65,536 UTF-16 units and read buffer 4 KiB–1 MiB.");
        var runtime = RuntimeInformation.FrameworkDescription + ";rid=" + RuntimeInformation.RuntimeIdentifier;
        var unicode = "runtime-bound:System.Private.CoreLib/" + typeof(Rune).Assembly.GetName().Version;
        var hash = CanonicalJson.Sha256(new { ContractId, DecoderContractId, TermContractId, RuntimeVersion = runtime,
            UnicodeDataBinding = unicode, Options = options,
            Rules = "strict UTF8 or BOM UTF16; raw-byte half-open spans; BOM excluded from UTF16 coordinates; maximal letter/digit/attached-mark words; whitespace/punctuation/symbol/control runs; original newlines; complete terms NFKC invariant lowercase; bounded explicit fragments; no semantic inference" });
        return new(ContractId, DecoderContractId, runtime, unicode, options, hash);
    }

    public static async Task<SourceLexiconResult> ExtractAsync(SourceBlobStore store, SourceBlobDescriptor blob,
        string newNdjsonPath, SourceLexiconOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(blob);
        SourceBlobStore.ValidateHash(blob.Sha256);
        var policy = Describe(options); options = policy.Options;
        var compressed = newNdjsonPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var format = compressed ? "ndjson-gzip" : "ndjson";
        SourceLexiconResult Unavailable(string status, string detail) => new(ContractId, status, policy, blob.Sha256,
            blob.ByteLength, "unknown", null, null, null, format, 0, 0, 0, 0, false, detail);
        cancellationToken.ThrowIfCancellationRequested();
        if (blob.ByteLength < 0) throw new ArgumentOutOfRangeException(nameof(blob));
        if (blob.ByteLength > options.MaxDocumentBytes) return Unavailable("too-large", "Source exceeds the explicit lexer document byte bound; raw CAS object is retained.");
        // Verify and decode the same open handle. The store descriptor's RelativePath is never trusted as a read path.
        await using var input = store.OpenRead(blob.Sha256);
        if (input.Length != blob.ByteLength) return Unavailable("source-length-mismatch", "Stored source length differs from the supplied descriptor.");
        var verified = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        if (verified != blob.Sha256) return Unavailable("source-hash-mismatch", "Stored source bytes failed SHA-256 verification.");
        input.Position = 0;
        var prefix = new byte[(int)Math.Min(8, input.Length)];
        await input.ReadExactlyAsync(prefix, cancellationToken);
        var (encoding, bom, initialStatus) = Detect(prefix);
        var destination = HarvestPaths.Full(newNdjsonPath);
        HarvestPaths.Directory(Path.GetDirectoryName(destination)!, create: true);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Immutable lexical output already exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".lexicon-" + Guid.NewGuid().ToString("N") + ".part");
        long ordinal = 0, words = 0, processed = 0, utf16 = 0, line = 1, column = 1;
        var status = initialStatus ?? "complete"; string? detail = null;
        string? canonicalHash = null;
        try
        {
            await using (var file = HarvestPaths.CreateNew(temporary))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                GZipStream? gzip = compressed ? new(file, CompressionLevel.Optimal, leaveOpen: true) : null;
                Stream output = gzip ?? (Stream)file;
                var recordBuffer = new ArrayBufferWriter<byte>(8192);
                var termCache = new Dictionary<string, (string Text, string Id)>(StringComparer.Ordinal);
                try
                {
                    async ValueTask Emit(string kind, long start, long length, long charStart, long charLength,
                        long startLine, long startColumn, string? text, bool fragmented = false,
                        string? group = null, int fragmentIndex = 0, bool previous = false, bool next = false, string? rawBytes = null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? term = null, termId = null;
                        // A source term is the normalized raw lexical unit, not an old SOM word or a translation.
                        if (kind == "word" && !fragmented)
                        {
                            if (termCache.TryGetValue(text!, out var cached)) { term = cached.Text; termId = cached.Id; }
                            else
                            {
                                term = text!.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
                                termId = "source-term:" + CanonicalJson.Sha256(new { Model = TermContractId, NormalizedText = term });
                                if (termCache.Count < 4096 && text.Length <= 256 && term.Length <= 1024) termCache.Add(text, (term, termId));
                            }
                        }
                        var id = OccurrenceId(policy.PolicyHash, blob.Sha256, ordinal, kind, start, length);
                        var record = new SourceLexicalOccurrence(ContractId, policy.PolicyHash, blob.Sha256, id, ordinal++,
                            kind, start, length, charStart, charLength, startLine, startColumn, "utf16-code-unit",
                            text, term, termId, fragmented, group, fragmentIndex, previous, next, rawBytes);
                        recordBuffer.Clear(); WriteCanonicalRecord(recordBuffer, record);
                        digest.AppendData(recordBuffer.WrittenSpan); await output.WriteAsync(recordBuffer.WrittenMemory, cancellationToken);
                        if (termId is not null) words++;
                    }

                    if (initialStatus is not null)
                    {
                        var kind = initialStatus == "binary" ? "binary-marker" : "unsupported-encoding";
                        await Emit(kind, 0, prefix.Length, 0, 0, 1, 1, null, rawBytes: Convert.ToBase64String(prefix));
                        processed = prefix.Length;
                        detail = initialStatus == "binary" ? "Recognized binary signature; raw bytes retained, no text semantics inferred."
                            : "UTF-32 BOM is unsupported by this decoder contract; raw bytes retained.";
                    }
                    else
                    {
                        if (bom > 0) { await Emit("bom", 0, bom, 0, 0, 1, 1, "", rawBytes: Convert.ToBase64String(prefix, 0, bom)); processed = bom; }
                        var cursor = new ByteCursor(input, options.ReadBufferBytes, prefix, bom);
                        var run = new StringBuilder(); string? runKind = null; long runStart = 0, runUtf16 = 0, runLine = 1, runColumn = 1, runBytes = 0;
                        string? groupId = null; int fragment = 0; bool priorCr = false;
                        async ValueTask Flush(bool continues)
                        {
                            if (run.Length == 0) return;
                            if (continues && groupId is null) groupId = "source-fragments:" + CanonicalJson.Sha256(new { Model = ContractId, policy.PolicyHash, Source = blob.Sha256, Kind = runKind, ByteStart = runStart });
                            await Emit(runKind!, runStart, runBytes, runUtf16, run.Length, runLine, runColumn,
                                run.ToString(), groupId is not null, groupId, fragment, fragment > 0, continues);
                            run.Clear(); runBytes = 0;
                            if (continues) fragment++; else { fragment = 0; groupId = null; runKind = null; }
                        }
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var decoded = await Decode(cursor, encoding, cancellationToken);
                            if (decoded is null) break;
                            var unit = decoded.Value;
                            if (unit.Error is not null)
                            {
                                await Flush(false);
                                await Emit("invalid-byte-sequence", unit.Start, unit.Bytes, utf16, 0, line, column, null, rawBytes: unit.RawBytes);
                                processed = cursor.Position; status = "invalid-encoding"; detail = unit.Error; break;
                            }
                            if (unit.Rune.Value == 0)
                            {
                                await Flush(false);
                                await Emit("binary-marker", unit.Start, unit.Bytes, utf16, 1, line, column, "\0");
                                utf16++; processed = cursor.Position; status = "binary";
                                detail = "Decoded NUL marks non-text input under this explicit source policy; remaining raw bytes are retained but not lexed."; break;
                            }
                            var kind = Kind(unit.Rune, runKind);
                            if (runKind is not null && runKind != kind) await Flush(false);
                            if (run.Length + unit.Rune.Utf16SequenceLength > options.MaxTokenUtf16Length) await Flush(true);
                            if (run.Length == 0)
                            { runKind = kind; runStart = unit.Start; runUtf16 = utf16; runLine = line; runColumn = column; }
                            Append(run, unit.Rune); runBytes += unit.Bytes; utf16 += unit.Rune.Utf16SequenceLength;
                            if (unit.Rune.Value == '\r') { line++; column = 1; priorCr = true; }
                            else if (unit.Rune.Value == '\n') { if (!priorCr) line++; column = 1; priorCr = false; }
                            else { column += unit.Rune.Utf16SequenceLength; priorCr = false; }
                            processed = cursor.Position;
                        }
                        await Flush(false);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                finally { if (gzip is not null) await gzip.DisposeAsync(); }
                canonicalHash = Convert.ToHexStringLower(digest.GetHashAndReset());
                await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string storedHash;
            await using (var encoded = HarvestPaths.OpenRead(temporary)) storedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(encoded, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested(); HarvestPaths.Publish(temporary, destination);
            return new(ContractId, status, policy, blob.Sha256, blob.ByteLength, encoding, destination, storedHash,
                canonicalHash, format, ordinal, words, processed, utf16, status == "complete" && processed == blob.ByteLength, detail);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async IAsyncEnumerable<SourceLexicalOccurrence> ReadAsync(string ndjsonPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var input = HarvestPaths.OpenRead(ndjsonPath);
        using var gzip = ndjsonPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
        using var reader = new StreamReader(gzip ?? (Stream)input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        var buffer = new char[8192]; var line = new StringBuilder(); long expectedOrdinal = 0, expectedByte = 0, expectedUtf16 = 0;
        string? source = null, policy = null;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            for (int index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var value = buffer[index];
                if (value != '\n')
                { if (line.Length >= 524288) throw new InvalidDataException("Lexical NDJSON record exceeds the bounded reader contract."); line.Append(value); continue; }
                if (line.Length == 0) throw new InvalidDataException("Empty lexical NDJSON record.");
                var occurrence = JsonSerializer.Deserialize<SourceLexicalOccurrence>(line.ToString(), Json) ?? throw new InvalidDataException("Null lexical record.");
                line.Clear();
                ValidateRecord(occurrence);
                source ??= occurrence.SourceSha256; policy ??= occurrence.PolicyHash;
                if (occurrence.Ordinal != expectedOrdinal++ || occurrence.ByteStart != expectedByte || occurrence.Utf16Start != expectedUtf16 ||
                    occurrence.SourceSha256 != source || occurrence.PolicyHash != policy)
                    throw new InvalidDataException("Lexical sequence, contiguous source coordinates, source hash or policy differs.");
                expectedByte = checked(expectedByte + occurrence.ByteLength); expectedUtf16 = checked(expectedUtf16 + occurrence.Utf16Length);
                yield return occurrence;
            }
        }
        if (line.Length != 0) throw new InvalidDataException("Lexical NDJSON ended without its committed line delimiter.");
    }

    private static void ValidateRecord(SourceLexicalOccurrence value)
    {
        if (value.Schema != ContractId || value.Ordinal < 0 || value.ByteStart < 0 || value.ByteLength <= 0 ||
            value.Utf16Start < 0 || value.Utf16Length < 0 || value.StartLine < 1 || value.StartColumn < 1 || value.ColumnUnit != "utf16-code-unit" ||
            value.Kind is not ("word" or "whitespace" or "punctuation" or "symbol" or "control" or "bom" or "binary-marker" or "unsupported-encoding" or "invalid-byte-sequence"))
            throw new InvalidDataException("Malformed lexical occurrence metadata.");
        try { SourceBlobStore.ValidateHash(value.SourceSha256); SourceBlobStore.ValidateHash(value.PolicyHash); }
        catch (ArgumentException error) { throw new InvalidDataException("Malformed lexical commitment.", error); }
        var expectedId = OccurrenceId(value.PolicyHash, value.SourceSha256, value.Ordinal, value.Kind, value.ByteStart, value.ByteLength);
        if (value.OccurrenceId != expectedId || value.Text is not null && value.Text.Length != value.Utf16Length || value.Text is null && value.Utf16Length != 0)
            throw new InvalidDataException("Lexical identity or decoded text extent differs.");
        var term = value.Kind == "word" && !value.Fragmented ? value.Text?.Normalize(NormalizationForm.FormKC).ToLowerInvariant() : null;
        var termId = term is null ? null : "source-term:" + CanonicalJson.Sha256(new { Model = TermContractId, NormalizedText = term });
        if (value.NormalizedTerm != term || value.TermId != termId || value.Kind == "word" && !value.Fragmented && term is null)
            throw new InvalidDataException("Lexical term commitment differs.");
        if (value.RawBytesBase64 is not null && Convert.FromBase64String(value.RawBytesBase64).LongLength != value.ByteLength)
            throw new InvalidDataException("Raw byte marker extent differs.");
    }

    private static string OccurrenceId(string policyHash, string source, long ordinal, string kind, long start, long length)
    {
        // These are validated ASCII constants/hashes and nonnegative integers. This is the exact ordinal-key
        // CanonicalJson preimage, without allocating/reparsing a JsonElement for every source occurrence.
        var preimage = string.Create(CultureInfo.InvariantCulture,
            $"{{\"byteLength\":{length},\"byteStart\":{start},\"kind\":\"{kind}\",\"model\":\"{ContractId}\",\"ordinal\":{ordinal},\"policyHash\":\"{policyHash}\",\"source\":\"{source}\"}}");
        return "source-occurrence:" + CanonicalJson.HashUtf8(preimage);
    }

    private static void WriteCanonicalRecord(ArrayBufferWriter<byte> buffer, SourceLexicalOccurrence value)
    {
        // Fixed DTO keys in ordinal order and the same Utf8JsonWriter escaping as CanonicalJson.Serialize.
        // Decoder-produced text has already been validated as Unicode scalars. Reuse bounded buffers.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", value.ByteLength); writer.WriteNumber("byteStart", value.ByteStart);
            writer.WriteString("columnUnit", value.ColumnUnit); writer.WriteBoolean("continuedFromPrevious", value.ContinuedFromPrevious);
            writer.WriteBoolean("continuesToNext", value.ContinuesToNext); writer.WriteString("fragmentGroupId", value.FragmentGroupId);
            writer.WriteNumber("fragmentIndex", value.FragmentIndex); writer.WriteBoolean("fragmented", value.Fragmented);
            writer.WriteString("kind", value.Kind); writer.WriteString("normalizedTerm", value.NormalizedTerm);
            writer.WriteString("occurrenceId", value.OccurrenceId); writer.WriteNumber("ordinal", value.Ordinal);
            writer.WriteString("policyHash", value.PolicyHash); writer.WriteString("rawBytesBase64", value.RawBytesBase64);
            writer.WriteString("schema", value.Schema); writer.WriteString("sourceSha256", value.SourceSha256);
            writer.WriteNumber("startColumn", value.StartColumn); writer.WriteNumber("startLine", value.StartLine);
            writer.WriteString("termId", value.TermId); writer.WriteString("text", value.Text);
            writer.WriteNumber("utf16Length", value.Utf16Length); writer.WriteNumber("utf16Start", value.Utf16Start);
            writer.WriteEndObject(); writer.Flush();
        }
        buffer.GetSpan(1)[0] = (byte)'\n'; buffer.Advance(1);
    }

    private static (string Encoding, int Bom, string? Status) Detect(byte[] prefix)
    {
        if (prefix.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || prefix.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return ("utf-32", 4, "unsupported-encoding");
        if (prefix.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return ("utf-8", 3, null);
        if (prefix.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return ("utf-16le", 2, null);
        if (prefix.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return ("utf-16be", 2, null);
        if (prefix.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }) || prefix.AsSpan().StartsWith(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) ||
            prefix.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 3, 4 }) || prefix.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b, 8 }) ||
            prefix.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return ("binary", 0, "binary");
        return ("utf-8", 0, null);
    }
    private static string Kind(Rune rune, string? previous)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (Rune.IsLetterOrDigit(rune) || previous == "word" && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) return "word";
        if (Rune.IsWhiteSpace(rune)) return "whitespace";
        if (Rune.IsPunctuation(rune)) return "punctuation";
        if (Rune.IsControl(rune)) return "control";
        return "symbol";
    }
    private static void Append(StringBuilder builder, Rune rune)
    { Span<char> chars = stackalloc char[2]; var length = rune.EncodeToUtf16(chars); builder.Append(chars[..length]); }

    private readonly record struct Decoded(Rune Rune, long Start, int Bytes, string? Error = null, string? RawBytes = null);
    private static async ValueTask<Decoded?> Decode(ByteCursor input, string encoding, CancellationToken token)
    {
        var start = input.Position; int a = await input.Read(token); if (a < 0) return null;
        int b = -1, c = -1, d = -1;
        Decoded Invalid(string reason)
        {
            var bytes = new[] { a, b, c, d }.Where(value => value >= 0).Select(value => (byte)value).ToArray();
            return new(default, start, bytes.Length, reason, Convert.ToBase64String(bytes));
        }
        if (encoding is "utf-16le" or "utf-16be")
        {
            b = await input.Read(token); if (b < 0) return Invalid("Odd byte count in UTF-16 source.");
            var first = encoding == "utf-16le" ? a | b << 8 : a << 8 | b;
            if (first is >= 0xdc00 and <= 0xdfff) return Invalid("Unpaired UTF-16 low surrogate.");
            if (first is >= 0xd800 and <= 0xdbff)
            {
                c = await input.Read(token); if (c < 0) return Invalid("Truncated UTF-16 surrogate pair.");
                d = await input.Read(token); if (d < 0) return Invalid("Truncated UTF-16 surrogate pair.");
                var second = encoding == "utf-16le" ? c | d << 8 : c << 8 | d;
                if (!Rune.TryCreate((char)first, (char)second, out var pair)) return Invalid("Invalid UTF-16 surrogate pair.");
                return new(pair, start, 4);
            }
            return new(new Rune(first), start, 2);
        }
        if (a < 0x80) return new(new Rune(a), start, 1);
        var length = a is >= 0xc2 and <= 0xdf ? 2 : a is >= 0xe0 and <= 0xef ? 3 : a is >= 0xf0 and <= 0xf4 ? 4 : 0;
        if (length == 0) return Invalid("Invalid UTF-8 leading byte; no replacement decoding was performed.");
        b = await input.Read(token); if (b is < 0x80 or > 0xbf) return Invalid("Invalid or truncated UTF-8 continuation.");
        if (length > 2) { c = await input.Read(token); if (c is < 0x80 or > 0xbf) return Invalid("Invalid or truncated UTF-8 continuation."); }
        if (length > 3) { d = await input.Read(token); if (d is < 0x80 or > 0xbf) return Invalid("Invalid or truncated UTF-8 continuation."); }
        var scalar = length == 2 ? (a & 31) << 6 | b & 63 : length == 3 ? (a & 15) << 12 | (b & 63) << 6 | c & 63 : (a & 7) << 18 | (b & 63) << 12 | (c & 63) << 6 | d & 63;
        if (length == 3 && scalar < 0x800 || length == 4 && scalar < 0x10000 || !Rune.TryCreate(scalar, out var rune)) return Invalid("Overlong or invalid Unicode scalar in UTF-8 source.");
        return new(rune, start, length);
    }

    private sealed class ByteCursor
    {
        private readonly Stream stream;
        private readonly byte[] buffer;
        private int index, available;
        internal long Position { get; private set; }
        internal ByteCursor(Stream stream, int bufferSize, byte[] prefix, int skip)
        { this.stream = stream; buffer = new byte[bufferSize]; prefix.CopyTo(buffer, 0); available = prefix.Length; index = skip; Position = skip; }
        internal async ValueTask<int> Read(CancellationToken token)
        {
            if (index == available) { available = await stream.ReadAsync(buffer, token); index = 0; if (available == 0) return -1; }
            Position++; return buffer[index++];
        }
    }
}
