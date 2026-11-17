using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public RouteReader OpenRouteReader(string snapshotId) => new(DatabasePath, snapshotId);

    /// <summary>A pinned, read-only SQLite view with prepared directed adjacency queries.</summary>
    public sealed class RouteReader : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly SqliteCommand node;
        private readonly SqliteCommand outgoing;
        public Snapshot Snapshot { get; }

        internal RouteReader(string path, string snapshotId)
        {
            connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 1 }.ToString());
            try
            {
                connection.Open();
                transaction = connection.BeginTransaction(deferred: true);
                using var snapshot = Command(connection, "SELECT * FROM snapshots WHERE id=$s", ("$s", snapshotId));
                snapshot.Transaction = transaction;
                using (var reader = snapshot.ExecuteReader())
                    Snapshot = reader.Read() ? ReadSnapshot(reader) : throw new KeyNotFoundException("Snapshot not found: " + snapshotId);
                node = Command(connection, "SELECT * FROM nodes WHERE snapshot_id=$s AND id=$n", ("$s", snapshotId), ("$n", ""));
                node.Transaction = transaction; node.Prepare();
                // The generic incident-edge OR query can select the snapshot-wide primary index.
                // Pin the existing directed index; the sort is restricted to this source node.
                outgoing = Command(connection, "SELECT * FROM edges INDEXED BY ix_edges_from WHERE snapshot_id=$s AND from_id=$n ORDER BY id LIMIT $l",
                    ("$s", snapshotId), ("$n", ""), ("$l", 1001));
                outgoing.Transaction = transaction; outgoing.Prepare();
            }
            catch { outgoing?.Dispose(); node?.Dispose(); transaction?.Dispose(); connection.Dispose(); throw; }
        }
        public MapNode? Node(string id)
        {
            node.Parameters["$n"].Value = id;
            using var reader = node.ExecuteReader();
            return reader.Read() ? ReadNode(reader) : null;
        }
        public IReadOnlyList<MapEdge> Outgoing(string id, int limit)
        {
            if (limit is < 1 or > 1001) throw new ArgumentOutOfRangeException(nameof(limit));
            outgoing.Parameters["$n"].Value = id; outgoing.Parameters["$l"].Value = limit;
            using var reader = outgoing.ExecuteReader();
            var edges = new List<MapEdge>();
            while (reader.Read()) edges.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDouble(6),
                reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(7), Json)));
            return edges;
        }
        public void Dispose() { outgoing.Dispose(); node.Dispose(); transaction.Dispose(); connection.Dispose(); }
    }
}
