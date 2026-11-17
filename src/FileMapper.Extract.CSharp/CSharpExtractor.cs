using FileMapper.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Security.Cryptography;

namespace FileMapper.Extract.CSharp;

/// <summary>Compiler-backed, non-executing analysis. Does not load MSBuild, analyzers or generators.</summary>
public sealed class CSharpExtractor : ICodeExtractor
{
    private readonly CSharpFlowModels models;
    private readonly CSharpAnalysisOptions options;
    public CSharpExtractor(CSharpFlowModels? models = null, CSharpAnalysisOptions? options = null)
    {
        this.models = models ?? CSharpFlowModels.Default(); this.options = options ?? new();
        if (this.options.MaxMethods < 1 || this.options.MaxBlocksPerMethod < 1 || this.options.MaxBlockVisits < 1 ||
            this.options.MaxOperations < 1 || this.options.MaxRouteLength < 2 || this.options.MaxOriginsPerValue < 1 ||
            this.options.MaxDefinitionsPerValue < 1 || this.options.MaxCallDepth < 0 || this.options.MaxSyntaxEntities < 1) throw new ArgumentException("Analysis budgets must be positive; call depth can be zero.");
    }
    public string Language => "C#";
    public Task<AnalysisResult> ExtractAsync(string snapshotId, string root, CancellationToken cancellationToken) =>
        Task.Run(() => Extract(snapshotId, Path.GetFullPath(root), cancellationToken), cancellationToken);

    public Task<SuppliedSourceAnalysis> ExtractDocumentsAsync(string snapshotId, string contextId,
        IReadOnlyList<SuppliedSourceDocument> documents, SuppliedSourceOptions? inputOptions = null,
        DateTimeOffset? observedAt = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batch = SuppliedSourceBatch.Prepare(snapshotId, contextId, documents, inputOptions, [".cs"], cancellationToken);
        var output = new ExtractionOutput(snapshotId, "roslyn-safe", observedAt, contextId, (inputOptions ?? new()).MaxGraphRecords);
        foreach (var rejected in batch.Documents.Where(x => x.Status != "admitted"))
            output.Error(rejected.LogicalPath, "supplied_source_" + rejected.Status, rejected.Detail ?? rejected.Status);
        var trees = new List<SyntaxTree>();
        foreach (var source in batch.Admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trees.Add(CSharpSyntaxTree.ParseText(source.Decoded.Text, new CSharpParseOptions(LanguageVersion.CSharp15), source.LogicalPath, cancellationToken: cancellationToken));
            output.Document(source.LogicalPath, source.Decoded.SourceSha256);
        }
        var analysis = trees.Count == 0 ? output.Result() : Analyze(contextId, trees, output, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new SuppliedSourceAnalysis(analysis, contextId,
            "Supplied verified bytes only; trusted host runtime metadata references; no target project restore, repository dependencies, MSBuild, analyzers, generators or source execution. Semantic context remains partial.",
            batch.Coverage, batch.Documents)
        { StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow, SuppliedObservedAt = observedAt };
    }, cancellationToken);

    private AnalysisResult Extract(string snapshot, string root, CancellationToken ct)
    {
        var output = new ExtractionOutput(snapshot, "roslyn-safe");
        var trees = new List<SyntaxTree>();
        foreach (var path in SourceFiles.Enumerate(root, [".cs"], output, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = SourceFiles.ReadBounded(path, ct);
                using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                trees.Add(CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp15), path, cancellationToken: ct));
                output.Document(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { output.Error(path, "read_error", ex.Message); }
        }
        return Analyze(root, trees, output, ct);
    }

