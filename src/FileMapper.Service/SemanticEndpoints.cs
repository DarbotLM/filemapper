using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Engine;
using FileMapper.Semantics;

namespace FileMapper.Service;

public static class SemanticEndpoints
{
    public static void MapSemanticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/snapshots/{id}/semantic-objects", (string id, string? kind, string? search, int? offset, int? limit,
            SemanticWorkspace workspace, CancellationToken cancellationToken) => workspace.Objects(id, kind, search, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/snapshots/{id}/semantic-export", WriteExportAsync).RequireCapability("read");
        app.MapGet("/api/semantic-patterns", (string before, string after, int? limit, SemanticWorkspace workspace,
            CancellationToken cancellationToken) => workspace.Patterns(before, after, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/snapshots/{id}/semantic-game/challenge", (string id, int? round, SemanticWorkspace workspace,
            CancellationToken cancellationToken) => workspace.Challenge(id, round ?? 1, cancellationToken)).RequireCapability("read");
        app.MapPost("/api/semantic-game/replay", async (HttpContext context, SemanticWorkspace workspace, CancellationToken cancellationToken) =>
        {
            var request = await ReadReplayRequestAsync(context, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SnapshotId);
            if (request.Moves is null || request.Moves.Length > SemanticGame.MaximumDeckLength) throw new ArgumentException("Moves exceed their bound.");
            var challenge = workspace.Challenge(request.SnapshotId, request.Round, cancellationToken);
            if (!string.Equals(challenge.ChallengeHash, request.ExpectedChallengeHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The snapshot challenge changed. Load it again before replaying.");
            return SemanticGame.Replay(challenge, request.Moves);
        }).RequireCapability("read");
    }

    private static readonly JsonSerializerOptions ReplayJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32
    };

    private static async Task<SemanticReplayRequest> ReadReplayRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) throw new ArgumentException("Replay requires application/json.");
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<SemanticReplayRequest>(ReplayJson) ?? throw new ArgumentException("Replay requires an object.");
        }
        catch (JsonException) { throw new ArgumentException("Replay must contain only the required snapshotId, round, expectedChallengeHash and moves fields, with valid typed move objects."); }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON properties are not supported.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static async Task WriteExportAsync(string id, string? compression, SemanticWorkspace workspace, HttpContext context)
    {
        if (context.Request.Query.Any(pair => pair.Key != "compression" || pair.Value.Count != 1))
            throw new ArgumentException("Only one compression query parameter is supported.");
        compression ??= "none";
        if (compression is not ("none" or "gzip")) throw new ArgumentException("Compression must be none or gzip.");

        // Opening and validating the pinned reader precedes headers and the gzip stream: an
        // unknown snapshot remains a normal 404, never a successful-looking empty gzip file.
        using var export = workspace.PrepareExport(id, context.RequestAborted);
        var compressed = compression == "gzip";
        context.Response.ContentType = compressed ? "application/gzip" : "application/x-ndjson; charset=utf-8";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"filemapper-{Uri.EscapeDataString(id)}.ndjson{(compressed ? ".gz" : "")}\"";
        context.Response.Headers["X-FileMapper-Export-Schema"] = SemanticWorkspace.ExportSchema;
        // gzip is the downloaded file format, not HTTP content encoding. Clients verify the
        // canonical NDJSON footer after decompression before publishing a completed file.
        GZipStream? gzip = null;
        try
        {
            if (compressed) gzip = new GZipStream(context.Response.Body, CompressionLevel.Optimal, leaveOpen: true);
            await export.WriteAsync(gzip is null ? context.Response.Body : gzip, context.RequestAborted);
            if (gzip is not null) { await gzip.DisposeAsync(); gzip = null; }
        }
        catch
        {
            // A failed stream cannot be replaced with JSON or a fabricated completion footer.
            context.Abort();
            if (gzip is not null) { try { await gzip.DisposeAsync(); } catch { } }
            throw;
        }
    }
}
public sealed record SemanticReplayRequest(string SnapshotId, int Round, string ExpectedChallengeHash, GameMove[] Moves);
