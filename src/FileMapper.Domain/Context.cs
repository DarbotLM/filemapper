using System.Text.Json.Serialization;

namespace FileMapper.Domain;

// Context carries recorded metadata and citations, never instructions or inferred execution paths.
public sealed record ContextNode(string Id, string Kind, string Name, string? Path);
public sealed record ContextEvidence(string Id, string SubjectId, string Predicate, string SourceKind,
    string SourceUri, string ObservedAt, double Confidence, string Completeness, string CollectorVersion,
    string? EvidenceHash, string ValuePreview, bool ValueTruncated);
public sealed record ContextConnection(string Id, string FromId, string ToId, string Kind, double Confidence,
    ContextEvidence Evidence);
public sealed record ContextStep(ContextNode Node, ContextConnection? Via);
public sealed record ContextItem(ContextNode Node, int Score, IReadOnlyDictionary<string, int> Components,
    IReadOnlyList<string> Reasons, IReadOnlyList<ContextStep> ConnectionPath, IReadOnlyList<ContextEvidence> Observations);
public sealed class ContextPackage(string snapshotId, string completeness, int byteBudget)
{
    public string SnapshotId { get; } = snapshotId;
    public string Completeness { get; } = completeness;
    public string ContentTrust => "untrusted-data";
    public string ModelVersion => "context-graph/v1";
    public int ByteBudget { get; } = byteBudget;
    public bool Truncated { get; set; }
    public List<string> TruncationReasons { get; set; } = [];
    public List<ContextItem> Items { get; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? QueryTerms { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CandidatesConsidered { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Scope { get; set; }
}
