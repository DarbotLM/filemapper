using System.Collections.Immutable;
using System.Text;

namespace FileMapper.Semantics;

public sealed record GamePiece(string Id, string SomId, string Label, ImmutableArray<string> Tokens, string Shape);
public sealed record GameAssociation(string Id, string FromSomId, string ToSomId, string Kind, ImmutableArray<string> EvidenceIds);
public sealed record GamePattern(string Id, ImmutableArray<string> Tokens, string Kind, ImmutableArray<string> EvidenceIds);

/// <summary>All evidence is supplied by the dataset adapter. A hash commits to content; it does not authenticate evidence.</summary>
public sealed record GameChallenge(string Id, string DatasetHash, string DatasetSchema, string DatasetVersion,
    ImmutableArray<GamePiece> Deck, ImmutableArray<GameAssociation> Associations, ImmutableArray<GamePattern> Patterns,
    string? ChallengeHash = null, int Width = 12, int Height = 20,
    string Schema = "filemapper.semantic-game.challenge/1", string RulesVersion = "semantic-tetris/1");

public sealed record GameMove(int Rotation, int Column, string ExpectedPriorStateHash);
public sealed record GameCell(int Column, int Row, string PieceId);
public sealed record GameWord(string PieceId, string SomId, string Token, int TokenIndex);
public sealed record GameRelationshipMatch(string AssociationId, string FromPieceId, string ToPieceId,
    string Kind, ImmutableArray<string> EvidenceIds, long Points);
public sealed record ClearedGameRow(int Row, ImmutableArray<string> PieceIds, ImmutableArray<GameWord> Words,
    ImmutableArray<string> MatchingPatternIds, ImmutableArray<GameRelationshipMatch> Relationships);
public sealed record GameScoreBreakdown(int Lines, long LinePoints, long GroundedPatternPoints,
    long InferredPatternPoints, long GroundedRelationshipPoints, long InferredRelationshipPoints, long Total);
public sealed record LearnedGameAssociation(string FirstSomId, string SecondSomId, int CoClearCount,
    ImmutableArray<string> GroundingAssociationIds, string Interpretation = "empirical-grounded-co-clear-count");
public sealed record GameStep(int Index, string PieceId, GameMove Move, int? LandingRow, bool TopOut,
    ImmutableArray<ClearedGameRow> ClearedRows, GameScoreBreakdown ScoreBreakdown,
    ImmutableArray<LearnedGameAssociation> LearningUpdates, string StateBeforeHash, string StateAfterHash);
public sealed record GameState(GameChallenge Challenge, ImmutableArray<GameCell> Board, int NextPieceIndex,
    long Score, int ClearedLines, bool IsOver, string? EndReason, ImmutableArray<GameStep> History,
    ImmutableArray<LearnedGameAssociation> Learning, string StateHash);
public sealed record GameCompetitionRound(string Id, string Label, GameChallenge Challenge,
    string SharedInputHash, string ScoringPriorHash);
public sealed record GameScoringPrior(string Version, int LineUnit, int GroundedPatternPerToken,
    int InferredPatternPerToken, int SameSourceRelationship, int PathRelationship,
    int GraphRelationship, int InferredLexicalRelationship, string LearningPolicy);

public sealed class GameValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>Pure, replayable hard-drop game. Y=0 is the bottom. No client score or learned weight is accepted.</summary>
public static class SemanticGame
{
    public const int BoardWidth = 12;
    public const int BoardHeight = 20;
    public const int MaximumDeckLength = 512;
    public static GameScoringPrior ScoringPrior { get; } = new("fixed-evidence-prior/1", 100, 250, 30,
        60, 40, 80, 10, "reset-per-match; empirical-co-clear-counts-only; never-used-for-scoring");
    public static string ScoringPriorHash { get; } = CanonicalJson.Sha256(ScoringPrior);

    public static GameState Start(GameChallenge challenge)
    {
        var checkedChallenge = ValidateChallenge(challenge);
        return InitialState(checkedChallenge);
    }

    public static GameState Apply(GameState state, GameMove move)
    {
        // Reconstruct, rather than trusting a caller-supplied board, score, learning table or even a recomputed hash.
        var verified = ValidateState(state);
        return ApplyCore(verified, move);
    }

    public static GameState Replay(GameChallenge challenge, IReadOnlyList<GameMove> moves)
    {
        if (moves is null) throw Invalid("moves-null", "Moves must be an array.");
        var state = Start(challenge);
        if (moves.Count > state.Challenge.Deck.Length) throw Invalid("move-limit", "More moves than deck pieces.");
        foreach (var move in moves) state = ApplyCore(state, move);
        return state;
    }

