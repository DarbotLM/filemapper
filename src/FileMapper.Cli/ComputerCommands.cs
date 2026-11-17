using System.Collections.Immutable;
using System.Text.Json;
using FileMapper.Semantics;
using FileMapper.Engine;

namespace FileMapper.Cli;

internal sealed record ExpertLineageEntry(ComputerFrozenChallenge Frozen, GameEpisode Episode, ExpertTrainingReceipt Receipt);
internal sealed record ComputerRequest(ComputerFrozenChallenge Frozen, GameMoveActor? Actor = null,
    GamePolicyModel? Policy = null, GamePolicyModel? After = null, GamePartitionManifest? Manifest = null,
    GameEpisode? Episode = null, SpatialMove[]? Moves = null, ExpertPolicyModel? ExpertPolicy = null,
    ExpertPolicyModel? ExpertAfter = null, ExpertReview? Review = null, ExpertTrainingReceipt? Receipt = null,
    ComputerFrozenChallenge? TrainingFrozen = null, GameEpisode? TrainingEpisode = null, ExpertLineageEntry[]? Lineage = null);

internal static class ComputerCommands
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
computer initial|expert-initial --out file
computer freeze|legal|play|capture|replay|train|compare|expert-play|expert-train|expert-verify|expert-lineage-verify|expert-compare --request file --out file

freeze request: editions (absolute paths), selectedEdition (zero-based), pieces (2-128), seed.
Legacy pieces limits words only; path/candidates may fill the 128-piece maximum. For explicit total quotas,
supply deckPolicy {wordLimit,pathLimit,candidateLimit,schema:"filemapper.computer-deck-quotas/1"};
the three limits sum to at most 128. Decks are ordered words, path components, then lexical candidates.
Other requests contain frozen (the entire freeze output). legal also accepts moves; capture accepts moves and actor.
expert-play takes expertPolicy and actor. expert-train takes episode, expertPolicy (parent), and review
(episodeHash, reviewerId, reviewId, rationale, reviewedAt); its output is a receipt containing the new policy.
expert-verify takes episode, expertPolicy (parent), and receipt. expert-compare takes a test frozen bundle,
trainingFrozen, trainingEpisode, expertPolicy (parent), receipt, and actor. Comparison rechecks the training receipt.
Expert training and comparison require freeze protection: registryId, validationEditions, testEditions, and optional trainingEditions.
List all known historical validation/test editions; their repository, revision, and shared exact-content families are reserved.

