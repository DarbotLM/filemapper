using FileMapper.Domain;

namespace FileMapper.Collect.Git;

/// <summary>Bounded read-only repository and declared build provenance. Never evaluates repository build logic.</summary>
public sealed class GitCollector(ProvenanceOptions? options = null) : ICodeExtractor
{
    public string Language => "git";
    public async Task<AnalysisResult> ExtractAsync(string snapshotId, string root, CancellationToken cancellationToken)
    {
        var limits = options ?? new(); limits.Validate();
        var graph = new ProvenanceGraph(snapshotId, Path.GetFullPath(root), limits);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(limits.TotalTimeout);
        var ct = budget.Token;
        try
        {
            var history = new RepositoryHistory(graph);
            await history.CollectAsync(ct);
            var documents = graph.Discover(ct).ToArray();
            var packages = new PackageProvenance(graph);
            packages.Collect(documents, ct);
            var imports = new SupplyChainImports(graph, history, packages);
            await imports.CollectAsync(documents, ct);
            await history.CollectOwnershipAsync(ct);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { graph.Diagnostic(root, "provenance_time_budget", "Provenance collection reached its total time budget; this graph is partial."); }
        return graph.Result();
    }
}

public sealed record ProvenanceOptions
{
    public string? GitExecutable { get; init; }
    public int MaxCommits { get; init; } = 200;
    public int MaxChangeCommits { get; init; } = 40;
    public int MaxTrackedFiles { get; init; } = 20_000;
    public int MaxChanges { get; init; } = 10_000;
    public int MaxBlameFiles { get; init; } = 32;
    public int MaxBlameLinesPerFile { get; init; } = 2_000;
    public int MaxComponents { get; init; } = 10_000;
    public int MaxDocuments { get; init; } = 2_000;
    public int MaxDocumentBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxArtifactBytes { get; init; } = 256L * 1024 * 1024;
    public int SimilarityPercent { get; init; } = 50;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(90);
    internal void Validate()
    {
        if (MaxCommits is < 1 or > 2_000 || MaxChangeCommits < 0 || MaxChangeCommits > MaxCommits ||
            MaxTrackedFiles is < 1 or > 100_000 || MaxChanges is < 1 or > 100_000 || MaxBlameFiles is < 0 or > 256 ||
            MaxBlameLinesPerFile is < 1 or > 20_000 || MaxComponents is < 1 or > 100_000 || MaxDocuments is < 1 or > 10_000 ||
            MaxDocumentBytes is < 1024 or > 16 * 1024 * 1024 || MaxArtifactBytes is < 1 or > 1024L * 1024 * 1024 ||
            SimilarityPercent is < 1 or > 100 || CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(1) ||
            TotalTimeout <= TimeSpan.Zero || TotalTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(ProvenanceOptions));
    }
}
