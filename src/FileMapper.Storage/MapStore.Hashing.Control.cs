using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    // A fixed sibling database keeps stop writes independent of long main-database admission/sealing
    // transactions. Caller-supplied identifiers are bound SQL values, never filesystem components.
    private string HashingControlPath => DatabasePath + ".hashing-control.db";
    private const long HashingControlApplicationId = 0x464D4843; // FMHC

    private SqliteConnection OpenHashingControl(bool writable)
    {
        if (writable) RequireWritable();
        if (writable && !File.Exists(HashingControlPath)) PublishHashingControl();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = HashingControlPath, Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            db.Open();
            if (writable) HashingExecute(db, null, "PRAGMA synchronous=FULL;");
            RequireHashingControlSchema(db, null);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private void PublishHashingControl()
    {
        // Never expose a zero-byte or partially initialized published database to read-only monitors.
        var temporary = HashingControlPath + ".initializing-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString()))
            {
                db.Open(); HashingExecute(db, null, "PRAGMA synchronous=FULL;");
                using var tx = db.BeginTransaction();
                HashingExecute(db, tx, """
                        CREATE TABLE hashing_control(
                          run_id TEXT PRIMARY KEY CHECK(length(run_id) BETWEEN 1 AND 256),
                          request_hash TEXT NOT NULL CHECK(length(request_hash) BETWEEN 1 AND 256),
                          started_at TEXT NOT NULL,
                          request TEXT NOT NULL CHECK(request IN ('paused','cancelled')),
                          requested_at TEXT NOT NULL);
                        PRAGMA application_id=0x464D4843;
                        PRAGMA user_version=1;
                        """);
                tx.Commit();
            }
            try { File.Move(temporary, HashingControlPath, overwrite: false); }
            catch (IOException) when (File.Exists(HashingControlPath)) { /* A concurrent publisher won; validate it on open. */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequireHashingControlSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (HashingLong(db, tx, "PRAGMA user_version") != 1 ||
            HashingLong(db, tx, "PRAGMA application_id") != HashingControlApplicationId ||
            HashingLong(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='hashing_control'") != 1)
            throw new InvalidDataException("Hashing control database has an unknown or corrupt schema.");
    }

    private static string? ReadHashingControl(SqliteConnection db, SqliteTransaction? tx, HashingRun run)
    {
        using var query = HashingCommand(db, tx, "SELECT request_hash,started_at,request,requested_at FROM hashing_control WHERE run_id=$id", ("$id", run.Id));
        using var row = query.ExecuteReader();
        var previous = run.StopRequest;
        if (previous is not (null or "paused" or "cancelled")) throw new InvalidDataException("Hashing run has an invalid stop request.");
        if (!row.Read()) return previous;
        if (row.GetString(0) != run.RequestHash || row.GetString(1) != run.StartedAt.ToString("O") ||
            row.GetString(2) is not ("paused" or "cancelled") || !DateTimeOffset.TryParse(row.GetString(3), out _))
            throw new InvalidDataException("Hashing control record does not match its exact run or contains invalid data.");
        return previous == "cancelled" ? previous : row.GetString(2);
    }

    private HashingRun? ReadHashingControlTarget(string runId)
    {
        using var db = OpenHashingRead();
        return HasHashingSchema(db) ? ReadHashingRun(db, null, runId) : null;
    }

    private HashingRun WithHashingControl(HashingRun run)
    {
        if (run.Status != "running" || run.ResultHash is not null || !File.Exists(HashingControlPath)) return run;
        using var control = OpenHashingControl(writable: false);
        return run with { StopRequest = ReadHashingControl(control, null, run) };
    }

    public bool RequestHashingStop(string runId, string request)
    {
        RequireWritable(); HashingText(runId, 256, "runId");
        if (request is not ("paused" or "cancelled")) throw new ArgumentException("Only pause and cancellation controls are supported.");
        using var admission = AcquireMaintenanceAdmission();
        if (ReadHashingControlTarget(runId) is not { Status: "running", ResultHash: null }) return false;
        using var control = OpenHashingControl(writable: true); using var tx = control.BeginTransaction();
        // Fresh WAL read after taking the control lock: never use a pre-lock main read snapshot.
        var run = ReadHashingControlTarget(runId);
        if (run is not { Status: "running", ResultHash: null }) return false;
        var previous = ReadHashingControl(control, tx, run);
        HashingExecute(control, tx, """
            INSERT INTO hashing_control(run_id,request_hash,started_at,request,requested_at) VALUES($id,$hash,$started,$request,$at)
            ON CONFLICT(run_id) DO UPDATE SET request=CASE WHEN hashing_control.request='cancelled' THEN 'cancelled' ELSE excluded.request END,
              requested_at=excluded.requested_at
            """, ("$id", run.Id), ("$hash", run.RequestHash), ("$started", run.StartedAt.ToString("O")),
            ("$request", previous == "cancelled" ? previous : request), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit(); return true;
    }

    public string? GetHashingStop(string runId)
    {
        HashingText(runId, 256, "runId");
        var run = ReadHashingControlTarget(runId);
        return run is { Status: "running", ResultHash: null } ? WithHashingControl(run).StopRequest : null;
    }
}
