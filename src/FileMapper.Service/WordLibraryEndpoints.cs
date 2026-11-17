using FileMapper.Engine;
namespace FileMapper.Service;
public sealed record WordLibraryConnectRequest(string Root);
public static class WordLibraryEndpoints
{
    public static void MapWordLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/word-library", (WordLibraryWorkspace w) => w.Status()).RequireCapability("read");
        app.MapGet("/api/word-library/editions", (WordLibraryWorkspace w, int? offset, int? limit) => w.Page(offset ?? 0, limit ?? 20)).RequireCapability("read");
        app.MapGet("/api/word-library/search", (WordLibraryWorkspace w, string word, string? kind, long? offset, int? limit, string? mode, CancellationToken ct) => w.SearchKind(kind ?? "word", word, offset ?? 0, limit ?? 30, ct, mode ?? "occurrences")).RequireCapability("read");
        app.MapPost("/api/word-library/connection", async (WordLibraryWorkspace w, WordLibraryConnectRequest request, CancellationToken ct) => { await w.ConnectAsync(request.Root, ct); return Results.Ok(w.Status()); }).RequireCapability("scan");
    }
}
