using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed partial class SourceSemanticCatalog
{
    private const string ProjectionSchema = """
        CREATE TABLE records(artifact TEXT NOT NULL, ordinal INTEGER NOT NULL,position INTEGER NOT NULL,length INTEGER NOT NULL,sha TEXT NOT NULL,
          id TEXT,node TEXT,kind TEXT,label TEXT,path TEXT,subject TEXT,from_id TEXT,to_id TEXT,evidence TEXT,som TEXT,source TEXT,term TEXT,
          byte_start INTEGER,byte_length INTEGER,document TEXT,native_hash TEXT,state TEXT,PRIMARY KEY(artifact,ordinal)) STRICT;
        CREATE UNIQUE INDEX identity ON records(artifact,id) WHERE id IS NOT NULL;
        CREATE INDEX node_lookup ON records(artifact,node,ordinal);
        CREATE INDEX subject_lookup ON records(artifact,subject,ordinal);
        CREATE INDEX from_lookup ON records(artifact,from_id,ordinal);
        CREATE INDEX to_lookup ON records(artifact,to_id,ordinal);
        CREATE INDEX kind_lookup ON records(artifact,kind,ordinal);
        CREATE TABLE subject_evidence(node TEXT NOT NULL,evidence TEXT NOT NULL,PRIMARY KEY(node,evidence)) STRICT;
        """;
    private static async Task<ProjectionSeal> BuildProjectionAsync(string stage, SourceSemanticManifest manifest, string id,
        Dictionary<string, FileStream> files, CancellationToken token)
    {
        var path = Path.Combine(stage, ProjectionName); var expandedPath = Path.Combine(stage, ExpandedName);
        long expandedBytes = 0, expandedRecords = 0, allBytes = 0, allRecords = 0;
        var replays = new Dictionary<string, SourceLexiconResult>(StringComparer.Ordinal);
        using (var db = Connect(path, writer: true))
        using (var cancel = new CancellationScope(db, token))
        {
            Execute(db, $"PRAGMA application_id={ProjectionId}; PRAGMA user_version=1; PRAGMA max_page_count=524288;" + ProjectionSchema);
            using var transaction = db.BeginTransaction(); using var insert = new RecordInserter(db);
            foreach (var artifact in manifest.Artifacts.Where(a => !a.Path.EndsWith(".gz", StringComparison.Ordinal)))
            {
                var input = files[artifact.Path]; input.Position = 0;
                var scan = await ScanAsync(input, MaximumExpandedBytes - allBytes, MaximumRecords - allRecords, (bytes, position, ordinal) =>
                {
                    var value = ParseRecord(bytes);
                    if (artifact.Path == "lexical-replays.ndjson")
                    {
                        var replay = Parse<SourceLexiconResult>(bytes); HashId(replay.SourceSha256); HashId(replay.Policy.PolicyHash);
                        if (replay.Schema != SourceLexicon.ContractId || replay.SourceBytes < 0 || replay.ProcessedBytes < 0 || replay.ProcessedBytes > replay.SourceBytes ||
                            replay.Occurrences < 0 || replay.WordOccurrences < 0 || replay.WordOccurrences > replay.Occurrences || !replays.TryAdd(replay.SourceSha256, replay))
                            throw new InvalidDataException("Invalid or duplicate lexical replay metadata.");
                        if (replay.OutputPath is not null && (replay.OutputPath != replay.SourceSha256 + ".lexical.ndjson.gz" || replay.OutputFormat != "ndjson-gzip"))
                            throw new InvalidDataException("Lexical replay path is not its immutable source artifact.");
                        if (replay.OutputPath is null && (replay.Occurrences != 0 || replay.OutputSha256 is not null || replay.CanonicalNdjsonSha256 is not null)) throw new InvalidDataException("Unavailable lexical replay claims output records.");
                    }
                    insert.Add(artifact.Path, bytes, position, ordinal, value);
                    if (artifact.Path == "object-sources.ndjson")
                    {
                        var node = Text(value, "id"); HashId(Text(value, "nativeNodeSha256"));
                        foreach (var evidence in value.GetProperty("subjectEvidenceIds").EnumerateArray())
                            Execute(db, "INSERT INTO subject_evidence(node,evidence) VALUES($n,$e)", ("$n", node), ("$e", evidence.GetString() ?? throw new InvalidDataException("Null subject evidence.")));
                    }
                }, token);
                if (scan.Records != artifact.Records || scan.Bytes != artifact.ByteLength || scan.Hash != artifact.Sha256) throw new InvalidDataException("Artifact count/hash differs on the held stream: " + artifact.Path);
                allBytes += scan.Bytes; allRecords += scan.Records;
            }
            await using (var expanded = new FileStream(expandedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                foreach (var artifact in manifest.Artifacts.Where(a => a.Path.EndsWith(".gz", StringComparison.Ordinal)))
                {
                    var sourceHash = artifact.Path[..64];
                    if (!replays.TryGetValue(sourceHash, out var replay) || replay.OutputPath != artifact.Path || replay.OutputSha256 != artifact.Sha256 || replay.Occurrences != artifact.Records)
                        throw new InvalidDataException("Lexical gzip is not bound to a matching replay record.");
                    long expectedByte = 0, expectedUtf16 = 0, words = 0; var baseOffset = expandedBytes; var baseOrdinal = expandedRecords;
                    var input = files[artifact.Path]; input.Position = 0; using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
                    var scan = await ScanAsync(gzip, MaximumExpandedBytes - allBytes, MaximumRecords - allRecords, (bytes, offset, ordinal) =>
                    {
                        if (bytes.Length > 524288) throw new InvalidDataException("Lexical record exceeds its native bounded reader contract.");
                        var occurrence = Parse<SourceLexicalOccurrence>(bytes);
                        ValidateOccurrence(occurrence, replay, ordinal, expectedByte, expectedUtf16);
                        expectedByte = checked(expectedByte + occurrence.ByteLength); expectedUtf16 = checked(expectedUtf16 + occurrence.Utf16Length);
                        if (occurrence.Kind == "word") words++;
                        if (occurrence.Kind == "word" && !occurrence.Fragmented && occurrence.TermId is not null)
                            insert.Add(ExpandedName, bytes, baseOffset + offset, baseOrdinal + ordinal, JsonSerializer.SerializeToElement(occurrence, Json));
                    }, token, expanded);
                    if (scan.Hash != replay.CanonicalNdjsonSha256 || scan.Records != artifact.Records || expectedByte != replay.ProcessedBytes || expectedUtf16 != replay.DecodedUtf16Units || words != replay.WordOccurrences)
                        throw new InvalidDataException("Lexical decompression hash, sequence extent or count differs from the sealed replay.");
                    expandedBytes += scan.Bytes; expandedRecords += scan.Records; allBytes += scan.Bytes; allRecords += scan.Records;
                }
                if (replays.Values.Any(r => r.OutputPath is not null && !files.ContainsKey(r.OutputPath))) throw new InvalidDataException("A replay's retained artifact is missing.");
                await expanded.FlushAsync(token); expanded.Flush(true);
            }
            cancel.Run(() => { ValidateProjection(db, manifest); return true; });
            await ValidateNativeProjectionAsync(db, manifest, files, token);
            cancel.Run(() => { transaction.Commit(); return true; });
            using var integrity = Command(db, "PRAGMA integrity_check"); if (!Equals(integrity.ExecuteScalar(), "ok")) throw new InvalidDataException("Derived semantic index failed SQLite integrity check.");
        }
        using var indexFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var expandedFile = new FileStream(expandedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new("filemapper.semantic-catalog-projection/1", id, await HashStreamAsync(indexFile, token), indexFile.Length,
            await HashStreamAsync(expandedFile, token), expandedBytes, expandedRecords);
    }
    private static void ValidateOccurrence(SourceLexicalOccurrence o, SourceLexiconResult replay, long ordinal, long expectedByte, long expectedUtf16)
    {
        if (o.Schema != SourceLexicon.ContractId || o.SourceSha256 != replay.SourceSha256 || o.PolicyHash != replay.Policy.PolicyHash || o.Ordinal != ordinal ||
            o.ByteStart != expectedByte || o.Utf16Start != expectedUtf16 || o.ByteLength <= 0 || o.ByteLength > replay.SourceBytes - o.ByteStart || o.Utf16Length < 0 ||
            o.StartLine < 1 || o.StartColumn < 1 || o.ColumnUnit != "utf16-code-unit" || o.Text is not null && o.Text.Length != o.Utf16Length || o.Text is null && o.Utf16Length != 0 ||
            o.Kind is not ("word" or "whitespace" or "punctuation" or "symbol" or "control" or "bom" or "binary-marker" or "unsupported-encoding" or "invalid-byte-sequence"))
            throw new InvalidDataException("Invalid lexical occurrence or noncontiguous source coordinates.");
        var occurrenceId = "source-occurrence:" + CanonicalJson.Sha256(new { model = SourceLexicon.ContractId, policyHash = o.PolicyHash, source = o.SourceSha256, ordinal, kind = o.Kind, byteStart = o.ByteStart, byteLength = o.ByteLength });
        var term = o.Kind == "word" && !o.Fragmented ? o.Text?.Normalize(NormalizationForm.FormKC).ToLowerInvariant() : null;
        var termId = term is null ? null : "source-term:" + CanonicalJson.Sha256(new { Model = SourceLexicon.TermContractId, NormalizedText = term });
        if (o.OccurrenceId != occurrenceId || o.TermId != termId || o.NormalizedTerm != term || o.Kind == "word" && !o.Fragmented && term is null ||
            o.RawBytesBase64 is not null && Convert.FromBase64String(o.RawBytesBase64).LongLength != o.ByteLength)
            throw new InvalidDataException("Lexical identity, term or raw-marker extent differs.");
    }
    private sealed class RecordInserter : IDisposable
    {
        private readonly SqliteCommand command;
        private static readonly string[] Columns = ["artifact", "ordinal", "position", "length", "sha", "id", "node", "kind", "label", "path", "subject", "from_id", "to_id", "evidence", "som", "source", "term", "byte_start", "byte_length", "document", "native_hash", "state"];
        internal RecordInserter(SqliteConnection db)
        {
            command = db.CreateCommand(); command.CommandText = "INSERT INTO records(" + string.Join(',', Columns) + ") VALUES(" + string.Join(',', Columns.Select(n => "$" + n)) + ")";
            foreach (var column in Columns) command.Parameters.AddWithValue("$" + column, DBNull.Value); command.Prepare();
        }
        private void Put(string key, object? value) => command.Parameters["$" + key].Value = value ?? DBNull.Value;
        internal void Add(string artifact, byte[] bytes, long offset, long ordinal, JsonElement value)
        {
            foreach (SqliteParameter parameter in command.Parameters) parameter.Value = DBNull.Value;
            Put("artifact", artifact); Put("ordinal", ordinal); Put("position", offset); Put("length", bytes.Length); Put("sha", Hash(bytes));
            switch (artifact)
            {
                case "nodes.ndjson": Put("id", Text(value, "id")); Put("kind", Text(value, "kind")); Put("label", Text(value, "name")); Put("path", Optional(value, "path")); break;
                case "edges.ndjson": Put("id", Text(value, "id")); Put("kind", Text(value, "kind")); Put("from_id", Text(value, "fromId")); Put("to_id", Text(value, "toId")); Put("evidence", Text(value, "evidenceId")); break;
                case "observations.ndjson": Put("id", Text(value, "id")); Put("subject", Text(value, "subjectId")); Put("state", Text(value, "completeness")); break;
                case "findings.ndjson": Put("id", Text(value, "id")); break;
                case "objects.ndjson": Put("id", Text(value, "somId")); Put("som", Text(value, "somId")); Put("node", Text(value.GetProperty("rawSourceNode"), "id")); Put("kind", Text(value.GetProperty("rawSourceNode"), "kind")); Put("label", Text(value, "label")); Put("path", Optional(value.GetProperty("rawSourceNode"), "path")); break;
                case "object-sources.ndjson": Put("id", Text(value, "id")); Put("node", Text(value, "id")); Put("som", Text(value, "somId")); Put("native_hash", Text(value, "nativeNodeSha256")); break;
                case "source-spans.ndjson":
                    Put("id", Text(value, "nodeId")); Put("node", Text(value, "nodeId")); Put("source", Optional(value, "sourceSha256")); Put("document", Optional(value, "documentId")); Put("state", Text(value, "status"));
                    Put("byte_start", value.GetProperty("byteStart").ValueKind == JsonValueKind.Null ? null : Number(value, "byteStart")); Put("byte_length", value.GetProperty("byteLength").ValueKind == JsonValueKind.Null ? null : Number(value, "byteLength")); break;
                case "word-links.ndjson":
                    Put("node", Text(value, "nodeId")); Put("som", Text(value, "somId")); Put("source", Text(value, "sourceSha256")); Put("document", Text(value, "documentId")); Put("term", Text(value, "termId"));
                    Put("subject", Text(value, "occurrenceId")); Put("byte_start", Number(value, "wordByteStart")); Put("byte_length", Number(value, "wordByteLength"));
                    if (Text(value, "relation") != "source-span-contains-lexical-word; not a resolved symbol binding") throw new InvalidDataException("Unsupported lexical containment relation."); break;
                case ExpandedName:
                    Put("id", Text(value, "occurrenceId")); Put("source", Text(value, "sourceSha256")); Put("term", Text(value, "termId")); Put("byte_start", Number(value, "byteStart")); Put("byte_length", Number(value, "byteLength")); break;
                case "documents.ndjson": Put("id", Text(value, "entryId")); Put("document", Optional(value, "documentId")); Put("source", Optional(value, "sourceSha256")); Put("state", Text(value, "status")); break;
                case "parser-documents.ndjson": Put("id", Text(value, "logicalPath")); Put("state", Text(value, "status")); break;
            }
            command.ExecuteNonQuery();
        }
        public void Dispose() => command.Dispose();
    }
    private static void ValidateProjection(SqliteConnection db, SourceSemanticManifest manifest)
    {
        var c = manifest.Coverage;
        foreach (var (artifact, expected) in new[] { ("nodes.ndjson", c.Nodes), ("edges.ndjson", c.Edges), ("observations.ndjson", c.Observations), ("findings.ndjson", c.Findings), ("objects.ndjson", c.Nodes), ("object-sources.ndjson", c.Nodes), ("source-spans.ndjson", c.Nodes), ("word-links.ndjson", c.LexicalLinks), ("documents.ndjson", c.SelectedEntries) })
            if (Scalar(db, "SELECT count(*) FROM records WHERE artifact=$a", ("$a", artifact)) != expected) throw new InvalidDataException("Artifact coverage differs: " + artifact);
        void None(string sql, string message) { if (Scalar(db, sql) != 0) throw new InvalidDataException(message); }
        None("SELECT count(*) FROM records s LEFT JOIN records n ON n.artifact='nodes.ndjson' AND n.id=s.node LEFT JOIN records o ON o.artifact='objects.ndjson' AND o.node=s.node AND o.som=s.som WHERE s.artifact='object-sources.ndjson' AND (n.id IS NULL OR n.sha<>s.native_hash OR o.id IS NULL)", "Native node/SOM commitment differs.");
        None("SELECT count(*) FROM records o LEFT JOIN records s ON s.artifact='object-sources.ndjson' AND s.node=o.node AND s.som=o.som WHERE o.artifact='objects.ndjson' AND s.id IS NULL", "Unmapped SOM object.");
        None("SELECT count(*) FROM subject_evidence s LEFT JOIN records e ON e.artifact='observations.ndjson' AND e.id=s.evidence AND e.subject=s.node WHERE e.id IS NULL", "SOM subject evidence is not an actual observation of that node.");
        None("SELECT count(*) FROM records e JOIN records n ON n.artifact='nodes.ndjson' AND n.id=e.subject LEFT JOIN subject_evidence s ON s.node=n.id AND s.evidence=e.id WHERE e.artifact='observations.ndjson' AND s.evidence IS NULL", "SOM source evidence omitted an actual native subject observation.");
        None("SELECT count(*) FROM records s LEFT JOIN records n ON n.artifact='nodes.ndjson' AND n.id=s.node WHERE s.artifact='source-spans.ndjson' AND (n.id IS NULL OR s.byte_start<0 OR s.byte_length<0 OR (s.byte_start IS NULL)<>(s.byte_length IS NULL))", "Source span identity or extent differs.");
        None($"SELECT count(*) FROM records w LEFT JOIN records l ON l.artifact='{ExpandedName}' AND l.id=w.subject AND l.source=w.source AND l.term=w.term AND l.byte_start=w.byte_start AND l.byte_length=w.byte_length LEFT JOIN records s ON s.artifact='source-spans.ndjson' AND s.node=w.node AND s.source=w.source AND s.document=w.document LEFT JOIN records o ON o.artifact='objects.ndjson' AND o.node=w.node AND o.som=w.som WHERE w.artifact='word-links.ndjson' AND (l.id IS NULL OR s.id IS NULL OR o.id IS NULL OR s.byte_start IS NULL OR w.byte_start<s.byte_start OR w.byte_length>s.byte_length OR w.byte_start-s.byte_start>s.byte_length-w.byte_length)", "Word link lacks its exact occurrence, SOM or containing source span.");
        if (Scalar(db, "SELECT count(*) FROM records WHERE artifact='source-spans.ndjson' AND byte_start IS NOT NULL") != c.ExactNodeSpans ||
            Scalar(db, "SELECT count(*) FROM records WHERE artifact='observations.ndjson' AND state<>'complete'") != c.PartialObservations ||
            Scalar(db, "SELECT count(*) FROM records WHERE artifact='parser-documents.ndjson' AND state='admitted'") != c.AdmittedDocuments)
            throw new InvalidDataException("Native span/observation/parser coverage differs.");
        var endpoints = Scalar(db, "SELECT count(*) FROM records e LEFT JOIN records f ON f.artifact='nodes.ndjson' AND f.id=e.from_id LEFT JOIN records t ON t.artifact='nodes.ndjson' AND t.id=e.to_id WHERE e.artifact='edges.ndjson' AND (f.id IS NULL OR t.id IS NULL)");
        var evidence = Scalar(db, "SELECT count(*) FROM records e LEFT JOIN records v ON v.artifact='observations.ndjson' AND v.id=e.evidence WHERE e.artifact='edges.ndjson' AND v.id IS NULL");
        if (endpoints != c.EdgesWithMissingEndpoints || evidence != c.EdgesWithMissingEvidence) throw new InvalidDataException("Edge endpoint/evidence coverage differs.");
    }
}