    private AnalysisResult Analyze(string root, List<SyntaxTree> trees, ExtractionOutput output, CancellationToken ct)
    {
        int syntaxEntities = 0; bool syntaxTruncated = false;
        bool SyntaxBudget()
        {
            ct.ThrowIfCancellationRequested();
            if (++syntaxEntities <= options.MaxSyntaxEntities) return true;
            if (!syntaxTruncated) output.Error(root, "syntax_entity_budget", "Syntax/reference entity budget exhausted; graph coverage is partial.");
            syntaxTruncated = true; return false;
        }
        // Only platform assemblies are loaded as metadata. Repository binaries are never loaded or run.
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? typeof(object).Assembly.Location)
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        bool topLevel = trees.Any(t => t.GetRoot(ct) is CompilationUnitSyntax unit && unit.Members.Any(m => m is GlobalStatementSyntax));
        var compilation = CSharpCompilation.Create("FileMapper.SafeAnalysis", trees, references,
            new CSharpCompilationOptions(topLevel ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        foreach (var tree in trees)
        {
            ct.ThrowIfCancellationRequested();
            var syntax = tree.GetRoot(ct);
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            var path = tree.FilePath;
            foreach (var diagnostic in model.GetDiagnostics(cancellationToken: ct).Where(d => d.Severity == DiagnosticSeverity.Error).Take(100))
            {
                output.Observe(Identity.For("source", path), "compiler_diagnostic", diagnostic.Id, path, completeness: "partial", line: diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
                output.Error(path, diagnostic.Id, diagnostic.GetMessage(), diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
            }
            var documentId = Identity.For("source", path);
            var documentEvidence = output.Observe(documentId, "analysis_mode", "safe compilation; runtime references only; project restore and target-framework context not loaded", path, 0.8, "partial");
            foreach (var declaration in syntax.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax or ParameterSyntax or BaseNamespaceDeclarationSyntax))
            {
                if (!SyntaxBudget()) break;
                ct.ThrowIfCancellationRequested();
                if (model.GetDeclaredSymbol(declaration, ct) is not { } symbol) continue;
                var node = SymbolNode(symbol, path, declaration, root);
                output.Add(node);
                output.Link(documentId, node.Id, "DEFINES", output.Observe(node.Id, "declaration", symbol.ToDisplayString(), path, 1, "complete", Line(declaration)));
                if (symbol.ContainingSymbol is { } parent && parent.Kind is not SymbolKind.NetModule and not SymbolKind.Assembly)
                {
                    var parentId = SymbolId(parent, root);
                    output.Add(SymbolNode(parent, path, null, root));
                    output.Link(parentId, node.Id, "CONTAINS", documentEvidence);
                }
            }
            foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SyntaxBudget()) break;
                ct.ThrowIfCancellationRequested();
                var info = model.GetSymbolInfo(identifier, ct);
                if (info.Symbol is not { } referenced) continue;
                var enclosing = model.GetEnclosingSymbol(identifier.SpanStart, ct);
                if (enclosing is null) continue;
                output.Add(SymbolNode(enclosing, path, null, root));
                output.Add(SymbolNode(referenced, path, null, root));
                output.Link(SymbolId(enclosing, root), SymbolId(referenced, root), "REFERENCES",
                    output.Observe(SymbolId(referenced, root), "reference", referenced.ToDisplayString(), path, 1, "complete", Line(identifier)),
                    properties: new() { ["line"] = Line(identifier), ["start"] = identifier.SpanStart });
            }
            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!SyntaxBudget()) break;
                ct.ThrowIfCancellationRequested();
                var enclosing = model.GetEnclosingSymbol(invocation.SpanStart, ct);
                if (enclosing is null) continue;
                var info = model.GetSymbolInfo(invocation, ct);
                var targets = info.Symbol is { } resolved ? new[] { resolved } : info.CandidateSymbols.ToArray();
                var callerId = SymbolId(enclosing, root);
                output.Add(SymbolNode(enclosing, path, null, root));
                foreach (var target in targets)
                {
                    var confidence = info.Symbol is null ? 0.5 : 1;
                    output.Add(SymbolNode(target, path, null, root));
                    var ev = output.Observe(callerId, "call", target.ToDisplayString(), path, confidence, info.Symbol is null ? "candidate" : "complete", Line(invocation));
                    output.Link(callerId, SymbolId(target, root), "CALLS", ev, confidence,
                        new() { ["line"] = Line(invocation), ["resolved"] = info.Symbol is not null, ["dispatch"] = target.IsVirtual ? "virtual-candidate" : "static-target" });
                }
                if (targets.Length == 0)
                    output.Observe(callerId, "unresolved_call", invocation.Expression.ToString(), path, 0.3, "partial", Line(invocation));
            }
            foreach (var operation in syntax.DescendantNodes().Where(n => n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AssignmentExpressionSyntax or ReturnStatementSyntax or ThrowStatementSyntax or AwaitExpressionSyntax))
            {
                if (!SyntaxBudget()) break;
                var id = Identity.For("syntax", path + ":" + operation.SpanStart + ":" + operation.RawKind);
                var evidence = output.Observe(id, "syntax_operation", operation.Kind().ToString(), path, line: Line(operation));
                output.Add(new(id, "SyntaxOperation", operation.Kind().ToString(), path, new() { ["line"] = Line(operation), ["start"] = operation.SpanStart, ["length"] = operation.Span.Length }));
                output.Link(documentId, id, "CONTAINS", evidence);
                if (operation is BaseObjectCreationExpressionSyntax && model.GetSymbolInfo(operation, ct).Symbol is IMethodSymbol constructor)
                {
                    output.Add(SymbolNode(constructor, path, null, root));
                    output.Link(id, SymbolId(constructor, root), "CALLS", evidence, properties: new() { ["resolved"] = true, ["dispatch"] = "constructor" });
                }
            }
        }
        new CSharpFlowEngine(output, new(compilation, root, syntaxTruncated), models, options, ct).Run();
        return output.Result();
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    internal static string SymbolId(ISymbol symbol, string root)
    {
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        var localSuffix = symbol.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Label
            ? ":" + declaration?.SyntaxTree.FilePath + ":" + declaration?.Span.Start : "";
        return Identity.For("symbol", root + ":" + symbol.ContainingAssembly?.Name + ":" +
            (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + localSuffix);
    }
    internal static MapNode SymbolNode(ISymbol symbol, string path, SyntaxNode? declaration, string root)
    {
        var location = declaration?.GetLocation() ?? symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var sourcePath = location?.SourceTree?.FilePath;
        var kind = symbol.Kind switch { SymbolKind.NamedType => "Type", SymbolKind.Namespace => "Namespace", SymbolKind.Method => "Function", SymbolKind.Parameter => "Parameter", _ => "Symbol" };
        return new(SymbolId(symbol, root), kind, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), sourcePath,
            new() { ["language"] = "C#", ["symbolKind"] = symbol.Kind.ToString(), ["accessibility"] = symbol.DeclaredAccessibility.ToString(), ["external"] = sourcePath is null,
                ["line"] = location?.GetLineSpan().StartLinePosition.Line + 1, ["start"] = location?.SourceSpan.Start, ["length"] = location?.SourceSpan.Length, ["analysisContext"] = "safe-runtime-references", ["generated"] = sourcePath?.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ?? false });
    }
}

