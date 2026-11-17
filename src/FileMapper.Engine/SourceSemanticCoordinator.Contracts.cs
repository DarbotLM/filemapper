namespace FileMapper.Engine;

/// <summary>A service-owned operation over retained source bytes, not a filesystem snapshot job.</summary>
public sealed record SourceSemanticJob(string Id, string Kind, string Status, string Phase,
    DateTimeOffset CreatedAt, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset UpdatedAt, string OwnerId, string AdmissionSha256, string RequestSha256, string? CaptureId,
    string? ExtractionId, string? AnalysisId, string OutputState, SourceSemanticCoverage? Coverage,
    string? Error, string? StopReason, long ProgressSequence, System.Text.Json.JsonElement? Progress);

internal sealed record SourceSemanticJobLedger(string Schema, long Revision, IReadOnlyList<SourceSemanticJob> Jobs);
internal sealed record SourceSemanticJobAdmission(string Schema, string JobId, string Kind,
    DateTimeOffset AcceptedAt, string OwnerId, SourceHarvestSelectionInfo? SourceSelection,
    FileMapper.Storage.SourceIndexedCapture? IndexedCapture, DateTimeOffset? IndexObservedAt,
    string SourcePinScope, SourceSemanticRequest? Request, string? ImportDirectory, string? ExpectedAnalysisId);
