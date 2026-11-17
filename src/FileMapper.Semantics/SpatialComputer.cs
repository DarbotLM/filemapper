using System.Collections.Immutable;

namespace FileMapper.Semantics;

// This model is independent of the semantic relevance learner. Features and rewards are game-only.
public sealed record GamePolicyModel(ImmutableArray<long> Weights, string? ParentHash = null, string? PartitionManifestHash = null,
    string Schema = "filemapper.spatial-policy/1")
{
    public string Hash => CanonicalJson.Sha256(new { Schema, Weights, ParentHash, PartitionManifestHash });
}
public sealed record GameEpisodeSource(string RepositoryFamily, string RevisionFamily, string Partition);
public sealed record GameMoveActor(string ActorId, string SessionId, string ModelId, string CheckpointHash);
public sealed record GameEpisodeMove(GameMoveActor Actor, string PieceId, string ObservationId, string SourceNodeId,
    string SomId, string? SourceSomId, string GeometryHash, SpatialMove Move, string StateBeforeHash,
    string StateAfterHash, long VerifiedReward, string RecordHash);
public sealed record GameEpisode(GameEpisodeSource Source, string ChallengeHash, ImmutableArray<GameEpisodeMove> Moves,
    string FinalStateHash, long VerifiedReward, string EpisodeHash);
public sealed record GamePolicyComparison(string BeforeHash, string AfterHash, string ChallengeHash,
    string Partition, GameEpisode Before, GameEpisode After, long RewardDelta);

/// <summary>Replay-verified placement policy. Advisory affinity points never become training reward.</summary>
public static class SpatialComputer
{
    public static GamePolicyModel InitialPolicy => new([0, 0, 0, 0]);

    public static GameEpisode Play(SpatialChallenge challenge, GameEpisodeSource source, GameMoveActor actor,
        GamePolicyModel policy, CancellationToken token = default)
    {
        Validate(source, actor, policy);
        if (actor.CheckpointHash != policy.Hash) throw new ArgumentException("Actor checkpoint must identify the supplied game policy.");
        var state = SpatialGame.Start(challenge);
        var records = ImmutableArray.CreateBuilder<GameEpisodeMove>();
        while (!state.IsOver)
        {
            token.ThrowIfCancellationRequested();
            var legal = SpatialGame.LegalMoves(state, token);
            var choice = legal.OrderByDescending(m => Score(policy, Features(state, m)))
                .ThenBy(m => m.Move.OrientationId, StringComparer.Ordinal).ThenBy(m => m.Move.X).ThenBy(m => m.Move.Z).First();
            var piece = state.Challenge.Deck[state.NextPieceIndex];
            var next = SpatialGame.Apply(state, choice.Move, token);
            var reward = Reward(next.History[^1]);
            var record = new GameEpisodeMove(actor, piece.Id, piece.ObservationId, piece.SourceNodeId, piece.SomId,
                piece.SourceSomId, piece.GeometryHash!, choice.Move, state.StateHash, next.StateHash, reward, "");
            records.Add(record with { RecordHash = HashRecord(record) });
            state = next;
        }
        var episode = new GameEpisode(source, state.Challenge.ChallengeHash!, records.ToImmutable(), state.StateHash,
            records.Sum(x => x.VerifiedReward), "");
        return episode with { EpisodeHash = HashEpisode(episode) };
    }