/// <summary>Shared bounded, non-executing extraction support for language adapters.</summary>
public sealed class ExtractionOutput(string snapshot, string collector, DateTimeOffset? observedAt = null, string? logicalSourceContextId = null,
    int? maximumGraphRecords = null)
{
    private readonly Dictionary<string, MapNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MapEdge> edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Evidence> observations = new(StringComparer.Ordinal);
    public List<Finding> Findings { get; } = [];
    private readonly int? graphLimit = ValidateGraphLimit(maximumGraphRecords);
    public string? LogicalSourceContextId => logicalSourceContextId;
    private static int? ValidateGraphLimit(int? value)
    { if (value is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(value)); return value; }
    private void ReserveRecord(string kind)
    {
        if (graphLimit is { } limit && (long)nodes.Count + edges.Count + observations.Count + Findings.Count >= limit)
            throw new SourceGraphLimitException(limit, kind);
    }
    public void AddFinding(Finding finding) { ReserveRecord("finding"); Findings.Add(finding); }
    public void Add(MapNode node)
    {
        if (!nodes.TryGetValue(node.Id, out var old))
        { ReserveRecord("node"); nodes[node.Id] = node; }
        else if (old.Path is null ||
            old.Properties.ContainsKey("referenceOnly") || old.Properties.TryGetValue("metadataComplete", out var complete) && complete is false)
            nodes[node.Id] = node;
    }
    public string Document(string path, string? hash = null)
    {
        var id = Identity.For("source", path);
        var properties = new Dictionary<string, object?> { ["sha256"] = hash, ["trust"] = "untrusted-data" };
        if (logicalSourceContextId is not null) properties["analysisContextId"] = logicalSourceContextId;
        Add(new(id, "SourceDocument", Path.GetFileName(path), path, properties));
        Observe(id, "source_document", path, path, hash: hash);
        return id;
    }
    public string Observe(string subject, string predicate, string value, string path, double confidence = 1, string completeness = "complete", int? line = null, string? hash = null)
    {
        var uri = line is null ? path : path + "#L" + line;
        var id = Identity.For("observation", snapshot + ":" + collector + ":" + subject + ":" + predicate + ":" + uri + ":" + value);
        if (!observations.ContainsKey(id)) ReserveRecord("observation");
        observations[id] = new(id, snapshot, subject, predicate, value, collector, uri, observedAt ?? DateTimeOffset.UtcNow, confidence, completeness, EvidenceHash: hash)
            { Scope = logicalSourceContextId ?? snapshot };
        return id;
    }
    public void Error(string path, string code, string detail, int? line = null)
    {
        var subject = Document(path);
        var id = Observe(subject, "diagnostic", detail, path, 1, "partial", line);
        observations[id] = observations[id] with { ErrorCode = code, ErrorDetail = detail };
    }
    public void Link(string from, string to, string kind, string evidence, double confidence = 1, Dictionary<string, object?>? properties = null)
    {
        var id = Identity.For("edge", from + ":" + kind + ":" + to + ":" + evidence);
        if (!edges.ContainsKey(id)) ReserveRecord("edge");
        edges[id] = new(id, from, to, kind, evidence, confidence, properties);
    }
    public AnalysisResult Result() => new(nodes.Values.ToArray(), edges.Values.ToArray(), observations.Values.ToArray(), Findings);
}

