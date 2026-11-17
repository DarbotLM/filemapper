namespace FileMapper.Harvest;

public sealed record SourceBlobDescriptor(string Sha256, long ByteLength, string RelativePath);
public sealed record SourceBlobPutResult(string Status, SourceBlobDescriptor? Blob, long BytesRead, string? Detail = null);
public sealed record SourceBlobVerification(string Status, SourceBlobDescriptor? Blob, string? ActualSha256, long BytesRead, string? Detail = null);

public sealed record SourceLexiconOptions(long MaxDocumentBytes = 16L * 1024 * 1024,
    int MaxTokenUtf16Length = 4096, int ReadBufferBytes = 65536);
public sealed record SourceLexiconPolicy(string ContractId, string DecoderContractId, string RuntimeVersion,
    string UnicodeDataBinding, SourceLexiconOptions Options, string PolicyHash);

/// <summary>Content-relative occurrence; repository/revision/path association is a separate document occurrence.</summary>
public sealed record SourceLexicalOccurrence(string Schema, string PolicyHash, string SourceSha256,
    string OccurrenceId, long Ordinal, string Kind, long ByteStart, long ByteLength,
    long Utf16Start, long Utf16Length, long StartLine, long StartColumn, string ColumnUnit,
    string? Text, string? NormalizedTerm, string? TermId, bool Fragmented, string? FragmentGroupId,
    int FragmentIndex, bool ContinuedFromPrevious, bool ContinuesToNext, string? RawBytesBase64 = null);

/// <summary>OutputSha256 commits stored bytes; CanonicalNdjsonSha256 commits the exact decompressed UTF-8 NDJSON stream.</summary>
public sealed record SourceLexiconResult(string Schema, string Status, SourceLexiconPolicy Policy,
    string SourceSha256, long SourceBytes, string Encoding, string? OutputPath, string? OutputSha256,
    string? CanonicalNdjsonSha256, string OutputFormat, long Occurrences, long WordOccurrences,
    long ProcessedBytes, long DecodedUtf16Units, bool Complete, string? Detail = null);
