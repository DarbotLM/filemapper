using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public sealed partial class SemanticReader
    {
        private bool contextCancellationActive;
        /// <summary>Interrupts CPU-bound reads too; a row limit alone cannot bound a literal search scan.</summary>
        public void SetContextCancellation(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
            contextCancellationActive = true;
        }
        private void ClearContextCancellation()
        {
            if (!contextCancellationActive) return;
            contextCancellationActive = false;
            // A pooled connection must never lend a previous request's callback to its next reader.
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 0, null!, null);
        }
        private const string ContextColumns = "id,kind,name,path";
        private static ContextNode ReadContextNode(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3));

        /// <summary>One literal candidate query; LIKE wildcards in user terms have no special meaning.</summary>
        public IReadOnlyList<ContextNode> ContextSeeds(string snapshotId, string phrase, IReadOnlyList<string> terms,
            int limit, CancellationToken cancellationToken = default)
        {
            if (terms.Count > 8 || limit is < 1 or > 65) throw new ArgumentOutOfRangeException(nameof(limit));
            var predicates = new List<string> { "id=$phrase", "name=$phrase COLLATE NOCASE", "path=$phrase COLLATE NOCASE" };
            var scores = new List<string> { "CASE WHEN id=$phrase THEN 1000 ELSE 0 END", "CASE WHEN name=$phrase COLLATE NOCASE OR path=$phrase COLLATE NOCASE THEN 500 ELSE 0 END" };
            var parameters = new List<(string, object?)> { ("$s", snapshotId), ("$phrase", phrase), ("$l", limit) };
            for (int index = 0; index < terms.Count; index++)
            {
                string key = "$t" + index;
                string name = $"instr(lower(name),lower({key}))>0", path = $"instr(lower(COALESCE(path,'')),lower({key}))>0";
                predicates.Add($"({name} OR {path})");
                scores.Add($"CASE WHEN {name} THEN 20 WHEN {path} THEN 5 ELSE 0 END");
                parameters.Add((key, terms[index]));
            }
            using var command = Query($"SELECT {ContextColumns} FROM nodes WHERE snapshot_id=$s AND ({string.Join(" OR ", predicates)}) ORDER BY ({string.Join("+", scores)}) DESC,kind,name,id LIMIT $l", parameters.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            using var rows = command.ExecuteReader(); var result = new List<ContextNode>();
            while (rows.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(ReadContextNode(rows)); }
            return result;
        }

        public ContextNode? ContextNodeById(string snapshotId, string nodeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Query($"SELECT {ContextColumns} FROM nodes WHERE snapshot_id=$s AND id=$n", ("$s", snapshotId), ("$n", nodeId));
            using var row = command.ExecuteReader(); return row.Read() ? ReadContextNode(row) : null;
        }

        private const string ContextEvidenceColumns = """
            id,subject_id,predicate,json_extract(payload,'$.sourceKind'),json_extract(payload,'$.sourceUri'),observed_at,
            COALESCE(json_extract(payload,'$.confidence'),1),COALESCE(json_extract(payload,'$.completeness'),'unknown'),
            COALESCE(json_extract(payload,'$.collectorVersion'),'unknown'),json_extract(payload,'$.evidenceHash'),
            COALESCE(substr(json_extract(payload,'$.value'),1,512),''),COALESCE(length(json_extract(payload,'$.value')),0)>512
            """;
        private static ContextEvidence ReadContextEvidence(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
            row.IsDBNull(3) ? "unknown" : row.GetString(3), row.IsDBNull(4) ? "unknown" : row.GetString(4), row.GetString(5), row.GetDouble(6),
            row.GetString(7), row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9), row.GetString(10), row.GetBoolean(11));

        public ContextEvidence? ContextEvidenceById(string snapshotId, string evidenceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Query($"SELECT {ContextEvidenceColumns} FROM observations WHERE snapshot_id=$s AND id=$e", ("$s", snapshotId), ("$e", evidenceId));
            using var row = command.ExecuteReader(); return row.Read() ? ReadContextEvidence(row) : null;
        }

        public IReadOnlyList<ContextEvidence> ContextObservations(string snapshotId, string nodeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Query($"SELECT {ContextEvidenceColumns} FROM observations WHERE snapshot_id=$s AND subject_id=$n ORDER BY filemapper_utc_ticks(observed_at) DESC,id LIMIT 4", ("$s", snapshotId), ("$n", nodeId));
            using var rows = command.ExecuteReader(); var result = new List<ContextEvidence>();
            while (rows.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(ReadContextEvidence(rows)); }
            return result;
        }

        // Separate directed index scans avoid the snapshot-wide plan often chosen for an incident-edge OR.
        public IReadOnlyList<MapEdge> ContextIncident(string snapshotId, string nodeId, bool incoming, int limit, CancellationToken cancellationToken = default)
        {
            if (limit is < 1 or > 33) throw new ArgumentOutOfRangeException(nameof(limit));
            string column = incoming ? "to_id" : "from_id", index = incoming ? "ix_edges_to" : "ix_edges_from";
            using var command = Query($"SELECT id,from_id,to_id,kind,evidence_id,confidence FROM edges INDEXED BY {index} WHERE snapshot_id=$s AND {column}=$n AND kind<>'CONTAINS' ORDER BY CASE kind WHEN 'OWNS' THEN 0 WHEN 'MODIFIED_BY' THEN 1 WHEN 'CHANGED_BY' THEN 2 WHEN 'TESTS' THEN 3 WHEN 'GENERATED_FROM' THEN 4 WHEN 'DEPENDS_ON' THEN 5 ELSE 6 END,id LIMIT $l", ("$s", snapshotId), ("$n", nodeId), ("$l", limit));
            cancellationToken.ThrowIfCancellationRequested();
            using var rows = command.ExecuteReader(); var result = new List<MapEdge>();
            while (rows.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetDouble(5))); }
            return result;
        }
    }
}
