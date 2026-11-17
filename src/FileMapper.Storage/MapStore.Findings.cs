using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public Finding? GetFinding(string snapshotId, string findingId)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT payload FROM findings WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId), ("$id", findingId));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Finding>(json, Json) : null;
    }
    public MapEdge? GetEdge(string snapshotId, string edgeId)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT * FROM edges WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId), ("$id", edgeId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDouble(6),
            r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(r.GetString(7), Json)) : null;
    }
    public Evidence? GetObservation(string snapshotId, string evidenceId)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT payload FROM observations WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId), ("$id", evidenceId));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Evidence>(json, Json) : null;
    }

    // Validation runs inside the same transaction as graph ingestion. A malformed witness rolls
    // back the entire analysis, including the analysis.committed marker and all graph changes.
    private static void ValidateFindingWitnesses(SqliteConnection db, SqliteTransaction tx, string snapshotId, IReadOnlyList<Finding> findings)
    {
        using var node = Command(db, "SELECT path,properties FROM nodes WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId)); node.Transaction = tx;
        using var edge = Command(db, "SELECT from_id,to_id,evidence_id FROM edges WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId)); edge.Transaction = tx;
        using var evidence = Command(db, "SELECT COUNT(*) FROM observations WHERE snapshot_id=$s AND id=$id", ("$s", snapshotId)); evidence.Transaction = tx;
        var knownEvidence = new HashSet<string>(StringComparer.Ordinal);
        void RequireEvidence(string id)
        {
            if (knownEvidence.Contains(id)) return;
            Set(evidence, ("$id", id));
            if ((long)evidence.ExecuteScalar()! != 1) throw new ArgumentException("Finding references missing snapshot evidence: " + id);
            knownEvidence.Add(id);
        }
        static bool SamePath(string? a, string b) => a is not null && string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        foreach (var finding in findings)
        {
            if (finding.Line < 1 || !double.IsFinite(finding.Confidence) || finding.Confidence is < 0 or > 1
                || string.IsNullOrWhiteSpace(finding.ModelVersion) || finding.EvidenceIds.Count == 0)
                throw new ArgumentException("Finding metadata is invalid: " + finding.Id);
            if (finding.Truncated && (finding.Completeness == "complete" || string.IsNullOrWhiteSpace(finding.TruncationReason)))
                throw new ArgumentException("A truncated finding must carry incomplete coverage and a reason: " + finding.Id);
            if (finding.Status == "modeled-flow" && finding.Route.Count < 2)
                throw new ArgumentException("Modeled flow requires an ordered source-to-sink witness: " + finding.Id);
            if (finding.Route.Count > 10000) throw new ArgumentException("Finding witness exceeds 10,000 steps.");
            foreach (var id in finding.EvidenceIds) RequireEvidence(id);
            for (var i = 0; i < finding.Route.Count; i++)
            {
                var step = finding.Route[i];
                if (step.Line < 1 || step.Column < 1 || string.IsNullOrWhiteSpace(step.Reason) || (i == 0 ? step.EdgeId is not null : step.EdgeId is null))
                    throw new ArgumentException("Finding route has invalid location, reason or edge order: " + finding.Id);
                Set(node, ("$id", step.NodeId));
                using (var r = node.ExecuteReader())
                {
                    if (!r.Read() || !SamePath(r.IsDBNull(0) ? null : r.GetString(0), step.Path))
                        throw new ArgumentException("Finding route node is missing or has a different source path: " + step.NodeId);
                    using var properties = JsonDocument.Parse(r.GetString(1));
                    if (properties.RootElement.TryGetProperty("line", out var line) && line.GetInt32() != step.Line
                        || properties.RootElement.TryGetProperty("column", out var column) && column.GetInt32() != step.Column)
                        throw new ArgumentException("Finding route location differs from the source node: " + step.NodeId);
                }
                if (step.EdgeId is not null)
                {
                    Set(edge, ("$id", step.EdgeId));
                    string evidenceId;
                    using (var r = edge.ExecuteReader())
                    {
                        if (!r.Read() || r.GetString(0) != finding.Route[i - 1].NodeId || r.GetString(1) != step.NodeId)
                            throw new ArgumentException("Finding route edge does not connect consecutive steps: " + step.EdgeId);
                        evidenceId = r.GetString(2);
                    }
                    RequireEvidence(evidenceId);
                }
            }
            if (finding.Route.Count > 0 && (!SamePath(finding.Route[^1].Path, finding.Path) || finding.Route[^1].Line != finding.Line))
                throw new ArgumentException("Finding location must match the witness destination: " + finding.Id);
        }
    }
}
