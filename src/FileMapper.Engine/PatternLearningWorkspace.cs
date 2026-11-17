using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record LearningCreateRequest(string LearnerId, PatternLearningPartitionPolicy Policy, string EpochId,
    PatternLearningConfig? Config = null);
public sealed record LearningObjectReference(string SnapshotId, string NodeId, string ExpectedObjectHash);
public sealed record LearningEventRequest(string EventId, string ExpectedCheckpointHash, string GroupKey,
    LearningObjectReference Left, LearningObjectReference? Right, PatternLearningContext Context,
    PatternLearningLabel? Label, DateTimeOffset CapturedAt, PatternLearningEpoch? Epoch = null);
public sealed record LearningPredictionRequest(string CheckpointHash, string Head, LearningObjectReference Left,
    LearningObjectReference Right, PatternLearningContext Context);
public sealed record LearningRankingRequest(string CheckpointHash, string Head, LearningObjectReference Query,
    LearningObjectReference[] Candidates, PatternLearningContext Context, int Limit = 25);
public sealed record LearningRankedCandidate(int Rank, LearningObjectReference Reference, PatternLearningSubject Subject,
    PatternLearningPrediction Prediction);
public sealed record LearningRanking(string CheckpointHash, string Head, PatternLearningSubject Query,
    LearningRankedCandidate[] Items, int CandidatesEvaluated, int Limit, bool Truncated,
    string Interpretation = "advisory ranking within the caller's explicit candidate set; neither physical identity nor a verified code-flow witness");
public sealed record LearningAppendResult(PatternLearningStoreReceipt Receipt, string EventHash, string CheckpointHash,
    long TrainingUpdates, long ObservationEvents, bool WeightsChanged);
public sealed record LearningObservePageRequest(string SnapshotId, string ExpectedCheckpointHash, string GroupKey,
    PatternLearningContext Context, DateTimeOffset CapturedAt, string BatchId, int Offset = 0, int Limit = 50);
public sealed record LearningObservationBatch(string LearnerId, string SnapshotId, string BatchId, int Offset, int Requested,
    int Processed, int NewEvents, long TotalObjectsInReadView, string CheckpointHash, bool PageComplete, bool SnapshotEnd,
    string? Error, string Scope = "One pinned source page; committed events survive cancellation. Exact page retry requires an unchanged source page; event history retains committed prefixes. Separate pages may observe later source analysis.");

