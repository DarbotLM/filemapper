namespace FileMapper.Domain;

public sealed record DirectoryMetrics(string Path, long RecursiveLogicalBytes, long? RecursiveAllocatedBytes,
    long? ExclusiveAllocatedBytes, long EntryCount, long UniqueFileCount, long DirectoryCount,
    long UnknownAllocationCount, string Completeness, string ModelVersion = "filesystem-scope-v1",
    string[]? Limitations = null);
public sealed record HierarchyItem(FileEntry Item, DirectoryMetrics? Metrics);
public sealed record DuplicateProjection(string Hash, long LogicalBytes, long? ReclaimableBytes,
    IReadOnlyList<FileEntry> Items, string Confidence, long UniqueIdentities, long PathCount,
    bool ItemsTruncated, string? SavingsLimitation);
public sealed record DifferenceProjection(string Kind, string Path, string NodeId, long BytesDelta,
    string Explanation, string? PreviousPath = null);
public sealed record StorageFacet(string Key, string Label, long UniqueIdentities, long UniqueFiles,
    long StreamIdentities, long PathEntries, long LogicalBytes, long? AllocatedBytes,
    long KnownAllocatedBytes, long UnknownAllocationCount, long ConflictingIdentities, string Completeness);
public sealed record StorageBreakdown(string SnapshotId, string Completeness, string Path, string Dimension,
    DateTimeOffset AsOf, string GroupingStrategy, Page<StorageFacet> Page);
