using FileMapper.Engine;
using FileMapper.Storage;
using FileMapper.Semantics;
if(args.Length!=1)throw new ArgumentException("Pass a retained completed eligible edition directory; the fixture is copied before testing.");
var source=Path.GetFullPath(args[0]);var temp=Path.Combine(Path.GetTempPath(),"filemapper-practice-test-"+Guid.NewGuid().ToString("N"));
var root=Path.Combine(temp,"catalog");var data=Path.Combine(temp,"data");Directory.CreateDirectory(Path.Combine(root,"edition"));Directory.CreateDirectory(data);
File.Copy(Path.Combine(source,"edition.db"),Path.Combine(root,"edition","edition.db"));File.Copy(Path.Combine(source,"complete.json"),Path.Combine(root,"edition","complete.json"));
using var lib=new WordLibraryWorkspace(data);await lib.ConnectAsync(root,CancellationToken.None);
var hash=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"edition","complete.json"))).RootElement.GetProperty("sha256").GetString()!;
Console.WriteLine("eligibility "+System.Text.Json.JsonSerializer.Serialize(lib.PracticePage(0,20,CancellationToken.None)));
var store=new MapStore(Path.Combine(data,"map.db"));store.Initialize();var workspace=new EditionPracticeWorkspace(store,lib);
var a=workspace.Freeze(new(hash,8,2,2));var b=workspace.Freeze(new(hash,8,2,2));if(a.ArtifactHash!=b.ArtifactHash)throw new Exception("non-deterministic freeze");
if(a.Arena.WordSources.Length!=a.Arena.Challenge.Deck.Count(p=>p.UnitKind=="word"))throw new Exception("word evidence missing");
var empty=new SpatialReplayRequest(a.ArtifactHash,[]);var r=workspace.Replay(empty);var move=r.LegalMoves.First(x=>!x.TopOut).Move;var played=workspace.Replay(new(a.ArtifactHash,[move]));var saved=workspace.SaveEpisode(new(a.ArtifactHash,[move]),"human");
if(saved.ParentHash!=a.ArtifactHash || workspace.Choose(empty,null).StateHash!=r.State.StateHash)throw new Exception("replay or computer mismatch");
try{workspace.Freeze(new(new string('0',64),8,2,2));throw new Exception("unknown pin accepted");}catch(ArgumentException){}
try{workspace.Replay(new(a.ArtifactHash,[move with {ExpectedPriorStateHash=new string('0',64)}]));throw new Exception("stale move accepted");}catch(ArgumentException){}catch(InvalidOperationException){}
var sidecar=Path.Combine(root,"edition","edition.db-wal");File.WriteAllText(sidecar,"unexpected");
try{workspace.Freeze(new(hash,8,2,2));throw new Exception("sidecar accepted");}catch(InvalidDataException){} finally{File.Delete(sidecar);}
await lib.ConnectAsync(root,CancellationToken.None);if(workspace.Replay(new(a.ArtifactHash,[move])).State.StateHash!=played.State.StateHash)throw new Exception("reconnect changed replay");
using var cancel=new CancellationTokenSource();cancel.Cancel();try{workspace.Freeze(new(hash,8,2,2),cancel.Token);throw new Exception("cancellation accepted");}catch(OperationCanceledException){}
lib.Dispose(); if(workspace.Replay(new(a.ArtifactHash,[move])).State.StateHash!=played.State.StateHash)throw new Exception("saved replay needs catalog");
Console.WriteLine($"PASS deterministic freeze; edition={hash}; arena={a.ArtifactHash}; words={a.Arena.WordSources.Length}; deck={a.Arena.Challenge.Deck.Length}; replay={played.State.StateHash}; episode={saved.Hash}; rejected unknown pin/stale move/precancel; replay survived reconnect and disposal; unexpected WAL rejected");