    /// <summary>Three fixed presentations. Both arms receive exactly the same challenge for a given round.</summary>
    public static ImmutableArray<GameCompetitionRound> CreateCompetitionRounds(GameChallenge challenge)
    {
        var original = ValidateChallenge(challenge);
        var orders = new[]
        {
            original.Deck,
            original.Deck.OrderBy(p => CanonicalJson.Sha256(new { Round = 2, original.DatasetHash, p.Id }), StringComparer.Ordinal)
                .ThenBy(p => p.Id, StringComparer.Ordinal).ToImmutableArray(),
            original.Deck.Reverse().ToImmutableArray()
        };
        var labels = new[] { "Observed deck order", "Fixed hash permutation", "Reverse deck order" };
        var result = ImmutableArray.CreateBuilder<GameCompetitionRound>(3);
        for (var index = 0; index < 3; index++)
        {
            var round = ValidateChallenge(original with
            {
                Id = original.Id + "/round-" + (index + 1), Deck = orders[index], ChallengeHash = null
            });
            result.Add(new("round-" + (index + 1), labels[index], round, round.ChallengeHash!, ScoringPriorHash));
        }
        return result.ToImmutable();
    }

    private static GameState InitialState(GameChallenge challenge)
    {
        var state = new GameState(challenge, [], 0, 0, 0, false, null, [], [], "");
        return state with { StateHash = HashState(state) };
    }

    private static GameState ValidateState(GameState state)
    {
        if (state is null) throw Invalid("state-null", "State is required.");
        if (state.History.IsDefault || state.History.Length > MaximumDeckLength
            || state.Board.IsDefault || state.Board.Length > BoardWidth * BoardHeight
            || state.Learning.IsDefault || state.Learning.Length > 2048)
            throw Invalid("state-bounds", "State arrays are absent or exceed their bounds.");
        RequireHash(state.StateHash, "state hash");
        var expected = Start(state.Challenge);
        foreach (var step in state.History)
        {
            if (step is null) throw Invalid("history-null", "History contains a null step.");
            expected = ApplyCore(expected, step.Move);
        }
        if (!string.Equals(CanonicalJson.Serialize(expected), CanonicalJson.Serialize(state), StringComparison.Ordinal))
            throw Invalid("state-replay-mismatch", "State or audit history differs from authoritative replay.");
        return expected;
    }

    private static GameState ApplyCore(GameState state, GameMove move)
    {
        if (move is null) throw Invalid("move-null", "Move is required.");
        RequireHash(move.ExpectedPriorStateHash, "expected prior state hash");
        if (!string.Equals(state.StateHash, move.ExpectedPriorStateHash, StringComparison.Ordinal))
            throw Invalid("stale-state", "Expected prior state hash does not match.");
        if (state.IsOver) throw Invalid("game-over", "The match has ended.");
        if (move.Rotation is < 0 or > 3) throw Invalid("rotation", "Rotation must be 0, 1, 2 or 3.");
        var piece = state.Challenge.Deck[state.NextPieceIndex];
        var cells = ShapeCatalog.Rotate(piece.Shape, move.Rotation);
        var width = cells.Max(c => c.X) + 1;
        var height = cells.Max(c => c.Y) + 1;
        if (move.Column < 0 || move.Column > BoardWidth - width)
            throw Invalid("column", "The rotated piece must fit within the board width.");
        var occupied = state.Board.Select(c => (c.Column, c.Row)).ToHashSet();
        var landing = BoardHeight - height;
        bool Fits(int row) => cells.All(c => row + c.Y >= 0 && !occupied.Contains((move.Column + c.X, row + c.Y)));
        var topOut = !Fits(landing);
        var board = state.Board;
        var cleared = ImmutableArray<ClearedGameRow>.Empty;
        var breakdown = new GameScoreBreakdown(0, 0, 0, 0, 0, 0, 0);
        var learning = state.Learning;
        var learningUpdates = ImmutableArray<LearnedGameAssociation>.Empty;
        if (!topOut)
        {
            while (Fits(landing - 1)) landing--;
            board = board.AddRange(cells.Select(c => new GameCell(move.Column + c.X, landing + c.Y, piece.Id)));
            var rows = board.GroupBy(c => c.Row).Where(g => g.Count() == BoardWidth)
                .Select(g => g.Key).Order().ToImmutableArray();
            (cleared, breakdown, learning, learningUpdates) = ScoreRows(state, board, rows);
            board = board.Where(c => !rows.Contains(c.Row))
                .Select(c => c with { Row = c.Row - rows.Count(y => y < c.Row) })
                .OrderBy(c => c.Row).ThenBy(c => c.Column).ToImmutableArray();
        }
        var nextIndex = state.NextPieceIndex + 1;
        var ended = topOut || nextIndex == state.Challenge.Deck.Length;
        var step = new GameStep(state.History.Length, piece.Id, move, topOut ? null : landing, topOut,
            cleared, breakdown, learningUpdates, state.StateHash, "");
        var next = new GameState(state.Challenge, board, nextIndex, checked(state.Score + breakdown.Total),
            state.ClearedLines + breakdown.Lines, ended, topOut ? "top-out" : ended ? "deck-exhausted" : null,
            state.History.Add(step), learning, "");
        var afterHash = HashState(next);
        return next with { StateHash = afterHash, History = next.History.SetItem(next.History.Length - 1, step with { StateAfterHash = afterHash }) };
    }