    /// <summary>Capture externally authored moves without claiming that a built-in heuristic chose them.</summary>
    public static GameEpisode Capture(SpatialChallenge challenge, GameEpisodeSource source, GameMoveActor actor,
        IReadOnlyList<SpatialMove> moves, CancellationToken token = default)
    {
        ValidateSource(source); ValidateActor(actor);
        if (moves is null || moves.Count > SpatialGame.MaximumDeckLength) throw new ArgumentException("Invalid move sequence.");
        var state = SpatialGame.Start(challenge); var records = ImmutableArray.CreateBuilder<GameEpisodeMove>();
        foreach (var move in moves)
        {
            if (state.IsOver) throw new ArgumentException("Moves extend past game end.");
            var piece = state.Challenge.Deck[state.NextPieceIndex];
            var next = SpatialGame.Apply(state, move, token);
            var record = new GameEpisodeMove(actor, piece.Id, piece.ObservationId, piece.SourceNodeId, piece.SomId,
                piece.SourceSomId, piece.GeometryHash!, move, state.StateHash, next.StateHash, Reward(next.History[^1]), "");
            records.Add(record with { RecordHash = HashRecord(record) }); state = next;
        }
        if (!state.IsOver) throw new ArgumentException("The episode must reach game end.");
        var episode = new GameEpisode(source, state.Challenge.ChallengeHash!, records.ToImmutable(), state.StateHash,
            records.Sum(r => r.VerifiedReward), "");
        return episode with { EpisodeHash = HashEpisode(episode) };
    }
    /// <summary>Verify all move provenance, hashes and rewards against the authoritative arena replay.</summary>
    public static void Verify(SpatialChallenge challenge, GameEpisode episode, CancellationToken token = default)
    {
        if (episode is null || episode.Moves.IsDefault) throw new ArgumentException("Episode and moves are required.");
        ValidateSource(episode.Source);
        var state = SpatialGame.Start(challenge);
        if (episode.ChallengeHash != state.Challenge.ChallengeHash) throw new ArgumentException("Challenge mismatch.");
        long total = 0;
        foreach (var record in episode.Moves)
        {
            token.ThrowIfCancellationRequested();
            if (state.IsOver) throw new ArgumentException("Moves extend past game end.");
            ValidateActor(record.Actor);
            var piece = state.Challenge.Deck[state.NextPieceIndex];
            if (record.PieceId != piece.Id || record.ObservationId != piece.ObservationId || record.SourceNodeId != piece.SourceNodeId
                || record.SomId != piece.SomId || record.SourceSomId != piece.SourceSomId || record.GeometryHash != piece.GeometryHash
                || record.StateBeforeHash != state.StateHash || record.RecordHash != HashRecord(record)) throw new ArgumentException("Move provenance or hash mismatch.");
            state = SpatialGame.Apply(state, record.Move, token);
            if (record.StateAfterHash != state.StateHash || record.VerifiedReward != Reward(state.History[^1])) throw new ArgumentException("Move replay or reward mismatch.");
            total = checked(total + record.VerifiedReward);
        }
        if (!state.IsOver || episode.FinalStateHash != state.StateHash || episode.VerifiedReward != total || episode.EpisodeHash != HashEpisode(episode))
            throw new ArgumentException("Episode is incomplete or its replay hash differs.");
    }

    /// <summary>One deterministic reward-weighted update from a verified training episode only.</summary>
    public static GamePolicyModel Train(SpatialChallenge challenge, GameEpisode episode, GamePolicyModel parent, GamePartitionManifest manifest, CancellationToken token = default)
    {
        ValidateModel(parent);
        GamePartitions.Require(manifest, episode.Source);
        if (parent.PartitionManifestHash is not null && parent.PartitionManifestHash != GamePartitions.Hash(manifest)) throw new ArgumentException("Policy is bound to a different partition manifest.");
        Verify(challenge, episode, token);
        if (episode.Source.Partition != "train") throw new ArgumentException("Only training episodes may update policy weights.");
        if (episode.Moves.Any(m => m.Actor.CheckpointHash != parent.Hash)) throw new ArgumentException("Episode must be collected by the parent checkpoint.");
        var state = SpatialGame.Start(challenge); var weights = parent.Weights.ToArray();
        foreach (var record in episode.Moves)
        {
            var choices = SpatialGame.LegalMoves(state, token);
            var legal = choices.OrderByDescending(m => Score(parent, Features(state, m)))
                .ThenBy(m => m.Move.OrientationId, StringComparer.Ordinal).ThenBy(m => m.Move.X).ThenBy(m => m.Move.Z).First();
            if (legal.Move != record.Move) throw new ArgumentException("Training move was not selected by the frozen parent policy.");
            var features = Features(state, legal);
            for (var i = 0; i < weights.Length; i++) weights[i] = Math.Clamp(checked(weights[i] + features[i] * Math.Clamp(record.VerifiedReward, -10000, 10000)), -1000000000L, 1000000000L);
            state = SpatialGame.Apply(state, record.Move, token);
        }
        return new(weights.ToImmutableArray(), parent.Hash, GamePartitions.Hash(manifest));
    }

