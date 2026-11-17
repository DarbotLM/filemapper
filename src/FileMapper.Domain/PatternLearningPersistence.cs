namespace FileMapper.Domain;

// Storage wire contracts deliberately do not depend on Semantics or interpret learned relations as evidence.
public sealed record PatternLearningStoreHead(string LearnerId, long Sequence, string EpochId,
    string CheckpointHash, string WeightsHash, string AuditHash);
public sealed record PatternLearningStoreDocument(string Hash, string JsonSha256, string Json);
public sealed record PatternLearningStoreEpoch(string LearnerId, string EpochId, string CohortId,
    string PartitionPolicyHash, long StartSequence, long? EndSequence, string? ParentCheckpointHash,
    PatternLearningStoreDocument Document);
public sealed record PatternLearningStoreGroup(string CohortId, string GroupKey, string Partition, string AssignmentHash);
public sealed record PatternLearningStoreTerm(string TermHash, string Category, string Token, long Count);
public sealed record PatternLearningStoreOccurrence(string SourceObservationId, string PatternHash,
    string SomId, string SourceNodeId, string SnapshotId, DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo,
    DateTimeOffset CapturedAt, PatternLearningStoreDocument Context);
public sealed record PatternLearningStoreCommit(string LearnerId, string EventId, string EventHash,
    string EventJsonSha256, string EventJson, PatternLearningStoreHead ExpectedHead, PatternLearningStoreEpoch Epoch,
    PatternLearningStoreGroup Group, PatternLearningStoreDocument Checkpoint, string WeightsHash,
    string AuditHash, PatternLearningStoreTerm[] Terms, PatternLearningStoreOccurrence[] Occurrences,
    PatternLearningStoreDocument? Weights = null);
public sealed record PatternLearningStoreReceipt(bool Duplicate, PatternLearningStoreHead Head, string EventId, string EventHash);
public sealed record PatternLearningStoredEvent(long Sequence, string EventId, string EventHash, string EventJson,
    string CheckpointHash, string AuditHash);
public sealed record PatternLearningEventPage(PatternLearningStoredEvent[] Items, long Total, int Offset, int Limit);
public sealed record PatternLearningVocabularyItem(string TermHash, string Category, string Token,
    long EncounterCount, long SourceObservationCount, long FirstSequence, long LastSequence);
public sealed record PatternLearningVocabularyPage(PatternLearningVocabularyItem[] Items, long Total, int Offset, int Limit);
public sealed record PatternLearningOccurrenceQuery(string? PatternHash = null, string? SomId = null,
    string? SourceObservationId = null, string? SnapshotId = null, string? SessionId = null,
    string? WorkspaceId = null, string? RepositoryId = null, string? TaskId = null, string? TerminalId = null,
    string? ComputerId = null, string? NetworkId = null, string? UnknownDimension = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string TimeBasis = "captured", int Offset = 0, int Limit = 50);
public sealed record PatternLearningOccurrenceItem(string OccurrenceId, string EventId, long Sequence,
    string SourceObservationId, string PatternHash, string SomId, string SourceNodeId, string SnapshotId,
    DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo, DateTimeOffset CapturedAt,
    string? SessionId, PatternLearningStoreDocument Context);
public sealed record PatternLearningOccurrencePage(PatternLearningOccurrenceItem[] Items, long Total,
    long TotalSourceOccurrences, int Offset, int Limit, string CountInterpretation = "Total counts matching encounter rows; TotalSourceOccurrences counts distinct session/source/pattern occurrences.");
public sealed record PatternLearningJournalVerification(string LearnerId, long CheckedEvents, long TotalEvents,
    bool Truncated, bool Complete, PatternLearningStoreHead? Head, string[] Problems,
    string Scope = "Stored payload hashes, sequence/audit chain, checkpoint/weights references and projection commitments; mathematical replay belongs to the semantic learner.");
