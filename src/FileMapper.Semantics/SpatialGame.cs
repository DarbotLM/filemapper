using System.Collections.Immutable;

namespace FileMapper.Semantics;

/// <summary>Pure rigid-body 3D arena. Observation layers are independent boards; Y alone is gravity.</summary>
public static class SpatialGame
{
    public const int MaximumDeckLength = 128;
    public static SpatialScoringPolicy ScoringPolicy { get; } = new("spatial-prior/v1", 1000, 100, 250, 250, 500_000, 25, 100);
    public static string ScoringPolicyHash { get; } = CanonicalJson.Sha256(ScoringPolicy);
    private static readonly Voxel[] Neighbors = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    public static SpatialState Start(SpatialChallenge challenge)
    {
        var frozen = ValidateChallenge(challenge);
        var state = new SpatialState(frozen, [], 0, 0, 0, [], [], [], false, null, "");
        return state with { StateHash = HashState(state) };
    }
    public static SpatialState Apply(SpatialState state, SpatialMove move, CancellationToken cancellationToken = default) =>
        ApplyVerified(Verify(state, cancellationToken), move, cancellationToken);
    public static SpatialState Replay(SpatialChallenge challenge, IReadOnlyList<SpatialMove> moves, CancellationToken cancellationToken = default)
    {
        if (moves is null || moves.Count > MaximumDeckLength) throw Invalid("moves", "Missing or excessive moves.");
        var state = Start(challenge);
        foreach (var move in moves) { cancellationToken.ThrowIfCancellationRequested(); state = ApplyVerified(state, move, cancellationToken); }
        return state;
    }
    /// <summary>In-bounds commands, including explicit TopOut outcomes. No shape may translate sideways during its drop.</summary>
    public static ImmutableArray<SpatialLegalMove> LegalMoves(SpatialState state, CancellationToken cancellationToken = default) =>
        LegalMovesVerified(Verify(state, cancellationToken), cancellationToken);

