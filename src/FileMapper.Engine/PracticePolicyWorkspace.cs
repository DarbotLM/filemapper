using System.Collections.Immutable;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;
namespace FileMapper.Engine;

public sealed record PracticePolicyCreateRequest(string ArenaHash);
public sealed record PracticePolicyTrainRequest(string ParentPolicyHash, string EpisodeHash, string ReviewerId, string ReviewId, string Rationale, DateTimeOffset ReviewedAt);
public sealed record PracticePolicyStepRequest(string ArenaHash, string PolicyHash, SpatialMove[] Moves);
public sealed record PracticePolicy(string Schema, string ArenaHash, string? ParentPolicyHash, string? EpisodeHash,
    string? TrajectoryHash, ExpertPolicyModel Model, ExpertTrainingReceipt? Receipt, long? VerifiedReward, int Depth, string Scope);
public sealed record SavedPracticePolicy(string ArtifactHash, DateTimeOffset StoredAt, PracticePolicy Policy);
public sealed record PracticePolicyDecision(string StateHash, string PolicyHash, string ModelHash, SpatialMove Move, int CandidatesEvaluated);

/// <summary>Explicit, arena-bound reviewed imitation; practice exposure is not a held-out split.</summary>
public sealed class PracticePolicyWorkspace(MapStore store, EditionPracticeWorkspace practice)
{
    private const string Kind="edition-practice-policy/1";
    private const string Schema="filemapper.edition-practice-policy/1";
    private const string Scope="practice-exposed; no protected or held-out partition; no semantic accuracy claim; actor and reviewer are caller-declared";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public SavedPracticePolicy Create(PracticePolicyCreateRequest request, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(request); _=practice.Load(request.ArenaHash); token.ThrowIfCancellationRequested();
        return Save(new(Schema,request.ArenaHash,null,null,null,SpatialExpertComputer.InitialPolicy,null,null,0,Scope),token);
    }
    public SavedPracticePolicy Train(PracticePolicyTrainRequest request, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (parent, seen)=VerifyChain(request.ParentPolicyHash,token);
        if(parent.Policy.Depth>=32)throw new ArgumentException("Practice policy lineage is limited to 32 updates.");
        var arena=practice.Load(parent.Policy.ArenaHash).Arena;
        var (episode, trajectory)=ReadEpisode(request.EpisodeHash,parent.Policy.ArenaHash,arena,token);
        if(seen.Contains(trajectory))throw new ArgumentException("This exact arena and trajectory already occur in the policy lineage.");
        var review=new ExpertReview(episode.EpisodeHash,request.ReviewerId,request.ReviewId,request.Rationale,request.ReviewedAt);
        var receipt=SpatialExpertComputer.Train(arena.Challenge,episode,review,parent.Policy.Model,Manifest(parent.Policy.ArenaHash,arena),token);
        return Save(new(Schema,parent.Policy.ArenaHash,parent.ArtifactHash,request.EpisodeHash,trajectory,receipt.Policy,receipt,episode.VerifiedReward,parent.Policy.Depth+1,Scope),token);
    }
    public SavedPracticePolicy Load(string hash, CancellationToken token=default)=>VerifyChain(hash,token).Policy;
    public PracticePolicyDecision Choose(PracticePolicyStepRequest request, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(request);var (policy,_)=VerifyChain(request.PolicyHash,token);
        if(policy.Policy.ArenaHash!=request.ArenaHash)throw new ArgumentException("Policy belongs to a different practice arena.");
        var state=practice.Replay(new(request.ArenaHash,request.Moves),token).State;
        var step=SpatialExpertComputer.Choose(state,policy.Policy.Model,token);
        return new(step.StateHash,policy.ArtifactHash,step.ModelHash,step.Move,step.CandidatesEvaluated);
    }
    private SavedPracticePolicy Save(PracticePolicy policy,CancellationToken token)
    {
        var payload=CanonicalJson.Serialize(policy);var hash=CanonicalJson.HashUtf8(payload);token.ThrowIfCancellationRequested();
        var saved=store.SaveSemanticArtifact(hash,Kind,policy.ParentPolicyHash,payload,
            CanonicalJson.Serialize(new {policy.ArenaHash,policy.Depth,ModelHash=policy.Model.Hash,policy.EpisodeHash,Updates=policy.Receipt?.Updates,policy.Scope}),token);
        return new(hash,saved.StoredAt,policy);
    }
    private (SavedPracticePolicy Policy,HashSet<string> Trajectories) VerifyChain(string hash,CancellationToken token)
    {
        var chain=new List<SavedPracticePolicy>(); var hashes=new HashSet<string>(StringComparer.Ordinal);string? next=hash;
        while(next is not null)
        {
            token.ThrowIfCancellationRequested(); if(!hashes.Add(next)||chain.Count>32)throw new InvalidDataException("Invalid or excessive practice policy lineage.");
            var artifact=store.GetSemanticArtifact(next)??throw new KeyNotFoundException("Practice policy not found.");
            if(artifact.Kind!=Kind)throw new ArgumentException("Artifact is not a practice policy.");
            var p=JsonSerializer.Deserialize<PracticePolicy>(artifact.Payload,Json)??throw new InvalidDataException("Invalid practice policy.");
            if(p.Schema!=Schema||p.Scope!=Scope||artifact.ParentHash!=p.ParentPolicyHash)throw new InvalidDataException("Practice policy metadata differs.");
            chain.Add(new(artifact.Hash,artifact.StoredAt,p));next=p.ParentPolicyHash;
        }
        chain.Reverse();var initial=chain[0].Policy;
        if(initial.Depth!=0||initial.EpisodeHash is not null||initial.TrajectoryHash is not null||initial.Receipt is not null||initial.VerifiedReward is not null||CanonicalJson.Serialize(initial.Model)!=CanonicalJson.Serialize(SpatialExpertComputer.InitialPolicy))throw new InvalidDataException("Invalid initial practice policy.");
        var arena=practice.Load(initial.ArenaHash).Arena;var manifest=Manifest(initial.ArenaHash,arena);var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=1;i<chain.Count;i++)
        {
            token.ThrowIfCancellationRequested();var p=chain[i].Policy;var parent=chain[i-1];
            if(p.Depth!=i||p.ArenaHash!=initial.ArenaHash||p.ParentPolicyHash!=parent.ArtifactHash||p.EpisodeHash is null||p.Receipt is null)throw new InvalidDataException("Invalid practice policy ancestry.");
            var (episode,trajectory)=ReadEpisode(p.EpisodeHash,p.ArenaHash,arena,token);
            if(p.TrajectoryHash!=trajectory||!seen.Add(trajectory)||p.VerifiedReward!=episode.VerifiedReward)throw new InvalidDataException("Practice trajectory or reward differs.");
            SpatialExpertComputer.VerifyReceipt(arena.Challenge,episode,parent.Policy.Model,manifest,p.Receipt,token);
            if(CanonicalJson.Serialize(p.Model)!=CanonicalJson.Serialize(p.Receipt.Policy))throw new InvalidDataException("Practice policy model differs from receipt.");
        }
        return(chain[^1],seen);
    }
    private (GameEpisode Episode,string Trajectory) ReadEpisode(string hash,string arenaHash,EditionPracticeArena arena,CancellationToken token)
    {
        var saved=store.GetSemanticArtifact(hash)??throw new KeyNotFoundException("Practice episode not found.");
        if(saved.Kind!="edition-practice-episode/1"||saved.ParentHash!=arenaHash)throw new ArgumentException("Episode belongs to a different practice arena.");
        var episode=JsonSerializer.Deserialize<EditionPracticeEpisode>(saved.Payload,Json)??throw new InvalidDataException("Invalid practice episode.");
        if(episode.Schema!="filemapper.edition-practice-episode/1"||episode.ArenaHash!=arenaHash||episode.Moves.IsDefault)throw new InvalidDataException("Practice episode binding differs.");
        var replay=practice.Replay(new(arenaHash,episode.Moves.ToArray()),token).State;
        if(!replay.IsOver)throw new ArgumentException("Training requires a complete saved episode ending the game.");
        if(CanonicalJson.Serialize(replay)!=CanonicalJson.Serialize(episode.FinalState))throw new InvalidDataException("Saved episode differs from replay.");
        var source=Source(arena);var actor=new GameMoveActor(episode.Competitor,"practice-episode:"+hash,"unattested-practice-adapter","unattested-episode:"+hash);
        var recorded=SpatialComputer.Capture(arena.Challenge,source,actor,episode.Moves,token);
        return(recorded,CanonicalJson.Sha256(new {Domain="filemapper.practice-trajectory/1",ArenaHash=arenaHash,Moves=episode.Moves}));
    }
    private static GameEpisodeSource Source(EditionPracticeArena arena)=>new(arena.Edition.Capture.RepositoryId,arena.Edition.Capture.Commit,"train");
    private static GamePartitionManifest Manifest(string arenaHash,EditionPracticeArena arena)
    {
        var s=Source(arena);return new([new(s.RepositoryFamily,s.RevisionFamily,s.Partition)],CanonicalJson.Sha256(new {Domain="filemapper.practice-only-cohort/1",ArenaHash=arenaHash,EditionSha256=arena.Edition.Sha256}));
    }
}
