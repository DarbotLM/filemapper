using FileMapper.Engine;

namespace FileMapper.Service;

internal static class SourceSemanticEndpoints
{
    public static void MapSourceSemanticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/source-semantics/analyses", (int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.ListAnalyses(offset ?? 0, limit ?? 20, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}", (string id, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.GetAnalysis(id, cancellationToken) is { } analysis ? Results.Ok(analysis) : Results.NotFound()).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/nodes", (string id, string? kind, string? search, int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.Nodes(id, kind, search, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/objects", (string id, string? kind, string? search, int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.Objects(id, kind, search, offset ?? 0, limit ?? 24, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/node", (string id, string nodeId, int? relatedLimit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.GetNode(id, nodeId, relatedLimit ?? 24, cancellationToken) is { } node ? Results.Ok(node) : Results.NotFound()).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/observations", (string id, string? subjectId, int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.Observations(id, subjectId, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/edges", (string id, string? nodeId, int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.Edges(id, nodeId, offset ?? 0, limit ?? 100, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/word-links", (string id, string nodeId, int? offset, int? limit, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
            catalog.WordLinks(id, nodeId, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/source-semantics/analyses/{id}/artifacts/{name}", async (string id, string name, HttpContext context, SourceSemanticCatalog catalog, CancellationToken cancellationToken) =>
        {
            using var artifact = catalog.OpenArtifact(id, name, cancellationToken);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = artifact.Descriptor.ByteLength;
            context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
            await artifact.Stream.CopyToAsync(context.Response.Body, 64 * 1024, cancellationToken);
        }).RequireCapability("read");
        app.MapGet("/api/source-semantics/jobs", (int? limit, SourceSemanticCoordinator coordinator) =>
            new { items = coordinator.List(limit ?? 100), coordinator.RecoveryDeferred }).RequireCapability("read");
        app.MapGet("/api/source-semantics/jobs/{id}", (string id, SourceSemanticCoordinator coordinator) =>
            coordinator.Get(id) is { } job ? Results.Ok(job) : Results.NotFound()).RequireCapability("read");
        app.MapPost("/api/source-semantics/jobs", async (HttpContext context, SourceSemanticCoordinator coordinator) =>
        {
            // This bounded endpoint accommodates all 2,000 explicit IDs supported by the native contract.
            const int requestLimit = 256 * 1024;
            var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = requestLimit;
            var request = await NativeRequestJson.ReadAsync<SourceSemanticRequest>(context.Request.Body, requestLimit, context.RequestAborted);
            var job = await coordinator.StartAsync(request, context.RequestAborted);
            return Results.Accepted("/api/source-semantics/jobs/" + job.Id, job);
        }).RequireCapability("scan");
        app.MapPost("/api/source-semantics/imports", async (HttpContext context, SourceSemanticCoordinator coordinator) =>
        {
            var request = await NativeRequestJson.ReadAsync<ImportSourceSemanticRequest>(context.Request.Body, 64 * 1024, context.RequestAborted);
            var job = await coordinator.StartImportAsync(request.Directory, request.AnalysisId, context.RequestAborted);
            return Results.Accepted("/api/source-semantics/jobs/" + job.Id, job);
        }).RequireCapability("scan");
        app.MapPost("/api/source-semantics/jobs/{id}/cancel", (string id, SourceSemanticCoordinator coordinator) =>
            coordinator.Cancel(id) ? Results.Ok(new { cancellationRequested = true }) : Results.Conflict(new { detail = "Only active source work can be cancelled." })).RequireCapability("scan");
    }
}

internal sealed record ImportSourceSemanticRequest(string Directory, string AnalysisId);
