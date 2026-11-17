using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed partial class SourceSemanticCatalog
{
    // Validate the native projection itself, rather than trusting a bridge's assertion about its payload.
    // At most one source node, one SOM and its bounded native evidence-ID list are materialized at a time.
    private static async Task ValidateNativeProjectionAsync(SqliteConnection db, SourceSemanticManifest manifest,
        IReadOnlyDictionary<string, FileStream> files, CancellationToken token)
    {
        var snapshot = new Snapshot(manifest.RunId, manifest.ContextId, manifest.StartedAt, manifest.ParsedAt,
            "semantic-extraction-context", "partial-static-analysis; not a filesystem observation", 0, 0, 0, 0, null, 0);
        var objects = files["objects.ndjson"]; objects.Position = 0;
        await ScanAsync(objects, MaximumExpandedBytes, MaximumRecords, (bytes, _, _) =>
        {
            token.ThrowIfCancellationRequested(); var shape = ParseRecord(bytes); var nodeId = Text(shape.GetProperty("rawSourceNode"), "id");
            var native = NativeRecord(db, files, "nodes.ndjson", "id", nodeId);
            var node = JsonSerializer.Deserialize<MapNode>(native.GetRawText(), Json) ?? throw new InvalidDataException("Null native node.");
            if (node.Properties is null) throw new InvalidDataException("Native node properties are missing.");
            var ids = new List<string>(); long evidenceBytes = 0; DateTimeOffset? from = null, to = null;
            using (var command = Command(db, "SELECT id FROM records WHERE artifact='observations.ndjson' AND subject=$n ORDER BY id", ("$n", nodeId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested(); var evidenceId = reader.GetString(0); evidenceBytes = checked(evidenceBytes + evidenceId.Length * 2L);
                    if (evidenceBytes > MaximumRecordBytes) throw new InvalidDataException("A native subject-evidence list exceeds catalog record admission bounds.");
                    ids.Add(evidenceId); var evidence = NativeRecord(db, files, "observations.ndjson", "id", evidenceId);
                    var time = evidence.GetProperty("observedAt").GetDateTimeOffset();
                    if (from is null || time < from) from = time; if (to is null || time > to) to = time;
                }
            var properties = new Dictionary<string, object?>(node.Properties, StringComparer.Ordinal);
            var prior = JsonSerializer.SerializeToElement(properties.GetValueOrDefault("evidenceIds"), Json);
            var existing = prior.ValueKind == JsonValueKind.Array
                ? prior.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray()
                : prior.ValueKind == JsonValueKind.String ? [prior.GetString()!] : Array.Empty<string>();
            properties["evidenceIds"] = existing.Concat(ids).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var expected = SemanticObjects.Create(snapshot, node with { Properties = properties }, from, to);
            if (Hash(bytes) != CanonicalJson.Sha256(expected)) throw new InvalidDataException("SOM payload differs from the native node, subject evidence, time context or supported geometry model.");
            var source = NativeRecord(db, files, "object-sources.ndjson", "id", nodeId);
            var bridge = new { node.Id, expected.SomId, NativeNodeSha256 = CanonicalJson.Sha256(node), SubjectEvidenceIds = ids.ToArray(),
                Projection = "native node plus actual subject observation IDs; original node retained in nodes.ndjson" };
            if (CanonicalJson.Sha256(source) != CanonicalJson.Sha256(bridge)) throw new InvalidDataException("Native SOM bridge differs from actual subject evidence and native node commitments.");
        }, token);
        var spans = files["source-spans.ndjson"]; spans.Position = 0;
        await ScanAsync(spans, MaximumExpandedBytes, MaximumRecords, (bytes, _, _) =>
        {
            token.ThrowIfCancellationRequested(); var span = Parse<SourceSemanticSpan>(bytes);
            var node = NativeRecord(db, files, "nodes.ndjson", "id", span.NodeId); var path = Optional(node, "path");
            var properties = node.GetProperty("properties");
            int? Integer(string name) => properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
            if (span.DocumentId is null)
            {
                if (span.SourceSha256 is not null || span.ByteStart is not null || span.ByteLength is not null ||
                    span.Status != (path is null ? "external-or-source-path-unavailable" : "outside-admitted-source-set") ||
                    span.Utf16Start != Integer("start") || span.Utf16Length != Integer("length"))
                    throw new InvalidDataException("Unavailable span claims unrelated source identity or differs from native coordinates.");
                return;
            }
            var document = NativeRecord(db, files, "documents.ndjson", "document", span.DocumentId);
            if (span.SourceSha256 is null || span.SourceSha256 != Optional(document, "sourceSha256") || path is null || path != Optional(document, "logicalUri") ||
                Text(document, "status") != "admitted-for-parser") throw new InvalidDataException("Source span is not bound to its exact admitted document/path/hash.");
            var parser = NativeRecord(db, files, "parser-documents.ndjson", "id", path);
            if (Text(parser, "status") != "admitted")
            {
                if (span.Status != "parser-rejected:" + Text(parser, "status") || span.Utf16Start is not null || span.Utf16Length is not null || span.ByteStart is not null || span.ByteLength is not null)
                    throw new InvalidDataException("Rejected parser document claims available source coordinates.");
                return;
            }
            if (Optional(parser, "expectedSha256") != span.SourceSha256 || Number(parser, "expectedByteLength") != Number(document, "sourceBytes"))
                throw new InvalidDataException("Parser and source-document commitments differ.");
            var whole = Text(node, "kind") == "SourceDocument";
            var expectedStart = whole ? 0 : Integer("start");
            var expectedLength = whole ? checked((int)Number(parser, "decodedUtf16Units")) : Integer("length");
            if (span.Utf16Start != expectedStart || span.Utf16Length != expectedLength) throw new InvalidDataException("Source span differs from the actual native node coordinates.");
            if (span.ByteStart is not null)
            {
                var sourceBytes = Number(document, "sourceBytes"); var chars = Number(parser, "decodedUtf16Units");
                if (span.ByteLength is null || span.Utf16Start is null || span.Utf16Length is null || span.ByteStart < 0 || span.ByteLength < 0 || span.ByteStart > sourceBytes - span.ByteLength ||
                    span.Utf16Start < 0 || span.Utf16Length < 0 || span.Utf16Start > chars - span.Utf16Length || span.Status != (whole ? "derived-whole-document-span" : "exact-native-source-span"))
                    throw new InvalidDataException("Source span exceeds its committed document or parser bounds.");
            }
            else if (span.ByteLength is not null || span.Status is not ("native-span-unavailable" or "native-span-invalid-or-scalar-interior"))
                throw new InvalidDataException("Unavailable source span claims an unsupported native status.");
        }, token);
    }
    private static JsonElement NativeRecord(SqliteConnection db, IReadOnlyDictionary<string, FileStream> files,
        string artifact, string identityColumn, string identity)
    {
        // identityColumn is an internal fixed identifier, never request text.
        if (identityColumn is not ("id" or "document")) throw new InvalidOperationException("Unsupported native identity column.");
        using var command = Command(db, "SELECT position,length,sha FROM records WHERE artifact=$a AND " + identityColumn + "=$id LIMIT 2", ("$a", artifact), ("$id", identity));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("A native provenance record is missing.");
        var position = reader.GetInt64(0); var length = reader.GetInt32(1); var sha = reader.GetString(2);
        if (length < 1 || length > MaximumRecordBytes || position < 0) throw new InvalidDataException("Native record extent is invalid.");
        var stream = files[artifact]; stream.Position = position; var bytes = new byte[length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != 10 || Hash(bytes) != sha || reader.Read()) throw new InvalidDataException("Native identity is ambiguous or its held record differs.");
        return ParseRecord(bytes);
    }
}