    public static GamePolicyComparison Compare(SpatialChallenge challenge, GameEpisodeSource source, string actorId,
        string sessionId, GamePolicyModel before, GamePolicyModel after, GamePartitionManifest manifest, CancellationToken token = default)
    {
        GamePartitions.Require(manifest, source);
        var manifestHash = GamePartitions.Hash(manifest);
        if (before.PartitionManifestHash is not null && before.PartitionManifestHash != manifestHash || after.PartitionManifestHash != manifestHash) throw new ArgumentException("Policies do not belong to this partition manifest.");
        if (source.Partition != "test") throw new ArgumentException("Frozen comparisons require a test episode.");
        var a = Play(challenge, source, new(actorId, sessionId, "game-policy", before.Hash), before, token);
        var b = Play(challenge, source, new(actorId, sessionId, "game-policy", after.Hash), after, token);
        Verify(challenge, a, token); Verify(challenge, b, token);
        return new(before.Hash, after.Hash, a.ChallengeHash, source.Partition, a, b, b.VerifiedReward - a.VerifiedReward);
    }

    private static long[] Features(SpatialState state, SpatialLegalMove move) =>
        [1, move.TopOut ? -1 : 0, -(move.LandingY ?? state.Challenge.Board!.Height), -move.Move.X - move.Move.Z];
    private static long Score(GamePolicyModel model, long[] features) => model.Weights.Zip(features, (w, f) => w * f).Sum();
    private static long Reward(SpatialStep step) => checked(step.ScoreBreakdown.Total - step.ScoreBreakdown.LearnedAdvisoryPoints - (step.TopOut ? 1000 : 0));
    private static string HashRecord(GameEpisodeMove record) => CanonicalJson.Sha256(record with { RecordHash = "" });
    private static string HashEpisode(GameEpisode episode) => CanonicalJson.Sha256(episode with { EpisodeHash = "" });
    private static void Validate(GameEpisodeSource source, GameMoveActor actor, GamePolicyModel model) { ValidateSource(source); ValidateActor(actor); ValidateModel(model); }
    private static void ValidateSource(GameEpisodeSource source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.RepositoryFamily) || string.IsNullOrWhiteSpace(source.RevisionFamily)
            || source.Partition is not ("train" or "validation" or "test")) throw new ArgumentException("Repository family, revision family and a known partition are required.");
    }
    private static void ValidateActor(GameMoveActor actor)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.ActorId) || string.IsNullOrWhiteSpace(actor.SessionId)
            || string.IsNullOrWhiteSpace(actor.ModelId) || string.IsNullOrWhiteSpace(actor.CheckpointHash)) throw new ArgumentException("Complete actor provenance is required.");
    }
    private static void ValidateModel(GamePolicyModel model)
    {
        if (model is null || model.Schema != "filemapper.spatial-policy/1" || model.Weights.IsDefault || model.Weights.Length != 4
            || model.Weights.Any(w => w is < -1000000000L or > 1000000000L)) throw new ArgumentException("Invalid game policy.");
    }
}

public sealed record GameEpisodeAssignment(string RepositoryFamily, string RevisionFamily, string Partition);
public sealed record GamePartitionManifest(ImmutableArray<GameEpisodeAssignment> Assignments, string? CohortHash = null,
    string Schema = "filemapper.spatial-policy.partitions/1");

