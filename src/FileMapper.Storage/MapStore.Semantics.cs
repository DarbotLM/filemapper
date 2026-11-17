using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public SemanticReader OpenSemanticReader() => new(DatabasePath);
    public sealed record ObservedNode(MapNode Node, DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedTo, long ObservationCount);

    /// <summary>One read-only SQLite transaction pins every object, edge, observation and time layer.</summary>
    public sealed partial class SemanticReader : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        internal SemanticReader(string path)
        {
            connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
            try
            {
                connection.Open();
                connection.CreateFunction<string, long>("filemapper_utc_ticks", value => Date(value).UtcTicks, isDeterministic: true);
                transaction = connection.BeginTransaction(deferred: true);
                // Force the deferred read view now, before callers request separate layers.
                using var pin = Query("SELECT COUNT(*) FROM schema_versions");
                pin.ExecuteScalar();
            }
            catch { transaction?.Dispose(); connection.Dispose(); throw; }
        }
        private SqliteCommand Query(string sql, params (string, object?)[] values)
        {
            var command = Command(connection, sql, values);
            command.Transaction = transaction;
            return command;
        }
        public Snapshot Snapshot(string id)
        {
            using var command = Query("SELECT * FROM snapshots WHERE id=$s", ("$s", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSnapshot(reader) : throw new KeyNotFoundException("Snapshot not found: " + id);
        }
        public Page<ObservedNode> Nodes(string snapshotId, string? kind = null, string? search = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        {
            ValidatePage(offset, limit);
            if (limit > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Semantic pages are limited to 500 objects.");
            var clause = "snapshot_id=$s";
            if (!string.IsNullOrEmpty(kind)) clause += " AND kind=$k";
            if (!string.IsNullOrEmpty(search)) clause += " AND (name LIKE $q ESCAPE '!' OR path LIKE $q ESCAPE '!')";
            using var count = Query("SELECT COUNT(*) FROM nodes WHERE " + clause, ("$s", snapshotId), ("$k", kind), ("$q", Like(search)));
            var total = (long)count.ExecuteScalar()!;
            using var command = Query("SELECT * FROM nodes WHERE " + clause + " ORDER BY id LIMIT $l OFFSET $o", ("$s", snapshotId), ("$k", kind), ("$q", Like(search)), ("$l", limit), ("$o", offset));
            var nodes = new List<MapNode>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); nodes.Add(ReadNode(reader)); }
            using var period = Query("SELECT MIN(filemapper_utc_ticks(observed_at)),MAX(filemapper_utc_ticks(observed_at)),COUNT(*) FROM observations WHERE snapshot_id=$s AND subject_id=$n", ("$s", snapshotId), ("$n", ""));
            period.Prepare();
            var result = new List<ObservedNode>();
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested(); period.Parameters["$n"].Value = node.Id;
                using var reader = period.ExecuteReader(); reader.Read();
                result.Add(new(node, reader.IsDBNull(0) ? null : new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetInt64(2)));
            }
            return new(result, total, offset, limit);
        }
        public IEnumerable<ObservedNode> AllNodes(string snapshotId, CancellationToken cancellationToken = default)
        {
            // SQLite groups the subject index once; the managed process does not retain the graph.
            using var command = Query("""
                SELECT n.*,p.first_at,p.last_at,p.observation_count FROM nodes n
                LEFT JOIN (SELECT subject_id,MIN(filemapper_utc_ticks(observed_at)) first_at,MAX(filemapper_utc_ticks(observed_at)) last_at,COUNT(*) observation_count
                  FROM observations WHERE snapshot_id=$s GROUP BY subject_id) p ON p.subject_id=n.id
                WHERE n.snapshot_id=$s ORDER BY n.id
                """, ("$s", snapshotId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(ReadNode(reader), reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero), reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero), reader.IsDBNull(8) ? 0 : reader.GetInt64(8));
            }
        }
        public IEnumerable<MapEdge> Edges(string snapshotId, CancellationToken cancellationToken = default)
        {
            using var command = Query("SELECT * FROM edges WHERE snapshot_id=$s ORDER BY id", ("$s", snapshotId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(7), Json));
            }
        }
        public IEnumerable<MapEdge> Outgoing(string snapshotId, string nodeId, int limit = 100)
        {
            if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            using var command = Query("SELECT * FROM edges INDEXED BY ix_edges_from WHERE snapshot_id=$s AND from_id=$n ORDER BY id LIMIT $l", ("$s", snapshotId), ("$n", nodeId), ("$l", limit));
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(7), Json));
        }
        public IEnumerable<Evidence> Observations(string snapshotId, CancellationToken cancellationToken = default)
        {
            using var command = Query("SELECT payload FROM observations WHERE snapshot_id=$s ORDER BY id", ("$s", snapshotId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); yield return JsonSerializer.Deserialize<Evidence>(reader.GetString(0), Json)!; }
        }
        public Evidence? Observation(string snapshotId, string id)
        {
            using var command = Query("SELECT payload FROM observations WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId), ("$id", id));
            var payload = command.ExecuteScalar() as string;
            return payload is null ? null : JsonSerializer.Deserialize<Evidence>(payload, Json);
        }
        public IEnumerable<FileEntry> Entries(string snapshotId, CancellationToken cancellationToken = default)
        {
            using var command = Query("SELECT * FROM entries WHERE snapshot_id=$s ORDER BY path", ("$s", snapshotId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); yield return ReadEntry(reader); }
        }
        public IEnumerable<Finding> Findings(string snapshotId, CancellationToken cancellationToken = default)
        {
            using var command = Query("SELECT payload FROM findings WHERE snapshot_id=$s ORDER BY id", ("$s", snapshotId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); yield return JsonSerializer.Deserialize<Finding>(reader.GetString(0), Json)!; }
        }
        public void Dispose() { ClearContextCancellation(); transaction.Dispose(); connection.Dispose(); }
    }
}
