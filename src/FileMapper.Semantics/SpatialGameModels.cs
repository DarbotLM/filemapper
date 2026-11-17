using System.Collections.Immutable;

namespace FileMapper.Semantics;

public sealed record SpatialLayer(string Id, string SnapshotId, DateTimeOffset? ObservedFrom,
    DateTimeOffset? ObservedTo, string Completeness);
public sealed record SpatialPiece(string Id, string SomId, string ObservationId, string SourceNodeId,
    string LayerId, string BaseShape, string GeometryVariant, string Label, ImmutableArray<string> Tokens,
    ImmutableArray<string> EvidenceIds, string UnitKind = "object", string? SourceSomId = null, string? GeometryHash = null);
public sealed record SpatialPattern(string Id, string LayerId, string Kind, ImmutableArray<string> MemberPieceIds,
    ImmutableArray<string> EvidenceIds, ImmutableArray<string> SourceEdgeIds);
public sealed record SpatialAffinity(string Id, string LayerId, string FirstPieceId, string SecondPieceId,
    int ProbabilityPpm, string ModelHash, string FeatureHash);
public sealed record SpatialBoardSpec(int Width = 6, int Height = 12, int Depth = 4);
public sealed record SpatialChallenge(string Id, string DatasetHash, string ModelArtifactHash,
    ImmutableArray<SpatialLayer> Layers, ImmutableArray<SpatialPiece> Deck, ImmutableArray<SpatialPattern> Patterns,
    ImmutableArray<SpatialAffinity> Affinities, SpatialBoardSpec? Board = null, string ClearMode = "planes",
    string? ChallengeHash = null, string Schema = "filemapper.spatial-game.challenge/1", string RulesVersion = "spatial-tetrid/1",
    string? ScoringPolicyHash = null);
public sealed record SpatialScoringPolicy(string Version, int PlanePoints, int LinePoints, int PairPatternPoints,
    int WholePatternPointsPerMember, int AffinityNeutralPpm, int MaximumAffinityPairPoints, int MaximumAffinityMatchPoints);
public sealed record SpatialGeometry(string BaseShape, string GeometryVariant, string GeometryHash, ImmutableArray<Voxel> Cells,
    bool IntrinsicallyThreeDimensional, string Interpretation);
public sealed record SpatialOrientation(string Id, ImmutableArray<Voxel> Cells, int Width, int Height, int Depth);
public sealed record SpatialMove(string OrientationId, int X, int Z, string LayerId, string ExpectedPriorStateHash);
public sealed record SpatialLegalMove(SpatialMove Move, int? LandingY, bool TopOut);
public sealed record SpatialPlacement(string PieceId, string LayerId, string OrientationId, int X, int Y, int Z);
public sealed record SpatialClearLine(string Axis, int Y, int OtherCoordinate);
public sealed record SpatialFall(string PieceId, int FromY, int ToY);
public sealed record SpatialClearWave(ImmutableArray<int> PlaneYs, ImmutableArray<SpatialClearLine> Lines,
    ImmutableArray<string> RemovedPieceIds, ImmutableArray<SpatialFall> Falls);
public sealed record SpatialPatternMatch(string PatternId, string LayerId, string Kind, string Basis,
    ImmutableArray<string> MemberPieceIds, ImmutableArray<string> EvidenceIds, ImmutableArray<string> SourceEdgeIds, long Points);
public sealed record SpatialAffinityMatch(string AffinityId, string LayerId, string FirstPieceId, string SecondPieceId,
    int ProbabilityPpm, string ModelHash, string FeatureHash, long Points,
    string Interpretation = "frozen-learned-similarity; not observed relationship evidence");
public sealed record SpatialScoreBreakdown(int Planes, int Lines, long ClearPoints, long ObservedLexicalPoints,
    long GroundedRelationshipPoints, long GroundedWholePatternPoints, long LearnedAdvisoryPoints, long Total);
public sealed record SpatialStep(int Index, string PieceId, SpatialMove Move, SpatialPlacement? Placement,
    bool TopOut, ImmutableArray<SpatialClearWave> ClearWaves, ImmutableArray<SpatialPatternMatch> PatternMatches,
    ImmutableArray<SpatialAffinityMatch> AffinityMatches, SpatialScoreBreakdown ScoreBreakdown,
    string StateBeforeHash, string StateAfterHash);
public sealed record SpatialState(SpatialChallenge Challenge, ImmutableArray<SpatialPlacement> Placements,
    int NextPieceIndex, long Score, long LearnedAdvisoryScore, ImmutableArray<string> PaidPatternIds,
    ImmutableArray<string> PaidAffinityIds, ImmutableArray<SpatialStep> History, bool IsOver,
    string? EndReason, string StateHash);
public sealed class SpatialGameException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
