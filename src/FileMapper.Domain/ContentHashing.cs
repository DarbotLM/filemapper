using System.Globalization;

namespace FileMapper.Domain;

/// <summary>Versioned, bounded analysis of content observed after a filesystem snapshot.</summary>
public sealed record HashingOptions(
    string Mode = "exact", string? ScopePath = null, long MinimumBytes = 0,
    int MaxIdentities = 1_000_000, int MaxPaths = 2_000_000,
    long MaxBytesRead = 1_099_511_627_776, int Workers = 2, long BytesPerSecond = 0,
    int SampleWindowBytes = 65_536, int ChunkMinimumBytes = 65_536,
    int ChunkAverageBytes = 262_144, int ChunkMaximumBytes = 1_048_576,
    int MaxChunksPerFile = 65_536, long MaxChunkRecords = 2_000_000,
    int MaxOverlapPairs = 100_000, int MaxChunkFanout = 256, long MaxPairComparisons = 2_000_000,
    bool ReuseCache = true)
{
    public const string ModelVersion = "staged-content/v1";
    public void Validate()
    {
        if (Mode is not ("exact" or "probable" or "partial" or "all")) throw new ArgumentException("Mode must be exact, probable, partial or all.");
        if (MinimumBytes < 0 || MaxBytesRead < 0 || MaxBytesRead > 1L << 60) throw new ArgumentException("Content byte bounds are invalid.");
        if (MaxIdentities is < 1 or > 10_000_000 || MaxPaths < MaxIdentities || MaxPaths > 20_000_000) throw new ArgumentException("Identity/path admission bounds are invalid.");
        if (Workers is < 1 or > 16 || BytesPerSecond < 0 || BytesPerSecond > 1L << 40) throw new ArgumentException("Hash worker/rate bounds are invalid.");
        new FileSamplePolicy(SampleWindowBytes).Validate();
        new FileChunkPolicy(ChunkMinimumBytes, ChunkAverageBytes, ChunkMaximumBytes, MaxChunksPerFile).Validate();
        if (MaxChunkRecords is < 1 or > 20_000_000 || MaxOverlapPairs is < 1 or > 1_000_000 || MaxChunkFanout is < 2 or > 1024 || MaxPairComparisons is < 1 or > 100_000_000) throw new ArgumentException("Chunk/overlap bounds are invalid.");
    }
}

public sealed record HashingRequest(string SnapshotId, HashingOptions? Options = null);
public sealed record FileContentReadRequest(string Path, string BoundaryRoot, string ExpectedIdentity,
    long ExpectedLength, DateTimeOffset ExpectedModifiedAt, string? ExpectedVersionKey = null,
    IReadOnlyList<string>? ExpectedPaths = null, bool PathsComplete = false);

public sealed record FileContentVersion(string Identity, string VolumeId, string FileSystem,
    long Length, long? AllocatedBytes, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt,
    DateTimeOffset ChangedAt, string Attributes, long LinkCount, string? JournalId, long? FileUsn,
    string VersionKey, bool CacheEligible, string CacheReason,
    bool? HasAlternateStreams = null, long ValidatedPathCount = 0, bool PathsComplete = false);
public sealed record FileVersionValidation(bool IsStable, FileContentVersion Current, string? Reason = null);

public sealed record FileSamplePolicy(int WindowBytes = 65_536)
{
    public const string ModelVersion = "first-middle-last-sha256/v1";
    public string PolicyHash => Identity.For("sample-policy", ModelVersion + "|" + WindowBytes.ToString(CultureInfo.InvariantCulture));
    public void Validate() { if (WindowBytes is < 4096 or > 1_048_576) throw new ArgumentException("Sample window must be 4096–1048576 bytes."); }
}
public sealed record FileChunkPolicy(int MinimumBytes = 65_536, int AverageBytes = 262_144,
    int MaximumBytes = 1_048_576, int MaxChunks = 65_536)
{
    public const string ModelVersion = "gear64-sha256-cdc/v1";
    public string PolicyHash => Identity.For("chunk-policy", FormattableString.Invariant($"{ModelVersion}|{MinimumBytes}|{AverageBytes}|{MaximumBytes}|{MaxChunks}"));
    public void Validate()
    {
        if (MinimumBytes < 4096 || AverageBytes < MinimumBytes || MaximumBytes < AverageBytes || MaximumBytes > 16_777_216 ||
            (AverageBytes & (AverageBytes - 1)) != 0 || MaxChunks is < 1 or > 65_536)
            throw new ArgumentException("CDC bounds require 4096 <= minimum <= power-of-two average <= maximum <= 16777216 and 1–65536 chunks.");
    }
}
public sealed record FileContentChunk(int Ordinal, long Offset, int Length, string Sha256);
public sealed record FileReadProgress(string Stage, long BytesRead, long SourceLength);
public sealed record FileReadReceipt(string Stage, string PolicyHash, FileContentVersion? Before,
    FileContentVersion? After, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    long BytesRead, string? Digest, long ChunkCount, long ChunkBytes, bool ChunksComplete,
    string Status = "complete", string? Error = null);