public static class SourceFiles
{
    public static byte[] ReadBounded(string path, CancellationToken ct)
    {
        const int maxBytes = 2 * 1024 * 1024;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
        if (input.Length > maxBytes) throw new IOException("Source file exceeds 2 MiB parser budget.");
        using var result = new MemoryStream((int)input.Length);
        var buffer = new byte[32768];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Length + read > maxBytes) throw new IOException("Source grew beyond 2 MiB parser budget while reading.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    public static IEnumerable<string> Enumerate(string root, string[] extensions, ExtractionOutput output, CancellationToken ct)
    {
        const int maxFiles = 2000;
        const long maxBytes = 2 * 1024 * 1024;
        const long maxTotalBytes = 64 * 1024 * 1024;
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0; long bytes = 0; var directoryCount = 0;
        var excluded = new HashSet<string>([".git", "bin", "obj", "node_modules", ".vs"], StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (++directoryCount > 50_000) { output.Error(root, "directory_budget", "Source extraction stopped at 50,000 directories."); yield break; }
            foreach (var path in EnumerateDirectory(directory, output))
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Error(path, "metadata_error", ex.Message); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { output.Observe(Identity.For("source", path), "excluded", "reparse point not followed", path, 1, "partial"); continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!excluded.Contains(Path.GetFileName(path)))
                    {
                        if (pending.Count >= 50_000) { output.Error(root, "directory_budget", "Pending directory budget of 50,000 reached."); yield break; }
                        pending.Push(path);
                    }
                    continue;
                }
                if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                long length;
                try { length = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Error(path, "metadata_error", ex.Message); continue; }
                if (length > maxBytes) { output.Error(path, "file_budget", "Source file exceeds 2 MiB parser budget."); continue; }
                if (++count > maxFiles || (bytes += length) > maxTotalBytes) { output.Error(root, "source_budget", "Source extraction stopped at 2,000 files or 64 MiB source bytes."); yield break; }
                yield return path;
            }
        }
    }
    private static IEnumerable<string> EnumerateDirectory(string directory, ExtractionOutput output)
    {
        IEnumerator<string>? iterator = null;
        try { iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Error(directory, "enumeration_error", ex.Message); }
        if (iterator is null) yield break;
        using (iterator)
        {
            while (true)
            {
                string? path = null;
                try { if (iterator.MoveNext()) path = iterator.Current; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Error(directory, "enumeration_error", ex.Message); }
                if (path is null) yield break;
                yield return path;
            }
        }
    }
}
