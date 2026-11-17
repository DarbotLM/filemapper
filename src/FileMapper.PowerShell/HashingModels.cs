namespace FileMapper.PowerShell;

public sealed record FileMapperHashingOptions(string Mode = "exact", string? ScopePath = null, long MinimumBytes = 0,
    int MaxIdentities = 1_000_000, int MaxPaths = 2_000_000, long MaxBytesRead = 1_099_511_627_776,
    int Workers = 2, long BytesPerSecond = 0, int SampleWindowBytes = 65_536,
    int ChunkMinimumBytes = 65_536, int ChunkAverageBytes = 262_144, int ChunkMaximumBytes = 1_048_576,
    int MaxChunksPerFile = 65_536, long MaxChunkRecords = 2_000_000, int MaxOverlapPairs = 100_000,
    int MaxChunkFanout = 256, long MaxPairComparisons = 2_000_000, bool ReuseCache = true);
public sealed record FileMapperHashingCoverage
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
    public Dictionary<string, long> AdmissionReasons { get; init; } = [];
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
public sealed record FileMapperHashingRun(string Id, string SnapshotId, FileMapperHashingOptions Options,
    string RequestHash, string Status, string Phase, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset UpdatedAt, long Revision, FileMapperHashingCoverage Coverage, string? ResultHash,
    string? Error, string? StopRequest, string ModelVersion, string Completeness);
public sealed record FileMapperHashingRuns(FileMapperHashingRun[] Items, bool RecoveryDeferred);
public sealed record FileMapperHashingGroup(string Id, string RunId, string Mode, string Digest, long LogicalBytes,
    long IdentityCount, long PathCount, long? ReclaimableBytes, string Confidence, string SavingsExplanation);
public sealed record FileMapperHashingMember(string NodeId, string Path, long LogicalBytes, long PathCount,
    long LinkCount, long? AllocatedBytes, string EvidenceId, bool CacheHit);
public sealed record FileMapperHashingOverlap(string RunId, string LeftId, string RightId, string LeftPath,
    string RightPath, long LeftBytes, long RightBytes, long SharedBytes, long SharedChunkOccurrences,
    double LeftFraction, double RightFraction, string Completeness, string Denominator, long? ReclaimableBytes);
public sealed record FileMapperContentVersion(string Identity, string VolumeId, string FileSystem,
    long Length, long? AllocatedBytes, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, DateTimeOffset ChangedAt,
    string Attributes, long LinkCount, string? JournalId, long? FileUsn, string VersionKey,
    bool CacheEligible, string CacheReason, bool? HasAlternateStreams, long ValidatedPathCount, bool PathsComplete);
public sealed record FileMapperContentReceipt(string Stage, string PolicyHash, FileMapperContentVersion? Before,
    FileMapperContentVersion? After, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, long BytesRead,
    string? Digest, long ChunkCount, long ChunkBytes, bool ChunksComplete, string Status, string? Error);
public sealed record FileMapperHashingEvidence(string Id, string RunId, string NodeId,
    FileMapperContentReceipt Receipt, bool CacheHit, string? CacheSourceEvidenceId);
public sealed record FileMapperContentChunk(int Ordinal, long Offset, int Length, string Sha256);
public sealed record FileMapperHashingPage<T>(string Id, string SnapshotId, string Completeness, FileMapperPage<T> Page)
{
    public string? ModelVersion { get; init; }
    public string? Status { get; init; }
    public FileMapperHashingCoverage? Coverage { get; init; }
}
public sealed record FileMapperContentChunks(string RunId, string NodeId, string Id, bool CacheHit,
    string? CacheSourceEvidenceId, FileMapperPage<FileMapperContentChunk> Page);
public sealed record FileMapperContentControl(string Requested);
