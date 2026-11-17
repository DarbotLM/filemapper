using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public void SetHashingPhase(string runId, string phase)
    {
        RequireWritable(); HashingText(phase, 64, "phase");
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction();
        var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
        SaveHashingRun(db, tx, run with { Phase = phase }); tx.Commit();
    }

    public void SetHashingStatus(string runId, string status, string? error = null)
    {
        RequireWritable(); if (status is not ("running" or "completed" or "paused" or "cancelled" or "failed" or "interrupted")) throw new ArgumentException("Unknown hashing job status.");
        using var control = status is "paused" or "cancelled" ? OpenHashingControl(writable: true) : null;
        using var controlTx = control?.BeginTransaction();
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId);
        if (run.Status == "completed") { if (status == "completed") return; throw new InvalidOperationException("A completed sealed analysis is immutable."); }
        if (status == "running" && run.ResultHash is not null) throw new InvalidOperationException("A sealed result cannot be resumed.");
        if (status == "completed" && run.ResultHash is null) throw new InvalidOperationException("Completion requires a sealed result.");
        if (control is not null && ReadHashingControl(control, controlTx, run) == "cancelled") status = "cancelled";
        if (status != "running") run = AbandonHashingAttempts(db, tx, run);
        SaveHashingRun(db, tx, run with { Status = status, FinishedAt = status == "running" ? null : DateTimeOffset.UtcNow, Error = error,
            StopRequest = null, Phase = status == "completed" ? "sealed" : run.Phase }); tx.Commit(); controlTx?.Commit();
    }

    /// <summary>Caller holds the exclusive writer; another conforming live job cannot be recovered.</summary>
    public void RecoverAbandonedHashingRuns()
    {
        RequireWritable(); using var db = Open(); if (!HasHashingSchema(db)) return;
        using var tx = db.BeginTransaction(); var ids = new List<string>();
        using (var query = HashingCommand(db, tx, "SELECT id FROM hashing_runs WHERE status='running' ORDER BY id"))
        using (var rows = query.ExecuteReader()) while (rows.Read()) ids.Add(rows.GetString(0));
        foreach (var id in ids)
        {
            var run = AbandonHashingAttempts(db, tx, RequireHashingRun(db, tx, id));
            SaveHashingRun(db, tx, run with { Status = "interrupted", FinishedAt = DateTimeOffset.UtcNow, StopRequest = null,
                Error = run.ResultHash is null ? "Writer exited before completion. Unfinished reads may have consumed unrecorded bytes; their full durable reservations remain charged. Explicit resume preserves the original baseline and policy."
                    : "The immutable result was sealed before the writer exited; terminal job completion was not recorded." });
        }
        tx.Commit();
    }

    public void PrepareHashingResume(string runId)
    {
        RequireWritable(); HashingText(runId, 256, "runId");
        using var control = OpenHashingControl(writable: true); using var controlTx = control.BeginTransaction();
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId);
        if (run.ResultHash is not null || run.Status is not ("paused" or "cancelled" or "failed" or "interrupted")) throw new InvalidOperationException("Only an unsealed stopped hashing run can resume.");
        _ = ReadHashingControl(control, controlTx, run);
        HashingExecute(control, controlTx, "DELETE FROM hashing_control WHERE run_id=$id", ("$id", runId));
        run = AbandonHashingAttempts(db, tx, run);
        SaveHashingRun(db, tx, run with { StopRequest = null, Error = null }); tx.Commit(); controlTx.Commit();
    }

    private static HashingRun AbandonHashingAttempts(SqliteConnection db, SqliteTransaction tx, HashingRun run)
    {
        long count, chunks, reserved;
        using (var query = HashingCommand(db, tx, "SELECT COUNT(*),COALESCE(SUM(chunk_count),0),COALESCE(SUM(byte_reserved),0) FROM hashing_attempts WHERE run_id=$r AND status='reading'", ("$r", run.Id)))
        using (var row = query.ExecuteReader()) { row.Read(); count = row.GetInt64(0); chunks = row.GetInt64(1); reserved = row.GetInt64(2); }
        if (count == 0) return run;
        HashingExecute(db, tx, "DELETE FROM hashing_chunks WHERE attempt_id IN (SELECT id FROM hashing_attempts WHERE run_id=$r AND status='reading')", ("$r", run.Id));
        HashingExecute(db, tx, "UPDATE hashing_attempts SET status='unaccounted',finished_at=$at WHERE run_id=$r AND status='reading'", ("$r", run.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        HashingExecute(db, tx, "UPDATE hashing_work SET active_attempt_id=NULL WHERE run_id=$r AND active_attempt_id IS NOT NULL", ("$r", run.Id));
        HashingExecute(db, tx, "UPDATE hashing_runs SET chunk_reserved=chunk_reserved-$chunks,bytes_reserved=bytes_reserved-$bytes WHERE id=$r", ("$r", run.Id), ("$chunks", chunks), ("$bytes", reserved));
        return run with { Coverage = run.Coverage with { UnaccountedReadAttempts = checked(run.Coverage.UnaccountedReadAttempts + count),
            BytesReadAreLowerBound = true, UnaccountedReservedBytes = checked(run.Coverage.UnaccountedReservedBytes + reserved),
            BudgetChargedBytes = checked(run.Coverage.BudgetChargedBytes + reserved) }, Completeness = "incomplete" };
    }

    public void PromoteHashingSamples(string runId, CancellationToken cancellationToken = default)
    {
        RequireWritable(); using var db = Open(); RequireHashingSchema(db); using var cancel = new SourceSqliteCancellation(db, cancellationToken);
        cancel.Run(() =>
        {
            using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
            if (HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_work WHERE run_id=$r AND stage='sample-pending'", ("$r", runId)) != 0)
                throw new InvalidOperationException("All admitted sampling work must close before promotion.");
            if (run.Options.Mode == "all") HashingExecute(db, tx, "UPDATE hashing_work SET stage='full-pending' WHERE run_id=$r AND stage='sampled'", ("$r", runId));
            else if (run.Options.Mode == "probable") HashingExecute(db, tx, "UPDATE hashing_work SET stage='done' WHERE run_id=$r AND stage='sampled'", ("$r", runId));
            else if (run.Options.Mode == "exact") HashingExecute(db, tx, """
                UPDATE hashing_work SET stage=CASE WHEN (logical_bytes,(SELECT digest FROM hashing_evidence WHERE id=sample_evidence_id)) IN (
                  SELECT w.logical_bytes,e.digest FROM hashing_work w JOIN hashing_evidence e ON e.id=w.sample_evidence_id
                  WHERE w.run_id=$r AND e.status='complete' GROUP BY w.logical_bytes,e.digest HAVING COUNT(*)>1)
                  THEN 'full-pending' ELSE 'done' END
                WHERE run_id=$r AND stage='sampled'
                """, ("$r", runId));
            tx.Commit();
        });
    }

    public void MarkHashingBudgetReached(string runId, string kind)
    {
        RequireWritable(); if (kind is not ("bytes" or "chunks")) throw new ArgumentException("Unknown hashing resource budget.");
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
        if (HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_work WHERE run_id=$r AND active_attempt_id IS NOT NULL", ("$r", runId)) != 0)
            throw new InvalidOperationException("Workers must join and finish their receipts before marking a budget limit.");
        var limited = HashingExecute(db, tx, "UPDATE hashing_work SET stage='limited',error=$reason WHERE run_id=$r AND stage IN ('sample-pending','full-pending','sampled')",
            ("$r", runId), ("$reason", "Run " + kind + " budget reached before this stage completed."));
        SaveHashingRun(db, tx, run with { Coverage = run.Coverage with { ByteBudgetReached = run.Coverage.ByteBudgetReached || kind == "bytes",
            ChunkBudgetReached = run.Coverage.ChunkBudgetReached || kind == "chunks", SkippedIdentities = checked(run.Coverage.SkippedIdentities + limited) } }); tx.Commit();
    }
}