/// <summary>Native database projections feed a supervised learner; game rewards never supply training labels.</summary>
public sealed class PatternLearningWorkspace(MapStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public PatternLearningStoreHead Create(LearningCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LearnerId) || request.LearnerId.Length > 256 || request.LearnerId.Any(char.IsControl))
            throw new ArgumentException("Learner ID must contain 1–256 non-control characters.");
        var epoch = new PatternLearningEpoch(request.EpochId, CanonicalJson.Sha256(request.Policy), 1, null, null);
        var state = PatternLearning.Create(request.Config ?? new(), request.Policy, epoch);
        store.InitializePatternLearning();
        return store.CreatePatternLearner(request.LearnerId, StoredEpoch(request.LearnerId, state.Checkpoint),
            Document(state.Checkpoint), Document(state.Weights), state.Checkpoint.WeightsHash, state.Checkpoint.AuditHash);
    }
    public FrozenPatternLearningModel Load(string learnerId, string? checkpointHash = null)
    {
        checkpointHash ??= (store.GetPatternLearningHead(learnerId) ?? throw new KeyNotFoundException("Learner was not found.")).CheckpointHash;
        var checkpointDocument = store.GetPatternLearningCheckpoint(learnerId, checkpointHash) ?? throw new KeyNotFoundException("Learner checkpoint was not found.");
        var checkpoint = ReadDocument<PatternLearningCheckpoint>(checkpointDocument);
        var weightsDocument = store.GetPatternLearningWeights(learnerId, checkpoint.WeightsHash) ?? throw new InvalidOperationException("Checkpoint weights are missing.");
        return PatternLearning.Freeze(new(checkpointHash, checkpoint, ReadDocument<PatternLearningWeights>(weightsDocument)));
    }
    public Page<PatternLearningSubject> Subjects(string snapshotId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var reader = store.OpenSemanticReader(); var snapshot = Finished(reader, snapshotId);
        var page = reader.Nodes(snapshotId, offset: offset, limit: limit, cancellationToken: cancellationToken);
        return new(page.Items.Select(value => ObservedPatternDatasetReader.LearningSubject(snapshot, value)).ToArray(), page.Total, offset, limit);
    }
    public PatternLearningPrediction Predict(string learnerId, LearningPredictionRequest request, CancellationToken cancellationToken = default)
    {
        var model = Load(learnerId, request.CheckpointHash);
        using var reader = store.OpenSemanticReader();
        return PatternLearning.Predict(model, new(Subject(reader, request.Left, cancellationToken),
            Subject(reader, request.Right, cancellationToken), request.Context), request.Head);
    }
    public LearningRanking Rank(string learnerId, LearningRankingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Candidates is null || request.Candidates.Length is < 1 or > 100 || request.Limit is < 1 or > 100)
            throw new ArgumentException("Ranking requires 1–100 explicit candidates and a result limit of 1–100.");
        if (request.Candidates.Any(item => item is null) || request.Candidates.Select(item => (item.SnapshotId, item.NodeId)).Distinct().Count() != request.Candidates.Length)
            throw new ArgumentException("Ranking candidates must be non-null, distinct native object references.");
        var model = Load(learnerId, request.CheckpointHash);
        using var reader = store.OpenSemanticReader(); var query = Subject(reader, request.Query, cancellationToken);
        var subjects = request.Candidates.Select(item => Subject(reader, item, cancellationToken)).ToArray();
        var predictions = PatternLearning.PredictMany(model, subjects.Select(subject => new PatternLearningInput(query, subject, request.Context)), request.Head, cancellationToken);
        var ordered = subjects.Select((subject, index) => new { Subject = subject, Reference = request.Candidates[index], Prediction = predictions[index] })
            .OrderByDescending(item => item.Prediction.ProbabilityQ).ThenBy(item => item.Reference.SnapshotId, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.NodeId, StringComparer.Ordinal).Take(request.Limit)
            .Select((item, index) => new LearningRankedCandidate(index + 1, item.Reference, item.Subject, item.Prediction)).ToArray();
        return new(model.CheckpointHash, request.Head, query, ordered, subjects.Length, request.Limit, subjects.Length > ordered.Length);
    }
    public LearningAppendResult Append(string learnerId, LearningEventRequest request, CancellationToken cancellationToken = default)
    {
        var model = Load(learnerId, request.ExpectedCheckpointHash);
        var existing = store.GetPatternLearningEvent(learnerId, request.EventId);
        if (existing is not null)
        {
            // A response can be lost before later source analysis. Match immutable request intent
            // against the committed event before consulting any mutable native source projection.
            var value = JsonSerializer.Deserialize<PatternLearningEvent>(existing.EventJson, Json) ?? throw new InvalidOperationException("Stored event is empty.");
            LearningObjectReference Reference(PatternLearningSubject subject) => new(subject.SnapshotId, subject.SourceNodeId, subject.ObjectHash);
            var intent = new LearningEventRequest(request.EventId, request.ExpectedCheckpointHash, value.GroupKey,
                Reference(value.Input.Left), value.Input.Right is null ? null : Reference(value.Input.Right),
                value.Input.Context, value.Label, value.CapturedAt, value.Epoch);
            if (CanonicalJson.Serialize(intent) != CanonicalJson.Serialize(request with { Epoch = request.Epoch ?? model.Checkpoint.Epoch }) ||
                CanonicalJson.HashUtf8(existing.EventJson) != existing.EventHash)
                throw new InvalidOperationException("Event ID is already committed with different request intent.");
            cancellationToken.ThrowIfCancellationRequested();
            var replay = PatternLearning.Apply(new(model.CheckpointHash, model.Checkpoint, model.Weights), value, model.CheckpointHash);
            if (replay.State.CheckpointHash != existing.CheckpointHash || replay.State.Checkpoint.AuditHash != existing.AuditHash)
                throw new InvalidOperationException("Stored event does not replay to its committed checkpoint.");
            var head = store.GetPatternLearningHead(learnerId) ?? throw new InvalidOperationException("Committed learner head is missing.");
            return new(new(true, head, request.EventId, existing.EventHash), existing.EventHash, existing.CheckpointHash,
                replay.State.Checkpoint.TrainingUpdates, replay.State.Checkpoint.ObservationEvents, replay.WeightsChanged);
        }
        using var reader = store.OpenSemanticReader();
        var input = new PatternLearningInput(Subject(reader, request.Left, cancellationToken),
            request.Right is null ? null : Subject(reader, request.Right, cancellationToken), request.Context);
        return Commit(learnerId, model, request.EventId, request.GroupKey, input, request.Label, request.CapturedAt, request.Epoch, cancellationToken);
    }
    public LearningObservationBatch ObservePage(string learnerId, LearningObservePageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > 100 || request.Offset < 0) throw new ArgumentException("Observation pages accept 1–100 source objects and a nonnegative offset.");
        if (string.IsNullOrWhiteSpace(request.BatchId) || request.BatchId.Length > 128 || request.BatchId.Any(char.IsControl)) throw new ArgumentException("Batch ID must contain 1–128 non-control characters.");
        var model = Load(learnerId, request.ExpectedCheckpointHash);
        using var reader = store.OpenSemanticReader(); var snapshot = Finished(reader, request.SnapshotId);
        var page = reader.Nodes(snapshot.Id, offset: request.Offset, limit: request.Limit, cancellationToken: cancellationToken);
        var processed = 0; var added = 0; string? error = null;
        foreach (var item in page.Items)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subject = ObservedPatternDatasetReader.LearningSubject(snapshot, item);
                var id = "observation:" + CanonicalJson.Sha256(new { request.BatchId, subject.SourceObservationId, subject.ObjectHash });
                var result = Commit(learnerId, model, id, request.GroupKey, new(subject, null, request.Context), null, request.CapturedAt, null, cancellationToken);
                processed++; if (!result.Receipt.Duplicate) added++;
                model = Load(learnerId, result.CheckpointHash);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ArgumentException)
            { error = ex is OperationCanceledException ? "Cancelled; earlier events remain committed." : ex.Message; break; }
        }
        return new(learnerId, snapshot.Id, request.BatchId, request.Offset, request.Limit, processed, added, page.Total,
            model.CheckpointHash, error is null, error is null && (long)request.Offset + processed >= page.Total, error);
    }
    private LearningAppendResult Commit(string learnerId, FrozenPatternLearningModel model, string eventId, string groupKey,
        PatternLearningInput input, PatternLearningLabel? label, DateTimeOffset capturedAt, PatternLearningEpoch? epoch,
        CancellationToken cancellationToken)
    {
        var before = model.Checkpoint;
        var value = new PatternLearningEvent(eventId, before.Sequence + 1, before.AuditHash, epoch ?? before.Epoch, groupKey,
            PatternLearning.AssignPartition(before.PartitionPolicy, groupKey), label is null ? "observation" : "training", input, label, capturedAt);
        var update = PatternLearning.Apply(new(model.CheckpointHash, before, model.Weights), value, model.CheckpointHash);
        var next = update.State.Checkpoint; var eventJson = CanonicalJson.Serialize(value);
        var expected = new PatternLearningStoreHead(learnerId, before.Sequence, before.Epoch.EpochId, model.CheckpointHash, before.WeightsHash, before.AuditHash);
        var commit = new PatternLearningStoreCommit(learnerId, eventId, update.EventHash, CanonicalJson.HashUtf8(eventJson), eventJson,
            expected, StoredEpoch(learnerId, next), new(before.PartitionPolicy.CohortId, groupKey, value.Partition, update.AssignmentHash),
            Document(next), next.WeightsHash, next.AuditHash,
            update.Terms.Select(term => new PatternLearningStoreTerm(term.TermHash, term.Category, term.Token, term.Count)).ToArray(),
            update.Occurrences.Select(item => new PatternLearningStoreOccurrence(item.SourceObservationId, item.PatternHash, item.SomId,
                item.SourceNodeId, item.SnapshotId, item.ObservedFrom, item.ObservedTo, item.CapturedAt, Document(item.Context))).ToArray(),
            update.WeightsChanged ? Document(update.State.Weights) : null);
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = store.CommitPatternLearning(commit);
        return new(receipt, update.EventHash, update.State.CheckpointHash, next.TrainingUpdates, next.ObservationEvents, update.WeightsChanged);
    }
    private static PatternLearningSubject Subject(MapStore.SemanticReader reader, LearningObjectReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference); var snapshot = Finished(reader, reference.SnapshotId);
        var observed = reader.Node(snapshot.Id, reference.NodeId, cancellationToken) ?? throw new KeyNotFoundException("Source node was not found in the recorded snapshot.");
        var subject = ObservedPatternDatasetReader.LearningSubject(snapshot, observed);
        if (subject.ObjectHash != reference.ExpectedObjectHash) throw new InvalidOperationException("Source object changed since selection. Reload it before recording a label.");
        return subject;
    }
    private static Snapshot Finished(MapStore.SemanticReader reader, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new ArgumentException("Invalid snapshot ID.");
        var snapshot = reader.Snapshot(id); if (snapshot.FinishedAt is null) throw new InvalidOperationException("Finish or stop source collection first."); return snapshot;
    }
    private static PatternLearningStoreEpoch StoredEpoch(string learnerId, PatternLearningCheckpoint checkpoint) =>
        new(learnerId, checkpoint.Epoch.EpochId, checkpoint.PartitionPolicy.CohortId, checkpoint.PartitionPolicyHash,
            checkpoint.Epoch.StartSequence, checkpoint.Epoch.EndSequence, checkpoint.Epoch.ParentCheckpointHash, Document(checkpoint.Epoch));
    private static PatternLearningStoreDocument Document<T>(T value)
    { var json = CanonicalJson.Serialize(value); var hash = CanonicalJson.HashUtf8(json); return new(hash, hash, json); }
    private static T ReadDocument<T>(PatternLearningStoreDocument document)
    {
        if (CanonicalJson.HashUtf8(document.Json) != document.JsonSha256 || document.JsonSha256 != document.Hash)
            throw new InvalidOperationException("Stored learning document failed its content commitment.");
        return JsonSerializer.Deserialize<T>(document.Json, Json) ?? throw new InvalidOperationException("Stored learning document is empty.");
    }
}