    internal static SpatialState Verify(SpatialState state, CancellationToken token)
    {
        if (state is null || state.History.IsDefault || state.History.Length > MaximumDeckLength || state.Placements.IsDefault
            || state.Placements.Length > MaximumDeckLength || state.PaidPatternIds.IsDefault || state.PaidPatternIds.Length > 256
            || state.PaidAffinityIds.IsDefault || state.PaidAffinityIds.Length > 512) throw Invalid("state-bounds", "State arrays are missing or excessive.");
        Hash(state.StateHash, "state hash");
        if (state.History.Any(h => h is null)) throw Invalid("history", "Null history step.");
        var expected = Replay(state.Challenge, state.History.Select(h => h.Move).ToArray(), token);
        if (CanonicalJson.Serialize(expected) != CanonicalJson.Serialize(state)) throw Invalid("state-replay", "State, score or audit differs from authoritative replay.");
        return expected;
    }
    internal static ImmutableArray<SpatialLegalMove> LegalMovesVerified(SpatialState state, CancellationToken token)
    {
        if (state.IsOver) return [];
        var piece = state.Challenge.Deck[state.NextPieceIndex]; var board = state.Challenge.Board!;
        var occupied = Occupied(state.Challenge, state.Placements, piece.LayerId); var result = ImmutableArray.CreateBuilder<SpatialLegalMove>();
        foreach (var orientation in SpatialGameGeometry.Orientations(piece.BaseShape, piece.GeometryVariant))
        for (var x = 0; x <= board.Width - orientation.Width; x++)
        for (var z = 0; z <= board.Depth - orientation.Depth; z++)
        {
            token.ThrowIfCancellationRequested();
            var y = Landing(board, occupied, orientation, x, z);
            result.Add(new(new(orientation.Id, x, z, piece.LayerId, state.StateHash), y, y is null));
        }
        return result.ToImmutable();
    }
    internal static SpatialState ApplyVerified(SpatialState state, SpatialMove move, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (move is null) throw Invalid("move", "Move is required.");
        if (state.IsOver) throw Invalid("game-over", "The match has ended.");
        Hash(move.ExpectedPriorStateHash, "expected state hash");
        if (move.ExpectedPriorStateHash != state.StateHash) throw Invalid("stale-state", "Expected prior state hash differs.");
        var challenge = state.Challenge; var piece = challenge.Deck[state.NextPieceIndex]; var board = challenge.Board!;
        if (move.LayerId != piece.LayerId) throw Invalid("layer", "A move must use its piece's assigned observation layer, not board Y.");
        var orientation = SpatialGameGeometry.Orientations(piece.BaseShape, piece.GeometryVariant).FirstOrDefault(o => o.Id == move.OrientationId)
            ?? throw Invalid("orientation", "Orientation does not belong to the declared geometry.");
        if (move.X < 0 || move.Z < 0 || move.X > board.Width - orientation.Width || move.Z > board.Depth - orientation.Depth)
            throw Invalid("position", "Shape exceeds the horizontal board bounds.");
        var y = Landing(board, Occupied(challenge, state.Placements, piece.LayerId), orientation, move.X, move.Z);
        var topOut = y is null;
        SpatialPlacement? placed = topOut ? null : new(piece.Id, piece.LayerId, orientation.Id, move.X, y!.Value, move.Z);
        var placements = placed is null ? state.Placements : Sort(state.Placements.Add(placed));
        var waves = ImmutableArray.CreateBuilder<SpatialClearWave>();
        var patternMatches = ImmutableArray.CreateBuilder<SpatialPatternMatch>();
        var affinityMatches = ImmutableArray.CreateBuilder<SpatialAffinityMatch>();
        var paidPatterns = state.PaidPatternIds.ToHashSet(StringComparer.Ordinal);
        var paidAffinities = state.PaidAffinityIds.ToHashSet(StringComparer.Ordinal);
        long lexical = 0, relationship = 0, whole = 0, advisory = 0, clearPoints = 0;
        int planes = 0, lines = 0;
        if (!topOut)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ScorePatterns();
                var occupancy = Occupied(challenge, placements, piece.LayerId);
                var planeYs = challenge.ClearMode == "planes"
                    ? occupancy.Keys.GroupBy(c => c.Y).Where(g => g.Count() == board.Width * board.Depth).Select(g => g.Key).Order().ToImmutableArray() : [];
                var fullLines = ImmutableArray.CreateBuilder<SpatialClearLine>();
                if (challenge.ClearMode == "lines")
                {
                    fullLines.AddRange(occupancy.Keys.GroupBy(c => (c.Y, c.Z)).Where(g => g.Count() == board.Width)
                        .Select(g => new SpatialClearLine("x", g.Key.Y, g.Key.Z)).OrderBy(l => l.Y).ThenBy(l => l.OtherCoordinate));
                    fullLines.AddRange(occupancy.Keys.GroupBy(c => (c.Y, c.X)).Where(g => g.Count() == board.Depth)
                        .Select(g => new SpatialClearLine("z", g.Key.Y, g.Key.X)).OrderBy(l => l.Y).ThenBy(l => l.OtherCoordinate));
                }
                if (planeYs.Length == 0 && fullLines.Count == 0) break;
                var removed = occupancy.Where(c => planeYs.Contains(c.Key.Y) || fullLines.Any(l => c.Key.Y == l.Y && (l.Axis == "x" ? c.Key.Z : c.Key.X) == l.OtherCoordinate))
                    .Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                placements = placements.Where(p => !removed.Contains(p.PieceId, StringComparer.Ordinal)).ToImmutableArray();
                var settled = Settle(challenge, placements, piece.LayerId, token); placements = settled.Placements;
                waves.Add(new(planeYs, fullLines.ToImmutable(), removed, settled.Falls));
                planes += planeYs.Length; lines += fullLines.Count;
                clearPoints = checked(clearPoints + planeYs.Length * ScoringPolicy.PlanePoints + fullLines.Count * ScoringPolicy.LinePoints);
            }
        }
        var score = new SpatialScoreBreakdown(planes, lines, clearPoints, lexical, relationship, whole, advisory,
            checked(clearPoints + lexical + relationship + whole + advisory));
        var index = state.NextPieceIndex + 1; var ended = topOut || index == challenge.Deck.Length;
        var step = new SpatialStep(state.History.Length, piece.Id, move, placed, topOut, waves.ToImmutable(), patternMatches.ToImmutable(),
            affinityMatches.ToImmutable(), score, state.StateHash, "");
        var next = new SpatialState(challenge, placements, index, checked(state.Score + score.Total), checked(state.LearnedAdvisoryScore + advisory),
            paidPatterns.Order(StringComparer.Ordinal).ToImmutableArray(), paidAffinities.Order(StringComparer.Ordinal).ToImmutableArray(),
            state.History.Add(step), ended, topOut ? "top-out" : ended ? "deck-exhausted" : null, "");
        var hash = HashState(next);
        return next with { StateHash = hash, History = next.History.SetItem(next.History.Length - 1, step with { StateAfterHash = hash }) };

        void ScorePatterns()
        {
            var occupancy = Occupied(challenge, placements, piece.LayerId);
            var present = placements.Where(p => p.LayerId == piece.LayerId).Select(p => p.PieceId).ToHashSet(StringComparer.Ordinal);
            var contact = present.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var cell in occupancy)
            foreach (var delta in Neighbors)
                if (occupancy.TryGetValue(new(cell.Key.X + delta.X, cell.Key.Y + delta.Y, cell.Key.Z + delta.Z), out var other) && other != cell.Value)
                    contact[cell.Value].Add(other);
            foreach (var pattern in challenge.Patterns.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (pattern.LayerId != piece.LayerId || paidPatterns.Contains(pattern.Id) || pattern.MemberPieceIds.Any(id => !present.Contains(id))) continue;
                // Versioned ordered patterns add an explicit X-axis condition. Legacy kinds retain connected-set behavior.
                if (pattern.Kind is "lexical-ordered-x/v1" or "path-ordered-x/v1")
                {
                    var ordered = pattern.MemberPieceIds.Select(id => placements.First(p => p.PieceId == id)).ToArray();
                    if (ordered.Zip(ordered.Skip(1), (left, right) => left.X < right.X).Any(ok => !ok)) continue;
                }
                var members = pattern.MemberPieceIds.ToHashSet(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal) { pattern.MemberPieceIds[0] }; var queue = new Queue<string>(seen);
                while (queue.TryDequeue(out var id)) foreach (var adjacent in contact[id]) if (members.Contains(adjacent) && seen.Add(adjacent)) queue.Enqueue(adjacent);
                if (seen.Count != members.Count) continue;
                var points = members.Count == 2 ? ScoringPolicy.PairPatternPoints : checked(members.Count * ScoringPolicy.WholePatternPointsPerMember);
                var basis = pattern.Kind == "code" ? "recorded-code-relation" : pattern.Kind is "lexical-ordered-x/v1" or "path-ordered-x/v1" ? "observed-source-order; connected whole pieces with increasing x anchors; not code reachability" : "observed-lexical-grouping; not code reachability";
                if (pattern.Kind != "code") lexical = checked(lexical + points);
                else if (members.Count == 2) relationship = checked(relationship + points);
                else whole = checked(whole + points);
                paidPatterns.Add(pattern.Id);
                patternMatches.Add(new(pattern.Id, pattern.LayerId, pattern.Kind, basis, pattern.MemberPieceIds, pattern.EvidenceIds, pattern.SourceEdgeIds, points));
            }
            foreach (var affinity in challenge.Affinities.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (affinity.LayerId != piece.LayerId || paidAffinities.Contains(affinity.Id)
                    || !contact.TryGetValue(affinity.FirstPieceId, out var neighbors) || !neighbors.Contains(affinity.SecondPieceId)) continue;
                var proposed = affinity.ProbabilityPpm <= ScoringPolicy.AffinityNeutralPpm ? 0
                    : (long)(affinity.ProbabilityPpm - ScoringPolicy.AffinityNeutralPpm) * ScoringPolicy.MaximumAffinityPairPoints / (1_000_000 - ScoringPolicy.AffinityNeutralPpm);
                var points = Math.Min(proposed, ScoringPolicy.MaximumAffinityMatchPoints - state.LearnedAdvisoryScore - advisory);
                advisory += points; paidAffinities.Add(affinity.Id);
                affinityMatches.Add(new(affinity.Id, affinity.LayerId, affinity.FirstPieceId, affinity.SecondPieceId,
                    affinity.ProbabilityPpm, affinity.ModelHash, affinity.FeatureHash, points));
            }
        }
    }

    private static int? Landing(SpatialBoardSpec board, Dictionary<Voxel, string> occupied, SpatialOrientation orientation, int x, int z)
    {
        var y = board.Height - orientation.Height;
        bool Fits(int at) => at >= 0 && orientation.Cells.All(c => !occupied.ContainsKey(new(x + c.X, at + c.Y, z + c.Z)));
        if (!Fits(y)) return null;
        while (Fits(y - 1)) y--;
        return y;
    }
    internal static ImmutableArray<Voxel> Cells(SpatialChallenge challenge, SpatialPlacement placement)
    {
        var piece = challenge.Deck.First(p => p.Id == placement.PieceId);
        var pose = SpatialGameGeometry.Orientations(piece.BaseShape, piece.GeometryVariant).First(o => o.Id == placement.OrientationId);
        return pose.Cells.Select(c => new Voxel(c.X + placement.X, c.Y + placement.Y, c.Z + placement.Z)).ToImmutableArray();
    }
    private static Dictionary<Voxel, string> Occupied(SpatialChallenge challenge, ImmutableArray<SpatialPlacement> placements, string layer)
    {
        var result = new Dictionary<Voxel, string>();
        foreach (var placement in placements.Where(p => p.LayerId == layer)) foreach (var cell in Cells(challenge, placement)) result.Add(cell, placement.PieceId);
        return result;
    }
    private static (ImmutableArray<SpatialPlacement> Placements, ImmutableArray<SpatialFall> Falls) Settle(SpatialChallenge challenge,
        ImmutableArray<SpatialPlacement> placements, string layer, CancellationToken token)
    {
        var initial = placements.ToDictionary(p => p.PieceId, p => p.Y, StringComparer.Ordinal);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var occupied = Occupied(challenge, placements, layer);
            var moving = placements.Where(p => p.LayerId == layer).Select(p => p.PieceId).ToHashSet(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var cell in occupied)
                {
                    if (!moving.Contains(cell.Value)) continue;
                    if (cell.Key.Y == 0 || occupied.TryGetValue(cell.Key with { Y = cell.Key.Y - 1 }, out var below) && below != cell.Value && !moving.Contains(below))
                    { moving.Remove(cell.Value); changed = true; }
                }
            } while (changed);
            if (moving.Count == 0) break;
            placements = placements.Select(p => moving.Contains(p.PieceId) ? p with { Y = p.Y - 1 } : p).ToImmutableArray();
        }
        return (Sort(placements), placements.Where(p => p.Y != initial[p.PieceId]).OrderBy(p => p.PieceId, StringComparer.Ordinal)
            .Select(p => new SpatialFall(p.PieceId, initial[p.PieceId], p.Y)).ToImmutableArray());
    }
    private static ImmutableArray<SpatialPlacement> Sort(IEnumerable<SpatialPlacement> placements) => placements.OrderBy(p => p.LayerId, StringComparer.Ordinal)
        .ThenBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X).ThenBy(p => p.PieceId, StringComparer.Ordinal).ToImmutableArray();
    private static string HashState(SpatialState state) => CanonicalJson.Sha256(new
    {
        state.Challenge.ChallengeHash, ScoringPolicyHash, state.Placements, state.NextPieceIndex, state.Score,
        state.LearnedAdvisoryScore, state.PaidPatternIds, state.PaidAffinityIds, Moves = state.History.Select(h => h.Move), state.IsOver, state.EndReason
    });

    private static SpatialChallenge ValidateChallenge(SpatialChallenge challenge)
    {
        if (challenge is null) throw Invalid("challenge", "Challenge is required.");
        Text(challenge.Id, "challenge ID"); Hash(challenge.DatasetHash, "dataset hash"); Hash(challenge.ModelArtifactHash, "model artifact hash");
        if (challenge.Schema != "filemapper.spatial-game.challenge/1" || challenge.RulesVersion != "spatial-tetrid/1") throw Invalid("rules", "Unsupported spatial schema or rules.");
        if (challenge.ClearMode is not ("planes" or "lines")) throw Invalid("clear-mode", "Choose planes or lines for this frozen match.");
        var board = challenge.Board ?? new();
        if (board.Width is < 2 or > 12 || board.Depth is < 2 or > 12 || board.Height is < 4 or > 24) throw Invalid("board", "Board dimensions exceed the supported bounds.");
        if (challenge.Layers.IsDefault || challenge.Layers.Length is < 1 or > 4 || challenge.Deck.IsDefault || challenge.Deck.Length is < 1 or > MaximumDeckLength
            || challenge.Patterns.IsDefault || challenge.Patterns.Length > 256 || challenge.Affinities.IsDefault || challenge.Affinities.Length > 512)
            throw Invalid("challenge-bounds", "Missing or excessive challenge arrays.");
        var layers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in challenge.Layers)
        {
            if (layer is null) throw Invalid("layer", "Null layer.");
            Text(layer.Id, "layer ID"); Text(layer.SnapshotId, "snapshot ID"); Text(layer.Completeness, "completeness");
            if (!layers.Add(layer.Id) || layer.ObservedFrom.HasValue && layer.ObservedTo.HasValue && layer.ObservedFrom > layer.ObservedTo) throw Invalid("layer", "Duplicate layer or reversed observation window.");
        }
        var pieces = new Dictionary<string, SpatialPiece>(StringComparer.Ordinal); var deck = ImmutableArray.CreateBuilder<SpatialPiece>();
        foreach (var piece in challenge.Deck)
        {
            if (piece is null) throw Invalid("piece", "Null piece.");
            foreach (var text in new[] { piece.Id, piece.SomId, piece.ObservationId, piece.SourceNodeId, piece.LayerId, piece.Label }) Text(text, "piece field");
            if (!layers.Contains(piece.LayerId) || !pieces.TryAdd(piece.Id, piece)) throw Invalid("piece", "Unknown layer or duplicate piece ID.");
            if (piece.UnitKind is not ("object" or "word") || piece.UnitKind == "word" && string.IsNullOrWhiteSpace(piece.SourceSomId)) throw Invalid("unit", "Word pieces require the parent source SOM ID.");
            if (piece.SourceSomId is not null) Text(piece.SourceSomId, "source SOM ID");
            Strings(piece.Tokens, 128, "tokens", allowEmpty: true); Strings(piece.EvidenceIds, 64, "piece evidence", allowEmpty: false);
            var geometry = SpatialGameGeometry.Get(piece.BaseShape, piece.GeometryVariant);
            if (piece.GeometryHash is not null && piece.GeometryHash != geometry.GeometryHash) throw Invalid("geometry-hash", "Geometry hash differs from the declared variant.");
            if (!SpatialGameGeometry.Orientations(piece.BaseShape, piece.GeometryVariant).Any(p => p.Width <= board.Width && p.Height <= board.Height && p.Depth <= board.Depth)) throw Invalid("geometry-fit", "Piece has no pose that fits this board.");
            deck.Add(piece with { GeometryHash = geometry.GeometryHash });
        }
        var ids = new HashSet<string>(StringComparer.Ordinal); var patternKeys = new HashSet<(string, string, string)>();
        foreach (var pattern in challenge.Patterns)
        {
            if (pattern is null) throw Invalid("pattern", "Null pattern.");
            Text(pattern.Id, "pattern ID");
            if (!ids.Add(pattern.Id) || pattern.Kind is not ("filename" or "path" or "lexical" or "code" or "lexical-ordered-x/v1" or "path-ordered-x/v1")) throw Invalid("pattern", "Duplicate pattern or unknown kind.");
            Strings(pattern.MemberPieceIds, 8, "pattern members", allowEmpty: false);
            if (pattern.MemberPieceIds.Length < 2 || pattern.MemberPieceIds.Distinct(StringComparer.Ordinal).Count() != pattern.MemberPieceIds.Length) throw Invalid("pattern", "Patterns require 2–8 distinct whole piece occurrences.");
            if (!patternKeys.Add((pattern.LayerId, pattern.Kind, CanonicalJson.Serialize(pattern.MemberPieceIds.Order(StringComparer.Ordinal)))))
                throw Invalid("pattern-duplicate", "Repeated layer/kind/member sets must merge their evidence into one frozen pattern.");
            Strings(pattern.EvidenceIds, 64, "pattern evidence", allowEmpty: false); Strings(pattern.SourceEdgeIds, 64, "pattern edges", allowEmpty: pattern.Kind != "code");
            foreach (var id in pattern.MemberPieceIds) if (!pieces.TryGetValue(id, out var member) || member.LayerId != pattern.LayerId) throw Invalid("pattern-layer", "Pattern member is absent or belongs to another observation layer.");
        }
        ids.Clear(); var pairs = new HashSet<(string, string, string)>();
        foreach (var affinity in challenge.Affinities)
        {
            if (affinity is null) throw Invalid("affinity", "Null affinity.");
            Text(affinity.Id, "affinity ID"); Hash(affinity.ModelHash, "affinity model hash"); Hash(affinity.FeatureHash, "feature hash");
            if (!ids.Add(affinity.Id) || affinity.ProbabilityPpm is < 0 or > 1_000_000 || affinity.ModelHash != challenge.ModelArtifactHash)
                throw Invalid("affinity", "Duplicate affinity, invalid probability or mismatched frozen model.");
            if (affinity.FirstPieceId == affinity.SecondPieceId || !pieces.TryGetValue(affinity.FirstPieceId, out var first) || !pieces.TryGetValue(affinity.SecondPieceId, out var second)
                || first.LayerId != affinity.LayerId || second.LayerId != affinity.LayerId) throw Invalid("affinity-layer", "Affinity endpoints must be distinct pieces on the assigned layer.");
            var pair = string.CompareOrdinal(first.Id, second.Id) < 0 ? (affinity.LayerId, first.Id, second.Id) : (affinity.LayerId, second.Id, first.Id);
            if (!pairs.Add(pair)) throw Invalid("affinity-pair", "A learned pair may appear only once per layer.");
        }
        if (challenge.ScoringPolicyHash is not null && challenge.ScoringPolicyHash != ScoringPolicyHash) throw Invalid("scoring-policy", "Scoring policy hash differs.");
        var frozen = challenge with { Board = board, Deck = deck.ToImmutable(), ChallengeHash = null, ScoringPolicyHash = ScoringPolicyHash };
        var hash = CanonicalJson.Sha256(frozen);
        if (challenge.ChallengeHash is not null && challenge.ChallengeHash != hash) throw Invalid("challenge-hash", "Challenge hash differs from its frozen content.");
        return frozen with { ChallengeHash = hash };
    }
    private static void Strings(ImmutableArray<string> values, int maximum, string label, bool allowEmpty)
    {
        if (values.IsDefault || values.Length > maximum || !allowEmpty && values.Length == 0) throw Invalid("array", "Missing or excessive " + label);
        foreach (var value in values) Text(value, label);
    }
    private static void Text(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw Invalid("text", "Invalid " + label);
    }
    private static void Hash(string value, string label)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid("hash", "Invalid " + label);
    }
    private static SpatialGameException Invalid(string code, string message) => new(code, message);
}
