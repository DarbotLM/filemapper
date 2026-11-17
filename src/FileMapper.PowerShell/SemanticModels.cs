using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMapper.PowerShell;

// Timestamp wire strings and arbitrary JSON evidence remain exact: parsing either can discard source lexemes.
public sealed record FileMapperSemanticSnapshot(string Id, string Root, string StartedAt, string? FinishedAt,
    string Status, string Completeness, long EntryCount, long FileCount, long DirectoryCount, long LogicalBytes,
    long? AllocatedBytes, long ErrorCount);
public sealed record FileMapperSemanticPose(string Shape, int Axis, int Rotation);
public sealed record FileMapperSemanticEvidenceReference(string Id, string SourceField);
public sealed record FileMapperSemanticSourceReference(string SnapshotId, string NodeId, string? Path);
public sealed record FileMapperSemanticSnapshotWindow(string SnapshotId, string Root, string StartedAt,
    string? FinishedAt, string Status, string Completeness);
public sealed record FileMapperSemanticObservedWindow(string? From, string? To, string Basis);
public sealed record FileMapperSemanticDimension(string Status, JsonElement? Value, string? SourceField);
public sealed record FileMapperSemanticBounds(int MaxTokens, int RetainedTokens, int TotalDistinctTokens, long TotalTokens, bool Truncated);
public sealed record FileMapperSemanticHashes(string RawSourceHash, string ContentHash, string GeometryHash, string ObjectHash);
public sealed record FileMapperSemanticLexicalFeatures(int RuneLength, int LetterCount, int DigitCount, bool HasNonAscii,
    bool HasCombiningMark, bool BeginsWithDigit, bool EndsWithDigit);
public sealed record FileMapperSemanticWord(string WordId, string Token, long Count, string[] Sources,
    [property: JsonPropertyName("shape_type")] string ShapeType,
    [property: JsonPropertyName("shape_color")] string ShapeColor,
    [property: JsonPropertyName("somshape")] FileMapperSemanticPose SomShape,
    int Signature9, string SignatureBits, FileMapperSemanticLexicalFeatures Features,
    string SignatureModel, bool SignatureCollisionsAllowed);
public sealed record FileMapperSemanticObject(string ModelVersion, string SomId, string ObservationId, string Label,
    [property: JsonPropertyName("some_type")] string SomeType,
    [property: JsonPropertyName("shape_type")] string ShapeType,
    [property: JsonPropertyName("shape_color")] string ShapeColor,
    [property: JsonPropertyName("somshape")] FileMapperSemanticPose SomShape,
    [property: JsonPropertyName("some_shapes")] FileMapperSemanticWord[] SomeShapes,
    JsonElement RawSourceNode, FileMapperSemanticEvidenceReference[] EvidenceRefs,
    FileMapperSemanticSourceReference SourceReference, FileMapperSemanticSnapshotWindow SnapshotWindow,
    FileMapperSemanticObservedWindow ObservedWindow, Dictionary<string, FileMapperSemanticDimension> Dimensions,
    FileMapperSemanticBounds Bounds, FileMapperSemanticHashes Hashes)
{
    [JsonIgnore]
    public string SnapshotId => SourceReference.SnapshotId;
    [JsonIgnore]
    public string RawSourceJson => RawSourceNode.GetRawText();
    [JsonIgnore]
    public string DimensionsJson => FileMapperSemanticJson.Serialize(Dimensions);
    public string ToJson() => FileMapperSemanticJson.Serialize(this);
}
public sealed record FileMapperSemanticObjectLimits(int MaximumPageSize, int MaximumWordsPerObject);
public sealed record FileMapperSemanticObjectPage(FileMapperSemanticObject[] Items, long Total, int Offset, int Limit,
    FileMapperSemanticSnapshot Snapshot, string ModelVersion, FileMapperSemanticObjectLimits Limits);
public sealed record FileMapperSemanticPatternMatch(FileMapperSemanticObject Before, FileMapperSemanticObject After,
    string MatchType, bool IdentityEqual, string PatternKey, string PatternHash, string Explanation);
