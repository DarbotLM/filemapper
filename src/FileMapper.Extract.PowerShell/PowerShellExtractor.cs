using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using System.Management.Automation.Language;
using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Extract.PowerShell;

public sealed record PowerShellAnalysisOptions(IReadOnlyList<string>? AnalyzerReportPaths = null);

/// <summary>Parser and offline metadata only: never starts a runspace, evaluates data, or imports a module.</summary>
public sealed class PowerShellExtractor(PowerShellAnalysisOptions? options = null) : ICodeExtractor
{
    public string Language => "PowerShell";
    public Task<AnalysisResult> ExtractAsync(string snapshotId, string root, CancellationToken cancellationToken) =>
        Task.Run(() => Extract(snapshotId, Path.GetFullPath(root), options ?? new(), cancellationToken), cancellationToken);
    public Task<SuppliedSourceAnalysis> ExtractDocumentsAsync(string snapshotId, string contextId,
        IReadOnlyList<SuppliedSourceDocument> documents, SuppliedSourceOptions? inputOptions = null,
        DateTimeOffset? observedAt = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var startedAt = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.AnalyzerReportPaths is { Count: > 0 })
            throw new InvalidOperationException("Supplied-document extraction does not admit analyzer report paths or read external reports.");
        var batch = SuppliedSourceBatch.Prepare(snapshotId, contextId, documents, inputOptions, [".ps1", ".psm1", ".psd1"], cancellationToken);
        var output = new ExtractionOutput(snapshotId, "powershell-parser", observedAt, contextId, (inputOptions ?? new()).MaxGraphRecords);
        foreach (var rejected in batch.Documents.Where(x => x.Status != "admitted"))
            output.Error(rejected.LogicalPath, "supplied_source_" + rejected.Status, rejected.Detail ?? rejected.Status);
        foreach (var source in batch.Admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ParseInput is synchronous and has no cancellation parameter. Check immediately around the bounded parser call.
            var ast = Parser.ParseInput(source.Decoded.Text, source.LogicalPath, out var tokens, out var errors);
            cancellationToken.ThrowIfCancellationRequested();
            var document = output.Document(source.LogicalPath, source.Decoded.SourceSha256);
            output.Observe(document, "parser", $"{tokens.Length} tokens; {errors.Length} parse errors; {PowerShellCommandCatalog.Version}; no execution or module loading", source.LogicalPath);
            foreach (var error in errors) output.Error(source.LogicalPath, error.ErrorId, error.Message, error.Extent.StartLineNumber);
            if (SuppliedSourceBatch.Extension(source.LogicalPath).Equals(".psd1", StringComparison.OrdinalIgnoreCase))
                PowerShellManifest.Read(ast, source.LogicalPath, document, output);
            else new PowerShellFlowAnalyzer(snapshotId, source.LogicalPath, document, output, cancellationToken).Analyze(ast);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new SuppliedSourceAnalysis(output.Result(), contextId,
            "Supplied verified bytes only; parser and versioned offline command/flow models; logical dependency references are unverified; no runspace, module/project dependency loading, analyzer reports or source execution. Semantic coverage remains partial.",
            batch.Coverage, batch.Documents)
        { StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow, SuppliedObservedAt = observedAt };
    }, cancellationToken);
    private static AnalysisResult Extract(string snapshot, string root, PowerShellAnalysisOptions options, CancellationToken ct)
    {
        var output = new ExtractionOutput(snapshot, "powershell-parser");
        var hashes = new Dictionary<string,string>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        foreach(var path in SourceFiles.Enumerate(root,[".ps1",".psm1",".psd1"],output,ct).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes=SourceFiles.ReadBounded(path,ct);var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));hashes[path]=hash;
                using var reader=new StreamReader(new MemoryStream(bytes),Encoding.UTF8,true);
                var ast=Parser.ParseInput(reader.ReadToEnd(),path,out var tokens,out var errors);
                var document=output.Document(path,hash);
                output.Observe(document,"parser",$"{tokens.Length} tokens; {errors.Length} parse errors; {PowerShellCommandCatalog.Version}; no execution or module loading",path);
                foreach(var error in errors)output.Error(path,error.ErrorId,error.Message,error.Extent.StartLineNumber);
                if(Path.GetExtension(path).Equals(".psd1",StringComparison.OrdinalIgnoreCase))PowerShellManifest.Read(ast,path,document,output);
                else new PowerShellFlowAnalyzer(snapshot,path,document,output,ct).Analyze(ast);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){output.Error(path,"read_error",ex.Message);}
        }
        foreach(var report in options.AnalyzerReportPaths??[])
            PSScriptAnalyzerReport.Import(Path.GetFullPath(report),root,hashes,output,ct);
        return output.Result();
    }
    internal static string Id(string path,Ast ast,string kind,string context="")=>Identity.For(kind,path+":"+ast.Extent.StartOffset+":"+ast.Extent.EndOffset+":"+context);
    internal static Dictionary<string,object?> Properties(Ast ast,string kind)=>new()
    {
        ["language"]="PowerShell",["astKind"]=ast.GetType().Name,["symbolKind"]=kind,["line"]=ast.Extent.StartLineNumber,
        ["column"]=ast.Extent.StartColumnNumber,["start"]=ast.Extent.StartOffset,["length"]=ast.Extent.EndOffset-ast.Extent.StartOffset,
        ["modelVersion"]=PowerShellCommandCatalog.Version
    };
}
