using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Domain;

public static class Identity
{
    public static string For(string kind, string value) => kind + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
public sealed record Evidence(string Id, string SnapshotId, string SubjectId, string Predicate, string Value,
    string SourceKind, string SourceUri, DateTimeOffset ObservedAt, double Confidence = 1,
    string Completeness = "complete", string? ErrorCode = null, string? ErrorDetail = null,
    string CollectorVersion = "0.1.0", string? EvidenceHash = null)
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset ValidFrom { get; init; } = ObservedAt;
    public DateTimeOffset? ValidTo { get; init; }
    public string Scope { get; init; } = SnapshotId;
    public string PrivilegeContext { get; init; } = "current-user";
    public IReadOnlyList<string> ContributingObservationIds { get; init; } = [];
}
public sealed record MapNode(string Id, string Kind, string Name, string? Path, Dictionary<string, object?> Properties);
public sealed record MapEdge(string Id, string FromId, string ToId, string Kind, string EvidenceId,
    double Confidence = 1, Dictionary<string, object?>? Properties = null);
public sealed record Snapshot(string Id, string Root, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string Status, string Completeness, long EntryCount, long FileCount, long DirectoryCount,
    long LogicalBytes, long? AllocatedBytes, long ErrorCount);
public sealed record FileEntry(string SnapshotId, string Path, string? ParentPath, string NodeId,
    string Name, string Kind, long LogicalBytes, long? AllocatedBytes, DateTimeOffset ModifiedAt,
    string Attributes, long LinkCount = 1, string? Owner = null, string? Hash = null);
public sealed record CollectionBatch(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges,
    IReadOnlyList<Evidence> Observations, IReadOnlyList<FileEntry> Entries);
public sealed record ScanOptions(string Root, string ReparsePolicy = "skip", int BatchSize = 512,
    string[]? Excludes = null, bool HashFiles = false);
public sealed record Page<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit);
public sealed record RouteStep(MapNode Node, MapEdge? Via);
public sealed record RouteResult(string SnapshotId, string Completeness, IReadOnlyList<RouteStep> Steps,
    bool Truncated, string? TruncationReason)
{
    public string ModelVersion { get; init; } = "directed-bfs/v2";
    public int VisitedNodes { get; init; }
    public int ExaminedEdges { get; init; }
    public int MaxDepth { get; init; }
    public int MaxNodes { get; init; } = 5000;
    public int MaxEdges { get; init; } = 100000;
}
public sealed record AnalysisResult(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges,
    IReadOnlyList<Evidence> Observations, IReadOnlyList<Finding> Findings);
public sealed record Finding(string Id, string RuleId, string Severity, string Title, string Path,
    int Line, string Explanation, double Confidence, IReadOnlyList<string> EvidenceIds,
    string? Cwe = null)
{
    public string Status { get; init; } = "review-candidate";
    public string ModelVersion { get; init; } = "legacy-candidate-v1";
    public string Completeness { get; init; } = "partial";
    public IReadOnlyList<FindingRouteStep> Route { get; init; } = [];
    public bool Truncated { get; init; }
    public string? TruncationReason { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
}
/// <summary>An ordered witness. EdgeId links the preceding step to this step; the first step has none.</summary>
public sealed record FindingRouteStep(string NodeId, string? EdgeId, string Path, int Line,
    string Reason, int Column = 1);
public sealed record FindingWitnessStep(FindingRouteStep Location, MapNode Node, MapEdge? Via);
public sealed record FindingExplanation(string SnapshotId, string Completeness, Finding Finding,
    IReadOnlyList<FindingWitnessStep> Steps, IReadOnlyList<Evidence> Observations, string ContentTrust = "untrusted-data");
public interface IMapCollector
{
    string Name { get; }
    IAsyncEnumerable<CollectionBatch> CollectAsync(string snapshotId, ScanOptions options, CancellationToken cancellationToken);
}
public interface ICodeExtractor
{
    string Language { get; }
    Task<AnalysisResult> ExtractAsync(string snapshotId, string root, CancellationToken cancellationToken);
}
