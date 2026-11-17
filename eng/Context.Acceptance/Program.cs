// Native regression automation derived from the sealed Context review; no bakeoff evidence is changed.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;
using Microsoft.Data.Sqlite;

if(args.Length!=1)throw new ArgumentException("Expected a new, empty fixture directory. This runner preserves its evidence there.");
var root=Path.GetFullPath(args[0]);
if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new ArgumentException("Fixture directory must be empty.");
Directory.CreateDirectory(root);
var checks=new List<object>();int failures=0;
void Check(string name,bool passed,object? detail=null){checks.Add(new{name,passed,detail});if(!passed)failures++;Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");}
var runtime = new[]{typeof(MapStore).Assembly,typeof(Exporter).Assembly,typeof(ContextPackage).Assembly}.Select(assembly => new {
    name=assembly.GetName().Name, version=assembly.GetName().Version!.ToString(), path=assembly.Location,
    sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
}).ToArray();
void Run(string name,Action test){try{test();}catch(Exception error){Check(name,false,new{exception=error.GetType().FullName,error.Message,error.StackTrace});}}
string Json<T>(T item)=>JsonSerializer.Serialize(item,Exporter.Json);
int Bytes(ContextPackage p)=>JsonSerializer.SerializeToUtf8Bytes(p,Exporter.Json).Length;
void Save<T>(string name,T item)=>File.WriteAllText(Path.Combine(root,name),Json(item));
string Hash(string s)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
void ExpectArgument(string name,Action test){try{test();Check(name,false,"No exception");}catch(ArgumentException error){Check(name,true,error.GetType().Name);}}
MapNode Node(string id,string name,string? path=null,string kind="File")=>new(id,kind,name,path,new(){["privateProperty"]="NODE_PROPERTY_MUST_NOT_APPEAR"});
var store=new MapStore(Path.Combine(root,"review.db"));store.Initialize();var snapshot=store.CreateSnapshot(root);
var fixedTime=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
Evidence EvidenceFor(string id,string subject,string predicate="recorded",string value="fixture evidence",int minute=0)=>new(id,snapshot.Id,subject,predicate,value,"independent-fixture","fixture://"+id,fixedTime.AddMinutes(minute),.85,"complete",CollectorVersion:"review/v1",EvidenceHash:"recorded-hash-"+id);
var actualSource=Path.Combine(root,"source-must-not-be-read.cs");File.WriteAllText(actualSource,"CURRENT_SOURCE_MUST_NOT_APPEAR");
var nodes=new List<MapNode>{Node("anchor","AsterEngine",actualSource),Node("owner","maintainer","/recorded/team","Identity"),Node("commit","historical change",null,"Commit"),Node("dependency","library",null,"Package"),Node("checks","spec checks"),Node("leaf","second hop"),Node("missing-evidence-neighbor","bad citation"),Node("decoy","unrelated decoy"),Node("contains-only","unrelated folder child"),Node("unicode","形状猫\"\\\n", "/記録/🌌"),Node("alias","physical alias",actualSource)};
var edges=new List<MapEdge>();var observations=new List<Evidence>();
void Edge(string id,string from,string to,string kind){edges.Add(new(id,from,to,kind,"e-"+id,.8));observations.Add(EvidenceFor("e-"+id,from,"relation:"+kind));}
Edge("owns","owner","anchor","OWNS");Edge("history","anchor","commit","MODIFIED_BY");Edge("depends","anchor","dependency","DEPENDS_ON");Edge("tests","checks","anchor","TESTS");Edge("second","dependency","leaf","LINKS");Edge("cycle","leaf","anchor","LINKS");Edge("contain","anchor","contains-only","CONTAINS");
edges.Add(new("broken-evidence","anchor","missing-evidence-neighbor","LINKS","absent-evidence"));edges.Add(new("broken-node","anchor","absent-node","LINKS","e-broken-node"));observations.Add(EvidenceFor("e-broken-node","anchor"));
for(int i=0;i<5;i++)observations.Add(EvidenceFor("anchor-observation-"+i,"anchor",value:i==4?string.Concat(Enumerable.Repeat("猫🌌\"\\",180)):"observation "+i,minute:i+10));
store.Ingest(snapshot.Id,new(nodes,edges,observations,[]));store.FinishSnapshot(snapshot.Id);
string LogicalState()=>Hash(Json(new{snapshot=store.GetSnapshot(snapshot.Id),nodes=store.GetNodes(snapshot.Id,limit:1000),edges=store.GetEdges(snapshot.Id,limit:1000),observations=store.GetObservations(snapshot.Id,limit:1000)}));
var before=LogicalState();var full=ContextBuilder.Build(store,snapshot.Id,"AsterEngine",100000);Save("full-context.json",full);
Run("graph relationship coverage",()=>{
 var ids=full.Items.Select(i=>i.Node.Id).ToHashSet();
 Check("anchor and recorded owner/history/dependency/test neighbors",new[]{"anchor","owner","commit","dependency","checks","leaf"}.All(ids.Contains),ids);
 Check("unrelated, containment-only and malformed neighbors omitted",new[]{"decoy","contains-only","missing-evidence-neighbor","absent-node","alias"}.All(id=>!ids.Contains(id)),ids);
 Check("missing relationship evidence disclosed",full.TruncationReasons.Contains("missing-node-or-edge-evidence"),full.TruncationReasons);
 Check("graph node identities unique",ids.Count==full.Items.Count);
 Check("ranking component sum",full.Items.All(i=>i.Score==i.Components.Values.Sum()));
 Check("score descending then ordinal identity",full.Items.Select(i=>i.Node.Id).SequenceEqual(full.Items.OrderByDescending(i=>i.Score).ThenBy(i=>i.Node.Id,StringComparer.Ordinal).Select(i=>i.Node.Id)));
 Check("exact task anchor outranks linked nonmatches",full.Items[0].Node.Id=="anchor",full.Items.Select(i=>new{i.Node.Id,i.Score}));
 Check("test name explicitly heuristic",full.Items.Single(i=>i.Node.Id=="checks").Reasons.Any(r=>r.Contains("heuristic")&&r.Contains("not proof")));
 Check("generic graph not execution/taint/ownership truth",full.Scope!.Any(s=>s.Contains("not execution, taint, verified ownership")));
 Check("node identity versus file ranking units disclosed",full.Scope!.Any(s=>s.Contains("graph node identities, not distinct physical files")));
 var okay=true;
 foreach(var item in full.Items){var path=item.ConnectionPath;okay &= path.Count is >=1 and <=3 && path[0].Via is null && path[^1].Node.Id==item.Node.Id && path.Select(s=>s.Node.Id).Distinct().Count()==path.Count;
  for(int i=1;i<path.Count;i++){var via=path[i].Via!;var original=edges.Single(e=>e.Id==via.Id);var previous=path[i-1].Node.Id;var next=path[i].Node.Id;okay &= original.FromId==via.FromId&&original.ToId==via.ToId&&original.EvidenceId==via.Evidence.Id&&((via.FromId==previous&&via.ToId==next)||(via.ToId==previous&&via.FromId==next))&&observations.Any(e=>e.Id==via.Evidence.Id);}}
 Check("self-contained bounded paths cite exact stored directed edges",okay);
 Check("incoming ownership keeps stored direction",full.Items.Single(i=>i.Node.Id=="owner").ConnectionPath[^1].Via is {FromId:"owner",ToId:"anchor",Kind:"OWNS"});
 var anchor=full.Items.Single(i=>i.Node.Id=="anchor");Check("latest three subject observations",anchor.Observations.Select(o=>o.Id).SequenceEqual(new[]{"anchor-observation-4","anchor-observation-3","anchor-observation-2"}),anchor.Observations.Select(o=>o.Id));
 var preview=anchor.Observations[0];Check("Unicode preview bounded by 512 code points",preview.ValueTruncated&&preview.ValuePreview.EnumerateRunes().Count()==512,new{runes=preview.ValuePreview.EnumerateRunes().Count(),utf16=preview.ValuePreview.Length});
 Check("observation and preview limits disclosed",full.TruncationReasons.Contains("subject-observations:3")&&full.TruncationReasons.Contains("evidence-value-preview:512"));
 Check("evidence provenance retained",preview.SourceKind=="independent-fixture"&&preview.SourceUri=="fixture://anchor-observation-4"&&preview.CollectorVersion=="review/v1"&&preview.EvidenceHash=="recorded-hash-anchor-observation-4"&&preview.Confidence==.85);
 Check("content marked untrusted and properties absent",full.ContentTrust=="untrusted-data"&&!Json(full).Contains("NODE_PROPERTY_MUST_NOT_APPEAR"));
});
Run("budget validation",()=>{
 foreach(var budget in new[]{256,257,300,511,512,1023,2048,4096,8192,16000,100000}){var result=ContextBuilder.Build(store,snapshot.Id,"AsterEngine",budget);Save("budget-"+budget+".json",result);Check("whole compact UTF8 envelope <= "+budget,Bytes(result)<=budget,new{actualBytes=Bytes(result),items=result.Items.Count,result.Truncated,result.TruncationReasons});Check("truthful omitted-item reason at "+budget,result.Items.Count==full.Items.Count||result.Truncated&&result.TruncationReasons.Contains("byte-budget"));}
 var small=ContextBuilder.Build(store,snapshot.Id,"AsterEngine",256);Check("minimal envelope retains identity/trust/version/completeness",small.SnapshotId==snapshot.Id&&small.ContentTrust=="untrusted-data"&&small.ModelVersion=="context-graph/v1"&&small.Completeness=="complete"&&small.ByteBudget==256&&small.Items.Count==0);
 var unicode=ContextBuilder.Build(store,snapshot.Id,"unicode",100000);Check("escaped Unicode is counted as serialized UTF8 bytes",Json(unicode).Contains("\\u")&&Bytes(unicode)==Encoding.UTF8.GetByteCount(Json(unicode)),new{bytes=Bytes(unicode),chars=Json(unicode).Length});
 ExpectArgument("budget 255 rejected",()=>ContextBuilder.Build(store,snapshot.Id,"a",255));ExpectArgument("budget 100001 rejected",()=>ContextBuilder.Build(store,snapshot.Id,"a",100001));ExpectArgument("null query rejected",()=>ContextBuilder.Build(store,snapshot.Id,null!,1000));ExpectArgument("oversize query rejected",()=>ContextBuilder.Build(store,snapshot.Id,new string('a',513),1000));ExpectArgument("control query rejected",()=>ContextBuilder.Build(store,snapshot.Id,"a\nb",1000));
 var unicodeBoundary=ContextBuilder.Build(store,snapshot.Id,string.Concat(Enumerable.Repeat("🌌",512)),1000);
 Check("512 astral Unicode code points accepted",unicodeBoundary.Items.Count==0);
 ExpectArgument("513 astral Unicode code points rejected",()=>ContextBuilder.Build(store,snapshot.Id,string.Concat(Enumerable.Repeat("🌌",513)),1000));
});
Run("literal and deterministic isolation",()=>{
 Check("repeat result byte deterministic",Json(full)==Json(ContextBuilder.Build(store,snapshot.Id,"AsterEngine",100000)));
 Check("exact identity seed",ContextBuilder.Build(store,snapshot.Id,"anchor",100000).Items[0].Node.Id=="anchor");
 Check("same physical path yields separate graph identities",ContextBuilder.Build(store,snapshot.Id,actualSource,100000).Items.Count(i=>i.Node.Path==actualSource)==2);
 Check("empty query does not infer context",ContextBuilder.Build(store,snapshot.Id,"",100000).Items.Count==0);
 Check("wildcard does not match every node",ContextBuilder.Build(store,snapshot.Id,"%",100000).Items.Count==0);
 var terms=ContextBuilder.Build(store,snapshot.Id,"one two three four five six seven eight nine ten",100000);Check("term bound disclosed",terms.QueryTerms!.Length==8&&terms.TruncationReasons.Contains("query-terms:8"));
 File.WriteAllText(actualSource,"CHANGED_CURRENT_SOURCE_MUST_NOT_APPEAR");var changed=ContextBuilder.Build(store,snapshot.Id,"AsterEngine",100000);File.Delete(actualSource);var deleted=ContextBuilder.Build(store,snapshot.Id,"AsterEngine",100000);
 Check("changing and deleting current source does not affect historical context",Json(full)==Json(changed)&&Json(full)==Json(deleted)&&!Json(full).Contains("CURRENT_SOURCE"));
 Check("context does not mutate logical stored graph",before==LogicalState());
 var foreign=store.CreateSnapshot("foreign");store.Ingest(foreign.Id,new([Node("anchor","FOREIGN_SECRET")],[],[],[]));store.FinishSnapshot(foreign.Id);
 Check("snapshot identities do not leak",Json(full)==Json(ContextBuilder.Build(store,snapshot.Id,"AsterEngine",100000)));
 using var pinned=store.OpenSemanticReader();var original=pinned.ContextNodeById(snapshot.Id,"decoy");store.Ingest(snapshot.Id,new([Node("decoy","changed after reader pin")],[],[],[]));var old=pinned.ContextNodeById(snapshot.Id,"decoy");using var later=store.OpenSemanticReader();var latest=later.ContextNodeById(snapshot.Id,"decoy");
 Check("one read transaction remains pinned across concurrent writer",original==old&&latest!.Name=="changed after reader pin",new{original=original!.Name,old=old!.Name,latest=latest!.Name});
});
Run("two-hop path boundary and insertion-order stability",()=>{
 var chainNodes=new[]{Node("start","ChainAnchor"),Node("one","first neighbor"),Node("two","second neighbor"),Node("three","third neighbor")};
 var chainEdges=new[]{new MapEdge("link1","start","one","DEPENDS_ON","proof1"),new MapEdge("link2","two","one","TESTS","proof2"),new MapEdge("link3","two","three","LINKS","proof3")};
 string LoadChain(bool reversed){var chain=store.CreateSnapshot("chain");var proof=chainEdges.Select((e,i)=>new Evidence(e.EvidenceId,chain.Id,e.FromId,"relation:"+e.Kind,"recorded fixture; not executable instructions","independent-fixture","fixture://chain",fixedTime.AddMinutes(i))).ToArray();store.Ingest(chain.Id,new(reversed?chainNodes.Reverse().ToArray():chainNodes,reversed?chainEdges.Reverse().ToArray():chainEdges,reversed?proof.Reverse().ToArray():proof,[]));store.FinishSnapshot(chain.Id);return chain.Id;}
 var first=ContextBuilder.Build(store,LoadChain(false),"ChainAnchor",100000);var reversed=ContextBuilder.Build(store,LoadChain(true),"ChainAnchor",100000);
 var two=first.Items.Single(i=>i.Node.Id=="two");
 Check("two-hop neighbor included and third-hop omitted",first.Items.Any(i=>i.Node.Id=="two")&&!first.Items.Any(i=>i.Node.Id=="three"));
 Check("two-hop path is independently self-contained",two.ConnectionPath.Select(p=>p.Node.Id).SequenceEqual(new[]{"start","one","two"})&&two.ConnectionPath[1].Via is {Id:"link1",FromId:"start",ToId:"one"}&&two.ConnectionPath[2].Via is {Id:"link2",FromId:"two",ToId:"one"});
 Check("two-hop boundary explicitly disclosed",first.Truncated&&first.TruncationReasons.Contains("connection-depth:2"));
 Check("ranking and paths independent of ingestion order",Json(first.Items)==Json(reversed.Items));
});
Run("bounds under hubs and cycles",()=>{
 var hub=store.CreateSnapshot("bounded-hubs");var hn=new List<MapNode>();var he=new List<MapEdge>();var ho=new List<Evidence>();
 for(int s=0;s<70;s++){var id=$"s{s:D3}";hn.Add(Node(id,"HubTarget"));if(s>=12)continue;for(int n=0;n<35;n++){var target=$"neighbor{s:D3}-{n:D3}";var eid=$"hub-edge{s:D3}-{n:D3}";hn.Add(Node(target,"nonmatching neighbor"));he.Add(new(eid,id,target,"LINKS",eid));ho.Add(new(eid,hub.Id,id,"relation:LINKS","fixture","independent-fixture","fixture://hub",fixedTime));}}
 store.Ingest(hub.Id,new(hn,he,ho,[]));store.FinishSnapshot(hub.Id);var result=ContextBuilder.Build(store,hub.Id,"HubTarget",100000);Save("hub-context.json",result);
 Check("candidate count bounded at 256",result.CandidatesConsidered==256,new{result.CandidatesConsidered,returned=result.Items.Count});
 foreach(var reason in new[]{"literal-seeds:64","expanded-nodes-per-hop:12","incident-edges-per-direction:32","candidate-nodes:256"})Check("bound disclosed: "+reason,result.TruncationReasons.Contains(reason),result.TruncationReasons);
 Check("every hub result still obeys envelope",Bytes(result)<=100000);
});
Run("pooling and SQL cancellation",()=>{
 var large=store.CreateSnapshot("cancellation-fixture");
 for(int offset=0;offset<20000;offset+=1000){var batch=Enumerable.Range(offset,1000).Select(i=>Node("load"+i,"probe-"+i+new string('x',128))).ToArray();store.Ingest(large.Id,new(batch,[],[],[]));}store.FinishSnapshot(large.Id);
 using(var completed=new CancellationTokenSource()){ContextBuilder.Build(store,large.Id,"absent search terms",16000,completed.Token);completed.Cancel();using var next=store.OpenSemanticReader();Check("completed request token cannot poison pooled reader",next.Nodes(large.Id,search:"absent search terms",limit:1).Total==0);}
 using(var pre=new CancellationTokenSource()){pre.Cancel();try{ContextBuilder.Build(store,large.Id,"absent",16000,pre.Token);Check("precancel surfaced as cancellation",false);}catch(OperationCanceledException){Check("precancel surfaced as cancellation",true);}}
 using(var reader=store.OpenSemanticReader())using(var source=new CancellationTokenSource()){
  reader.SetContextCancellation(source.Token);using var timer=new Timer(_=>source.Cancel(),null,5,Timeout.Infinite);var watch=Stopwatch.StartNew();var canceled=false;string? type=null;int? code=null;
  try{reader.ContextSeeds(large.Id,"not found",new[]{"notfound1","notfound2","notfound3","notfound4","notfound5","notfound6","notfound7","notfound8"},65,CancellationToken.None);}catch(SqliteException ex){canceled=ex.SqliteErrorCode==9;type=ex.GetType().Name;code=ex.SqliteErrorCode;}finally{watch.Stop();}
  Check("in-flight native SQL progress interruption",canceled&&source.IsCancellationRequested,new{watch.ElapsedMilliseconds,type,code,rows=20000,explicitRowCancellationToken="None; only native progress callback can interrupt this statement"});
 }
 using(var source=new CancellationTokenSource()){using var timer=new Timer(_=>source.Cancel(),null,5,Timeout.Infinite);var watch=Stopwatch.StartNew();try{ContextBuilder.Build(store,large.Id,"notfound1 notfound2 notfound3 notfound4 notfound5 notfound6 notfound7 notfound8",16000,source.Token);Check("context exposes in-flight cancellation",false,new{watch.ElapsedMilliseconds});}catch(OperationCanceledException error){Check("context exposes in-flight cancellation",source.IsCancellationRequested,new{watch.ElapsedMilliseconds,inner=error.InnerException?.GetType().Name});}}
 using(var next=store.OpenSemanticReader())Check("canceled request does not poison next pooled reader",next.Nodes(large.Id,search:"absent",limit:1).Total==0);
 Check("new context succeeds after cancellation",ContextBuilder.Build(store,large.Id,"load42",16000).Items.Any(i=>i.Node.Id=="load42"));
});
Run("SARIF runtime driver version",()=>{
 using var memory=new MemoryStream();Exporter.WriteAsync(store,snapshot.Id,"sarif",memory,CancellationToken.None).GetAwaiter().GetResult();using var parsed=JsonDocument.Parse(memory.ToArray());var version=parsed.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("version").GetString();var actual=typeof(Exporter).Assembly.GetName().Version!.ToString(3);Check("SARIF driver matches loaded Engine assembly",version==actual,new{version,actual});File.WriteAllBytes(Path.Combine(root,"result.sarif"),memory.ToArray());
});
Save("review-results.json",new{schemaVersion=1,createdAt=DateTimeOffset.UtcNow,engineAssembly=typeof(Exporter).Assembly.GetName().Version!.ToString(),fixture="product regression automation derived from an independent review; not fresh independent evidence, relevance evaluation, or a production gate",runtime,checks=checks.Count,failures,results=checks});
Console.WriteLine($"Completed {checks.Count} checks; {failures} failed.");
return failures==0?0:1;
