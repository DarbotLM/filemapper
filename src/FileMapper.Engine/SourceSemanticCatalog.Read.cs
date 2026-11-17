using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed partial class SourceSemanticCatalog
{
    private static void PageArguments(int offset, int limit)
    { if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit), "Pages require nonnegative offset and limit 1–200."); }
    private RegistryRow? Find(string id, CancellationToken token)
    {
        HashId(id); token.ThrowIfCancellationRequested(); if (!File.Exists(DatabasePath)) return null;
        using var root = new HeldDirectory(RootDirectory); using var db = Connect(DatabasePath, writer: false); using var cancel = new CancellationScope(db, token);
        return cancel.Run(() => { CheckDatabase(db, RegistryId); return RegistryFind(db, id); });
    }
    public SourceSemanticCatalogPage<SourceSemanticCatalogAnalysis> ListAnalyses(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        PageArguments(offset, limit); var token = cancellationToken; token.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath)) return new([], 0, offset, limit, null, false);
        using var root = new HeldDirectory(RootDirectory); using var db = Connect(DatabasePath, writer: false); using var cancel = new CancellationScope(db, token);
        return cancel.Run(() =>
        {
            CheckDatabase(db, RegistryId); using var transaction = db.BeginTransaction(deferred: true); var total = Scalar(db, "SELECT count(*) FROM analyses");
            using var command = Command(db, "SELECT id,seal_hash,imported_at,sealed_bytes FROM analyses ORDER BY imported_at DESC,id LIMIT $limit OFFSET $offset", ("$limit", limit), ("$offset", offset));
            using var reader = command.ExecuteReader(); var items = new List<SourceSemanticCatalogAnalysis>(); long bytes = 0; var bounded = false;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var row = new RegistryRow(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(3));
                var item = ReadAnalysis(row); var length = Canonical(item).LongLength;
                if (length > MaximumPageBytes - bytes) { bounded = true; break; } bytes += length; items.Add(item);
            }
            return new SourceSemanticCatalogPage<SourceSemanticCatalogAnalysis>(items, total, offset, limit, Next(offset, items.Count, total), bounded);
        });
    }
    public SourceSemanticCatalogAnalysis? GetAnalysis(string id, CancellationToken cancellationToken = default)
    { var row = Find(id, cancellationToken); cancellationToken.ThrowIfCancellationRequested(); return row is null ? null : ReadAnalysis(row); }
    private SourceSemanticCatalogAnalysis ReadAnalysis(RegistryRow row)
    {
        using var directory = new HeldDirectory(Path.Combine(RootDirectory, "analyses", row.Id)); using var file = directory.Open("manifest.json");
        var manifest = ValidateManifest(BoundedRead(file, MaximumManifestBytes), row.Id);
        using var sealFile = directory.Open(SealName); var sealBytes = BoundedRead(sealFile, MaximumManifestBytes);
        if (Hash(sealBytes) != row.SealHash || Parse<ProjectionSeal>(sealBytes).AnalysisId != row.Id) throw new InvalidDataException("Managed catalog seal differs from registration.");
        return new(row.Id, manifest, row.ImportedAt, row.Bytes);
    }
    public SourceSemanticCatalogPage<JsonElement> Nodes(string id, string? kind = null, string? search = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => Read(id, cancellationToken, context => context.Page("nodes.ndjson", Filters(kind, search), FilterValues(kind, search), offset, limit));
    public SourceSemanticCatalogPage<JsonElement> Objects(string id, string? kind = null, string? search = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        => Read(id, cancellationToken, context => context.Page("objects.ndjson", Filters(kind, search), FilterValues(kind, search), offset, limit));
    public SourceSemanticCatalogPage<JsonElement> Observations(string id, string? subjectId = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => Read(id, cancellationToken, context => context.Page("observations.ndjson", subjectId is null ? "1" : "r.subject=$subject", subjectId is null ? [] : [("$subject", subjectId)], offset, limit));
    public SourceSemanticCatalogPage<JsonElement> Edges(string id, string? nodeId = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => Read(id, cancellationToken, context => context.Page("edges.ndjson", nodeId is null ? "1" : "(r.from_id=$node OR r.to_id=$node)", nodeId is null ? [] : [("$node", nodeId)], offset, limit));
    public SourceSemanticCatalogPage<SourceSemanticCatalogWord> WordLinks(string id, string nodeId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => Read(id, cancellationToken, context => context.Words(nodeId, offset, limit));
    public SourceSemanticCatalogNode? GetNode(string id, string nodeId, int relatedLimit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId); PageArguments(0, relatedLimit);
        return Read(id, cancellationToken, context =>
        {
            var node = context.Single("nodes.ndjson", "r.id=$id", ("$id", nodeId)); if (node is null) return null;
            var shape = context.Single("objects.ndjson", "r.node=$id", ("$id", nodeId));
            var span = context.Single("source-spans.ndjson", "r.node=$id", ("$id", nodeId));
            var observations = context.Page("observations.ndjson", "r.subject=$node", [("$node", nodeId)], 0, relatedLimit);
            var edges = context.Page("edges.ndjson", "(r.from_id=$node OR r.to_id=$node)", [("$node", nodeId)], 0, relatedLimit);
            var words = context.Words(nodeId, 0, relatedLimit);
            var edgeEvidence = context.Page("observations.ndjson", "r.id IN(SELECT evidence FROM records WHERE artifact='edges.ndjson' AND from_id=$node UNION SELECT evidence FROM records WHERE artifact='edges.ndjson' AND to_id=$node)", [("$node", nodeId)], 0, relatedLimit);
            return new SourceSemanticCatalogNode(node.Value, shape, span, observations, edges, words, edgeEvidence);
        });
    }
    public SourceSemanticCatalogArtifact OpenArtifact(string id, string artifactName, CancellationToken cancellationToken = default)
    {
        HashId(id); var row = Find(id, cancellationToken) ?? throw new KeyNotFoundException("Analysis is not registered.");
        var context = new ReadContext(this, row, cancellationToken);
        try
        {
            var descriptor = context.Manifest.Artifacts.SingleOrDefault(a => a.Path == artifactName);
            if (artifactName == "manifest.json") descriptor = new("manifest.json", id, context.ManifestBytes, 1);
            if (descriptor is null) throw new KeyNotFoundException("Artifact is not in the sealed manifest allowlist.");
            var stream = context.File(descriptor.Path);
            if (stream.Length != descriptor.ByteLength || HashStream(stream, cancellationToken) != descriptor.Sha256) throw new InvalidDataException("Managed download artifact commitment differs.");
            return new(stream, descriptor, context);
        }
        catch { context.Dispose(); throw; }
    }
    private static string Filters(string? kind, string? search)
    {
        if (kind?.Length > 1024 || search?.Length > 1024) throw new ArgumentException("Catalog search filters are limited to 1024 characters.");
        return (kind is null ? "1" : "r.kind=$kind") + (search is null ? "" : " AND (r.label LIKE $search ESCAPE char(92) OR r.path LIKE $search ESCAPE char(92) OR r.id LIKE $search ESCAPE char(92))");
    }
    private static (string, object?)[] FilterValues(string? kind, string? search)
    {
        var values = new List<(string, object?)>(); if (kind is not null) values.Add(("$kind", kind));
        if (search is not null) values.Add(("$search", "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        return values.ToArray();
    }
    private static int? Next(int offset, int count, long total) => (long)offset + count < total ? checked(offset + count) : null;
    private T Read<T>(string id, CancellationToken token, Func<ReadContext, T> action)
    {
        var row = Find(id, token) ?? throw new KeyNotFoundException("Analysis is not registered.");
        using var context = new ReadContext(this, row, token); return context.Run(action);
    }
    private sealed class ReadContext : IDisposable
    {
        private readonly HeldDirectory directory; private readonly Dictionary<string, FileStream> files = new(StringComparer.Ordinal);
        private readonly ProjectionSeal seal; private readonly CancellationToken token;
        private SqliteConnection? database; private CancellationScope? cancellation; private SqliteTransaction? transaction;
        private int budget = MaximumPageBytes;
        internal SourceSemanticManifest Manifest { get; }
        internal long ManifestBytes { get; }
        private SqliteConnection Db => database ?? throw new ObjectDisposedException(nameof(ReadContext));
        internal ReadContext(SourceSemanticCatalog owner, RegistryRow row, CancellationToken token)
        {
            this.token = token; token.ThrowIfCancellationRequested(); directory = new HeldDirectory(Path.Combine(owner.RootDirectory, "analyses", row.Id));
            try
            {
                var manifest = File("manifest.json"); var bytes = BoundedRead(manifest, MaximumManifestBytes); ManifestBytes = bytes.LongLength; Manifest = ValidateManifest(bytes, row.Id);
                var sealBytes = BoundedRead(File(SealName), MaximumManifestBytes);
                if (Hash(sealBytes) != row.SealHash) throw new InvalidDataException("Managed projection seal differs from registration.");
                seal = Parse<ProjectionSeal>(sealBytes);
                if (seal.Schema != "filemapper.semantic-catalog-projection/1" || seal.AnalysisId != row.Id) throw new InvalidDataException("Managed projection identity differs.");
                var index = File(ProjectionName); if (index.Length != seal.IndexBytes || HashStream(index, token) != seal.IndexSha256) throw new InvalidDataException("Managed query projection commitment differs.");
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) if (System.IO.File.Exists(Path.Combine(directory.Path, ProjectionName + suffix))) throw new InvalidDataException("Immutable projection cannot have SQLite sidecars.");
                database = Connect(Path.Combine(directory.Path, ProjectionName), writer: false); cancellation = new CancellationScope(database, token);
                cancellation.Run(() => { CheckDatabase(database, ProjectionId); transaction = database.BeginTransaction(deferred: true); return true; });
            }
            catch { Dispose(); throw; }
        }
        internal T Run<T>(Func<ReadContext, T> action) => cancellation!.Run(() => action(this));
        internal FileStream File(string name)
        {
            token.ThrowIfCancellationRequested(); if (files.TryGetValue(name, out var file)) return file;
            file = directory.Open(name);
            try
            {
                long? expected = name == ExpandedName ? seal.ExpandedBytes : Manifest?.Artifacts.FirstOrDefault(a => a.Path == name)?.ByteLength;
                if (expected.HasValue && file.Length != expected.Value) throw new InvalidDataException("Managed record file byte length differs.");
                files.Add(name, file); return file;
            }
            catch { file.Dispose(); throw; }
        }
        private JsonElement Record(SqliteDataReader reader, int start = 0)
        {
            var artifact = reader.GetString(start); var position = reader.GetInt64(start + 1); var length = reader.GetInt32(start + 2); var hash = reader.GetString(start + 3);
            if (length < 1 || length > MaximumRecordBytes || position < 0) throw new InvalidDataException("Invalid projected record extent.");
            var file = File(artifact); if (position > file.Length - length - 1) throw new InvalidDataException("Projected record exceeds its retained artifact.");
            var bytes = new byte[length]; file.Position = position; file.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            if (file.ReadByte() != 10 || Hash(bytes) != hash) throw new InvalidDataException("Managed native record differs from the validated projection.");
            return ParseRecord(bytes);
        }
        internal JsonElement? Single(string artifact, string where, params (string, object?)[] values)
        {
            using var command = Command(Db, "SELECT r.artifact,r.position,r.length,r.sha FROM records r WHERE r.artifact=$a AND " + where + " ORDER BY r.ordinal LIMIT 2", values.Append(("$a", (object?)artifact)).ToArray());
            using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            var length = reader.GetInt32(2); if (length > budget) throw new InvalidDataException("A selected native object exceeds the catalog response byte limit; use the original artifact download.");
            var record = Record(reader); budget -= length; if (reader.Read()) throw new InvalidDataException("Selected native identity is ambiguous."); return record;
        }
        internal SourceSemanticCatalogPage<JsonElement> Page(string artifact, string where, (string, object?)[] values, int offset, int limit)
        {
            PageArguments(offset, limit); token.ThrowIfCancellationRequested(); var parameters = values.Append(("$a", (object?)artifact)).ToArray();
            var filter = "r.artifact=$a AND " + where; var total = Scalar(Db, "SELECT count(*) FROM records r WHERE " + filter, parameters);
            using var command = Command(Db, "SELECT r.artifact,r.position,r.length,r.sha FROM records r WHERE " + filter + " ORDER BY r.ordinal LIMIT $limit OFFSET $offset", parameters.Concat([("$limit", (object?)limit), ("$offset", offset)]).ToArray());
            using var reader = command.ExecuteReader(); var items = new List<JsonElement>(); var bounded = false;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var length = reader.GetInt32(2);
                if (length > MaximumPageBytes) throw new InvalidDataException("A native record exceeds the catalog page byte limit; use the original artifact download.");
                if (length > budget) { bounded = true; break; } items.Add(Record(reader)); budget -= length;
            }
            return new(items, total, offset, limit, Next(offset, items.Count, total), bounded);
        }
        internal SourceSemanticCatalogPage<SourceSemanticCatalogWord> Words(string nodeId, int offset, int limit)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId); PageArguments(offset, limit); token.ThrowIfCancellationRequested();
            var total = Scalar(Db, "SELECT count(*) FROM records WHERE artifact='word-links.ndjson' AND node=$node", ("$node", nodeId));
            using var command = Command(Db, $"SELECT w.artifact,w.position,w.length,w.sha,l.artifact,l.position,l.length,l.sha FROM records w JOIN records l ON l.artifact='{ExpandedName}' AND l.id=w.subject AND l.source=w.source WHERE w.artifact='word-links.ndjson' AND w.node=$node ORDER BY w.ordinal LIMIT $limit OFFSET $offset", ("$node", nodeId), ("$limit", limit), ("$offset", offset));
            using var reader = command.ExecuteReader(); var items = new List<SourceSemanticCatalogWord>(); var bounded = false;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var length = checked(reader.GetInt32(2) + reader.GetInt32(6));
                if (length > MaximumPageBytes) throw new InvalidDataException("A retained word result exceeds the response byte bound.");
                if (length > budget) { bounded = true; break; } items.Add(new(Record(reader), Record(reader, 4))); budget -= length;
            }
            return new(items, total, offset, limit, Next(offset, items.Count, total), bounded);
        }
        public void Dispose()
        {
            transaction?.Dispose(); cancellation?.Dispose(); database?.Dispose(); database = null;
            foreach (var file in files.Values) file.Dispose(); files.Clear(); directory.Dispose();
        }
    }
}
