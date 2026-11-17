using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using FileMapper.Harvest;

namespace FileMapper.Engine;

public sealed record SourceSemanticOptions(int MaximumDocuments = 256, long MaximumDocumentBytes = 2 * 1024 * 1024,
    long MaximumTotalBytes = 32 * 1024 * 1024, long MaximumOutputBytes = 512 * 1024 * 1024,
    int MaximumLexicalLinks = 500_000, CSharpAnalysisOptions? CSharp = null,
    int MaximumJoinWordsPerDocument = 250_000, int MaximumGraphRecords = 500_000);
public sealed record SourceSemanticRequest(string CaptureId, string ExtractionId,
    IReadOnlyList<string>? EntryIds = null, SourceSemanticOptions? Options = null);
public sealed record SourceSemanticArtifact(string Path, string Sha256, long ByteLength, long Records);
public sealed record SourceSemanticImplementation(string Name, string Version, string Sha256);
public sealed record SourceSemanticDocument(string EntryId, string? DocumentId, string RepositoryPath,
    string? LogicalUri, string? SourceSha256, long? SourceBytes, string? Language, string Status,
    string SavedLexicalStatus, string? Detail = null);
public sealed record SourceSemanticSpan(string NodeId, string? DocumentId, string? SourceSha256,
    string Status, int? Utf16Start, int? Utf16Length, long? ByteStart, long? ByteLength);
public sealed record SourceSemanticWordLink(string NodeId, string SomId, string DocumentId, string SourceSha256,
    string OccurrenceId, string TermId, long WordByteStart, long WordByteLength,
    string Relation = "source-span-contains-lexical-word; not a resolved symbol binding");
public sealed record SourceSemanticCoverage(long CaptureEntries, long SelectedEntries, long SupportedDocuments,
    long AdmittedDocuments, long RejectedOrLimitedDocuments, long UnsupportedEntries,
    long Nodes, long Edges, long Observations, long Findings, long ExactNodeSpans,
    long UnavailableNodeSpans, long LexicalLinks, bool LexicalLinksTruncated)
{
    public IReadOnlyDictionary<string, long> SourceAdmissionStatuses { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> ParserAdmissionStatuses { get; init; } = new Dictionary<string, long>();
    public long JoinWordLimitedDocuments { get; init; }
    public long PartialObservations { get; init; }
    public long EdgesWithMissingEndpoints { get; init; }
    public long EdgesWithMissingEvidence { get; init; }
}
public sealed record SourceSemanticManifest(string Schema, string RunId, string ContextId,
    string CaptureId, string ExtractionId, string RepositoryId, string Commit,
    string RequestSha256, string InputSetSha256, string PolicySha256, SourceSemanticOptions Options,
    IReadOnlyList<SourceSemanticImplementation> Implementation,
    IReadOnlyList<SourceSemanticImplementation> TrustedRuntimeMetadata,
    DateTimeOffset StartedAt, DateTimeOffset ParsedAt, DateTimeOffset FinishedAt,
    DateTimeOffset CaptureStartedAt, DateTimeOffset CaptureFinishedAt, DateTimeOffset? OriginalSourceObservedAt,
    SourceSemanticCoverage Coverage, IReadOnlyList<SourceSemanticArtifact> Artifacts,
    string State, string Scope)
{
    public string SemanticInputSetSha256 { get; init; } = "";
}
public sealed record SourceSemanticReceipt(string AnalysisId, string ManifestPath, SourceSemanticManifest Manifest);

internal sealed record SourceSemanticSelectedDocument(CapturedSourceEntry Capture, SourceExtractionEntry Extraction,
    string Uri, string Language, byte[] Bytes);
internal sealed record SourceSemanticInput(SourceCaptureManifest Capture, SourceExtractionManifest Extraction,
    IReadOnlyDictionary<string, long> AdmissionStatuses, IReadOnlyList<SourceSemanticSelectedDocument> Selected,
    long TotalEntries, long SelectedEntries, long SupportedDocuments, long UnsupportedEntries, string InputSetSha256,
    string SemanticInputSetSha256);
