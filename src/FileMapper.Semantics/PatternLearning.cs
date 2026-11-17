using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Semantics;

/// <summary>Pure reproducible online supervised learning. No file, graph, command or session is inferred or executed.</summary>
public static class PatternLearning
{
    public static ImmutableArray<string> Heads => PatternLearningRules.Heads;
    public static string ConfigurationHash(PatternLearningConfig config)
    {
        PatternLearningRules.Config(config);
        return CanonicalJson.Sha256(new { Config = config, Algorithm = PatternLearningMath.Version, PatternLearningMath.TableHash,
            ProbabilityScale = PatternLearningMath.Scale, FeatureScale = 1000, Rounding = "nearest-ties-away-from-zero" });
    }
    public static string AssignmentHash(PatternLearningPartitionPolicy policy, string groupKey)
    {
        PatternLearningRules.Policy(policy); PatternLearningRules.Text(groupKey, 512, "groupKey");
        return CanonicalJson.Sha256(new { Schema = "filemapper.pattern-group-assignment/1", PolicyHash = CanonicalJson.Sha256(policy), GroupKey = groupKey });
    }
    public static string AssignPartition(PatternLearningPartitionPolicy policy, string groupKey)
    {
        var digest = Convert.FromHexString(AssignmentHash(policy, groupKey));
        var bucket = BinaryPrimitives.ReadUInt64BigEndian(digest) % (ulong)policy.TotalBuckets;
        return bucket < (ulong)policy.TrainBuckets ? "train" : bucket < (ulong)(policy.TrainBuckets + policy.ValidationBuckets) ? "validation" : "test";
    }
    public static PatternLearningState Create(PatternLearningConfig config, PatternLearningPartitionPolicy policy, PatternLearningEpoch epoch)
    {
        PatternLearningRules.Config(config); PatternLearningRules.Policy(policy);
        var policyHash = CanonicalJson.Sha256(policy); PatternLearningRules.Epoch(epoch, policyHash);
        if (epoch.StartSequence != 1 || epoch.ParentCheckpointHash is not null) throw new ArgumentException("Genesis epoch begins at sequence one with no parent checkpoint.");
        var configurationHash = ConfigurationHash(config); var featureHash = PatternLearningFeatures.SchemaHash(config);
        var weights = new PatternLearningWeights(configurationHash, featureHash,
            Heads.Select(head => new PatternLearningHeadWeights(head, ImmutableArray.CreateRange(new int[config.FeatureCount]))).ToImmutableArray());
        var audit = CanonicalJson.Sha256(new { Schema = "filemapper.pattern-audit.genesis/1", ConfigurationHash = configurationHash, PolicyHash = policyHash, Epoch = epoch });
        var checkpoint = new PatternLearningCheckpoint(configurationHash, config, policy, policyHash, epoch, featureHash,
            CanonicalJson.Sha256(weights), null, 0, 0, 0, audit, null);
        return new(CanonicalJson.Sha256(checkpoint), checkpoint, weights);
    }
    public static FrozenPatternLearningModel Freeze(PatternLearningState state)
    {
        Validate(state); return new(state.CheckpointHash, state.Checkpoint, state.Weights);
    }
    public static void Validate(PatternLearningState state)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(state.Checkpoint); ArgumentNullException.ThrowIfNull(state.Weights);
        var checkpoint = state.Checkpoint; PatternLearningRules.Config(checkpoint.Config); PatternLearningRules.Policy(checkpoint.PartitionPolicy);
        var configurationHash = ConfigurationHash(checkpoint.Config); var featureHash = PatternLearningFeatures.SchemaHash(checkpoint.Config);
        PatternLearningRules.Hash(state.CheckpointHash, "checkpointHash"); PatternLearningRules.Hash(checkpoint.AuditHash, "auditHash");
        if (checkpoint.ParentCheckpointHash is not null) PatternLearningRules.Hash(checkpoint.ParentCheckpointHash, "parentCheckpointHash");
        if (checkpoint.LastEventHash is not null) PatternLearningRules.Hash(checkpoint.LastEventHash, "lastEventHash");
        if (checkpoint.Schema != "filemapper.pattern-learning.checkpoint/1" || state.Weights.Schema != "filemapper.pattern-learning.weights/1" ||
            checkpoint.ConfigHash != configurationHash || state.Weights.ConfigHash != configurationHash ||
            checkpoint.FeatureSchemaHash != featureHash || state.Weights.FeatureSchemaHash != featureHash ||
            checkpoint.PartitionPolicyHash != CanonicalJson.Sha256(checkpoint.PartitionPolicy) ||
            checkpoint.Sequence < 0 || checkpoint.Sequence > checkpoint.Config.MaximumEvents || checkpoint.TrainingUpdates < 0 ||
            checkpoint.ObservationEvents < 0 || checkpoint.TrainingUpdates + checkpoint.ObservationEvents != checkpoint.Sequence ||
            checkpoint.TrainingUpdates > checkpoint.Config.MaximumTrainingUpdates || state.Weights.Heads.IsDefault || state.Weights.Heads.Length != Heads.Length)
            throw new ArgumentException("Checkpoint configuration, counters or model schema are invalid.");
        PatternLearningRules.Epoch(checkpoint.Epoch, checkpoint.PartitionPolicyHash);
        if (checkpoint.Sequence > 0 && (checkpoint.Sequence < checkpoint.Epoch.StartSequence || checkpoint.Sequence > checkpoint.Epoch.EndSequence))
            throw new ArgumentException("Checkpoint lies outside its recorded epoch.");
        for (var i = 0; i < Heads.Length; i++)
        {
            var head = state.Weights.Heads[i];
            if (head is null || head.Head != Heads[i] || head.ValuesQ.IsDefault || head.ValuesQ.Length != checkpoint.Config.FeatureCount ||
                head.ValuesQ.Any(weight => weight < -checkpoint.Config.WeightBoundQ || weight > checkpoint.Config.WeightBoundQ))
                throw new ArgumentException("Weights are absent, reordered or exceed their numeric bounds.");
        }
        if (checkpoint.WeightsHash != CanonicalJson.Sha256(state.Weights) || state.CheckpointHash != CanonicalJson.Sha256(checkpoint))
            throw new ArgumentException("Checkpoint or weights failed their immutable content commitment.");
    }
    public static PatternLearningUpdate Apply(PatternLearningState state, PatternLearningEvent value, string expectedCheckpointHash)
    {
        Validate(state); ArgumentNullException.ThrowIfNull(value);
        if (state.CheckpointHash != expectedCheckpointHash) throw new InvalidOperationException("Stale checkpoint; replay against the current committed head.");
        ValidateEvent(state.Checkpoint, value, evaluation: false);
        var eventJson = CanonicalJson.Serialize(value);
        if (Encoding.UTF8.GetByteCount(eventJson) > 1_048_576) throw new ArgumentException("Learning event exceeds 1 MiB.");
        var eventHash = CanonicalJson.HashUtf8(eventJson);
        var features = PatternLearningFeatures.Extract(state.Checkpoint.Config, value.Input);
        var weights = state.Weights;
        var updates = state.Checkpoint.TrainingUpdates;
        if (value.Kind == "training")
        {
            if (updates >= state.Checkpoint.Config.MaximumTrainingUpdates) throw new InvalidOperationException("Training update bound reached; create a new explicitly versioned learner.");
            var label = value.Label!; var headIndex = Heads.IndexOf(label.Head); var previous = weights.Heads[headIndex];
            var values = previous.ValuesQ.ToArray(); var probability = Score(previous, features).Probability;
            var errorQ = label.Value * PatternLearningMath.Scale - probability;
            foreach (var feature in features.Items)
            {
                var change = PatternLearningMath.Divide((long)state.Checkpoint.Config.LearningRateQ * errorQ * feature.ValueQ, 1_000_000_000);
                values[feature.Index] = checked((int)Math.Clamp(values[feature.Index] + change,
                    -state.Checkpoint.Config.WeightBoundQ, state.Checkpoint.Config.WeightBoundQ));
            }
            weights = weights with { Heads = weights.Heads.SetItem(headIndex, previous with { ValuesQ = ImmutableArray.Create(values) }) };
            updates++;
        }
        var weightsHash = CanonicalJson.Sha256(weights);
        var auditHash = AuditHash(state.Checkpoint.AuditHash, eventHash, value.Sequence);
        var checkpoint = state.Checkpoint with
        {
            Epoch = value.Epoch, WeightsHash = weightsHash, ParentCheckpointHash = state.CheckpointHash, Sequence = value.Sequence,
            TrainingUpdates = updates, ObservationEvents = state.Checkpoint.ObservationEvents + (value.Kind == "observation" ? 1 : 0),
            AuditHash = auditHash, LastEventHash = eventHash
        };
        var (terms, occurrences) = PatternLearningFeatures.Observe(value);
        return new(new(CanonicalJson.Sha256(checkpoint), checkpoint, weights), eventHash,
            AssignmentHash(checkpoint.PartitionPolicy, value.GroupKey), features, terms, occurrences, weightsHash != state.Checkpoint.WeightsHash);
    }
    public static string AuditHash(string previousAuditHash, string eventHash, long sequence)
    {
        PatternLearningRules.Hash(previousAuditHash, "previousAuditHash"); PatternLearningRules.Hash(eventHash, "eventHash");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        return CanonicalJson.Sha256(new { Schema = "filemapper.pattern-audit/1", PreviousAuditHash = previousAuditHash, EventHash = eventHash, Sequence = sequence });
    }
    public static PatternLearningPrediction Predict(FrozenPatternLearningModel model, PatternLearningInput input, string head)
    {
        ArgumentNullException.ThrowIfNull(model); Validate(new(model.CheckpointHash, model.Checkpoint, model.Weights));
        var index = Heads.IndexOf(head); if (index < 0) throw new ArgumentException("Unknown relevance head.");
        return PredictCore(model, input, index);
    }
    public static PatternLearningPrediction[] PredictMany(FrozenPatternLearningModel model,
        IEnumerable<PatternLearningInput> inputs, string head, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(inputs); cancellationToken.ThrowIfCancellationRequested();
        Validate(new(model.CheckpointHash, model.Checkpoint, model.Weights));
        var index = Heads.IndexOf(head); if (index < 0) throw new ArgumentException("Unknown relevance head.");
        var results = new List<PatternLearningPrediction>();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count == 4096) throw new ArgumentException("Prediction batches are limited to 4096 pairs.");
            results.Add(PredictCore(model, input, index));
        }
        return results.ToArray();
    }
    private static PatternLearningPrediction PredictCore(FrozenPatternLearningModel model, PatternLearningInput input, int index)
    {
        PatternLearningRules.Input(input, requirePair: true);
        var features = PatternLearningFeatures.Extract(model.Checkpoint.Config, input); var weights = model.Weights.Heads[index];
        var (probability, logit) = Score(weights, features);
        var contributions = features.Items.Select(feature => new PatternLearningContribution(feature.Index, feature.Keys,
                feature.ValueQ, weights.ValuesQ[feature.Index], PatternLearningMath.Divide((long)weights.ValuesQ[feature.Index] * feature.ValueQ, 1000)))
            .OrderByDescending(item => Math.Abs(item.ContributionQ)).ThenBy(item => item.Index).Take(12).ToImmutableArray();
        return new(Heads[index], probability, logit, model.CheckpointHash, model.Checkpoint.WeightsHash, features.SchemaHash,
            features.InputHash, contributions, features.MissingContext, features.Truncated, features.CollisionCount);
    }
    private static (int Probability, int Logit) Score(PatternLearningHeadWeights weights, PatternLearningFeatureVector features)
    {
        long sum = 0;
        foreach (var feature in features.Items) sum = checked(sum + (long)weights.ValuesQ[feature.Index] * feature.ValueQ);
        var logit = (int)Math.Clamp(PatternLearningMath.Divide(sum, 1000), -8000000, 8000000);
        return (PatternLearningMath.Probability(logit), logit);
    }
    private static void ValidateEvent(PatternLearningCheckpoint checkpoint, PatternLearningEvent value, bool evaluation)
    {
        ArgumentNullException.ThrowIfNull(value);
        PatternLearningRules.Text(value.EventId, 256, "eventId"); PatternLearningRules.Text(value.GroupKey, 512, "groupKey");
        if (value.Schema != "filemapper.pattern-learning.event/1" || value.Kind is not ("observation" or "training") ||
            value.Partition != AssignPartition(checkpoint.PartitionPolicy, value.GroupKey)) throw new ArgumentException("Event schema/kind or sealed partition assignment is invalid.");
        PatternLearningRules.Input(value.Input, value.Kind == "training");
        if (value.Kind == "training")
        {
            PatternLearningRules.Label(value.Label!);
            if (!evaluation && value.Partition != "train") throw new ArgumentException("Held-out labels cannot update weights.");
        }
        else if (value.Label is not null) throw new ArgumentException("Unlabeled observations must not smuggle supervised labels.");
        if (evaluation) return;
        if (value.Sequence != checkpoint.Sequence + 1 || value.Sequence > checkpoint.Config.MaximumEvents || value.PreviousAuditHash != checkpoint.AuditHash)
            throw new InvalidOperationException("Duplicate, out-of-order or corrupt event sequence/audit commitment.");
        PatternLearningRules.Epoch(value.Epoch, checkpoint.PartitionPolicyHash);
        if (value.Sequence < value.Epoch.StartSequence || value.Sequence > value.Epoch.EndSequence) throw new ArgumentException("Event lies outside the epoch range.");
        if (value.Epoch.EpochId == checkpoint.Epoch.EpochId)
        {
            if (CanonicalJson.Sha256(value.Epoch) != CanonicalJson.Sha256(checkpoint.Epoch)) throw new ArgumentException("An epoch cannot be retroactively changed.");
        }
        else if (value.Epoch.StartSequence != value.Sequence || value.Epoch.ParentCheckpointHash != CanonicalJson.Sha256(checkpoint))
            throw new ArgumentException("A successor epoch must begin at the next event and commit its exact parent checkpoint.");
    }

    public static string HashEvaluationEvents(IEnumerable<PatternLearningEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var count = 0;
        foreach (var value in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 100000) throw new ArgumentException("Evaluation exceeds 100,000 events.");
            var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value));
            if (bytes.Length > 1_048_576) throw new ArgumentException("Evaluation event exceeds 1 MiB.");
            digest.AppendData(bytes); digest.AppendData(new byte[] { (byte)'\n' });
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    public static PatternLearningEvaluation Evaluate(FrozenPatternLearningModel model, PatternLearningEvaluationManifest manifest,
        IReadOnlyList<PatternLearningEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(manifest); ArgumentNullException.ThrowIfNull(events);
        Validate(new(model.CheckpointHash, model.Checkpoint, model.Weights));
        PatternLearningRules.Text(manifest.DatasetId, 256, "datasetId"); PatternLearningRules.Text(manifest.LabelPolicy, 2048, "labelPolicy");
        if (manifest.Schema != "filemapper.pattern-learning.evaluation/1" || manifest.Partition is not ("validation" or "test") ||
            manifest.PartitionPolicyHash != model.Checkpoint.PartitionPolicyHash || manifest.FirstSequence <= model.Checkpoint.Sequence ||
            manifest.LastSequence < manifest.FirstSequence || events.Count is < 1 or > 100000)
            throw new ArgumentException("Evaluation needs a bounded sealed held-out cohort after the frozen training sequence.");
        var eventHash = HashEvaluationEvents(events, cancellationToken);
        if (eventHash != manifest.EventsHash) throw new ArgumentException("Evaluation dataset differs from its sealed content commitment.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var sequences = new HashSet<long>();
        var metrics = Heads.ToDictionary(head => head, head => new Metrics(head), StringComparer.Ordinal);
        foreach (var value in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEvent(model.Checkpoint, value, evaluation: true);
            if (value.Kind != "training" || value.Partition != manifest.Partition || value.Sequence < manifest.FirstSequence || value.Sequence > manifest.LastSequence ||
                !ids.Add(value.EventId) || !sequences.Add(value.Sequence)) throw new ArgumentException("Evaluation contains training-group data, duplicates or out-of-range events.");
            var label = value.Label!;
            var features = PatternLearningFeatures.Extract(model.Checkpoint.Config, value.Input);
            var (probability, logit) = Score(model.Weights.Heads[Heads.IndexOf(label.Head)], features);
            metrics[label.Head].Add(probability, logit, label.Value);
        }
        return new(model.CheckpointHash, model.Checkpoint.WeightsHash, CanonicalJson.Sha256(manifest), eventHash,
            manifest.Partition, Heads.Select(head => metrics[head].Result()).ToImmutableArray(), events.Count);
    }
    private sealed class Metrics(string head)
    {
        private long count, tp, fp, tn, fn, loss, square;
        internal void Add(int probability, int logit, int label)
        {
            count++; var positive = probability >= 500000;
            if (positive && label == 1) tp++; else if (positive) fp++; else if (label == 1) fn++; else tn++;
            loss = checked(loss + PatternLearningMath.LogLoss(logit, label));
            var error = (long)probability - label * PatternLearningMath.Scale; square = checked(square + error * error);
        }
        internal PatternLearningHeadMetrics Result() => new(head, count, tp, fp, tn, fn, loss, square,
            count == 0 ? 0 : PatternLearningMath.Divide(loss, count), count == 0 ? 0 : PatternLearningMath.Divide(square, count),
            tp + fp == 0 ? null : (int)PatternLearningMath.Divide(tp * PatternLearningMath.Scale, tp + fp),
            tp + fn == 0 ? null : (int)PatternLearningMath.Divide(tp * PatternLearningMath.Scale, tp + fn));
    }
}
