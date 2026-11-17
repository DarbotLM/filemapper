using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using FileMapper.Engine;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Service;

internal static class HarvestEndpoints
{
    public static void MapHarvestEndpoints(this WebApplication app, SourceHarvestSelection selection, SourceHarvestConnectionStore connections)
    {
        ISourceHarvestReader Reader() => selection.CreateReader();
        app.MapGet("/api/harvest/connection", async (CancellationToken cancellationToken) =>
        {
            SourceHarvestConnection? saved = null;
            var savedState = "absent";
            try { saved = await connections.LoadAsync(cancellationToken); if (saved is not null) savedState = "configured"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            { savedState = "invalid"; }
            var active = selection.Info;
            var effectiveWorkspace = saved?.Connection.WorkspacePath ?? Path.Combine(connections.ServiceDataDirectory, "sources");
            var effectiveMode = saved?.Connection.Mode ?? "ingestion";
            var effectiveIndex = saved?.Connection.CompactIndexPath ?? Path.Combine(effectiveWorkspace, "source-harvest.db");
            var sameSource = savedState != "invalid" && effectiveMode == active.Mode &&
                SamePath(effectiveWorkspace, selection.WorkspacePath) && SamePath(effectiveIndex, selection.IndexPath) &&
                saved?.Connection.CompactSha256 == active.CompactSha256 && saved?.Connection.CompactByteLength == active.CompactByteLength;
            return new
            {
                schema = "filemapper.source-connection-status/1", active.Origin, active.Mode,
                selection.WorkspacePath, selection.IndexPath,
                workspaceAvailable = Directory.Exists(selection.WorkspacePath),
                workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(selection.WorkspacePath)),
                indexName = Path.GetFileName(selection.IndexPath), indexAvailable = selection.IndexExists,
                active.ConnectionId, active.ConnectedAt, active.CompactSha256, active.CompactByteLength, active.ImmutableIndexPinned,
                savedState, savedConnectionId = saved?.ConnectionId,
                configurationChanged = savedState == "invalid" || saved?.ConnectionId != active.ConnectionId,
                savedMatchesActive = saved is not null && sameSource,
                restartRequired = active.Origin != "explicit" && !sameSource,
                savedIgnoredByOverride = active.Origin == "explicit" && savedState != "absent",
                applicationPolicy = "The source selection is fixed for this service session. Restart to apply a saved selection; explicit startup arguments take precedence."
            };
        }).RequireCapability("read");
        app.MapGet("/api/harvest/captures", (int? offset, int? limit, CancellationToken cancellationToken) =>
        {
            if ((offset ?? 0) < 0 || (limit ?? 100) is < 1 or > 500) throw new ArgumentException("Invalid source capture page.");
            return selection.Info.Origin != "default" || selection.IndexExists ? Reader().ListCaptures(offset ?? 0, limit ?? 100, cancellationToken)
                : new SourceIndexPage<SourceIndexedCapture>([], 0, offset ?? 0, limit ?? 100);
        }).RequireCapability("read");
        app.MapGet("/api/harvest/captures/{id}/counts", (string id, CancellationToken cancellationToken) => Reader().Counts(id, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/harvest/captures/{id}/documents", (string id, string? search, bool? supportedOnly, int? offset, int? limit, CancellationToken cancellationToken) =>
            Reader().Documents(id, search, supportedOnly ?? false, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
        app.MapGet("/api/harvest/captures/{id}/terms", (string id, string? search, int? offset, int? limit, CancellationToken cancellationToken) =>
        {
            var page = Reader().Terms(id, search, offset ?? 0, limit ?? 24, cancellationToken);
            return new SourceIndexPage<SourceWordShape>(page.Items.Select(Project).ToArray(), page.Total, page.Offset, page.Limit);
        }).RequireCapability("read");
        app.MapGet("/api/harvest/captures/{id}/occurrences", (string id, string term, int? offset, int? limit, CancellationToken cancellationToken) =>
            Reader().Occurrences(id, term, offset ?? 0, limit ?? 50, cancellationToken)).RequireCapability("read");
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static SourceWordShape Project(SourceTermCount term)
    {
        var runes = term.Term.EnumerateRunes().ToArray();
        var signature = (runes.Length <= 3 ? 1 : runes.Length <= 7 ? 2 : 4)
            | (runes.Any(Rune.IsLetter) ? 8 : 0) | (runes.Any(Rune.IsDigit) ? 16 : 0)
            | (runes.Any(r => !r.IsAscii) ? 32 : 0)
            | (runes.Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) ? 64 : 0)
            | (runes.Length > 0 && Rune.IsDigit(runes[0]) ? 128 : 0) | (runes.Length > 0 && Rune.IsDigit(runes[^1]) ? 256 : 0);
        string[] shapes = ["I", "O", "T", "L", "J", "S", "Z", "pillar", "stair"];
        var shape = shapes[signature % shapes.Length];
        return new(term, "source-som:" + CanonicalJson.Sha256(new { Model = "filemapper.source-word-shape/1", term.TermId }),
            shape, ShapeCatalog.ColorFor(shape), signature, Convert.ToString(signature, 2).PadLeft(9, '0'), ShapeCatalog.Shapes[shape],
            "filemapper.source-word-shape/1; lexical features only; collisions do not establish equivalence");
    }
}

internal sealed record SourceWordShape(SourceTermCount Source, string SomId,
    [property: JsonPropertyName("shape_type")] string ShapeType,
    [property: JsonPropertyName("shape_color")] string ShapeColor,
    int Signature9, string SignatureBits, IReadOnlyList<Voxel> Cells, string Model);
