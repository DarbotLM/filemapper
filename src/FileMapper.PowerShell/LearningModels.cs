using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMapper.PowerShell;

// Deep evidence is deliberately JSON, not decimal/double or PowerShell dynamic conversion.
// This module targets the host's .NET 10 and does not load the .NET 11 semantic engine.
public abstract record FileMapperLearningResult
{
    public string ToJson() => FileMapperSemanticJson.Serialize(this);
}
public sealed record FileMapperLearningPage<T>(T[] Items, long Total, int Offset, int Limit) : FileMapperLearningResult;
public sealed record FileMapperLearnerHead(string LearnerId, long Sequence, string EpochId,
    string CheckpointHash, string WeightsHash, string AuditHash) : FileMapperLearningResult;
public sealed record FileMapperLearningModel(string CheckpointHash, JsonElement Checkpoint, JsonElement Weights,
    string Interpretation) : FileMapperLearningResult
{
    [JsonIgnore] public string CheckpointJson => Checkpoint.GetRawText();
    [JsonIgnore] public string WeightsJson => Weights.GetRawText();
}
public sealed record FileMapperLearningVocabulary(string TermHash, string Category, string Token,
    long EncounterCount, long SourceObservationCount, long FirstSequence, long LastSequence) : FileMapperLearningResult;
public sealed record FileMapperLearningSubject(string SomId, string ObjectHash, string SourceObservationId,
    string SourceNodeId, string SnapshotId, string ShapeType, string GeometryHash,
    JsonElement Words, JsonElement Path, JsonElement Commands, string? ObservedFrom, string? ObservedTo) : FileMapperLearningResult;
public sealed record FileMapperLearningReceipt(bool Duplicate, FileMapperLearnerHead Head,
    string EventId, string EventHash) : FileMapperLearningResult;
public sealed record FileMapperLearningAppend(FileMapperLearningReceipt Receipt, string EventHash,
    string CheckpointHash, long TrainingUpdates, long ObservationEvents, bool WeightsChanged) : FileMapperLearningResult;
public sealed record FileMapperLearningObservation(string LearnerId, string SnapshotId, string BatchId,
    int Offset, int Requested, int Processed, int NewEvents, long TotalObjectsInReadView,
    string CheckpointHash, bool PageComplete, bool SnapshotEnd, string? Error, string Scope) : FileMapperLearningResult;
public sealed record FileMapperLearningPartition(string GroupKey, string Partition, string PartitionPolicyHash) : FileMapperLearningResult;
public sealed record FileMapperLearningPrediction(string Head, int ProbabilityQ, int LogitQ, string CheckpointHash,
    string WeightsHash, string FeatureSchemaHash, string InputHash, JsonElement Contributions,
    string[] MissingContext, bool FeaturesTruncated, int FeatureCollisions, string Interpretation) : FileMapperLearningResult;
public sealed record FileMapperLearningDocument(string Hash, string JsonSha256, string Json) : FileMapperLearningResult;
public sealed record FileMapperLearningOccurrence(string OccurrenceId, string EventId, long Sequence,
    string SourceObservationId, string PatternHash, string SomId, string SourceNodeId, string SnapshotId,
    string? ObservedFrom, string? ObservedTo, string CapturedAt, string? SessionId,
    FileMapperLearningDocument Context) : FileMapperLearningResult;
public sealed record FileMapperLearningOccurrencePage(FileMapperLearningOccurrence[] Items, long Total,
    long TotalSourceOccurrences, int Offset, int Limit, string CountInterpretation) : FileMapperLearningResult;

public sealed record FileMapperArena(string Schema, JsonElement Request, JsonElement Dataset,
    FileMapperLearningModel Model, JsonElement Context, JsonElement Challenge, int CandidateAffinities,
    bool AffinitiesTruncated, string AffinitySelectionPolicy) : FileMapperLearningResult;
public sealed record FileMapperSavedArena(string ArtifactHash, string StoredAt, FileMapperArena Arena) : FileMapperLearningResult;
public sealed record FileMapperArenaMove(string OrientationId, int X, int Z, string LayerId,
    string ExpectedPriorStateHash) : FileMapperLearningResult;
public sealed record FileMapperArenaLegalMove(FileMapperArenaMove Move, int? LandingY, bool TopOut) : FileMapperLearningResult;
public sealed record FileMapperArenaState(JsonElement Challenge, JsonElement Placements, int NextPieceIndex,
    long Score, long LearnedAdvisoryScore, string[] PaidPatternIds, string[] PaidAffinityIds,
    JsonElement History, bool IsOver, string? EndReason, string StateHash) : FileMapperLearningResult;
public sealed record FileMapperArenaReplay(string ArenaHash, FileMapperArenaState State,
    FileMapperArenaLegalMove[] LegalMoves, JsonElement Orientations) : FileMapperLearningResult;
public sealed record FileMapperArenaEvaluation(bool TopOut, long ScoreGain, long GroundedCodePoints,
    int CoveredEmptyCells, int MaximumOccupiedHeight, int SumColumnHeights) : FileMapperLearningResult;
public sealed record FileMapperArenaDecision(FileMapperArenaMove? Move, FileMapperArenaEvaluation? Evaluation,
    bool Completed, bool Truncated, int ActionsEvaluated, int NodesExpanded, int LegalActionsEnumerated,
    string SearchPolicyHash, string StateHash, string? Reason) : FileMapperLearningResult;
public sealed record FileMapperSavedArenaReplay(string Hash, string Kind, string? ParentHash,
    string StoredAt, JsonElement Summary) : FileMapperLearningResult;
