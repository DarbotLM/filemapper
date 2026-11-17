using System.Text.Json;

namespace FileMapper.Engine;

public sealed record SourceSemanticCatalogAnalysis(string AnalysisId, SourceSemanticManifest Manifest,
    DateTimeOffset ImportedAt, long SealedBytes);
public sealed record SourceSemanticCatalogPage<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit,
    int? NextOffset, bool ByteLimitReached);
/// <summary>The link states source-span containment, never symbol resolution. Occurrence is the exact retained lexical record.</summary>
public sealed record SourceSemanticCatalogWord(JsonElement Link, JsonElement Occurrence);
public sealed record SourceSemanticCatalogNode(JsonElement Node, JsonElement? Object, JsonElement? Span,
    SourceSemanticCatalogPage<JsonElement> SubjectEvidence, SourceSemanticCatalogPage<JsonElement> Edges,
    SourceSemanticCatalogPage<SourceSemanticCatalogWord> WordLinks,
    SourceSemanticCatalogPage<JsonElement> RelatedEdgeEvidence);
/// <summary>Caller owns the lease. Its held, hash-verified file denies concurrent write/delete until disposal.</summary>
public sealed class SourceSemanticCatalogArtifact : IDisposable
{
    private readonly IDisposable lease;
    internal SourceSemanticCatalogArtifact(Stream stream, SourceSemanticArtifact descriptor, IDisposable lease)
    { Stream = stream; Descriptor = descriptor; this.lease = lease; }
    public Stream Stream { get; }
    public SourceSemanticArtifact Descriptor { get; }
    public void Dispose() { Stream.Dispose(); lease.Dispose(); }
}
