using FileMapper.Engine;
using FileMapper.Storage;
using FileMapper.Semantics;
if(args.Length!=1)throw new ArgumentException("Pass a retained completed eligible edition directory; the fixture is copied before testing.");
var source=Path.GetFullPath(args[0]);var temp=Path.Combine(Path.GetTempPath(),"filemapper-practice-policy-test-"+Guid.NewGuid().ToString("N"));
var root=Path.Combine(temp,"catalog");var data=Path.Combine(temp,"data");Directory.CreateDirectory(Path.Combine(root,"edition"));Directory.CreateDirectory(data);
File.Copy(Path.Combine(source,"edition.db"),Path.Combine(root,"edition","edition.db"));File.Copy(Path.Combine(source,"complete.json"),Path.Combine(root,"edition","complete.json"));using var lib=new WordLibraryWorkspace(data);await lib.ConnectAsync(root,CancellationToken.None);
var pin=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"edition","complete.json"))).RootElement.GetProperty("sha256").GetString()!;
var store=new MapStore(Path.Combine(data,"map.db"));store.Initialize();var arenaApi=new EditionPracticeWorkspace(store,lib);var api=new PracticePolicyWorkspace(store,arenaApi);
var a=arenaApi.Freeze(new(pin,4,0,0));var b=arenaApi.Freeze(new(pin,5,0,0));var initial=api.Create(new(a.ArtifactHash));if(initial.Policy.Depth!=0||initial.Policy.Receipt!=null)throw new Exception("initial wrong");
var first=arenaApi.Replay(new(a.ArtifactHash,[]));var one=first.LegalMoves.Last(x=>!x.TopOut).Move;var partial=arenaApi.SaveEpisode(new(a.ArtifactHash,[one]),"human");
try{api.Train(new(initial.ArtifactHash,partial.Hash,"r","1","review",DateTimeOffset.UtcNow));throw new Exception("partial accepted");}catch(ArgumentException e) when(e.Message.Contains("complete")){}
var moves=new List<SpatialMove>();var state=first;while(!state.State.IsOver){var move=state.LegalMoves.Last().Move;moves.Add(move);state=arenaApi.Replay(new(a.ArtifactHash,moves.ToArray()));}
var saved=arenaApi.SaveEpisode(new(a.ArtifactHash,moves.ToArray()),"human");var review=new PracticePolicyTrainRequest(initial.ArtifactHash,saved.Hash,"reviewer","review-1","reviewed demonstration",DateTimeOffset.Parse("2026-01-01T00:00:00Z"));var trained=api.Train(review);
var again=api.Train(review);if(trained.ArtifactHash!=again.ArtifactHash||trained.Policy.Depth!=1||api.Load(trained.ArtifactHash).Policy.Model.Hash!=trained.Policy.Model.Hash)throw new Exception("lineage mismatch");
var step=api.Choose(new(a.ArtifactHash,trained.ArtifactHash,[]));if(step.StateHash!=first.State.StateHash||step.PolicyHash!=trained.ArtifactHash||!first.LegalMoves.Any(x=>x.Move==step.Move))throw new Exception("invalid step");
var renamed=arenaApi.SaveEpisode(new(a.ArtifactHash,moves.ToArray()),"different caller label");try{api.Train(review with {ParentPolicyHash=trained.ArtifactHash,EpisodeHash=renamed.Hash});throw new Exception("duplicate accepted");}catch(ArgumentException e) when(e.Message.Contains("trajectory")){}
var moves2=new List<SpatialMove>();var state2=first;while(!state2.State.IsOver){var move=state2.LegalMoves.First().Move;moves2.Add(move);state2=arenaApi.Replay(new(a.ArtifactHash,moves2.ToArray()));}
var saved2=arenaApi.SaveEpisode(new(a.ArtifactHash,moves2.ToArray()),"another caller");var second=api.Train(review with {ParentPolicyHash=trained.ArtifactHash,EpisodeHash=saved2.Hash,ReviewId="review-2"});if(second.Policy.Depth!=2||api.Load(second.ArtifactHash).Policy.Depth!=2)throw new Exception("two update ancestry failed");
try{api.Choose(new(b.ArtifactHash,trained.ArtifactHash,[]));throw new Exception("cross arena accepted");}catch(ArgumentException e) when(e.Message.Contains("different")){}
try{api.Train(review with {ReviewerId=""});throw new Exception("review accepted");}catch(ArgumentException){}
using var cancel=new CancellationTokenSource();cancel.Cancel();try{api.Load(trained.ArtifactHash,cancel.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
Console.WriteLine($"PASS initial={initial.ArtifactHash} trained={trained.ArtifactHash} model={trained.Policy.Model.Hash} updates={trained.Policy.Receipt!.Updates} attempts={trained.Policy.Receipt.UpdateAttempts} reward={trained.Policy.VerifiedReward} candidates={step.CandidatesEvaluated} moves={moves.Count}; partial, duplicate renamed trajectory, cross-arena, bad review and precancel rejected; two-update replay-verified ancestry and idempotence");