CLI expert training, lineage verification and comparison replay up to 32 ordered lineage entries
(frozen, episode, receipt), starting from the initial checkpoint. Duplicate challenge/move trajectories
are rejected even if relabeled.
expert-lineage-verify audits that lineage without training or requiring a test family. expert-play may
derive its checkpoint from lineage instead of accepting an unverified supplied policy.
Imitation uses at most 64 evenly sampled legal candidates plus the reviewed
move during training. Features are
verified clear and observed-pattern outcomes, top-out, height, cavities, landing, and 3D orientation dimensions.
A review is an explicit caller declaration, not authenticated identity. Game results never become semantic labels.
Frozen editions bind the cohort and exact shared source bytes; unknown-fork ancestry is excluded. This does not
prove independence from near-duplicates or forks outside the cohort. A test family must exist before evaluation.
Outputs are atomic and create-new. legal accepts partial moves; capture requires a completed episode.
""");
            return 0;
        }
        if (args.Length is not (3 or 5) || args[1] != (args[0] is "initial" or "expert-initial" ? "--out" : "--request")
            || args[0] is not ("initial" or "expert-initial") && args[3] != "--out") throw new ArgumentException("Use computer --help.");
        var output = Path.GetFullPath(args[^1]);
        object result;
        if (args[0] == "initial") result = SpatialComputer.InitialPolicy;
        else if (args[0] == "expert-initial") result = SpatialExpertComputer.InitialPolicy;
        else
        {
            var info = new FileInfo(args[2]);
            if (!info.Exists || info.Length > 16 * 1024 * 1024) throw new ArgumentException("Request must exist and be at most 16 MiB.");
            var json = await File.ReadAllTextAsync(info.FullName, token);
            if (args[0] == "freeze")
                result = ComputerChallengeWorkspace.Freeze(JsonSerializer.Deserialize<ComputerFreezeRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Invalid freeze request."), token);
            else
            {
            var request = JsonSerializer.Deserialize<ComputerRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("Invalid request.");
            ComputerChallengeWorkspace.Verify(request.Frozen, token);
            var challenge = request.Frozen.Challenge;
            var source = request.Frozen.Source;
            if (request.Episode is not null && request.Episode.Source != source) throw new ArgumentException("Episode source differs from frozen challenge source.");
            if (args[0] is "expert-train" or "expert-compare" or "expert-lineage-verify" && request.Frozen.Request.Protection is null) throw new ArgumentException("A protected historical-family registry is required for expert training, lineage verification and comparison.");
            var actor = request.Actor;
            result = args[0] switch
            {
                "expert-play" => PlayExpert(request, token),
                "expert-train" => TrainExpert(request, token),
                "expert-verify" => VerifyExpert(challenge, request.Episode ?? throw new ArgumentException("Episode required."), request.ExpertPolicy ?? throw new ArgumentException("Parent policy required."), request.Frozen.Manifest, request.Receipt ?? throw new ArgumentException("Receipt required."), token),
                "expert-lineage-verify" => AuditLineage(request, token),
                "expert-compare" => CompareExpert(request, token),
                "legal" => Legal(challenge, request.Moves ?? [], token),
                "play" => SpatialComputer.Play(challenge, source, actor ?? throw new ArgumentException("Actor required."), request.Policy ?? throw new ArgumentException("Policy required."), token),
                "capture" => SpatialComputer.Capture(challenge, source, actor ?? throw new ArgumentException("Actor required."), request.Moves ?? throw new ArgumentException("Moves required."), token),
                "replay" => Replay(challenge, request.Episode ?? throw new ArgumentException("Episode required."), token),
                "train" => SpatialComputer.Train(challenge, request.Episode ?? throw new ArgumentException("Episode required."), request.Policy ?? throw new ArgumentException("Policy required."), request.Frozen.Manifest, token),
                "compare" => SpatialComputer.Compare(challenge, source, actor?.ActorId ?? throw new ArgumentException("Actor required."), actor.SessionId, request.Policy ?? throw new ArgumentException("Policy required."), request.After ?? throw new ArgumentException("After policy required."), request.Frozen.Manifest, token),
                _ => throw new ArgumentException("Unknown computer command.")
            };
            }
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(result));
        var stage = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); }
            token.ThrowIfCancellationRequested(); File.Move(stage, output, false);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
        Console.WriteLine(CanonicalJson.Serialize(new { path = output, sha256 = CanonicalJson.HashUtf8(System.Text.Encoding.UTF8.GetString(bytes)) }));
        return 0;
    }
    private static object Legal(SpatialChallenge challenge, SpatialMove[] moves, CancellationToken token)
    {
        var state = SpatialGame.Replay(challenge, moves, token);
        return new { state, legalMoves = SpatialGame.LegalMoves(state, token), orientations = state.IsOver ? ImmutableArray<SpatialOrientation>.Empty : SpatialGameGeometry.Orientations(state.Challenge.Deck[state.NextPieceIndex].BaseShape, state.Challenge.Deck[state.NextPieceIndex].GeometryVariant) };
    }
    private static object TrainExpert(ComputerRequest request, CancellationToken token)
    {
        if ((request.Lineage?.Length ?? 0) >= 32) throw new ArgumentException("Lineage is at the 32-update limit.");
        var parent = VerifyLineage(request.Lineage ?? [], request.Frozen.Manifest, token);
        if (request.ExpertPolicy is not null && request.ExpertPolicy.Hash != parent.Hash) throw new ArgumentException("Supplied parent differs from verified lineage.");
        if (request.Episode is not null && (request.Lineage ?? []).Any(x => TrajectoryHash(x.Episode) == TrajectoryHash(request.Episode))) throw new ArgumentException("A trajectory cannot be reused in one lineage, including under different actor metadata.");
        return SpatialExpertComputer.Train(request.Frozen.Challenge, request.Episode ?? throw new ArgumentException("Episode required."),
            request.Review ?? throw new ArgumentException("Review required."), parent, request.Frozen.Manifest, token);
    }
    private static ExpertPolicyModel VerifyLineage(ExpertLineageEntry[] entries, GamePartitionManifest manifest, CancellationToken token)
    {
        if (entries.Length > 32) throw new ArgumentException("At most 32 lineage updates are supported.");
        var parent = SpatialExpertComputer.InitialPolicy; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || entry.Frozen?.Request.Protection is null || entry.Episode is null || entry.Receipt is null)
                throw new ArgumentException("Every lineage entry requires a protected frozen source, episode and receipt.");
            ComputerChallengeWorkspace.Verify(entry.Frozen, token);
            if (entry.Episode.Source != entry.Frozen.Source || GamePartitions.Hash(entry.Frozen.Manifest) != GamePartitions.Hash(manifest))
                throw new ArgumentException("Lineage episode or cohort differs.");
            if (!seen.Add(TrajectoryHash(entry.Episode))) throw new ArgumentException("Duplicate trajectory in lineage, including under different actor metadata.");
            SpatialExpertComputer.VerifyReceipt(entry.Frozen.Challenge, entry.Episode, parent, entry.Frozen.Manifest, entry.Receipt, token);
            parent = entry.Receipt.Policy;
        }
        return parent;
    }
    private static string TrajectoryHash(GameEpisode episode) => CanonicalJson.Sha256(new
    { episode.ChallengeHash, Moves = episode.Moves.Select(m => m.Move).ToArray() });
    private static object AuditLineage(ComputerRequest request, CancellationToken token)
    {
        var lineage = request.Lineage ?? throw new ArgumentException("Lineage is required; an empty array identifies the initial checkpoint.");
        var policy = VerifyLineage(lineage, request.Frozen.Manifest, token);
        if (request.ExpertPolicy is not null && request.ExpertPolicy.Hash != policy.Hash)
            throw new ArgumentException("Supplied checkpoint differs from the verified lineage.");
        return new { verified = true, updates = lineage.Length,
            lineageHash = CanonicalJson.Sha256(lineage.Select(x => x.Receipt.ReceiptHash).ToArray()),
            partitionManifestHash = GamePartitions.Hash(request.Frozen.Manifest), resultingCheckpoint = policy.Hash,
            policy, scope = "Receipt and provenance verification; not a performance or generalization assessment." };
    }
    private static object PlayExpert(ComputerRequest request, CancellationToken token)
    {
        var policy = request.Lineage is null
            ? request.ExpertPolicy ?? throw new ArgumentException("Expert policy or verified lineage required.")
            : VerifyLineage(request.Lineage, request.Frozen.Manifest, token);
        if (request.Lineage is not null && request.ExpertPolicy is not null && request.ExpertPolicy.Hash != policy.Hash)
            throw new ArgumentException("Supplied checkpoint differs from the verified lineage.");
        if (policy.PartitionManifestHash is not null && policy.PartitionManifestHash != GamePartitions.Hash(request.Frozen.Manifest))
            throw new ArgumentException("Policy belongs to a different cohort.");
        return SpatialExpertComputer.Play(request.Frozen.Challenge, request.Frozen.Source,
            request.Actor ?? throw new ArgumentException("Actor required."), policy, token);
    }
    private static object CompareExpert(ComputerRequest request, CancellationToken token)
    {
        var actor = request.Actor ?? throw new ArgumentException("Actor required.");
        var lineage = request.Lineage ?? [];
        if (lineage.Length == 0)
        {
            var train = request.TrainingFrozen ?? throw new ArgumentException("Training frozen challenge required.");
            var episode = request.TrainingEpisode ?? throw new ArgumentException("Training episode required.");
            var receipt = request.Receipt ?? throw new ArgumentException("Training receipt required.");
            lineage = [new(train, episode, receipt)];
        }
        var after = VerifyLineage(lineage, request.Frozen.Manifest, token);
        var before = SpatialExpertComputer.InitialPolicy;
        if (request.ExpertPolicy is not null && request.ExpertPolicy.Hash != before.Hash) throw new ArgumentException("Comparison baseline must be the verified initial checkpoint.");
        if (request.ExpertAfter is not null && request.ExpertAfter.Hash != after.Hash) throw new ArgumentException("After policy differs from verified lineage.");
        var comparison = SpatialExpertComputer.Compare(request.Frozen.Challenge, request.Frozen.Source, actor.ActorId, actor.SessionId,
            before, after, request.Frozen.Manifest, token);
        return new { lineageHash = CanonicalJson.Sha256(lineage.Select(x => x.Receipt.ReceiptHash).ToArray()), resultingCheckpoint = after.Hash, comparison };
    }
    private static object VerifyExpert(SpatialChallenge challenge, GameEpisode episode, ExpertPolicyModel parent,
        GamePartitionManifest manifest, ExpertTrainingReceipt receipt, CancellationToken token)
    {
        SpatialExpertComputer.VerifyReceipt(challenge, episode, parent, manifest, receipt, token);
        return new { verified = true, receipt.ReceiptHash, receipt.Policy.Hash };
    }
    private static object Replay(SpatialChallenge challenge, GameEpisode episode, CancellationToken token)
    {
        SpatialComputer.Verify(challenge, episode, token);
        return new { verified = true, episode.EpisodeHash, episode.FinalStateHash, episode.VerifiedReward };
    }
}
