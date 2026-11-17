using System.Collections.Immutable;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record SpatialArenaRequest(PatternDatasetRequest Dataset, string LearnerId, string CheckpointHash,
    PatternLearningContext? Context = null, SpatialBoardSpec? Board = null);
public sealed record SpatialArena(string Schema, SpatialArenaRequest Request, ObservedPatternDataset Dataset,
    FrozenPatternLearningModel Model, PatternLearningContext Context, SpatialChallenge Challenge,
    int CandidateAffinities, bool AffinitiesTruncated, string AffinitySelectionPolicy);
public sealed record SavedSpatialArena(string ArtifactHash, DateTimeOffset StoredAt, SpatialArena Arena);
public sealed record SpatialReplayRequest(string ArenaHash, SpatialMove[] Moves);
public sealed record SpatialReplayRecord(string Schema, string ArenaHash, string Competitor, string Attribution,
    ImmutableArray<SpatialMove> Moves, SpatialState FinalState);
public sealed record SpatialArenaView(string ArenaHash, SpatialState State, ImmutableArray<SpatialLegalMove> LegalMoves,
    ImmutableArray<SpatialOrientation> Orientations);

/// <summary>Freezes native source evidence and one immutable learned model before the first move.</summary>
public sealed class SpatialWorkspace(MapStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public SpatialArena Preview(SpatialArenaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = new PatternLearningWorkspace(store).Load(request.LearnerId, request.CheckpointHash);
        var context = request.Context ?? PatternLearningContext.Unknown;
        var dataset = new ObservedPatternDatasetReader(store).Read(request.Dataset, cancellationToken);
        var head = request.Dataset.Console is "csharp" or "powershell" ? "code-path" : "storage-group";
        var affinities = new List<SpatialAffinity>();
        var pairs = new List<(string Layer, PatternDatasetObject Left, PatternDatasetObject Right, PatternLearningInput Input)>();
        // Endpoint order is ordinal and fixed. Predictions are directed relevance measurements;
        // this one ordering is used for the unordered game pair, rather than selecting the higher score.
        foreach (var layer in dataset.Objects.GroupBy(value => value.Piece.LayerId, StringComparer.Ordinal))
        {
            var values = layer.OrderBy(value => value.Piece.Id, StringComparer.Ordinal).ToArray();
            for (var left = 0; left < values.Length; left++)
            for (var right = left + 1; right < values.Length; right++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = new PatternLearningInput(values[left].LearningSubject, values[right].LearningSubject, context);
                pairs.Add((layer.Key, values[left], values[right], input));
            }
        }
        var predictions = PatternLearning.PredictMany(model, pairs.Select(pair => pair.Input), head, cancellationToken);
        for (var index = 0; index < pairs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var pair = pairs[index];
            var featureHash = CanonicalJson.Sha256(PatternLearningFeatures.Extract(model.Checkpoint.Config, pair.Input));
            var id = "spatial-affinity:" + CanonicalJson.Sha256(new { pair.Layer, First = pair.Left.Piece.Id, Second = pair.Right.Piece.Id, model.CheckpointHash, featureHash });
            affinities.Add(new(id, pair.Layer, pair.Left.Piece.Id, pair.Right.Piece.Id, predictions[index].ProbabilityQ, model.CheckpointHash, featureHash));
        }
        const string selection = "same-layer pairs; endpoints ordered by piece ID; predicted relevance descending then affinity ID; first 512; no held-out labels or game patterns as model features";
        var selected = affinities.OrderByDescending(a => a.ProbabilityPpm).ThenBy(a => a.Id, StringComparer.Ordinal).Take(512).ToImmutableArray();
        var challenge = SpatialGame.Start(new("spatial:" + CanonicalJson.Sha256(new { dataset.DatasetHash, model.CheckpointHash, context, request.Board }),
            dataset.DatasetHash, model.CheckpointHash, dataset.Layers.Select(layer => layer.Layer).ToImmutableArray(),
            dataset.Objects.Select(value => value.Piece).ToImmutableArray(), dataset.Patterns, selected,
            request.Board, request.Dataset.ClearMode)).Challenge;
        return new("filemapper.spatial-arena/1", request, dataset, model, context, challenge, affinities.Count, affinities.Count > 512, selection);
    }
    public SavedSpatialArena Freeze(SpatialArenaRequest request, CancellationToken cancellationToken = default)
    {
        var arena = Preview(request, cancellationToken); cancellationToken.ThrowIfCancellationRequested();
        var payload = CanonicalJson.Serialize(arena); var hash = CanonicalJson.HashUtf8(payload);
        var saved = store.SaveSemanticArtifact(hash, "spatial-arena/1", null, payload,
            CanonicalJson.Serialize(new { Console = request.Dataset.Console, arena.Challenge.ChallengeHash, request.LearnerId,
                arena.Model.CheckpointHash, arena.Model.Checkpoint.TrainingUpdates, Pieces = arena.Challenge.Deck.Length,
                Layers = arena.Challenge.Layers.Length, arena.Dataset.DatasetHash }));
        return new(hash, saved.StoredAt, arena);
    }
    public SavedSpatialArena Load(string hash)
    {
        var artifact = store.GetSemanticArtifact(hash) ?? throw new KeyNotFoundException("Saved arena was not found.");
        if (artifact.Kind != "spatial-arena/1") throw new ArgumentException("Artifact is not an arena.");
        var arena = JsonSerializer.Deserialize<SpatialArena>(artifact.Payload, Json) ?? throw new InvalidOperationException("Invalid stored arena.");
        if (arena.Schema != "filemapper.spatial-arena/1" || arena.Model.CheckpointHash != arena.Challenge.ModelArtifactHash ||
            arena.Dataset.DatasetHash != arena.Challenge.DatasetHash) throw new InvalidOperationException("Arena commitments differ.");
        PatternLearning.Validate(new(arena.Model.CheckpointHash, arena.Model.Checkpoint, arena.Model.Weights));
        _ = SpatialGame.Start(arena.Challenge);
        return new(hash, artifact.StoredAt, arena);
    }
    public SpatialArenaView Replay(SpatialReplayRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Moves is null || request.Moves.Length > SpatialGame.MaximumDeckLength) throw new ArgumentException("A replay accepts at most 128 moves.");
        var arena = Load(request.ArenaHash); var state = SpatialGame.Replay(arena.Arena.Challenge, request.Moves, cancellationToken);
        var orientations = state.IsOver ? ImmutableArray<SpatialOrientation>.Empty : SpatialGameGeometry.Orientations(
            state.Challenge.Deck[state.NextPieceIndex].BaseShape, state.Challenge.Deck[state.NextPieceIndex].GeometryVariant);
        return new(request.ArenaHash, state, SpatialGame.LegalMoves(state, cancellationToken), orientations);
    }
    public SpatialDecision Choose(SpatialReplayRequest request, SpatialSearchBudget? budget = null, CancellationToken cancellationToken = default) =>
        SpatialOpponent.ChooseMove(Replay(request, cancellationToken).State, budget, cancellationToken);
    public SemanticArtifact SaveReplay(SpatialReplayRequest request, string competitor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(competitor) || competitor.Length > 128 || competitor.Any(char.IsControl)) throw new ArgumentException("A competitor label is required (up to 128 characters).");
        var state = Replay(request, cancellationToken).State;
        var record = new SpatialReplayRecord("filemapper.spatial-replay/1", request.ArenaHash, competitor,
            "caller-declared competitor; replay verifies rules and state, not external model identity", request.Moves.ToImmutableArray(), state);
        var payload = CanonicalJson.Serialize(record); cancellationToken.ThrowIfCancellationRequested();
        return store.SaveSemanticArtifact(CanonicalJson.HashUtf8(payload), "spatial-replay/1", request.ArenaHash, payload,
            CanonicalJson.Serialize(new { Competitor = competitor, state.Score, state.LearnedAdvisoryScore, state.NextPieceIndex, state.IsOver, state.StateHash }));
    }
}