    private static (ImmutableArray<ClearedGameRow> Rows, GameScoreBreakdown Score,
        ImmutableArray<LearnedGameAssociation> Learning, ImmutableArray<LearnedGameAssociation> Updates)
        ScoreRows(GameState state, ImmutableArray<GameCell> board, ImmutableArray<int> rowNumbers)
    {
        var pieces = state.Challenge.Deck.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<ClearedGameRow>();
        var paidPatterns = new HashSet<string>(StringComparer.Ordinal);
        var paidRelationships = new HashSet<string>(StringComparer.Ordinal);
        var learned = state.Learning.ToDictionary(a => (a.FirstSomId, a.SecondSomId));
        var updatedPairs = new HashSet<(string First, string Second)>();
        long groundedPattern = 0, inferredPattern = 0, groundedRelationship = 0, inferredRelationship = 0;
        foreach (var rowNumber in rowNumbers)
        {
            // First appearance from left to right. A wide, disconnected or repeated piece contributes words only once.
            var identities = board.Where(c => c.Row == rowNumber).OrderBy(c => c.Column).Select(c => c.PieceId)
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            var words = identities.SelectMany(id => pieces[id].Tokens.Select((token, index) =>
                new GameWord(id, pieces[id].SomId, token, index))).ToImmutableArray();
            var matchedPatterns = ImmutableArray.CreateBuilder<string>();
            foreach (var pattern in state.Challenge.Patterns.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (!Matches(words, pattern.Tokens)) continue;
                matchedPatterns.Add(pattern.Id);
                if (!paidPatterns.Add(pattern.Id)) continue;
                if (pattern.Kind == "lexical") inferredPattern += ScoringPrior.InferredPatternPerToken * pattern.Tokens.Length;
                else groundedPattern += ScoringPrior.GroundedPatternPerToken * pattern.Tokens.Length;
            }
            var matches = ImmutableArray.CreateBuilder<GameRelationshipMatch>();
            for (var i = 0; i < identities.Length - 1; i++)
            {
                var left = pieces[identities[i]];
                var right = pieces[identities[i + 1]];
                if (left.SomId == right.SomId) continue;
                foreach (var association in state.Challenge.Associations.OrderBy(a => a.Id, StringComparer.Ordinal))
                {
                    if (!Connects(association, left.SomId, right.SomId)) continue;
                    var points = paidRelationships.Add(association.Id) ? RelationshipPoints(association.Kind) : 0;
                    if (association.Kind == "lexical") inferredRelationship += points;
                    else groundedRelationship += points;
                    matches.Add(new(association.Id, left.Id, right.Id, association.Kind, association.EvidenceIds, points));
                }
            }
            // All grounded objects co-cleared on this row can update empirical counts, regardless of adjacency.
            // A pair updates at most once per move, even if the same pieces span two or more cleared rows.
            var somIds = identities.Select(id => pieces[id].SomId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            for (var first = 0; first < somIds.Length; first++)
            for (var second = first + 1; second < somIds.Length; second++)
            {
                var pair = (First: somIds[first], Second: somIds[second]);
                var grounding = state.Challenge.Associations.Where(a => a.Kind != "lexical" && Connects(a, pair.First, pair.Second))
                    .Select(a => a.Id).Order(StringComparer.Ordinal).ToImmutableArray();
                if (grounding.IsEmpty || !updatedPairs.Add(pair)) continue;
                var count = learned.TryGetValue(pair, out var previous) ? checked(previous.CoClearCount + 1) : 1;
                learned[pair] = new(pair.First, pair.Second, count, grounding);
            }
            result.Add(new(rowNumber, identities, words, matchedPatterns.ToImmutable(), matches.ToImmutable()));
        }
        var linePoints = (long)ScoringPrior.LineUnit * rowNumbers.Length * rowNumbers.Length;
        var total = checked(linePoints + groundedPattern + inferredPattern + groundedRelationship + inferredRelationship);
        var allLearning = learned.Values.OrderBy(a => a.FirstSomId, StringComparer.Ordinal)
            .ThenBy(a => a.SecondSomId, StringComparer.Ordinal).ToImmutableArray();
        var updates = allLearning.Where(a => updatedPairs.Contains((a.FirstSomId, a.SecondSomId))).ToImmutableArray();
        return (result.ToImmutable(), new(rowNumbers.Length, linePoints, groundedPattern, inferredPattern,
            groundedRelationship, inferredRelationship, total), allLearning, updates);
    }

    private static bool Matches(ImmutableArray<GameWord> words, ImmutableArray<string> pattern)
    {
        for (var start = 0; start <= words.Length - pattern.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < pattern.Length; offset++)
                if (!string.Equals(Normalize(words[start + offset].Token), Normalize(pattern[offset]), StringComparison.Ordinal))
                { matched = false; break; }
            if (matched && words.Skip(start).Take(pattern.Length).Select(w => w.SomId).Distinct(StringComparer.Ordinal).Take(2).Count() == 2)
                return true;
        }
        return false;
    }

