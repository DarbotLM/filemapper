namespace FileMapper.PowerShell;

public sealed record FileMapperSnapshot(string Id, string Root, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string Status, string Completeness, long EntryCount, long FileCount, long DirectoryCount, long LogicalBytes, long? AllocatedBytes, long ErrorCount);
public sealed record FileMapperItem(string SnapshotId, string Path, string? ParentPath, string NodeId, string Name, string Kind,
    long LogicalBytes, long? AllocatedBytes, DateTimeOffset ModifiedAt, string Attributes, long LinkCount, string? Owner, string? Hash);
public sealed record FileMapperJob(string Id, string SnapshotId, string Kind, string Status, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, long Entries, string? Error);
public sealed record FileMapperScanReceipt(string JobId, string SnapshotId);
public sealed record FileMapperRanking(FileMapperItem Item, double Score, Dictionary<string, double> Components, string ModelVersion);
public sealed record FileMapperRankingResult(string SnapshotId, string Completeness, bool Truncated, int ProjectionLimit, FileMapperRanking[] Items);
public sealed record FileMapperDuplicate(string Hash, long LogicalBytes, long? ReclaimableBytes, FileMapperItem[] Items,
    string Confidence, long UniqueIdentities, long PathCount, bool ItemsTruncated, string? SavingsLimitation);
public sealed record FileMapperDuplicateResult(string SnapshotId, string Completeness, bool Truncated, long HashedFiles,
    FileMapperDuplicate[] Groups, long TotalGroups, int Offset, int Limit);
public sealed record FileMapperDuplicateMembers(string SnapshotId, string Completeness, string Hash, long LogicalBytes, FileMapperPage<FileMapperItem> Page);
public sealed record FileMapperFinding(string Id, string RuleId, string Severity, string Title, string Path, int Line,
    string Explanation, double Confidence, string[] EvidenceIds, string? Cwe)
{
    public string Status { get; init; } = "review-candidate";
    public string ModelVersion { get; init; } = "legacy-candidate-v1";
    public string Completeness { get; init; } = "partial";
    public FileMapperFindingLocation[] Route { get; init; } = [];
    public bool Truncated { get; init; }
    public string? TruncationReason { get; init; }
    public string[] Assumptions { get; init; } = [];
}
public sealed record FileMapperFindingLocation(string NodeId, string? EdgeId, string Path, int Line, string Reason, int Column = 1);
public sealed record FileMapperFindingStep(FileMapperFindingLocation Location, FileMapperNode Node, FileMapperEdge? Via);
public sealed record FileMapperFindingExplanation(string SnapshotId, string Completeness, FileMapperFinding Finding,
    FileMapperFindingStep[] Steps, System.Text.Json.JsonElement[] Observations, string ContentTrust);
public sealed record FileMapperPage<T>(T[] Items, long Total, int Offset, int Limit);
public sealed record FileMapperDirectoryMetrics(string Path, long RecursiveLogicalBytes, long? RecursiveAllocatedBytes,
    long? ExclusiveAllocatedBytes, long EntryCount, long UniqueFileCount, long DirectoryCount,
    long UnknownAllocationCount, string Completeness, string ModelVersion, string[]? Limitations);
public sealed record FileMapperHierarchyItem(FileMapperItem Item, FileMapperDirectoryMetrics? Metrics)
{
    public string SnapshotId => Item.SnapshotId;
    public string Path => Item.Path;
    public string Name => Item.Name;
    public string Kind => Item.Kind;
    public long LogicalBytes => Metrics?.RecursiveLogicalBytes ?? Item.LogicalBytes;
    public long? AllocatedBytes => Metrics is null ? Item.AllocatedBytes : Metrics.RecursiveAllocatedBytes;
}
public sealed record FileMapperHierarchy(string SnapshotId, string Completeness, FileMapperDirectoryMetrics Scope, FileMapperPage<FileMapperHierarchyItem> Page);
public sealed record FileMapperStorageFacet(string Key, string Label, long UniqueIdentities, long UniqueFiles,
    long StreamIdentities, long PathEntries, long LogicalBytes, long? AllocatedBytes, long KnownAllocatedBytes,
    long UnknownAllocationCount, long ConflictingIdentities, string Completeness);
public sealed record FileMapperBreakdown(string SnapshotId, string Completeness, string Path, string Dimension,
    DateTimeOffset AsOf, string GroupingStrategy, FileMapperPage<FileMapperStorageFacet> Page);
public sealed record FileMapperNode(string Id, string Kind, string Name, string? Path, Dictionary<string, object?> Properties);
public sealed record FileMapperEdge(string Id, string FromId, string ToId, string Kind, string EvidenceId, double Confidence, Dictionary<string, object?>? Properties);
public sealed record FileMapperRouteStep(FileMapperNode Node, FileMapperEdge? Via);
public sealed record FileMapperRoute(string SnapshotId, string Completeness, FileMapperRouteStep[] Steps, bool Truncated, string? TruncationReason);