/// <summary>Frozen source-family assignments; repository families cannot cross partitions.</summary>
public static class GamePartitions
{
    public static string Hash(GamePartitionManifest manifest)
    {
        Validate(manifest);
        return CanonicalJson.Sha256(manifest);
    }
    public static void Validate(GamePartitionManifest manifest)
    {
        if (manifest is null || manifest.Schema != "filemapper.spatial-policy.partitions/1" || manifest.Assignments.IsDefaultOrEmpty)
            throw new ArgumentException("A nonempty partition manifest is required.");
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in manifest.Assignments)
        {
            if (assignment is null || string.IsNullOrWhiteSpace(assignment.RepositoryFamily) || string.IsNullOrWhiteSpace(assignment.RevisionFamily)
                || assignment.Partition is not ("train" or "validation" or "test")) throw new ArgumentException("Invalid family assignment.");
            if (families.TryGetValue(assignment.RepositoryFamily, out var prior) && prior != assignment.Partition)
                throw new ArgumentException("Repository family crosses partitions.");
            if (revisions.TryGetValue(assignment.RevisionFamily, out prior) && prior != assignment.Partition)
                throw new ArgumentException("Revision family crosses partitions.");
            families[assignment.RepositoryFamily] = assignment.Partition;
            revisions[assignment.RevisionFamily] = assignment.Partition;
        }
    }
    public static void Require(GamePartitionManifest manifest, GameEpisodeSource source)
    {
        Validate(manifest);
        if (source is null || !manifest.Assignments.Any(a => a.RepositoryFamily == source.RepositoryFamily
            && a.RevisionFamily == source.RevisionFamily && a.Partition == source.Partition))
            throw new ArgumentException("Episode source is absent from the frozen partition manifest.");
    }
}






// Separate from semantic supervision and from the original reward-weighted policy.
public sealed record ExpertPolicyModel(ImmutableArray<int> Weights, string? ParentHash = null, string? PartitionManifestHash = null,
    string Schema = "filemapper.spatial-expert-policy/1", string FeatureSchema = "spatial-outcome-features/1")
{
    public string Hash => CanonicalJson.Sha256(new { Schema, FeatureSchema, Weights, ParentHash, PartitionManifestHash });
}
public sealed record ExpertReview(string EpisodeHash, string ReviewerId, string ReviewId, string Rationale, DateTimeOffset ReviewedAt);
public sealed record ExpertTrainingReceipt(string Schema, string FeatureSchema, string ManifestHash, string ChallengeHash,
    string EpisodeHash, ExpertReview Review, string ParentPolicyHash, ExpertPolicyModel Policy,
    int Moves, int Updates, string CandidateTraceHash, string ReceiptHash,
    string Attribution = "reviewer and external actor identity are declared; replay verifies moves, not identity", int UpdateAttempts = 0, int CandidateLimit = 64);
public sealed record ExpertStep(string StateHash, string ModelHash, SpatialMove Move, int CandidatesEvaluated);
public sealed record ExpertComparison(string ManifestHash, string ChallengeHash, string Partition, GameEpisode Before, GameEpisode After, long RewardDelta);

/// <summary>Bounded pairwise imitation of reviewed choices. Game outcomes are features, never semantic labels.</summary>
public static class SpatialExpertComputer
{
    public const int CandidateLimit = 64;
    public static ExpertPolicyModel InitialPolicy => new([0, 0, 0, 0, 0, 0, 0, 0]);
    private sealed record Candidate(SpatialMove Move, long[] Features, SpatialState Result);