    private static bool Connects(GameAssociation association, string first, string second) =>
        association.FromSomId == first && association.ToSomId == second
        || association.FromSomId == second && association.ToSomId == first;

    private static int RelationshipPoints(string kind) => kind switch
    {
        "same-source" => ScoringPrior.SameSourceRelationship,
        "path" => ScoringPrior.PathRelationship,
        "graph" => ScoringPrior.GraphRelationship,
        "lexical" => ScoringPrior.InferredLexicalRelationship,
        _ => throw Invalid("association-kind", "Unknown association kind.")
    };

    private static string HashState(GameState state) => CanonicalJson.Sha256(new
    {
        Schema = "filemapper.semantic-game.state/1",
        state.Challenge.ChallengeHash,
        ScoringPriorHash,
        state.Board,
        state.NextPieceIndex,
        state.Score,
        state.ClearedLines,
        state.IsOver,
        state.EndReason,
        state.Learning,
        // After-hashes are excluded to avoid self-reference. Apply validates the full audit through replay.
        Moves = state.History.Select(s => s.Move).ToImmutableArray()
    });

    private static GameChallenge ValidateChallenge(GameChallenge challenge)
    {
        if (challenge is null) throw Invalid("challenge-null", "Challenge is required.");
        RequireText(challenge.Id, "challenge id", 256);
        RequireHash(challenge.DatasetHash, "dataset hash");
        RequireText(challenge.DatasetSchema, "dataset schema", 128);
        RequireText(challenge.DatasetVersion, "dataset version", 128);
        if (challenge.Schema != "filemapper.semantic-game.challenge/1" || challenge.RulesVersion != "semantic-tetris/1")
            throw Invalid("version", "Unsupported challenge schema or game rules version.");
        if (challenge.Width != BoardWidth || challenge.Height != BoardHeight)
            throw Invalid("dimensions", "The supported board is exactly 12 columns by 20 rows.");
        if (challenge.Deck.IsDefault || challenge.Deck.Length is < 1 or > MaximumDeckLength)
            throw Invalid("deck-size", "Deck must contain 1 to 512 pieces.");
        if (challenge.Associations.IsDefault || challenge.Associations.Length > 2048
            || challenge.Patterns.IsDefault || challenge.Patterns.Length > 512)
            throw Invalid("metadata-size", "Association or pattern arrays are absent or exceed their bounds.");
        var pieceIds = new HashSet<string>(StringComparer.Ordinal);
        var somIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in challenge.Deck)
        {
            if (piece is null) throw Invalid("piece-null", "Deck contains a null piece.");
            RequireText(piece.Id, "piece id", 256);
            RequireText(piece.SomId, "SOM id", 256);
            RequireText(piece.Label, "piece label", 1024);
            if (!pieceIds.Add(piece.Id)) throw Invalid("duplicate-piece", "Deck piece ids must be unique.");
            somIds.Add(piece.SomId);
            if (piece.Shape is null || !ShapeCatalog.Shapes.ContainsKey(piece.Shape))
                throw Invalid("shape", "Piece shape is not in the canonical shape catalog.");
            ValidateTokens(piece.Tokens, "piece", 1);
        }
        var associationIds = new HashSet<string>(StringComparer.Ordinal);
        var associationPairs = new HashSet<(string, string, string)>();
        foreach (var association in challenge.Associations)
        {
            if (association is null) throw Invalid("association-null", "Association array contains null.");
            RequireText(association.Id, "association id", 256);
            if (!associationIds.Add(association.Id)) throw Invalid("duplicate-association", "Association ids must be unique.");
            if (association.FromSomId is null || association.ToSomId is null
                || !somIds.Contains(association.FromSomId) || !somIds.Contains(association.ToSomId)
                || association.FromSomId == association.ToSomId)
                throw Invalid("association-endpoint", "Associations must connect two distinct SOM ids present in the deck.");
            if (association.Kind is not ("same-source" or "path" or "graph" or "lexical"))
                throw Invalid("association-kind", "Association kind must be same-source, path, graph or lexical.");
            var endpoints = new[] { association.FromSomId, association.ToSomId }.Order(StringComparer.Ordinal).ToArray();
            if (!associationPairs.Add((endpoints[0], endpoints[1], association.Kind)))
                throw Invalid("duplicate-association-pair", "One association per unordered SOM pair and kind is allowed.");
            ValidateEvidence(association.EvidenceIds, association.Kind != "lexical");
        }
        var patternIds = new HashSet<string>(StringComparer.Ordinal);
        var patternValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in challenge.Patterns)
        {
            if (pattern is null) throw Invalid("pattern-null", "Pattern array contains null.");
            RequireText(pattern.Id, "pattern id", 256);
            if (!patternIds.Add(pattern.Id)) throw Invalid("duplicate-pattern", "Pattern ids must be unique.");
            ValidateTokens(pattern.Tokens, "pattern", 2);
            if (pattern.Kind is not ("filename" or "path" or "lexical"))
                throw Invalid("pattern-kind", "Pattern kind must be filename, path or lexical.");
            if (!patternValues.Add(CanonicalJson.Serialize(pattern.Tokens.Select(Normalize).ToArray())))
                throw Invalid("duplicate-pattern-value", "A normalized token sequence may appear only once, preventing duplicate rewards.");
            ValidateEvidence(pattern.EvidenceIds, pattern.Kind != "lexical");
        }
        // Commit to the fixed prior as well as the challenge; changes to scoring change the challenge identity.
        var hash = CanonicalJson.Sha256(new { Challenge = challenge with { ChallengeHash = null }, ScoringPriorHash });
        if (challenge.ChallengeHash is not null)
        {
            RequireHash(challenge.ChallengeHash, "challenge hash");
            if (!string.Equals(challenge.ChallengeHash, hash, StringComparison.Ordinal))
                throw Invalid("challenge-hash", "Supplied challenge hash does not match its content and frozen scoring prior.");
        }
        return challenge with { ChallengeHash = hash };
    }

    private static void ValidateTokens(ImmutableArray<string> tokens, string kind, int minimum)
    {
        if (tokens.IsDefault || tokens.Length < minimum || tokens.Length > 32)
            throw Invalid("tokens", $"{kind} tokens must contain {minimum} to 32 entries.");
        foreach (var token in tokens) RequireText(token, kind + " token", 128);
    }

    private static void ValidateEvidence(ImmutableArray<string> evidence, bool grounded)
    {
        if (evidence.IsDefault || evidence.Length > 32 || (grounded ? evidence.Length == 0 : evidence.Length != 0))
            throw Invalid("evidence", "Grounded records require 1 to 32 evidence ids; lexical inference must have no evidence ids.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in evidence)
        {
            RequireText(id, "evidence id", 256);
            if (!ids.Add(id)) throw Invalid("duplicate-evidence", "Evidence ids within a record must be unique.");
        }
    }

    private static void RequireText(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw Invalid("text", $"{field} must be nonempty, at most {maximum} characters, and contain no control characters.");
    }

    private static void RequireHash(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("hash-format", $"{field} must be a lowercase 64-character SHA-256 digest.");
    }

    private static string Normalize(string token) => token.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    private static GameValidationException Invalid(string code, string message) => new(code, message);
}
