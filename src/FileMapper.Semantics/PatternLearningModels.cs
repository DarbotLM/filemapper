using System.Collections.Immutable;

namespace FileMapper.Semantics;

public sealed record PatternLearningConfig(
    string Schema = "filemapper.pattern-learning.config/1", int FeatureCount = 4096,
    int MaximumActiveFeatures = 128, int LearningRateQ = 25000, int WeightBoundQ = 8000000,
    long MaximumEvents = 1000000000, long MaximumTrainingUpdates = 1000000000);

// Partition assignment is permanent within one learner lineage. A new cohort starts from new weights.
public sealed record PatternLearningPartitionPolicy(string CohortId, string SeedHash,
    int TrainBuckets = 8000, int ValidationBuckets = 1000, int TotalBuckets = 10000,
    string Schema = "filemapper.pattern-learning.partition/1");
public sealed record PatternLearningEpoch(string EpochId, string PartitionPolicyHash, long StartSequence,
    long? EndSequence, string? ParentCheckpointHash);
public sealed record PatternLearningContextValue(string Status, string? Value, string? Source);
public sealed record PatternLearningContext(PatternLearningContextValue Workspace,
    PatternLearningContextValue Repository, PatternLearningContextValue Task, PatternLearningContextValue Terminal,
    PatternLearningContextValue Computer, PatternLearningContextValue Network, PatternLearningContextValue Session)
{
    public static PatternLearningContext Unknown => new(UnknownValue(), UnknownValue(), UnknownValue(),
        UnknownValue(), UnknownValue(), UnknownValue(), UnknownValue());
    private static PatternLearningContextValue UnknownValue() => new("unknown", null, null);
}
// Tokens are supplied, provenance-bearing lexical observations. The learner never runs a command or
// reinterprets unordered words as temporal execution. No runtime-dependent normalization is performed.
public sealed record PatternLearningTokens(string Status, string Order, ImmutableArray<string> Tokens,
    ImmutableArray<string> Sources, bool Truncated = false)
{
    public static PatternLearningTokens Unknown => new("unknown", "unknown", [], []);
}
public sealed record PatternLearningSubject(string SomId, string ObjectHash, string SourceObservationId,
    string SourceNodeId, string SnapshotId, string ShapeType, string GeometryHash,
    PatternLearningTokens Words, PatternLearningTokens Path, PatternLearningTokens Commands,
    DateTimeOffset? ObservedFrom = null, DateTimeOffset? ObservedTo = null);
public sealed record PatternLearningInput(PatternLearningSubject Left, PatternLearningSubject? Right,
    PatternLearningContext Context);
public sealed record PatternLearningLabel(string Head, int Value, string SourceKind, string SourceId, string Rationale);
public sealed record PatternLearningEvent(string EventId, long Sequence, string PreviousAuditHash,
    PatternLearningEpoch Epoch, string GroupKey, string Partition, string Kind, PatternLearningInput Input,
    PatternLearningLabel? Label, DateTimeOffset CapturedAt,
    string Schema = "filemapper.pattern-learning.event/1");
public sealed record PatternLearningFeature(int Index, int ValueQ, ImmutableArray<string> Keys);
public sealed record PatternLearningFeatureVector(string SchemaHash, string InputHash,
    ImmutableArray<PatternLearningFeature> Items, int CandidateFeatures, int CollisionCount,
    bool Truncated, ImmutableArray<string> MissingContext);
public sealed record PatternLearningHeadWeights(string Head, ImmutableArray<int> ValuesQ);
public sealed record PatternLearningWeights(string ConfigHash, string FeatureSchemaHash,
    ImmutableArray<PatternLearningHeadWeights> Heads, string Schema = "filemapper.pattern-learning.weights/1");
public sealed record PatternLearningCheckpoint(string ConfigHash, PatternLearningConfig Config,
    PatternLearningPartitionPolicy PartitionPolicy, string PartitionPolicyHash, PatternLearningEpoch Epoch,
    string FeatureSchemaHash, string WeightsHash, string? ParentCheckpointHash, long Sequence,
    long TrainingUpdates, long ObservationEvents, string AuditHash, string? LastEventHash,
    string Schema = "filemapper.pattern-learning.checkpoint/1");
public sealed record PatternLearningState(string CheckpointHash, PatternLearningCheckpoint Checkpoint,
    PatternLearningWeights Weights);
public sealed record FrozenPatternLearningModel(string CheckpointHash, PatternLearningCheckpoint Checkpoint,
    PatternLearningWeights Weights, string Interpretation = "advisory learned relevance; never authoritative source evidence");
public sealed record PatternLearningTerm(string TermHash, string Category, string Token, long Count);
public sealed record PatternLearningOccurrence(string SourceObservationId, string PatternHash, string SomId,
    string SourceNodeId, string SnapshotId, DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo,
    DateTimeOffset CapturedAt, PatternLearningContext Context);
public sealed record PatternLearningUpdate(PatternLearningState State, string EventHash, string AssignmentHash,
    PatternLearningFeatureVector Features, ImmutableArray<PatternLearningTerm> Terms,
    ImmutableArray<PatternLearningOccurrence> Occurrences, bool WeightsChanged);
public sealed record PatternLearningContribution(int Index, ImmutableArray<string> Keys, int FeatureValueQ,
    int WeightQ, long ContributionQ);
public sealed record PatternLearningPrediction(string Head, int ProbabilityQ, int LogitQ,
    string CheckpointHash, string WeightsHash, string FeatureSchemaHash, string InputHash,
    ImmutableArray<PatternLearningContribution> Contributions, ImmutableArray<string> MissingContext,
    bool FeaturesTruncated, int FeatureCollisions, string Interpretation = "advisory learned relevance; not a calibrated evidence probability");
public sealed record PatternLearningEvaluationManifest(string DatasetId, string PartitionPolicyHash,
    string Partition, long FirstSequence, long LastSequence, string EventsHash,
    string LabelPolicy, string Schema = "filemapper.pattern-learning.evaluation/1");
public sealed record PatternLearningHeadMetrics(string Head, long Cases, long TruePositive, long FalsePositive,
    long TrueNegative, long FalseNegative, long LogLossSumQ, long SquaredErrorSumPico,
    long MeanLogLossQ, long MeanSquaredErrorPico, int? PrecisionQ, int? RecallQ);
public sealed record PatternLearningEvaluation(string CheckpointHash, string WeightsHash, string ManifestHash,
    string EventsHash, string Partition, ImmutableArray<PatternLearningHeadMetrics> Heads,
    int Cases, bool ModelChanged = false,
    string Interpretation = "held-out labeled relevance; results apply to this dataset and label policy only");
