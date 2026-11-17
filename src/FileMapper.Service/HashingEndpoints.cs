using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

namespace FileMapper.Service;

internal static class HashingEndpoints
{
    public static void MapHashingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/hashing/jobs/{id}/objects", (string id, string? nodeId, int? offset, int? limit,
            HashingSpatialProjection projection, CancellationToken cancellationToken) =>
            projection.Objects(id, nodeId, offset ?? 0, limit ?? 24, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/hashing/jobs", (string? snapshotId, int? limit, HashingCoordinator coordinator) =>
            new { items = coordinator.List(snapshotId, limit ?? 100), coordinator.RecoveryDeferred }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}", (string id, HashingCoordinator coordinator) => coordinator.Get(id)).RequireCapability("read");
        app.MapPost("/api/hashing/jobs", async (HttpContext context, HashingCoordinator coordinator) =>
        {
            var request = await NativeRequestJson.ReadAsync<HashingRequest>(context.Request.Body, 64 * 1024, context.RequestAborted);
            var run = coordinator.Start(request);
            return Results.Accepted("/api/hashing/jobs/" + run.Id, run);
        }).RequireCapability("scan");
        app.MapPost("/api/hashing/jobs/{id}/pause", (string id, HashingCoordinator coordinator) =>
            coordinator.Pause(id) ? Results.Ok(new { requested = "paused" }) : Results.Conflict(new { detail = "Only a running content analysis can be paused." })).RequireCapability("scan");
        app.MapPost("/api/hashing/jobs/{id}/cancel", (string id, HashingCoordinator coordinator) =>
            coordinator.Cancel(id) ? Results.Ok(new { requested = "cancelled" }) : Results.Conflict(new { detail = "Only a running content analysis can be cancelled." })).RequireCapability("scan");
        app.MapPost("/api/hashing/jobs/{id}/resume", (string id, HashingCoordinator coordinator) =>
            Results.Accepted("/api/hashing/jobs/" + id, coordinator.Resume(id))).RequireCapability("scan");
        app.MapGet("/api/hashing/jobs/{id}/groups", (string id, string? mode, int? offset, int? limit, MapStore store) =>
        {
            var run = Require(store, id, sealedResult: true);
            return new { run.Id, run.SnapshotId, run.Completeness, run.ModelVersion, run.Coverage,
                page = store.GetHashingGroups(id, mode ?? "exact", offset ?? 0, limit ?? 50) };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}/groups/{groupId}/members", (string id, string groupId, int? offset, int? limit, MapStore store) =>
        {
            var run = Require(store, id, sealedResult: true);
            return new { run.Id, run.SnapshotId, run.Completeness, page = store.GetHashingMembers(id, groupId, offset ?? 0, limit ?? 50) };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}/overlaps", (string id, string? nodeId, int? offset, int? limit, MapStore store) =>
        {
            var run = Require(store, id, sealedResult: true);
            return new { run.Id, run.SnapshotId, run.Completeness, run.ModelVersion, run.Coverage,
                page = store.GetHashingOverlaps(id, nodeId, offset ?? 0, limit ?? 50) };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}/evidence", (string id, string? nodeId, int? offset, int? limit, MapStore store) =>
        {
            var run = Require(store, id);
            return new { run.Id, run.SnapshotId, run.Status, run.Completeness,
                page = store.GetHashingEvidencePage(id, nodeId, offset ?? 0, limit ?? 50) };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}/work", (string id, string stage, string? afterNodeId, int? limit, MapStore store) =>
        {
            var run = Require(store, id);
            var items = store.GetHashingWork(id, stage, afterNodeId, limit ?? 50);
            return new { run.Id, run.SnapshotId, run.Status, items, nextNodeId = items.LastOrDefault()?.NodeId };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/jobs/{id}/paths", (string id, string nodeId, int? offset, int? limit, MapStore store) =>
        {
            var run = Require(store, id);
            return new { run.Id, run.SnapshotId, run.Completeness, page = store.GetHashingPaths(id, nodeId, limit ?? 200, offset ?? 0) };
        }).RequireCapability("read");
        app.MapGet("/api/hashing/evidence/{id}", (string id, MapStore store) =>
            store.GetHashingEvidence(id) is { } evidence ? Results.Ok(evidence) : Results.NotFound()).RequireCapability("read");
        app.MapGet("/api/hashing/evidence/{id}/chunks", (string id, int? offset, int? limit, MapStore store) =>
        {
            var evidence = store.GetHashingEvidence(id) ?? throw new KeyNotFoundException("Content evidence not found.");
            return new { evidence.RunId, evidence.NodeId, evidence.Id, evidence.CacheHit, evidence.CacheSourceEvidenceId,
                page = store.GetHashingChunks(id, offset ?? 0, limit ?? 100) };
        }).RequireCapability("read");
    }
    private static HashingRun Require(MapStore store, string id, bool sealedResult = false)
    {
        var run = store.GetHashingRun(id) ?? throw new KeyNotFoundException("Content analysis not found.");
        if (sealedResult && run.ResultHash is null) throw new InvalidOperationException("Content results are not sealed yet. Inspect the job and its committed evidence, or resume unfinished work.");
        return run;
    }
}
