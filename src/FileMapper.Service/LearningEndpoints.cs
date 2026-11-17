using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Service;

public static class LearningEndpoints
{
    private static readonly SemaphoreSlim ArenaWork = new(2, 2);
    public static void MapLearningEndpoints(this WebApplication app)
    {
        app.MapGet("/api/learning", (int? offset, int? limit, MapStore db) => db.ListPatternLearners(offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapPost("/api/learning", async (HttpContext context, PatternLearningWorkspace workspace) =>
            workspace.Create(await Read<LearningCreateRequest>(context))).RequireCapability("learn");
        app.MapGet("/api/learning/{id}", (string id, string? checkpoint, PatternLearningWorkspace workspace) =>
            workspace.Load(id, checkpoint)).RequireCapability("read");
        app.MapGet("/api/learning/{id}/vocabulary", (string id, string? search, string? category, int? offset, int? limit, MapStore db) =>
            db.ReadPatternLearningVocabulary(id, search, category, offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapGet("/api/learning/{id}/events", (string id, int? offset, int? limit, MapStore db) =>
            db.ReadPatternLearningEvents(id, offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapGet("/api/learning/{id}/partition", (string id, string groupKey, PatternLearningWorkspace workspace) =>
        {
            var model = workspace.Load(id);
            return new { groupKey, partition = PatternLearning.AssignPartition(model.Checkpoint.PartitionPolicy, groupKey), model.Checkpoint.PartitionPolicyHash };
        }).RequireCapability("read");
        app.MapPost("/api/learning/{id}/occurrences", async (string id, HttpContext context, MapStore db) =>
            db.ReadPatternLearningOccurrences(id, await Read<PatternLearningOccurrenceQuery>(context))).RequireCapability("read");
        app.MapGet("/api/learning/{id}/verify", async (string id, int? maxEvents, HttpContext context, MapStore db) =>
            await Bounded(context, ct => db.VerifyPatternLearningJournal(id, maxEvents ?? 1000, ct))).RequireCapability("read");
        app.MapGet("/api/snapshots/{id}/learning-subjects", (string id, int? offset, int? limit, PatternLearningWorkspace workspace, CancellationToken cancellationToken) =>
            workspace.Subjects(id, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapPost("/api/learning/{id}/events", async (string id, HttpContext context, PatternLearningWorkspace workspace) =>
            workspace.Append(id, await Read<LearningEventRequest>(context), context.RequestAborted)).RequireCapability("learn");
        app.MapPost("/api/learning/{id}/observe", async (string id, HttpContext context, PatternLearningWorkspace workspace) =>
            workspace.ObservePage(id, await Read<LearningObservePageRequest>(context), context.RequestAborted)).RequireCapability("learn");
        app.MapPost("/api/learning/{id}/predict", async (string id, HttpContext context, PatternLearningWorkspace workspace) =>
            workspace.Predict(id, await Read<LearningPredictionRequest>(context), context.RequestAborted)).RequireCapability("read");
        app.MapPost("/api/learning/{id}/rank", async (string id, HttpContext context, PatternLearningWorkspace workspace) =>
        {
            var request = await Read<LearningRankingRequest>(context);
            return await Bounded(context, ct => workspace.Rank(id, request, ct));
        }).RequireCapability("read");
        app.MapGet("/api/practice/editions", async (int? offset, int? limit, HttpContext context, WordLibraryWorkspace library) =>
            await Bounded(context, ct => library.PracticePage(offset ?? 0, limit ?? 20, ct))).RequireCapability("read");
        app.MapGet("/api/practice/policies", (int? offset, int? limit, MapStore db) => db.ListSemanticArtifacts("edition-practice-policy/1", offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapPost("/api/practice/policies", async (HttpContext context, PracticePolicyWorkspace workspace) =>
        {
            var request=await Read<PracticePolicyCreateRequest>(context);
            return await Bounded(context, ct => workspace.Create(request,ct));
        }).RequireCapability("learn");
        app.MapGet("/api/practice/policies/{hash}", async (string hash, HttpContext context, PracticePolicyWorkspace workspace) =>
            await Bounded(context, ct => workspace.Load(hash,ct))).RequireCapability("read");
        app.MapPost("/api/practice/policies/train", async (HttpContext context, PracticePolicyWorkspace workspace) =>
        {
            var request=await Read<PracticePolicyTrainRequest>(context);
            return await Bounded(context, ct => workspace.Train(request,ct));
        }).RequireCapability("learn");
        app.MapPost("/api/practice/policy-step", async (HttpContext context, PracticePolicyWorkspace workspace) =>
        {
            var request=await Read<PracticePolicyStepRequest>(context);
            return await Bounded(context, ct => workspace.Choose(request,ct));
        }).RequireCapability("read");
        app.MapGet("/api/practice/arenas", (int? offset, int? limit, MapStore db) => db.ListSemanticArtifacts("edition-practice-arena/1", offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapPost("/api/practice/arenas", async (HttpContext context, EditionPracticeWorkspace workspace) =>
        {
            var request = await Read<EditionPracticeRequest>(context);
            return await Bounded(context, ct => workspace.Freeze(request, ct), 180);
        }).RequireCapability("learn");
        app.MapGet("/api/practice/arenas/{hash}", (string hash, EditionPracticeWorkspace workspace) => workspace.Load(hash)).RequireCapability("read");
        app.MapPost("/api/practice/replay", async (HttpContext context, EditionPracticeWorkspace workspace) =>
        {
            var request = await Read<SpatialReplayRequest>(context);
            return await Bounded(context, ct => workspace.Replay(request, ct));
        }).RequireCapability("read");
        app.MapPost("/api/practice/computer", async (HttpContext context, EditionPracticeWorkspace workspace) =>
        {
            var request = await Read<SpatialComputerRequest>(context);
            return await Bounded(context, ct => workspace.Choose(new(request.ArenaHash, request.Moves), request.Budget, ct));
        }).RequireCapability("read");
        app.MapPost("/api/practice/episodes", async (HttpContext context, EditionPracticeWorkspace workspace) =>
        {
            var request = await Read<SpatialSaveReplayRequest>(context);
            return await Bounded(context, ct =>
            {
                var artifact = workspace.SaveEpisode(new(request.ArenaHash, request.Moves), request.Competitor, ct);
                return new { artifact.Hash, artifact.Kind, artifact.ParentHash, artifact.StoredAt, Summary = JsonSerializer.Deserialize<JsonElement>(artifact.Summary) };
            });
        }).RequireCapability("learn");
        app.MapGet("/api/spatial-artifacts", (string? kind, int? offset, int? limit, MapStore db) =>
            db.ListSemanticArtifacts(kind, offset ?? 0, limit ?? 50)).RequireCapability("read");
        app.MapGet("/api/spatial-artifacts/{hash}/export", (string hash, MapStore db) =>
        {
            var artifact = db.GetSemanticArtifact(hash) ?? throw new KeyNotFoundException("Saved artifact was not found.");
            return Results.Text(artifact.Payload, "application/json; charset=utf-8");
        }).RequireCapability("read");
        app.MapGet("/api/spatial/geometry", (string baseShape, string variant) => new
        { geometry = SpatialGameGeometry.Get(baseShape, variant), orientations = SpatialGameGeometry.Orientations(baseShape, variant) }).RequireCapability("read");
        app.MapPost("/api/spatial/preview", async (HttpContext context, SpatialWorkspace workspace) =>
        {
            var request = await Read<SpatialArenaRequest>(context);
            return await Bounded(context, ct => workspace.Preview(request, ct));
        }).RequireCapability("read");
        app.MapPost("/api/spatial/arenas", async (HttpContext context, SpatialWorkspace workspace) =>
        {
            var request = await Read<SpatialArenaRequest>(context);
            return await Bounded(context, ct => workspace.Freeze(request, ct));
        }).RequireCapability("learn");
        app.MapGet("/api/spatial/arenas/{hash}", (string hash, SpatialWorkspace workspace) => workspace.Load(hash)).RequireCapability("read");
        app.MapPost("/api/spatial/replay", async (HttpContext context, SpatialWorkspace workspace) =>
        {
            var request = await Read<SpatialReplayRequest>(context);
            return await Bounded(context, ct => workspace.Replay(request, ct));
        }).RequireCapability("read");
        app.MapPost("/api/spatial/computer", async (HttpContext context, SpatialWorkspace workspace) =>
        {
            var request = await Read<SpatialComputerRequest>(context);
            return await Bounded(context, ct => workspace.Choose(new(request.ArenaHash, request.Moves), request.Budget, ct));
        }).RequireCapability("read");
        app.MapPost("/api/spatial/replays", async (HttpContext context, SpatialWorkspace workspace) =>
        {
            var request = await Read<SpatialSaveReplayRequest>(context);
            return await Bounded(context, ct =>
            {
                var artifact = workspace.SaveReplay(new(request.ArenaHash, request.Moves), request.Competitor, ct);
                return new { artifact.Hash, artifact.Kind, artifact.ParentHash, artifact.StoredAt, Summary = JsonSerializer.Deserialize<JsonElement>(artifact.Summary) };
            });
        }).RequireCapability("learn");
    }
    private static async Task<T> Read<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new ArgumentException("This operation requires application/json.");
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 64 }, context.RequestAborted);
            return NativeRequestJson.Read<T>(document.RootElement);
        }
        catch (JsonException ex) { throw new ArgumentException("Request is not valid JSON.", ex); }
    }
    private static async Task<T> Bounded<T>(HttpContext context, Func<CancellationToken, T> action, int timeoutSeconds = 30)
    {
        if (!await ArenaWork.WaitAsync(0, context.RequestAborted)) throw new InvalidOperationException("Two arena operations are already running. Retry when one finishes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { return await Task.Run(() => action(deadline.Token), deadline.Token); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { throw new InvalidOperationException($"Arena operation reached its {timeoutSeconds}-second cooperative budget; no successful result was reported."); }
        finally { ArenaWork.Release(); }
    }
}
public sealed record SpatialComputerRequest(string ArenaHash, SpatialMove[] Moves, SpatialSearchBudget? Budget = null);
public sealed record SpatialSaveReplayRequest(string ArenaHash, SpatialMove[] Moves, string Competitor);
