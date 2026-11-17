using FileMapper.Domain;
using FileMapper.Extract.PowerShell;
using FileMapper.Storage;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length!=3)throw new ArgumentException("Usage: corpus.json fixture-directory report.json");
var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true};
var cases=JsonSerializer.Deserialize<Case[]>(File.ReadAllText(args[0]),options)!;
var fixture=Path.GetFullPath(args[1]);Directory.CreateDirectory(fixture);
var store=new MapStore(Path.Combine(fixture,"acceptance.db"));store.Initialize();
var results=new List<object>();int tp=0,tn=0,fp=0,fn=0;var failures=new List<string>();
foreach(var item in cases)
{
 var root=Path.Combine(fixture,item.Id);Directory.CreateDirectory(root);var source=Path.Combine(root,"case.ps1");File.WriteAllText(source,item.Script);
 var snapshot=store.CreateSnapshot(root);
 var result=await new PowerShellExtractor().ExtractAsync(snapshot.Id,root,default);
 store.SaveAnalysis(snapshot.Id,result);
 var actual=result.Findings.Select(x=>x.RuleId).Distinct().Order().ToArray();var expected=item.Rules.Order().ToArray();
 bool positive=actual.Length>0,truth=item.Label=="vulnerable";if(positive&&truth)tp++;else if(positive)fp++;else if(truth)fn++;else tn++;
 var issues=Integrity(result);if(!actual.SequenceEqual(expected))issues.Add("rules expected="+string.Join(',',expected)+" actual="+string.Join(',',actual));
 if(item.RequiredSourceKind is not null&&!result.Findings.Any(f=>f.Route.Count>0&&f.Route[0].Reason==item.RequiredSourceKind))issues.Add("missing required witness source kind: "+item.RequiredSourceKind);
 if(item.RequireTruncated&&(result.Findings.Count==0||result.Findings.Any(f=>!f.Truncated||f.Completeness!="partial"||string.IsNullOrWhiteSpace(f.TruncationReason))))issues.Add("expected every finding to disclose partial trace truncation");
 if(item.ExpectedFindingCount is int count&&result.Findings.Count!=count)issues.Add("finding count expected="+count+" actual="+result.Findings.Count);
 if(File.Exists(Path.Combine(root,"fixture-executed.txt")))issues.Add("Repository script executed");
 if(issues.Count>0)failures.Add(item.Id+": "+string.Join("; ",issues));
 results.Add(new{item.Id,item.Label,item.RequiredSourceKind,item.RequireTruncated,item.ExpectedFindingCount,ExpectedRules=expected,ActualRules=actual,Passed=issues.Count==0,Issues=issues,Findings=result.Findings,NodeCount=result.Nodes.Count,EdgeCount=result.Edges.Count});
}
var metadataRoot=Path.Combine(fixture,"metadata");Directory.CreateDirectory(metadataRoot);
File.WriteAllText(Path.Combine(metadataRoot,"module.psd1"),"@{ RootModule='module.psm1'; RequiredModules=@('Example', @{ModuleName='Other';ModuleVersion='1.0'}); NestedModules=@('nested.psm1') }");
File.WriteAllText(Path.Combine(metadataRoot,"module.psm1"),"Import-Module 'External'\n. './helper.ps1'\n");
var metadata=await new PowerShellExtractor().ExtractAsync("ps-metadata",metadataRoot,default);
if(metadata.Edges.Count(e=>e.Kind=="IMPORTS")!=6)failures.Add("metadata: expected 6 static dependencies");
failures.AddRange(Integrity(metadata).Select(x=>"metadata: "+x));
var adapterRoot=Path.Combine(fixture,"adapter");Directory.CreateDirectory(adapterRoot);var adapterSource=Path.Combine(adapterRoot,"case.ps1");File.WriteAllText(adapterSource,"Write-Output 'safe'");
var hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(adapterSource)));var report=Path.Combine(adapterRoot,"analyzer.json");
File.WriteAllText(report,JsonSerializer.Serialize(new{SourceHashes=new Dictionary<string,string>{{"case.ps1",hash}},Diagnostics=new[]{new{ScriptPath="case.ps1",RuleName="PSAvoidUsingWriteHost",Message="Imported diagnostic",Severity="Warning",Line=1}}}));
var imported=await new PowerShellExtractor(new([report])).ExtractAsync("ps-report",adapterRoot,default);
if(imported.Findings.Count!=1||imported.Findings[0].Status!="external-diagnostic"||!imported.Findings[0].Assumptions.Any(x=>x.Contains("matched")))failures.Add("adapter: valid report provenance not retained");
File.WriteAllText(report,JsonSerializer.Serialize(new[]{new{ScriptPath="case.ps1",RuleName="PSNativeShape",Message="Native JSON shape",Severity=2,Extent=new{StartLineNumber=3}}}),System.Text.Encoding.Unicode);
var nativeReport=await new PowerShellExtractor(new([report])).ExtractAsync("ps-native-report",adapterRoot,default);
if(nativeReport.Findings.Count!=1||nativeReport.Findings[0].Severity!="error"||nativeReport.Findings[0].Line!=3||!nativeReport.Findings[0].Assumptions.Any(x=>x.Contains("unverified")))failures.Add("adapter: native enum/extent/UTF16 shape lost");
File.WriteAllText(report,JsonSerializer.Serialize(new{SourceHashes=new Dictionary<string,string>{{"case.ps1","incorrect"}},Diagnostics=new[]{new{ScriptPath="case.ps1",RuleName="PSExample",Message="stale",Line=1},new{ScriptPath="../outside.ps1",RuleName="PSExample",Message="outside",Line=1}}}));
var rejected=await new PowerShellExtractor(new([report])).ExtractAsync("ps-report-stale",adapterRoot,default);
if(rejected.Findings.Count!=0||!rejected.Observations.Any(x=>x.ErrorCode=="external_source_hash_mismatch")||!rejected.Observations.Any(x=>x.ErrorCode=="external_source_out_of_scope"))failures.Add("adapter: stale/outside report accepted");
var partialRoot=Path.Combine(fixture,"partial");Directory.CreateDirectory(partialRoot);File.WriteAllText(Path.Combine(partialRoot,"partial.ps1"),"function Incomplete { param($value)\niex $value\n");
var partial=await new PowerShellExtractor().ExtractAsync("ps-partial",partialRoot,default);
if(partial.Nodes.Count==0||!partial.Observations.Any(x=>x.ErrorCode is not null))failures.Add("incomplete syntax lost diagnostics");
var boundedRoot=Path.Combine(fixture,"bounded-route");Directory.CreateDirectory(boundedRoot);
File.WriteAllText(Path.Combine(boundedRoot,"case.ps1"),"$v0 = Read-Host\n"+string.Join("\n",Enumerable.Range(1,80).Select(i=>$"$v{i} = $v{i-1}"))+"\niex $v80");
var boundedSnapshot=store.CreateSnapshot(boundedRoot);var bounded=await new PowerShellExtractor().ExtractAsync(boundedSnapshot.Id,boundedRoot,default);
store.SaveAnalysis(boundedSnapshot.Id,bounded);
if(!bounded.Findings.Any(f=>f.Truncated&&f.Route.Count<=64&&f.TruncationReason is not null&&f.Completeness=="partial"))failures.Add("route truncation not visible");
failures.AddRange(Integrity(bounded).Select(x=>"bounded: "+x));
var final=new{ModelVersion=PowerShellCommandCatalog.Version,CaseCount=cases.Length,TruePositive=tp,TrueNegative=tn,FalsePositive=fp,FalseNegative=fn,Precision=tp+fp==0?0:(double)tp/(tp+fp),Recall=tp+fn==0?0:(double)tp/(tp+fn),ManifestDependencies=metadata.Edges.Count(e=>e.Kind=="IMPORTS"),AnalyzerProvenancePassed=imported.Findings.Count==1&&rejected.Findings.Count==0,Failures=failures,Cases=results};
File.WriteAllText(args[2],JsonSerializer.Serialize(final,options));Console.WriteLine(JsonSerializer.Serialize(new{final.CaseCount,final.TruePositive,final.TrueNegative,final.FalsePositive,final.FalseNegative,final.Precision,final.Recall,final.ManifestDependencies,Failures=failures},options));
return failures.Count==0?0:1;
static List<string> Integrity(AnalysisResult result)
{
 var errors=new List<string>();var nodes=result.Nodes.ToDictionary(x=>x.Id);var edges=result.Edges.ToDictionary(x=>x.Id);var evidence=result.Observations.Select(x=>x.Id).ToHashSet();
 foreach(var edge in result.Edges)if(!nodes.ContainsKey(edge.FromId)||!nodes.ContainsKey(edge.ToId)||!evidence.Contains(edge.EvidenceId))errors.Add("dangling graph edge");
 foreach(var finding in result.Findings)
 {
  if(finding.EvidenceIds.Any(x=>!evidence.Contains(x)))errors.Add("missing finding evidence");
  for(int i=0;i<finding.Route.Count;i++)
  {var step=finding.Route[i];if(!nodes.TryGetValue(step.NodeId,out var node)){errors.Add("missing route node");continue;}
   if(node.Path!=step.Path||Convert.ToInt32(node.Properties["line"])!=step.Line||Convert.ToInt32(node.Properties["column"])!=step.Column)errors.Add("route location mismatch");
   if(i==0&&step.EdgeId is not null)errors.Add("first route has edge");
   if(i>0&&(step.EdgeId is null||!edges.TryGetValue(step.EdgeId,out var edge)||edge.FromId!=finding.Route[i-1].NodeId||edge.ToId!=step.NodeId))errors.Add("broken ordered route");
  }
  if(finding.Route.Count>0&&(finding.Route[^1].Path!=finding.Path||finding.Route[^1].Line!=finding.Line))errors.Add("sink location mismatch");
 }
 return errors.Distinct().ToList();
}
record Case(string Id,string Label,string Script,string[] Rules,string? RequiredSourceKind=null,bool RequireTruncated=false,int? ExpectedFindingCount=null);
