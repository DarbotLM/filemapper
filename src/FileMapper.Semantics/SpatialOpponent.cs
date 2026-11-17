using System.Collections.Immutable;

namespace FileMapper.Semantics;

public sealed record SpatialSearchBudget(int MaximumEvaluations = 2048, int BeamWidth = 8, int LookaheadPlies = 2);
public sealed record SpatialEvaluation(bool TopOut, long ScoreGain, long GroundedCodePoints, int CoveredEmptyCells,
    int MaximumOccupiedHeight, int SumColumnHeights);
public sealed record SpatialDecision(SpatialMove? Move, SpatialEvaluation? Evaluation, bool Completed, bool Truncated,
    int ActionsEvaluated, int NodesExpanded, int LegalActionsEnumerated, string SearchPolicyHash, string StateHash, string? Reason);
public sealed record SpatialRun(string Competitor, string ChallengeHash, string SearchPolicyHash,
    ImmutableArray<SpatialMove> Moves, ImmutableArray<SpatialDecision> Decisions, SpatialState FinalState,
    bool Completed, long ActionsEvaluated, string? Reason);

/// <summary>A native baseline, never an attributed Astra/SOL run. The operation budget, ordering and ties are fixed.</summary>
public static class SpatialOpponent
{
    public const string PolicyVersion = "spatial-beam/v1";
    public static string PolicyHash(SpatialSearchBudget budget)
    {
        Validate(budget);
        return CanonicalJson.Sha256(new { Version = PolicyVersion, Budget = budget,
            Ordering = "orientation-id ordinal, x ascending, z ascending", Evaluation = "avoid-topout, score-gain, grounded-code-points, fewer-covered-empty-cells, lower-max-height, lower-column-height-sum",
            TieBreak = "first stable root action", Lookahead = "first-level half budget; fixed equal per-beam second-level quotas; no wall clock or random choices" });
    }
    public static SpatialDecision ChooseMove(SpatialState state, SpatialSearchBudget? searchBudget = null, CancellationToken cancellationToken = default)
    {
        var budget = searchBudget ?? new(); var policyHash = PolicyHash(budget);
        var actions = 0; var nodes = 0; var enumerated = 0; var truncated = false;
        try
        {
            var verified = SpatialGame.Verify(state, cancellationToken);
            if (verified.IsOver) return new(null, null, true, false, 0, 0, 0, policyHash, verified.StateHash, "match-already-ended");
            var candidates = SpatialGame.LegalMovesVerified(verified, cancellationToken); nodes++; enumerated += candidates.Length;
            var rootLimit = budget.LookaheadPlies == 2 ? Math.Max(1, budget.MaximumEvaluations / 2) : budget.MaximumEvaluations;
            var ranked = new List<Candidate>();
            foreach (var legal in candidates.Take(rootLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var after = SpatialGame.ApplyVerified(verified, legal.Move, cancellationToken); actions++;
                ranked.Add(new(legal.Move, after, Evaluate(verified, after), ranked.Count));
            }
            truncated |= ranked.Count < candidates.Length;
            ranked.Sort(Compare);
            if (ranked.Count == 0) throw new SpatialGameException("no-command", "The declared piece has no in-bounds orientation.");
            if (budget.LookaheadPlies == 2 && actions < budget.MaximumEvaluations)
            {
                var beam = ranked.Take(budget.BeamWidth).ToArray();
                truncated |= beam.Length < ranked.Count;
                var quota = (budget.MaximumEvaluations - actions) / beam.Length;
                for (var index = 0; index < beam.Length; index++)
                {
                    var parent = beam[index];
                    if (parent.After.IsOver || quota == 0) continue;
                    var children = SpatialGame.LegalMovesVerified(parent.After, cancellationToken); nodes++; enumerated += children.Length;
                    SpatialEvaluation? best = null;
                    foreach (var child in children.Take(quota))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var after = SpatialGame.ApplyVerified(parent.After, child.Move, cancellationToken); actions++;
                        var evaluation = Evaluate(verified, after);
                        if (best is null || CompareEvaluation(evaluation, best) < 0) best = evaluation;
                    }
                    truncated |= children.Length > quota;
                    if (best is not null) beam[index] = parent with { Evaluation = best };
                }
                Array.Sort(beam, Compare); ranked = [.. beam];
            }
            var chosen = ranked[0];
            return new(chosen.Move, chosen.Evaluation, true, truncated, actions, nodes, enumerated, policyHash, verified.StateHash,
                chosen.After.EndReason == "top-out" ? "all-evaluated-root-actions-top-out" : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(null, null, false, true, actions, nodes, enumerated, policyHash, state?.StateHash ?? "", "cancelled; no move selected"); }
    }
    public static SpatialRun Play(SpatialChallenge challenge, SpatialSearchBudget? searchBudget = null, CancellationToken cancellationToken = default)
    {
        var budget = searchBudget ?? new(); var policyHash = PolicyHash(budget); var state = SpatialGame.Start(challenge);
        var decisions = ImmutableArray.CreateBuilder<SpatialDecision>(); var moves = ImmutableArray.CreateBuilder<SpatialMove>(); long actions = 0;
        while (!state.IsOver)
        {
            var decision = ChooseMove(state, budget, cancellationToken); decisions.Add(decision); actions += decision.ActionsEvaluated;
            if (!decision.Completed || decision.Move is null)
                return new("FileMapper deterministic native baseline", state.Challenge.ChallengeHash!, policyHash, moves.ToImmutable(), decisions.ToImmutable(), state, false, actions, decision.Reason);
            // ChooseMove already reconstructed this state; apply the selected legal command using the same core transition.
            try { state = SpatialGame.ApplyVerified(state, decision.Move, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return new("FileMapper deterministic native baseline", state.Challenge.ChallengeHash!, policyHash, moves.ToImmutable(), decisions.ToImmutable(), state, false, actions, "cancelled before selected move committed"); }
            moves.Add(decision.Move);
        }
        return new("FileMapper deterministic native baseline", state.Challenge.ChallengeHash!, policyHash, moves.ToImmutable(), decisions.ToImmutable(), state, true, actions, state.EndReason);
    }
    private sealed record Candidate(SpatialMove Move, SpatialState After, SpatialEvaluation Evaluation, int Ordinal);
    private static int Compare(Candidate left, Candidate right)
    { var compared = CompareEvaluation(left.Evaluation, right.Evaluation); return compared != 0 ? compared : left.Ordinal.CompareTo(right.Ordinal); }
    private static int CompareEvaluation(SpatialEvaluation left, SpatialEvaluation right)
    {
        var compared = left.TopOut.CompareTo(right.TopOut); if (compared != 0) return compared;
        compared = right.ScoreGain.CompareTo(left.ScoreGain); if (compared != 0) return compared;
        compared = right.GroundedCodePoints.CompareTo(left.GroundedCodePoints); if (compared != 0) return compared;
        compared = left.CoveredEmptyCells.CompareTo(right.CoveredEmptyCells); if (compared != 0) return compared;
        compared = left.MaximumOccupiedHeight.CompareTo(right.MaximumOccupiedHeight); if (compared != 0) return compared;
        return left.SumColumnHeights.CompareTo(right.SumColumnHeights);
    }
    private static SpatialEvaluation Evaluate(SpatialState before, SpatialState after)
    {
        var occupied = after.Placements.SelectMany(p => SpatialGame.Cells(after.Challenge, p).Select(c => (p.LayerId, Cell: c))).ToArray();
        var holes = 0; var max = 0; var sum = 0;
        foreach (var column in occupied.GroupBy(c => (c.LayerId, c.Cell.X, c.Cell.Z)))
        {
            var height = column.Max(c => c.Cell.Y) + 1; max = Math.Max(max, height); sum += height;
            holes += height - column.Count();
        }
        var grounded = after.History.Skip(before.History.Length).Sum(h => h.ScoreBreakdown.GroundedRelationshipPoints + h.ScoreBreakdown.GroundedWholePatternPoints);
        return new(after.EndReason == "top-out", after.Score - before.Score, grounded, holes, max, sum);
    }
    private static void Validate(SpatialSearchBudget budget)
    {
        if (budget is null || budget.MaximumEvaluations is < 1 or > 20_000 || budget.BeamWidth is < 1 or > 32 || budget.LookaheadPlies is < 1 or > 2)
            throw new SpatialGameException("search-budget", "Search requires 1–20000 evaluations, beam width 1–32 and lookahead 1–2.");
    }
}