public sealed record FileMapperSemanticPatternLimits(int MaximumItems, int CandidateObjectsPerSnapshot,
    bool CandidatesTruncated, int MinimumLexicalSegments, string CandidateKind, string PathPolicy);
public sealed record FileMapperSemanticPatternPage(FileMapperSemanticSnapshot Before, FileMapperSemanticSnapshot After,
    FileMapperSemanticPatternMatch[] Items, string MatchedBy, FileMapperSemanticPatternLimits Limits);

public sealed record FileMapperSemanticGamePiece(string Id, string SomId, string Label, string[] Tokens, string Shape);
public sealed record FileMapperSemanticGameAssociation(string Id, string FromSomId, string ToSomId, string Kind, string[] EvidenceIds);
public sealed record FileMapperSemanticGamePattern(string Id, string[] Tokens, string Kind, string[] EvidenceIds);
public sealed record FileMapperSemanticChallenge(string Id, string DatasetHash, string DatasetSchema, string DatasetVersion,
    FileMapperSemanticGamePiece[] Deck, FileMapperSemanticGameAssociation[] Associations,
    FileMapperSemanticGamePattern[] Patterns, string ChallengeHash, int Width, int Height, string Schema, string RulesVersion);
// Public setters and a parameterless constructor permit PowerShell's typed-hashtable conversion.
public sealed class FileMapperSemanticMove
{
    public int Rotation { get; set; }
    public int Column { get; set; }
    public string ExpectedPriorStateHash { get; set; } = "";
}
public sealed record FileMapperSemanticGameCell(int Column, int Row, string PieceId);
public sealed record FileMapperSemanticGameWord(string PieceId, string SomId, string Token, int TokenIndex);
public sealed record FileMapperSemanticRelationshipMatch(string AssociationId, string FromPieceId, string ToPieceId,
    string Kind, string[] EvidenceIds, long Points);
public sealed record FileMapperSemanticClearedRow(int Row, string[] PieceIds, FileMapperSemanticGameWord[] Words,
    string[] MatchingPatternIds, FileMapperSemanticRelationshipMatch[] Relationships);
public sealed record FileMapperSemanticScoreBreakdown(int Lines, long LinePoints, long GroundedPatternPoints,
    long InferredPatternPoints, long GroundedRelationshipPoints, long InferredRelationshipPoints, long Total);
public sealed record FileMapperSemanticLearnedAssociation(string FirstSomId, string SecondSomId, int CoClearCount,
    string[] GroundingAssociationIds, string Interpretation);
public sealed record FileMapperSemanticGameStep(int Index, string PieceId, FileMapperSemanticMove Move, int? LandingRow,
    bool TopOut, FileMapperSemanticClearedRow[] ClearedRows, FileMapperSemanticScoreBreakdown ScoreBreakdown,
    FileMapperSemanticLearnedAssociation[] LearningUpdates, string StateBeforeHash, string StateAfterHash);
public sealed record FileMapperSemanticReplay(FileMapperSemanticChallenge Challenge, FileMapperSemanticGameCell[] Board,
    int NextPieceIndex, long Score, int ClearedLines, bool IsOver, string? EndReason, FileMapperSemanticGameStep[] History,
    FileMapperSemanticLearnedAssociation[] Learning, string StateHash);
public sealed record FileMapperSemanticExportResult(string Schema, string SnapshotId, string ContentSha256, long ContentBytes,
    long Objects, long Edges, long Observations, long Locations, long Findings, Dictionary<string, long> ObjectKinds);
public sealed record FileMapperSemanticExport(string Path, string Compression, long FileBytes, FileMapperSemanticExportResult Result)
{
    public string SnapshotId => Result.SnapshotId;
    public string ContentSha256 => Result.ContentSha256;
    public long Objects => Result.Objects;
}

/// <summary>Lossless JSON-evidence serialization for PowerShell, whose ConvertTo-Json treats JsonElement as metadata.</summary>
public static class FileMapperSemanticJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
}
