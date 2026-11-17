using FileMapper.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    private sealed record StoredJobState(string SnapshotId, string Kind, string Status, string SnapshotStatus, string? Options);

    private static StoredJobState ReadJobState(SqliteConnection db, SqliteTransaction tx, string jobId)
    {
        using var query = Command(db, """
            SELECT j.snapshot_id,j.kind,j.status,s.status,j.options FROM jobs j JOIN snapshots s ON s.id=j.snapshot_id WHERE j.id=$id
            """, ("$id", jobId));
        query.Transaction = tx;
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Job not found.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// <summary>Publishes scan totals, final status and job status in one transaction. A stop
    /// accepted before this transaction is observed before deciding whether to seal completion.</summary>
    public void FinishScanJob(string jobId, string status = "completed", string? error = null)
    {
        RequireWritable();
        if (status is not ("completed" or "cancelled" or "failed" or "paused")) throw new ArgumentException("Invalid terminal scan status.", nameof(status));
        using var db = Open();
        using var tx = db.BeginTransaction();
        FinishScanJobCore(db, tx, jobId, status, error);
        tx.Commit();
    }

    private static void FinishScanJobCore(SqliteConnection db, SqliteTransaction tx, string jobId, string status, string? error)
    {
        var job = ReadJobState(db, tx, jobId);
        if (job.Kind != "scan") throw new InvalidOperationException("Only scans can finalize a snapshot.");
        if (job.Status != "running")
        {
            if (job.Status == status && job.SnapshotStatus == status) return;
            throw new InvalidOperationException("The scan job is already terminal.");
        }
        if (job.SnapshotStatus != "running") throw new InvalidOperationException("The scan snapshot is not running. Recover its job before proceeding.");
        using (var stop = Command(db, "SELECT request FROM job_controls WHERE job_id=$id", ("$id", jobId)))
        {
            stop.Transaction = tx;
            if (stop.ExecuteScalar() is string request && status is "completed" or "paused") status = request;
        }
        if (status == "completed")
        {
            using var pending = Command(db, "SELECT 1 FROM scan_work WHERE job_id=$id AND done=0 LIMIT 1", ("$id", jobId));
            pending.Transaction = tx;
            if (pending.ExecuteScalar() is not null)
                throw new InvalidOperationException("The scan frontier still has pending work; completion cannot be published.");
        }
        var now = DateTimeOffset.UtcNow;
        FinishSnapshotCore(db, tx, job.SnapshotId, status, now);
        using var finish = Command(db, "UPDATE jobs SET status=$status,finished_at=$at,error=$error WHERE id=$id",
            ("$status", status), ("$at", now.ToString("O")), ("$error", error), ("$id", jobId));
        finish.Transaction = tx;
        finish.ExecuteNonQuery();
        using var clear = Command(db, "DELETE FROM job_controls WHERE job_id=$id", ("$id", jobId));
        clear.Transaction = tx;
        clear.ExecuteNonQuery();
    }

    private static void RequireRunningScan(SqliteConnection db, SqliteTransaction tx, string jobId)
    {
        var state = ReadJobState(db, tx, jobId);
        if (state.Kind != "scan" || state.Status != "running" || state.SnapshotStatus != "running")
            throw new InvalidOperationException("Only a running scan can change its durable frontier.");
    }

    // The caller holds a SQLite write transaction. Checking on the same connection keeps
    // checkpoint admission and terminal publication ordered even for direct storage clients.
    private static string SnapshotState(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var state = Command(db, "SELECT status FROM snapshots WHERE id=$s", ("$s", id));
        state.Transaction = tx;
        return state.ExecuteScalar() as string ?? throw new KeyNotFoundException("Snapshot not found.");
    }

    private static string ValidateSnapshotIngestion(SqliteConnection db, SqliteTransaction tx, string id,
        bool containsEntries, IReadOnlyList<Evidence> observations, IReadOnlyList<MapNode> nodes)
    {
        var state = SnapshotState(db, tx, id);
        if (containsEntries && state != "running")
            throw new InvalidOperationException("Filesystem entries can only be ingested into a running snapshot. Resume the scan or create a new snapshot.");
        if (state == "completed" && nodes.Count != 0)
        {
            using var physical = Command(db, "SELECT 1 FROM entries WHERE snapshot_id=$s AND node_id=$n LIMIT 1");
            physical.Transaction = tx;
            foreach (var node in nodes)
            {
                Set(physical, ("$s", id), ("$n", node.Id));
                if (physical.ExecuteScalar() is not null)
                    throw new InvalidOperationException("Completed filesystem node observations are immutable; add separate derived nodes and evidence instead.");
            }
        }
        foreach (var group in observations.Where(e => e.Predicate is "scan.options" or "scan.policy").GroupBy(e => e.Predicate, StringComparer.Ordinal))
        {
            if (state != "running") throw new InvalidOperationException("A finished snapshot's scan policy is immutable.");
            if (group.Count() != 1 || group.Single().SubjectId != id)
                throw new ArgumentException("Each snapshot has one observation for each scan policy predicate, addressed to the snapshot.", nameof(observations));
            var supplied = group.Single();
            using var prior = Command(db, "SELECT payload FROM observations WHERE snapshot_id=$s AND predicate=$p", ("$s", id), ("$p", group.Key));
            prior.Transaction = tx;
            using var reader = prior.ExecuteReader();
            bool found = false;
            while (reader.Read())
            {
                found = true;
                var evidence = JsonSerializer.Deserialize<Evidence>(reader.GetString(0), Json)
                    ?? throw new InvalidDataException("Stored scan policy is missing.");
                if (evidence.Id != supplied.Id || evidence.SubjectId != supplied.SubjectId || evidence.Value != supplied.Value)
                    throw new InvalidOperationException("The scan policy is already recorded and cannot be replaced during resume or ingestion.");
            }
            reader.Close();
            if (!found && HasScanJob(db, tx, id))
                throw new InvalidOperationException("Scan policy must be recorded before creating the scan job; missing historical policy remains unknown.");
        }
        return state;
    }

    private static bool HasScanJob(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var schema = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='jobs'");
        schema.Transaction = tx;
        if (schema.ExecuteScalar() is null) return false;
        using var scan = Command(db, "SELECT 1 FROM jobs WHERE snapshot_id=$s AND kind='scan' LIMIT 1", ("$s", id));
        scan.Transaction = tx;
        return scan.ExecuteScalar() is not null;
    }

    private static void ValidateScanJobOptions(SqliteConnection db, SqliteTransaction tx, string id, ScanOptions options)
    {
        using var root = Command(db, "SELECT root FROM snapshots WHERE id=$s", ("$s", id));
        root.Transaction = tx;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(options.Root)),
            System.IO.Path.TrimEndingDirectorySeparator((string)root.ExecuteScalar()!), comparison))
            throw new ArgumentException("Scan options must name the snapshot root.", nameof(options));
        using var recorded = Command(db, "SELECT predicate,payload FROM observations WHERE snapshot_id=$s AND predicate IN ('scan.options','scan.policy') ORDER BY predicate,id", ("$s", id));
        recorded.Transaction = tx;
        using var reader = recorded.ExecuteReader();
        var predicates = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var evidence = JsonSerializer.Deserialize<Evidence>(reader.GetString(1), Json);
            var policy = evidence is null ? null : JsonSerializer.Deserialize<ScanOptions>(evidence.Value, Json);
            if (!predicates.Add(reader.GetString(0)) || policy is null || JsonSerializer.Serialize(policy, Json) != JsonSerializer.Serialize(options, Json))
                throw new InvalidOperationException("Scan job options must match the single recorded observation for each scan policy predicate.");
        }
    }

    private static void RefreshObservationState(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var update = Command(db, """
            UPDATE snapshots SET
              completeness=CASE WHEN EXISTS(SELECT 1 FROM observations WHERE snapshot_id=$s AND completeness<>'complete') THEN 'incomplete' ELSE completeness END,
              error_count=(SELECT COUNT(*) FROM observations WHERE snapshot_id=$s AND error_code IS NOT NULL)
            WHERE id=$s
            """, ("$s", id));
        update.Transaction = tx;
        update.ExecuteNonQuery();
    }

    private static void FinishSnapshotCore(SqliteConnection db, SqliteTransaction tx, string id, string status, DateTimeOffset now)
    {
        var previous = SnapshotState(db, tx, id);
        if (previous != "running")
        {
            if (previous == status) return; // Retain the original terminal timestamp on retries.
            throw new InvalidOperationException("Only a running snapshot can transition to a terminal state. Resume its scan explicitly or create a new snapshot.");
        }
        // MIN/MAX detect multiple distinct non-null allocations; a null paired with one known value
        // makes allocation unknown, but does not itself mark the physical identity conflicting.
        using var finish = Command(db, """
            WITH physical AS (
              SELECT node_id,MAX(logical_bytes) logical_bytes,MAX(CASE WHEN kind='File' THEN 1 ELSE 0 END) is_file,
                CASE WHEN COUNT(*)=COUNT(allocated_bytes) AND MIN(allocated_bytes)=MAX(allocated_bytes) THEN MAX(allocated_bytes) END allocation,
                CASE WHEN MIN(logical_bytes)<>MAX(logical_bytes) OR MIN(allocated_bytes)<>MAX(allocated_bytes) THEN 1 ELSE 0 END conflicting
              FROM entries WHERE snapshot_id=$id AND kind IN ('File','Stream') GROUP BY node_id
            )
            UPDATE snapshots SET status=$status,finished_at=$now,
              entry_count=(SELECT COUNT(*) FROM entries WHERE snapshot_id=$id),
              directory_count=(SELECT COUNT(DISTINCT node_id) FROM entries WHERE snapshot_id=$id AND kind='Directory'),
              error_count=(SELECT COUNT(*) FROM observations WHERE snapshot_id=$id AND error_code IS NOT NULL),
              (file_count,logical_bytes,allocated_bytes,completeness)=(SELECT
                COALESCE(SUM(is_file),0), COALESCE(SUM(logical_bytes),0),
                CASE WHEN COUNT(*)=COUNT(allocation) THEN COALESCE(SUM(allocation),0) END,
                CASE WHEN $status='completed'
                  AND NOT EXISTS(SELECT 1 FROM observations WHERE snapshot_id=$id AND completeness<>'complete')
                  AND COALESCE(MAX(conflicting),0)=0 THEN 'complete' ELSE 'incomplete' END
                FROM physical)
            WHERE id=$id
            """, ("$id", id), ("$status", status), ("$now", now.ToString("O")));
        finish.Transaction = tx;
        finish.ExecuteNonQuery();
    }
}
