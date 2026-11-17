namespace FileMapper.Domain;

public sealed record DurableJob(string Id, string SnapshotId, string Kind, string Status,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long Entries, string? Error, ScanOptions? Options);
public sealed record ScanWork(string Path, string? ParentPath, string? ParentId);
public sealed record ScanCheckpointBatch(IReadOnlyList<string> CompletedPaths, CollectionBatch Batch);

/// <summary>Durable frontier; discovery is idempotent and completion commits with emitted observations.</summary>
public interface IScanWorkQueue
{
    IReadOnlyList<ScanWork> Pending(int limit);
    void Discover(IReadOnlyList<ScanWork> paths);
    bool EnterDirectory(string stableIdentity, string path);
    string? DirectoryIdentity(string path);
}