    public static ExpertTrainingReceipt Train(SpatialChallenge challenge, GameEpisode episode, ExpertReview review,
        ExpertPolicyModel parent, GamePartitionManifest manifest, CancellationToken token = default)
    {
        Validate(parent); GamePartitions.Require(manifest, episode.Source);
        var manifestHash = GamePartitions.Hash(manifest);
        if (episode.Source.Partition != "train") throw new ArgumentException("Reviewed expert training requires a training partition.");
        if (string.IsNullOrWhiteSpace(manifest.CohortHash)) throw new ArgumentException("A content-bound cohort is required.");
        if (parent.PartitionManifestHash is not null && parent.PartitionManifestHash != manifestHash) throw new ArgumentException("Parent belongs to a different cohort.");
        if (review is null || review.EpisodeHash != episode.EpisodeHash || review.ReviewedAt == default || !ValidText(review.ReviewerId)
            || !ValidText(review.ReviewId) || !ValidText(review.Rationale)) throw new ArgumentException("An explicit review bound to the exact episode is required.");
        SpatialComputer.Verify(challenge, episode, token);
        review = review with { ReviewedAt = review.ReviewedAt.ToUniversalTime() };
        var state = SpatialGame.Start(challenge); var weights = parent.Weights.ToArray(); var updates = 0; var attempts = 0;
        var trace = new List<object>();
        foreach (var move in episode.Moves)
        {
            var options = Candidates(state, move.Move, token);
            var chosen = options.First(x => x.Move == move.Move);
            var best = Rank(options, weights);
            trace.Add(new { State = state.StateHash, Expert = move.Move, CandidateMoves = options.Select(x => x.Move).ToArray(), Best = best.Move });
            if (best.Move != chosen.Move && Score(weights, best.Features) >= Score(weights, chosen.Features))
            {
                attempts++; var changed = false;
                for (var i = 0; i < weights.Length; i++)
                {
                    var updated = (int)Math.Clamp((long)weights[i] + chosen.Features[i] - best.Features[i], -10000, 10000);
                    changed |= updated != weights[i]; weights[i] = updated;
                }
                if (changed) updates++;
            }
            state = chosen.Result;
        }
        var model = new ExpertPolicyModel(weights.ToImmutableArray(), parent.Hash, manifestHash);
        var receipt = new ExpertTrainingReceipt("filemapper.spatial-expert-training/1", model.FeatureSchema, manifestHash,
            state.Challenge.ChallengeHash!, episode.EpisodeHash, review, parent.Hash, model, episode.Moves.Length, updates,
            CanonicalJson.Sha256(trace), "", UpdateAttempts: attempts, CandidateLimit: CandidateLimit);
        return receipt with { ReceiptHash = HashReceipt(receipt) };
    }
    public static void VerifyReceipt(SpatialChallenge challenge, GameEpisode episode, ExpertPolicyModel parent,
        GamePartitionManifest manifest, ExpertTrainingReceipt receipt, CancellationToken token = default)
    {
        if (receipt is null || receipt.ReceiptHash != HashReceipt(receipt)) throw new ArgumentException("Receipt hash differs.");
        var expected = Train(challenge, episode, receipt.Review, parent, manifest, token);
        if (CanonicalJson.Serialize(expected) != CanonicalJson.Serialize(receipt with { Review = receipt.Review with { ReviewedAt = receipt.Review.ReviewedAt.ToUniversalTime() } })) throw new ArgumentException("Training receipt does not reproduce.");
    }
    /// <summary>One bounded deterministic expert-ranking decision for an already replayed practice state.</summary>
    public static ExpertStep Choose(SpatialState state, ExpertPolicyModel model, CancellationToken token = default)
    {
        Validate(model);
        if (state is null || state.IsOver) throw new ArgumentException("A live replay state is required.");
        var options = Candidates(state, null, token);
        var selected = Rank(options, model.Weights);
        return new(state.StateHash, model.Hash, selected.Move, options.Count);
    }
    public static GameEpisode Play(SpatialChallenge challenge, GameEpisodeSource source, GameMoveActor actor,
        ExpertPolicyModel model, CancellationToken token = default)
    {
        Validate(model);
        if (actor is null || actor.CheckpointHash != model.Hash) throw new ArgumentException("Actor checkpoint differs from policy.");
        var state = SpatialGame.Start(challenge); var moves = new List<SpatialMove>();
        while (!state.IsOver)
        {
            var selected = Rank(Candidates(state, null, token), model.Weights.ToArray());
            moves.Add(selected.Move); state = selected.Result;
        }
        return SpatialComputer.Capture(challenge, source, actor, moves, token);
    }
    public static ExpertComparison Compare(SpatialChallenge challenge, GameEpisodeSource source, string actorId, string sessionId,
        ExpertPolicyModel before, ExpertPolicyModel after, GamePartitionManifest manifest, CancellationToken token = default)
    {
        GamePartitions.Require(manifest, source); var hash = GamePartitions.Hash(manifest);
        if (source.Partition != "test" || string.IsNullOrWhiteSpace(manifest.CohortHash)) throw new ArgumentException("Comparison requires a content-bound test family.");
        if (before.PartitionManifestHash is not null && before.PartitionManifestHash != hash || after.PartitionManifestHash != hash)
            throw new ArgumentException("Policies belong to a different cohort.");
        var a = Play(challenge, source, new(actorId, sessionId, "expert-ranking-policy", before.Hash), before, token);
        var b = Play(challenge, source, new(actorId, sessionId, "expert-ranking-policy", after.Hash), after, token);
        SpatialComputer.Verify(challenge, a, token); SpatialComputer.Verify(challenge, b, token);
        return new(hash, a.ChallengeHash, source.Partition, a, b, checked(b.VerifiedReward - a.VerifiedReward));
    }
    private static List<Candidate> Candidates(SpatialState state, SpatialMove? expert, CancellationToken token)
    {
        var legal = SpatialGame.LegalMovesVerified(state, token).OrderBy(m => m.Move.OrientationId, StringComparer.Ordinal)
            .ThenBy(m => m.Move.X).ThenBy(m => m.Move.Z).ToArray();
        if (legal.Length == 0) throw new ArgumentException("No legal moves.");
        var picked = new SortedSet<int>();
        var count = Math.Min(CandidateLimit, legal.Length);
        for (var i = 0; i < count; i++) picked.Add(count == 1 ? 0 : (int)((long)i * (legal.Length - 1) / (count - 1)));
        if (expert is not null)
        {
            var index = Array.FindIndex(legal, m => m.Move == expert);
            if (index < 0) throw new ArgumentException("Reviewed move is not legal.");
            picked.Add(index);
        }
        var result = new List<Candidate>(picked.Count);
        foreach (var index in picked)
        {
            token.ThrowIfCancellationRequested(); var action = legal[index];
            var next = SpatialGame.ApplyVerified(state, action.Move, token); var step = next.History[^1];
            var layer = action.Move.LayerId;
            var cells = next.Placements.Where(p => p.LayerId == layer).SelectMany(p => SpatialGame.Cells(next.Challenge, p)).ToArray();
            var height = cells.Length == 0 ? 0 : cells.Max(c => c.Y) + 1;
            var cavities = cells.GroupBy(c => (c.X, c.Z)).Sum(g => g.Max(c => c.Y) + 1 - g.Count());
            var orientation = SpatialGameGeometry.Orientations(state.Challenge.Deck[state.NextPieceIndex].BaseShape,
                state.Challenge.Deck[state.NextPieceIndex].GeometryVariant).First(o => o.Id == action.Move.OrientationId);
            long[] features = [action.TopOut ? -1 : 0, step.ScoreBreakdown.Lines + 10L * step.ScoreBreakdown.Planes,
                step.PatternMatches.Length, -(action.LandingY ?? state.Challenge.Board!.Height), -height,
                -cavities, orientation.Width * orientation.Depth, orientation.Height];
            result.Add(new(action.Move, features, next));
        }
        return result;
    }
    private static Candidate Rank(IEnumerable<Candidate> candidates, IReadOnlyList<int> weights) => candidates
        .OrderByDescending(c => Score(weights, c.Features)).ThenBy(c => c.Move.OrientationId, StringComparer.Ordinal)
        .ThenBy(c => c.Move.X).ThenBy(c => c.Move.Z).First();
    private static long Score(IReadOnlyList<int> weights, long[] features)
    {
        long value = 0; for (var i = 0; i < features.Length; i++) value = checked(value + weights[i] * features[i]); return value;
    }
    private static bool ValidText(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private static string HashReceipt(ExpertTrainingReceipt receipt) => CanonicalJson.Sha256(receipt with { ReceiptHash = "", Review = receipt.Review with { ReviewedAt = receipt.Review.ReviewedAt.ToUniversalTime() } });
    private static void Validate(ExpertPolicyModel model)
    {
        if (model is null || model.Schema != "filemapper.spatial-expert-policy/1" || model.FeatureSchema != "spatial-outcome-features/1"
            || model.Weights.IsDefault || model.Weights.Length != 8 || model.Weights.Any(w => w is < -10000 or > 10000)) throw new ArgumentException("Invalid expert policy.");
    }
}
