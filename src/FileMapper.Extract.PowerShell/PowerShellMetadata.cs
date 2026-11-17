using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using System.Management.Automation.Language;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileMapper.Extract.PowerShell;

internal static class PowerShellManifest
{
    internal static void Read(ScriptBlockAst ast,string path,string document,ExtractionOutput output)
    {
        var table=ast.FindAll(n=>n is HashtableAst,false).OfType<HashtableAst>().FirstOrDefault();
        if(table is null){output.Error(path,"manifest_not_literal","Module manifest has no static hashtable; it was not evaluated.");return;}
        foreach(var pair in table.KeyValuePairs)
        {
            var key=(pair.Item1 as StringConstantExpressionAst)?.Value;
            if(key is null)continue;
            if(new[]{"RootModule","ModuleToProcess","NestedModules","RequiredModules","ScriptsToProcess","RequiredAssemblies"}.Contains(key,StringComparer.OrdinalIgnoreCase))
            {
                var values=Literals(pair.Item2).ToArray();
                foreach(var value in values)Dependency(output,document,path,pair.Item2,value,"manifest:"+key);
                if(values.Length==0)output.Observe(document,"manifest_dependency_unresolved",key+" is dynamic or empty; no evaluation performed",path,.5,"partial",pair.Item2.Extent.StartLineNumber);
            }
        }
    }
    private static IEnumerable<string> Literals(Ast ast)
    {
        switch(ast)
        {
            case StringConstantExpressionAst text:yield return text.Value;break;
            case HashtableAst table:
                foreach(var pair in table.KeyValuePairs)if((pair.Item1 as StringConstantExpressionAst)?.Value.Equals("ModuleName",StringComparison.OrdinalIgnoreCase)==true)foreach(var item in Literals(pair.Item2))yield return item;
                break;
            case PipelineAst pipeline:foreach(var part in pipeline.PipelineElements)foreach(var item in Literals(part))yield return item;break;
            case CommandExpressionAst expression:foreach(var item in Literals(expression.Expression))yield return item;break;
            case ArrayLiteralAst array:foreach(var part in array.Elements)foreach(var item in Literals(part))yield return item;break;
            case ArrayExpressionAst array:foreach(var item in Literals(array.SubExpression))yield return item;break;
            case StatementBlockAst block:foreach(var part in block.Statements)foreach(var item in Literals(part))yield return item;break;
        }
    }
    internal static void Dependency(ExtractionOutput output,string document,string path,Ast ast,string? value,string kind)
    {
        if(string.IsNullOrWhiteSpace(value)){output.Observe(document,"dependency_unresolved",kind+": dynamic dependency",path,.4,"partial",ast.Extent.StartLineNumber);return;}
        var isPath=value.Contains('/')||value.Contains('\\')||new[]{".ps1",".psm1",".psd1",".dll"}.Contains(Path.GetExtension(value),StringComparer.OrdinalIgnoreCase);
        string? targetPath=null;
        var logical=output.LogicalSourceContextId is not null;
        if(isPath&&logical)targetPath=LogicalTarget(path,value);
        else if(isPath)try{targetPath=Path.GetFullPath(value,Path.GetDirectoryName(path)!);}catch(Exception ex)when(ex is ArgumentException or NotSupportedException){output.Error(path,"dependency_path_invalid",ex.Message,ast.Extent.StartLineNumber);}
        var unresolvedLogical=isPath&&logical&&targetPath is null;
        var id=unresolvedLogical?Identity.For("ps-logical-dependency",output.LogicalSourceContextId+":"+path+":"+value):targetPath is null?Identity.For("ps-module",value.ToLowerInvariant()):Identity.For("source",targetPath);
        var properties=new Dictionary<string,object?>{["language"]="PowerShell",["resolution"]="literal-unverified",["referenceOnly"]=true,["dependencyKind"]=kind};
        if(logical){properties["analysisContextId"]=output.LogicalSourceContextId;properties["referenceBasis"]="logical-source-only";properties["targetMembershipVerified"]=false;properties["literalReference"]=value;}
        output.Add(new(id,unresolvedLogical?"DependencyReference":targetPath is null?"Package":"SourceDocument",value,targetPath,properties));
        var ev=output.Observe(document,"static_dependency",kind+": "+value,path,.8,"partial",ast.Extent.StartLineNumber);
        output.Link(document,id,"IMPORTS",ev,.8,new(){["dependencyKind"]=kind,["executesCode"]=false});
    }
    private static string? LogicalTarget(string path,string value)
    {
        if(!Uri.TryCreate(path,UriKind.Absolute,out var source)||source.Scheme!="filemapper-source"||source.Query.Length!=0||source.Fragment.Length!=0)return null;
        if(!Uri.TryCreate(source,value.Replace('\\','/'),out var target)||target.Scheme!=source.Scheme||target.Authority!=source.Authority||target.Query.Length!=0||target.Fragment.Length!=0)return null;
        // Never resolve outside the immutable repository revision represented by the first path segment.
        var boundary=source.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var targetBoundary=target.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return boundary is not null&&boundary==targetBoundary?target.AbsoluteUri:null;
    }
}