public sealed record HashingCoverage
{
    public long TotalIdentities { get; init; }
    public long TotalPaths { get; init; }
    public long AdmittedIdentities { get; init; }
    public long AdmittedPaths { get; init; }
    public long CandidateIdentities { get; init; }
    public long SampledIdentities { get; init; }
    public long FullyHashedIdentities { get; init; }
    public long ChunkedIdentities { get; init; }
    public long CacheHits { get; init; }
    public long InvalidatedIdentities { get; init; }
    public long ErrorIdentities { get; init; }
    public long SkippedIdentities { get; init; }
    public long UnsafeExcludedIdentities { get; init; }
    public long PolicyExcludedIdentities { get; init; }
    public IReadOnlyDictionary<string, long> AdmissionReasons { get; init; } = new Dictionary<string, long>();
    public long BytesRead { get; init; }
    public long InFlightBytesRead { get; init; }
    public long BudgetChargedBytes { get; init; }
    public long UnaccountedReservedBytes { get; init; }
    public long ChunkRecords { get; init; }
    public long ChunkBytes { get; init; }
    public long OverlapPairs { get; init; }
    public long PairComparisons { get; init; }
    public long SkippedHighFanoutChunks { get; init; }
    public bool AdmissionTruncated { get; init; }
    public bool ByteBudgetReached { get; init; }
    public bool ChunkBudgetReached { get; init; }
    public bool OverlapPairsTruncated { get; init; }
    public bool PairComparisonBudgetReached { get; init; }
    public long UnaccountedReadAttempts { get; init; }
    public bool BytesReadAreLowerBound { get; init; }
}
public sealed record HashingRun(string Id, string SnapshotId, HashingOptions Options, string RequestHash,
    string Status, string Phase, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset UpdatedAt, long Revision, HashingCoverage Coverage, string? ResultHash = null,
    string? Error = null, string? StopRequest = null, string ModelVersion = HashingOptions.ModelVersion)
{
    public string Completeness { get; init; } = "incomplete";
}
public sealed record HashingWorkItem(string RunId, string NodeId, string Path, long LogicalBytes,
    DateTimeOffset ModifiedAt, string Attributes, long PathCount, long LinkCount, long? AllocatedBytes,
    bool HasAlternateStreams, bool MetadataConsistent, string AdmissionStatus, string Stage,
    string? SampleEvidenceId = null, string? FullEvidenceId = null, string? Error = null,
    string? ActiveAttemptId = null);
public sealed record HashingEvidence(string Id, string RunId, string NodeId, FileReadReceipt Receipt,
    bool CacheHit = false, string? CacheSourceEvidenceId = null);
public sealed record HashingCacheEntry(HashingEvidence Evidence, string StreamScope = "unnamed-data-stream");
public sealed record HashingGroup(string Id, string RunId, string Mode, string Digest, long LogicalBytes,
    long IdentityCount, long PathCount, long? ReclaimableBytes, string Confidence, string SavingsExplanation);
public sealed record HashingMember(string NodeId, string Path, long LogicalBytes, long PathCount,
    long LinkCount, long? AllocatedBytes, string EvidenceId, bool CacheHit);
public sealed record HashingOverlap(string RunId, string LeftId, string RightId, string LeftPath,
    string RightPath, long LeftBytes, long RightBytes, long SharedBytes, long SharedChunkOccurrences,
    double LeftFraction, double RightFraction, string Completeness,
    string Denominator = "sum(min(left multiplicity,right multiplicity)*chunk length); fractions use each full unnamed stream length",
    long? ReclaimableBytes = null);
public sealed record HashingResult(string RunId, string SnapshotId, string Status, string Phase,
    HashingCoverage Coverage, string? ResultHash, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string Scope = "Content read after the baseline snapshot; observation intervals are not an atomic filesystem snapshot.");
