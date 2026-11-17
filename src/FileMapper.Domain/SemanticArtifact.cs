namespace FileMapper.Domain;

/// <summary>An immutable local artifact. Authorship claims are metadata, never proof of model identity.</summary>
public sealed record SemanticArtifact(string Hash, string Kind, string? ParentHash, string Payload, string Summary,
    DateTimeOffset StoredAt);
public sealed record SemanticArtifactSummary(string Hash, string Kind, string? ParentHash, string Summary, DateTimeOffset StoredAt);