/// <summary>Imports precomputed, untrusted PSScriptAnalyzer JSON. Never loads or runs an analyzer.</summary>
public static class PSScriptAnalyzerReport
{
    public static AnalysisResult Import(string snapshotId,string reportPath,string root,IReadOnlyDictionary<string,string> sourceHashes,CancellationToken cancellationToken=default)
    {var output=new ExtractionOutput(snapshotId,"powershell-external-diagnostics");Import(Path.GetFullPath(reportPath),Path.GetFullPath(root),sourceHashes,output,cancellationToken);return output.Result();}
    internal static void Import(string reportPath,string root,IReadOnlyDictionary<string,string> sourceHashes,ExtractionOutput output,CancellationToken ct)
    {
        try
        {
            var bytes=SourceFiles.ReadBounded(reportPath,ct);var reportHash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            var reportId=output.Document(reportPath,reportHash);
            using var reader=new StreamReader(new MemoryStream(bytes),Encoding.UTF8,true);
            using var json=JsonDocument.Parse(reader.ReadToEnd(),new JsonDocumentOptions{MaxDepth=32});
            var top=json.RootElement;var records=top.ValueKind==JsonValueKind.Array?top:top.GetProperty("Diagnostics");
            if(records.ValueKind!=JsonValueKind.Array)throw new JsonException("Diagnostics must be an array.");
            int count=0;
            foreach(var diagnostic in records.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();if(++count>5000){output.Error(reportPath,"external_diagnostic_budget","Report stopped at 5,000 diagnostics.");break;}
                string? Get(string key)=>diagnostic.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
                var source=Get("ScriptPath")??Get("ScriptName");var rule=Get("RuleName");var message=Get("Message");
                if(source is null||rule is null||message is null){output.Error(reportPath,"external_diagnostic_invalid","Diagnostic requires ScriptPath/ScriptName, RuleName and Message.");continue;}
                var fullPath=Path.GetFullPath(source,root);var relative=Path.GetRelativePath(root,fullPath);
                if(relative==".."||relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)||Path.IsPathRooted(relative)||!sourceHashes.TryGetValue(fullPath,out var sourceHash))
                {output.Error(reportPath,"external_source_out_of_scope","External diagnostic does not identify a hashed source in this analysis: "+source);continue;}
                bool matched=false;
                if(top.ValueKind==JsonValueKind.Object&&top.TryGetProperty("SourceHashes",out var hashes)&&hashes.TryGetProperty(source,out var declared))
                {
                    if(declared.ValueKind!=JsonValueKind.String||!string.Equals(declared.GetString(),sourceHash,StringComparison.OrdinalIgnoreCase)){output.Error(reportPath,"external_source_hash_mismatch","Rejected stale diagnostic for "+source);continue;}
                    matched=true;
                }
                int line=diagnostic.TryGetProperty("Line",out var lineValue)&&lineValue.ValueKind==JsonValueKind.Number&&lineValue.TryGetInt32(out var parsed)?Math.Max(1,parsed):1;
                if(diagnostic.TryGetProperty("Extent",out var extent)&&extent.ValueKind==JsonValueKind.Object&&extent.TryGetProperty("StartLineNumber",out var startLine)&&startLine.TryGetInt32(out var nativeLine))line=Math.Max(1,nativeLine);
                var severity=(Get("Severity")??"warning").ToLowerInvariant();
                if(diagnostic.TryGetProperty("Severity",out var severityValue)&&severityValue.ValueKind==JsonValueKind.Number&&severityValue.TryGetInt32(out var number))severity=number switch{0=>"information",2 or 3=>"error",_=>"warning"};
                if(severity=="parseerror")severity="error";
                if(!new[]{"error","warning","information"}.Contains(severity))severity="warning";
                var subject=output.Document(fullPath,sourceHash);var evidence=output.Observe(subject,"external_psscriptanalyzer",$"rule={rule}; reportSha256={reportHash}; sourceSha256={sourceHash}; sourceHashMatch={matched}; report={reportPath}",reportPath,.6,"partial",line,reportHash);
                output.Link(reportId,subject,"REFERENCES",evidence,.6,new(){["sourceHashMatched"]=matched,["provenance"]="untrusted-precomputed-report"});
                output.AddFinding(new(Identity.For("finding",reportHash+":"+count),rule,severity,"PSScriptAnalyzer: "+rule,fullPath,line,message,.6,[evidence])
                {Status="external-diagnostic",ModelVersion="psscriptanalyzer-json-adapter/1",Completeness="partial",Assumptions=["Precomputed diagnostic is untrusted external evidence, not a FileMapper taint route.",matched?"Declared source SHA-256 matched the analyzed bytes.":"Report supplied no verified source hash; source revision association is unverified."]});
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException){output.Error(reportPath,"external_report_invalid",ex.Message);}
    }
}
