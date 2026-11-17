using System.Text.Json;
using FileMapper.Domain;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public sealed record StoredFindingPrefix(string Id, string RuleId, string Completeness, bool DeclaredTruncated,
        int StoredRouteSteps, IReadOnlyList<FindingRouteStep> Steps);
    public sealed partial class SemanticReader
    {
        public Evidence? MatchingFilesystemMetadata(string snapshotId, string nodeId, string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Query("""
                SELECT payload FROM observations WHERE snapshot_id=$s AND subject_id=$n
                AND predicate='filesystem.metadata' AND completeness='complete' AND error_code IS NULL
                AND json_extract(payload,'$.sourceUri')=$path ORDER BY id LIMIT 1
                """, ("$s", snapshotId), ("$n", nodeId), ("$path", path));
            var payload = command.ExecuteScalar() as string;
            cancellationToken.ThrowIfCancellationRequested();
            if (payload is null) return null;
            var evidence = JsonSerializer.Deserialize<Evidence>(payload, Json)!;
            return evidence.SnapshotId == snapshotId && evidence.SubjectId == nodeId && evidence.SourceUri == path
                && evidence.Predicate == "filesystem.metadata" && evidence.Completeness == "complete" && evidence.ErrorCode is null ? evidence : null;
        }

        /// <summary>Projects a bounded route prefix in SQL; never materializes the unreturned witness suffix in managed memory.</summary>
        public Page<StoredFindingPrefix> FindingPrefixes(string snapshotId, int offset = 0, int limit = 500,
            int maximumSteps = 8, CancellationToken cancellationToken = default)
        {
            ValidatePage(offset, limit);
            if (limit > 500 || maximumSteps is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(limit));
            cancellationToken.ThrowIfCancellationRequested();
            using var count = Query("SELECT COUNT(*) FROM findings WHERE snapshot_id=$s", ("$s", snapshotId));
            var total = (long)count.ExecuteScalar()!;
            using var command = Query("""
                SELECT f.id,f.rule_id,COALESCE(json_extract(f.payload,'$.completeness'),'unknown'),
                  COALESCE(json_extract(f.payload,'$.truncated'),0),COALESCE(json_array_length(f.payload,'$.route'),0),
                  (SELECT json_group_array(json(part.value)) FROM
                    (SELECT value FROM json_each(f.payload,'$.route') WHERE CAST(key AS INTEGER)<$steps ORDER BY CAST(key AS INTEGER)) part)
                FROM findings f WHERE f.snapshot_id=$s ORDER BY f.id LIMIT $l OFFSET $o
                """, ("$s", snapshotId), ("$l", limit), ("$o", offset), ("$steps", maximumSteps));
            using var rows = command.ExecuteReader(); var result = new List<StoredFindingPrefix>();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3) != 0,
                    rows.GetInt32(4), JsonSerializer.Deserialize<FindingRouteStep[]>(rows.GetString(5), Json)!));
            }
            return new(result, total, offset, limit);
        }

        public Page<MapEdge> OutgoingPage(string snapshotId, string nodeId, int offset = 0, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            ValidatePage(offset, limit);
            if (limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            cancellationToken.ThrowIfCancellationRequested();
            using var count = Query("SELECT COUNT(*) FROM edges WHERE snapshot_id=$s AND from_id=$n", ("$s", snapshotId), ("$n", nodeId));
            var total = (long)count.ExecuteScalar()!;
            using var command = Query("SELECT * FROM edges INDEXED BY ix_edges_from WHERE snapshot_id=$s AND from_id=$n ORDER BY id LIMIT $l OFFSET $o",
                ("$s", snapshotId), ("$n", nodeId), ("$l", limit), ("$o", offset));
            using var rows = command.ExecuteReader(); var result = new List<MapEdge>();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetDouble(6),
                    rows.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(rows.GetString(7), Json)));
            }
            return new(result, total, offset, limit);
        }
        public ObservedNode? Node(string snapshotId, string nodeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MapNode value;
            using (var command = Query("SELECT * FROM nodes WHERE snapshot_id=$s AND id=$n", ("$s", snapshotId), ("$n", nodeId)))
            using (var row = command.ExecuteReader())
            {
                if (!row.Read()) return null;
                value = ReadNode(row);
            }
            using var period = Query("SELECT MIN(filemapper_utc_ticks(observed_at)),MAX(filemapper_utc_ticks(observed_at)),COUNT(*) FROM observations WHERE snapshot_id=$s AND subject_id=$n", ("$s", snapshotId), ("$n", nodeId));
            using var range = period.ExecuteReader(); range.Read();
            cancellationToken.ThrowIfCancellationRequested();
            return new(value, range.IsDBNull(0) ? null : new DateTimeOffset(range.GetInt64(0), TimeSpan.Zero),
                range.IsDBNull(1) ? null : new DateTimeOffset(range.GetInt64(1), TimeSpan.Zero), range.GetInt64(2));
        }

        public IReadOnlyList<Evidence> SubjectObservations(string snapshotId, string nodeId, int limit = 32,
            CancellationToken cancellationToken = default)
        {
            if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
            using var command = Query("SELECT payload FROM observations WHERE snapshot_id=$s AND subject_id=$n ORDER BY id LIMIT $l", ("$s", snapshotId), ("$n", nodeId), ("$l", limit));
            using var rows = command.ExecuteReader();
            var result = new List<Evidence>();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(JsonSerializer.Deserialize<Evidence>(rows.GetString(0), Json)!);
            }
            return result;
        }

        public MapEdge? Edge(string snapshotId, string edgeId)
        {
            using var command = Query("SELECT * FROM edges WHERE snapshot_id=$s AND id=$e", ("$s", snapshotId), ("$e", edgeId));
            using var row = command.ExecuteReader();
            return row.Read() ? new(row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.GetDouble(6),
                row.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(row.GetString(7), Json)) : null;
        }

        /// <summary>Uses an explicitly recorded language property, never a filename guess.</summary>
        public Page<ObservedNode> NodesForLanguage(string snapshotId, string language, string kind = "Function",
            int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (language is not ("C#" or "PowerShell")) throw new ArgumentException("Only recorded C# or PowerShell language values are supported.", nameof(language));
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > 128) throw new ArgumentException("Invalid node kind.", nameof(kind));
            ValidatePage(offset, limit);
            if (limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
            const string clause = "snapshot_id=$s AND kind=$k AND json_extract(properties,'$.language')=$language";
            using var count = Query("SELECT COUNT(*) FROM nodes WHERE " + clause, ("$s", snapshotId), ("$k", kind), ("$language", language));
            var total = (long)count.ExecuteScalar()!;
            var ids = new List<string>();
            using (var command = Query("SELECT id FROM nodes WHERE " + clause + " ORDER BY id LIMIT $l OFFSET $o", ("$s", snapshotId), ("$k", kind), ("$language", language), ("$l", limit), ("$o", offset)))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) { cancellationToken.ThrowIfCancellationRequested(); ids.Add(rows.GetString(0)); }
            var items = ids.Select(id => Node(snapshotId, id, cancellationToken) ?? throw new InvalidOperationException("Pinned node disappeared.")).ToArray();
            return new(items, total, offset, limit);
        }
    }
}
