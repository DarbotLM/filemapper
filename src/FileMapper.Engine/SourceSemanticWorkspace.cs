using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using FileMapper.Extract.PowerShell;
using FileMapper.Harvest;
using FileMapper.Semantics;

namespace FileMapper.Engine;

/// <summary>Non-executing semantic derivation from immutable, pinned source receipts. Never opens a query-index writer.</summary>
public static class SourceSemanticWorkspace
{
    public const string Schema = "filemapper.source-semantics/1";
    public static async Task<SourceSemanticReceipt> AnalyzeAsync(string existingCorpus, string newDestination,
        SourceSemanticRequest request, IProgress<object>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = cancellationToken; token.ThrowIfCancellationRequested();
        var options = ValidateOptions(request.Options ?? new());
        var corpus = SourceSemanticInputReader.Ordinary(existingCorpus);
        var destination = SourceSemanticInputReader.Ordinary(newDestination);
        if (!Directory.Exists(corpus)) throw new DirectoryNotFoundException("An existing source corpus is required.");
        if (Related(corpus, destination)) throw new ArgumentException("Semantic output must be separate from, and cannot contain, the source corpus.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Semantic output is immutable; choose a new destination.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The output parent must already exist.");
        var stage = SourceSemanticInputReader.Ordinary(Path.Combine(parent, ".semantic-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(stage) || File.Exists(stage)) throw new IOException("Staging path already exists.");
        Directory.CreateDirectory(stage);
        var startedAt = DateTimeOffset.UtcNow;
        var runId = "source-semantic-run:" + Guid.NewGuid().ToString("N");
        var artifacts = new SourceSemanticArtifacts(stage, options.MaximumOutputBytes);
        try
        {
            SourceSemanticInput input;
            progress?.Report(new { phase = "verifying-source-admission", runId });
            await using (var documents = artifacts.Open("documents.ndjson"))
            {
                input = await SourceSemanticInputReader.ReadAsync(corpus, request, options,
                    (value, ct) => documents.AddAsync(value, ct), token);
                await documents.CompleteAsync(token);
            }
            var implementation = await ImplementationAsync(token);
            using var runtimeLease = await RuntimeReferenceLease.OpenAsync(token);
            var metadata = runtimeLease.Metadata;
            var requestHash = CanonicalJson.Sha256(request with { Options = options });
            var contextId = "source-semantic-context:" + CanonicalJson.Sha256(new
            {
                Schema, input.Capture.RepositoryId, input.Capture.Commit, input.SemanticInputSetSha256, input.Extraction.Policy.PolicyHash,
                Options = options, Implementation = implementation, TrustedRuntimeMetadata = metadata,
                Decoder = SuppliedSourceDecoder.ContractId, CSharpModel = CSharpFlowModels.Default().Version,
                SomModel = SemanticObjects.ModelVersion, Join = "raw-byte-span-containment/v1"
            });
            var analyses = new List<SuppliedSourceAnalysis>();
            var remainingGraphRecords = options.MaximumGraphRecords;
            foreach (var language in new[] { "C#", "PowerShell" })
            {
                var selected = input.Selected.Where(d => d.Language == language).ToArray();
                if (selected.Length == 0) continue;
                if (remainingGraphRecords == 0) throw new SourceGraphLimitException(options.MaximumGraphRecords, "next language batch");
                var nativeInputOptions = new SuppliedSourceOptions(options.MaximumDocuments, options.MaximumDocumentBytes, options.MaximumTotalBytes, remainingGraphRecords);
                progress?.Report(new { phase = "parsing-retained-source", runId, language, documents = selected.Length });
                var supplied = selected.Select(d => new SuppliedSourceDocument(d.Uri, d.Bytes, d.Capture.Blob!.Sha256, d.Bytes.LongLength)).ToArray();
                var analysis = language == "C#"
                    ? await new CSharpExtractor(options: options.CSharp).ExtractDocumentsAsync(runId, contextId, supplied, nativeInputOptions, cancellationToken: token)
                    : await new PowerShellExtractor().ExtractDocumentsAsync(runId, contextId, supplied, nativeInputOptions, cancellationToken: token);
                analyses.Add(analysis);
                remainingGraphRecords -= checked(analysis.Analysis.Nodes.Count + analysis.Analysis.Edges.Count + analysis.Analysis.Observations.Count + analysis.Analysis.Findings.Count);
                if (remainingGraphRecords < 0) throw new InvalidDataException("Native parser exceeded its supplied aggregate graph record limit.");
            }
            var parsedAt = DateTimeOffset.UtcNow;
            await artifacts.WriteAsync("parsers.ndjson", analyses.Select(a => new
            { a.ContextId, a.SourceScope, a.Coverage, a.StartedAt, a.FinishedAt, a.SuppliedObservedAt }), token);
            var parserDocuments = analyses.SelectMany(a => a.Documents).ToArray();
            await artifacts.WriteAsync("parser-documents.ndjson", parserDocuments, token);
            var nodes = Unique(analyses.SelectMany(a => a.Analysis.Nodes), n => n.Id, "node");
            var edges = Unique(analyses.SelectMany(a => a.Analysis.Edges), e => e.Id, "edge");
            var observations = Unique(analyses.SelectMany(a => a.Analysis.Observations), e => e.Id, "observation");
            var findings = Unique(analyses.SelectMany(a => a.Analysis.Findings), f => f.Id, "finding");
            await artifacts.WriteAsync("nodes.ndjson", nodes, token);
            await artifacts.WriteAsync("edges.ndjson", edges, token);
            await artifacts.WriteAsync("observations.ndjson", observations, token);
            await artifacts.WriteAsync("findings.ndjson", findings, token);

            progress?.Report(new { phase = "projecting-native-shapes", runId, nodes = nodes.Length });
            var bySubject = observations.ToLookup(o => o.SubjectId, StringComparer.Ordinal);
            var envelope = new Snapshot(runId, contextId, startedAt, parsedAt, "semantic-extraction-context",
                "partial-static-analysis; not a filesystem observation", input.SelectedEntries, parserDocuments.LongCount(d => d.Status == "admitted"),
                0, input.Selected.Sum(d => d.Bytes.LongLength), null, observations.LongCount(o => o.ErrorCode is not null));
            await using (var shapes = artifacts.Open("objects.ndjson"))
            await using (var shapeSources = artifacts.Open("object-sources.ndjson"))
            {
                foreach (var node in nodes)
                {
                    token.ThrowIfCancellationRequested();
                    var evidence = bySubject[node.Id].OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
                    var properties = new Dictionary<string, object?>(node.Properties, StringComparer.Ordinal);
                    // Preserve native evidence fields. The added subject evidence uses its own field if a collector already supplied references.
                    properties["evidenceIds"] = MergeEvidenceIds(properties.GetValueOrDefault("evidenceIds"), evidence.Select(e => e.Id));
                    var projected = node with { Properties = properties };
                    var shape = SemanticObjects.Create(envelope, projected,
                        evidence.Length == 0 ? null : evidence.Min(e => e.ObservedAt), evidence.Length == 0 ? null : evidence.Max(e => e.ObservedAt));
                    await shapes.AddAsync(shape, token);
                    await shapeSources.AddAsync(new { node.Id, shape.SomId, NativeNodeSha256 = CanonicalJson.Sha256(node),
                        SubjectEvidenceIds = evidence.Select(e => e.Id).ToArray(), Projection = "native node plus actual subject observation IDs; original node retained in nodes.ndjson" }, token);
                }
                await shapes.CompleteAsync(token); await shapeSources.CompleteAsync(token);
            }
            progress?.Report(new { phase = "replaying-lexical-spans", runId });
            var joined = await SourceSemanticLinker.WriteAsync(corpus, input, nodes, parserDocuments, options, artifacts, token);
            var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var evidenceIds = observations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var admitted = parserDocuments.LongCount(d => d.Status == "admitted");
            var coverage = new SourceSemanticCoverage(input.TotalEntries, input.SelectedEntries, input.SupportedDocuments,
                admitted, input.SelectedEntries - admitted - input.UnsupportedEntries, input.UnsupportedEntries,
                nodes.LongLength, edges.LongLength, observations.LongLength, findings.LongLength,
                joined.ExactSpans, nodes.LongLength - joined.ExactSpans, joined.Links, joined.LinksTruncated)
            {
                SourceAdmissionStatuses = input.AdmissionStatuses,
                ParserAdmissionStatuses = parserDocuments.GroupBy(d => d.Status, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal),
                JoinWordLimitedDocuments = joined.WordLimitedDocuments,
                PartialObservations = observations.LongCount(o => o.Completeness != "complete"),
                EdgesWithMissingEndpoints = edges.LongCount(e => !nodeIds.Contains(e.FromId) || !nodeIds.Contains(e.ToId)),
                EdgesWithMissingEvidence = edges.LongCount(e => !evidenceIds.Contains(e.EvidenceId))
            };
            var finishedAt = DateTimeOffset.UtcNow;
            var manifest = new SourceSemanticManifest(Schema, runId, contextId, request.CaptureId, request.ExtractionId,
                input.Capture.RepositoryId, input.Capture.Commit, requestHash, input.InputSetSha256, input.Extraction.Policy.PolicyHash,
                options, implementation, metadata, startedAt, parsedAt, finishedAt, input.Capture.StartedAt, input.Capture.FinishedAt,
                null, coverage, artifacts.Items.OrderBy(a => a.Path, StringComparer.Ordinal).ToArray(), "sealed-partial-semantic-analysis",
                "Static C#/PowerShell analysis of selected retained bytes; exact capture/extraction commitments and fresh lexical replay. " +
                "C# uses trusted host runtime metadata, not target-framework/project restore; PowerShell dependencies are unverified logical references. " +
                "Source-span word containment is not symbol binding, runtime execution, translation equivalence or learned similarity. " +
                "Capture times, actual analysis times and unknown original observation time are separate. " +
                "Native IDs and 9-bit collisions are preserved; no source corpus/index/learner writes. " +
                "Output ceiling applies to sealed artifacts plus manifest; bounded lexical temporary output and failed stages can exceed it before rejection. " +
                "Input/graph limits are not a process-memory or wall-time guarantee.")
            { SemanticInputSetSha256 = input.SemanticInputSetSha256 };
            var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(manifest));
            if (bytes.Length > 1024 * 1024 || bytes.Length > options.MaximumOutputBytes - artifacts.TotalBytes)
                throw new IOException("Semantic manifest or publication bytes exceed their explicit bound.");
            var analysisId = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await DurableWriteAsync(artifacts.PathFor("manifest.json"), bytes, token);
            token.ThrowIfCancellationRequested();
            SourceSemanticInputReader.Ordinary(stage); SourceSemanticInputReader.Ordinary(destination);
            Directory.Move(stage, destination); // Same-parent atomic publication; never overwrites an existing result.
            return new(analysisId, Path.Combine(destination, "manifest.json"), manifest);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try
            {
                var failure = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new { Schema, runId,
                    state = token.IsCancellationRequested ? "canceled-unsealed" : "failed-unsealed", startedAt,
                    finishedAt = DateTimeOffset.UtcNow, error = error.GetType().Name,
                    detail = error.Message.Length <= 4096 ? error.Message : error.Message[..4096] }));
                await DurableWriteAsync(artifacts.PathFor("failure.json"), failure, CancellationToken.None);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or ArgumentException) { }
            throw;
        }
    }

    internal static SourceSemanticOptions ValidateOptions(SourceSemanticOptions value)
    {
        if (value.MaximumDocuments is < 1 or > 2000 || value.MaximumDocumentBytes is < 1 or > 16L * 1024 * 1024 ||
            value.MaximumTotalBytes is < 1 or > 64L * 1024 * 1024 || value.MaximumOutputBytes is < 1024 or > 4L * 1024 * 1024 * 1024 ||
            value.MaximumLexicalLinks is < 0 or > 5_000_000 || value.MaximumJoinWordsPerDocument is < 1 or > 1_000_000 ||
            value.MaximumGraphRecords is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(value), "Semantic input, output and join budgets are outside their supported bounds.");
        var c = value.CSharp ?? new(); _ = new CSharpExtractor(options: c);
        if (c.MaxMethods > 10000 || c.MaxBlocksPerMethod > 10000 || c.MaxBlockVisits > 100000 || c.MaxOperations > 2_000_000 ||
            c.MaxRouteLength > 1024 || c.MaxOriginsPerValue > 1024 || c.MaxDefinitionsPerValue > 2048 || c.MaxCallDepth > 8 || c.MaxSyntaxEntities > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(value), "C# analysis budgets exceed the semantic publication ceilings.");
        return value with { CSharp = c };
    }
    private static bool Related(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var aPrefix = Path.EndsInDirectorySeparator(a) ? a : a + Path.DirectorySeparatorChar;
        var bPrefix = Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar;
        return a.Equals(b, comparison) || a.StartsWith(bPrefix, comparison) || b.StartsWith(aPrefix, comparison);
    }
    private static T[] Unique<T>(IEnumerable<T> values, Func<T, string> identity, string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var id = identity(value);
            if (result.TryGetValue(id, out var existing) && CanonicalJson.Serialize(existing) != CanonicalJson.Serialize(value))
                throw new InvalidDataException("Conflicting native " + kind + " identity; semantic output is not sealed.");
            result[id] = value;
        }
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
    }
    private static string[] MergeEvidenceIds(object? existing, IEnumerable<string> added)
    {
        // Existing native collectors use string arrays; canonical parsing also handles a JSON value without trusting object.ToString().
        using var document = System.Text.Json.JsonDocument.Parse(CanonicalJson.Serialize(existing));
        var prior = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Where(v => v.ValueKind == System.Text.Json.JsonValueKind.String).Select(v => v.GetString()!).ToArray()
            : document.RootElement.ValueKind == System.Text.Json.JsonValueKind.String ? [document.RootElement.GetString()!] : Array.Empty<string>();
        return prior.Concat(added).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task<SourceSemanticImplementation[]> ImplementationAsync(CancellationToken token)
    {
        Assembly[] assemblies = [typeof(SourceSemanticWorkspace).Assembly, typeof(CSharpExtractor).Assembly,
            typeof(PowerShellExtractor).Assembly, typeof(SourceLexicon).Assembly, typeof(SemanticObjects).Assembly, typeof(MapNode).Assembly,
            typeof(Microsoft.CodeAnalysis.Compilation).Assembly, typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
            typeof(System.Management.Automation.Language.Parser).Assembly];
        var result = new List<SourceSemanticImplementation>();
        foreach (var assembly in assemblies.Distinct().OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
            result.Add(await AssemblyProofAsync(assembly.Location, token));
        return result.ToArray();
    }
    private sealed class RuntimeReferenceLease : IDisposable
    {
        private readonly List<FileStream> handles = [];
        public List<SourceSemanticImplementation> Metadata { get; } = [];
        public static async Task<RuntimeReferenceLease> OpenAsync(CancellationToken token)
        {
            var lease = new RuntimeReferenceLease();
            try
            {
                var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? typeof(object).Assembly.Location).Split(Path.PathSeparator);
                // Preserve reference order and hold ordinary Windows write/delete-denying handles through parsing/publication.
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    lease.handles.Add(stream);
                    lease.Metadata.Add(new(Path.GetFileName(path), AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "unknown",
                        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token))));
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose() { foreach (var stream in handles) stream.Dispose(); handles.Clear(); }
    }
    private static async Task<SourceSemanticImplementation> AssemblyProofAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return new(Path.GetFileName(path), AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "unknown",
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)));
    }
    private static async Task DurableWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true);
    }
}
