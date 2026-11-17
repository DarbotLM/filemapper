using System.Text.Json;
using FileMapper.Domain;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public void InitializeJobs()
    {
        using(var existing=Open())
        using(var probe=Command(existing,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('jobs','scan_work','scan_directories','job_controls')"))
            if((long)probe.ExecuteScalar()! == 4)
            {
                using var version=Command(existing,"SELECT COUNT(*) FROM schema_versions WHERE version=2");
                if((long)version.ExecuteScalar()! == 1)return;
            }
        using var ownership=AcquireWriter(allowInstallerSession:true);
        using var db = Open();using var migration=db.BeginTransaction();using var cmd = Command(db, """
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES snapshots(id),
              kind TEXT NOT NULL, status TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
              entries INTEGER NOT NULL DEFAULT 0, error TEXT, options TEXT);
            CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs(status,started_at);
            CREATE TABLE IF NOT EXISTS scan_work(job_id TEXT NOT NULL REFERENCES jobs(id), path TEXT NOT NULL,
              parent_path TEXT,parent_id TEXT,done INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(job_id,path));
            CREATE INDEX IF NOT EXISTS ix_scan_pending ON scan_work(job_id,done,path);
            CREATE TABLE IF NOT EXISTS scan_directories(job_id TEXT NOT NULL REFERENCES jobs(id),identity TEXT NOT NULL,
              path TEXT NOT NULL,PRIMARY KEY(job_id,identity));
            CREATE INDEX IF NOT EXISTS ix_scan_directory_path ON scan_directories(job_id,path);
            CREATE TABLE IF NOT EXISTS job_controls(job_id TEXT PRIMARY KEY REFERENCES jobs(id),request TEXT NOT NULL);
            INSERT OR IGNORE INTO schema_versions VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """);cmd.Transaction=migration;cmd.ExecuteNonQuery();migration.Commit();
    }

    public DurableJob CreateJob(string snapshotId,string kind,ScanOptions? options = null)
    {
        RequireWritable();
        if (kind is not ("scan" or "analysis")) throw new ArgumentException("Job kind must be scan or analysis.", nameof(kind));
        if (kind == "scan" && options is null) throw new ArgumentException("Scan options are required.", nameof(options));
        var job=new DurableJob(Guid.NewGuid().ToString("N"),snapshotId,kind,"running",DateTimeOffset.UtcNow,null,0,null,options);
        using var db=Open(); using var tx=db.BeginTransaction();
        var snapshotState = SnapshotState(db, tx, snapshotId);
        if (snapshotState != (kind == "scan" ? "running" : "completed"))
            throw new InvalidOperationException("A scan requires a running snapshot; analysis requires a completed snapshot.");
        if (kind == "scan")
        {
            ValidateScanJobOptions(db, tx, snapshotId, options!);
            using var prior = Command(db, "SELECT COUNT(*) FROM jobs WHERE snapshot_id=$s AND kind='scan'", ("$s", snapshotId));
            prior.Transaction = tx;
            if ((long)prior.ExecuteScalar()! != 0) throw new InvalidOperationException("This snapshot already has a scan job. Resume its retained frontier instead.");
        }
        using var cmd=Command(db,"INSERT INTO jobs VALUES($id,$s,$k,$state,$at,NULL,0,NULL,$o)",("$id",job.Id),("$s",snapshotId),("$k",kind),("$state",job.Status),("$at",job.StartedAt.ToString("O")),("$o",options is null?null:JsonSerializer.Serialize(options,Json)));
        cmd.Transaction=tx;cmd.ExecuteNonQuery();
        if(options is not null) { using var work=Command(db,"INSERT INTO scan_work(job_id,path) VALUES($id,$p)",("$id",job.Id),("$p",options.Root));work.Transaction=tx;work.ExecuteNonQuery(); }
        tx.Commit(); return job;
    }

    public IReadOnlyList<DurableJob> ListJobs()
    {
        using var db=Open();using var cmd=Command(db,"SELECT * FROM jobs ORDER BY started_at DESC,id DESC LIMIT 100");using var r=cmd.ExecuteReader();var result=new List<DurableJob>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Date(r.GetString(4)),r.IsDBNull(5)?null:Date(r.GetString(5)),r.GetInt64(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:JsonSerializer.Deserialize<ScanOptions>(r.GetString(8),Json)));
        return result;
    }
    public DurableJob GetJob(string jobId)
    {
        using var db=Open();using var cmd=Command(db,"SELECT * FROM jobs WHERE id=$id",("$id",jobId));using var r=cmd.ExecuteReader();
        if(!r.Read())throw new KeyNotFoundException("Job not found.");
        return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Date(r.GetString(4)),r.IsDBNull(5)?null:Date(r.GetString(5)),r.GetInt64(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:JsonSerializer.Deserialize<ScanOptions>(r.GetString(8),Json));
    }
    public void SetJobStatus(string jobId,string status,string? error=null)
    {
        RequireWritable();
        if (status is not ("running" or "completed" or "paused" or "cancelled" or "failed"))
            throw new ArgumentException("Unsupported job status.", nameof(status));
        using var db=Open();using var tx=db.BeginTransaction();
        var state = ReadJobState(db, tx, jobId);
        if (state.Kind == "scan" && status != "running")
        {
            FinishScanJobCore(db, tx, jobId, status, error);
            tx.Commit();
            return;
        }
        if (status == "running")
        {
            if (state.Kind != "scan" || state.Options is null || state.Status is not ("paused" or "interrupted" or "cancelled" or "failed")
                || state.SnapshotStatus is not ("paused" or "cancelled" or "failed"))
                throw new InvalidOperationException("Only an interrupted or stopped scan with an unfinished snapshot can resume.");
            ValidateScanJobOptions(db, tx, state.SnapshotId, JsonSerializer.Deserialize<ScanOptions>(state.Options, Json)
                ?? throw new InvalidDataException("The scan options are missing."));
        }
        else if (state.Status != "running")
        {
            if (state.Status == status) { tx.Commit(); return; }
            throw new InvalidOperationException("The job is already terminal.");
        }
        using var cmd=Command(db,"UPDATE jobs SET status=$status,finished_at=$at,error=$error WHERE id=$id",("$status",status),("$at",status=="running"?null:DateTimeOffset.UtcNow.ToString("O")),("$error",error),("$id",jobId));cmd.Transaction=tx;
        if(cmd.ExecuteNonQuery()!=1)throw new KeyNotFoundException("Job not found.");
        using(var control=Command(db,"DELETE FROM job_controls WHERE job_id=$id",("$id",jobId))) {control.Transaction=tx;control.ExecuteNonQuery();}
        if(status=="running") { using var snapshot=Command(db,"UPDATE snapshots SET status='running',finished_at=NULL,completeness='incomplete' WHERE id=(SELECT snapshot_id FROM jobs WHERE id=$id) AND (SELECT kind FROM jobs WHERE id=$id)='scan'",("$id",jobId));snapshot.Transaction=tx;snapshot.ExecuteNonQuery(); }
        tx.Commit();
    }
    /// <summary>Caller must hold AcquireWriter. A successfully acquired lock proves no conforming writer remains alive.</summary>
    public void RecoverAbandonedJobs()
    {
        RequireWritable();
        using var db=Open();using var tx=db.BeginTransaction();
        // Older versions sealed the snapshot and job in separate transactions. Preserve a
        // completed snapshot from that crash window instead of reopening its filesystem facts.
        using (var sealedJobs = Command(db, """
            UPDATE jobs SET status='completed',finished_at=(SELECT finished_at FROM snapshots WHERE id=jobs.snapshot_id),
              error='Recovered the job record of an already completed snapshot; the original seal was preserved.'
            WHERE status='running' AND kind='scan' AND snapshot_id IN (SELECT id FROM snapshots WHERE status='completed')
            """)) { sealedJobs.Transaction = tx; sealedJobs.ExecuteNonQuery(); }
        using (var controls = Command(db, "DELETE FROM job_controls WHERE job_id IN (SELECT id FROM jobs WHERE status='completed' AND kind='scan')"))
        { controls.Transaction = tx; controls.ExecuteNonQuery(); }
        using var snapshots=Command(db,"UPDATE snapshots SET status='paused',completeness='incomplete' WHERE id IN (SELECT snapshot_id FROM jobs WHERE status='running' AND kind='scan')");snapshots.Transaction=tx;snapshots.ExecuteNonQuery();
        using var jobs=Command(db,"UPDATE jobs SET status='interrupted',finished_at=$at,error='Writer exited before the job reached a terminal state; scan can resume from its durable frontier.' WHERE status='running'",("$at",DateTimeOffset.UtcNow.ToString("O")));jobs.Transaction=tx;jobs.ExecuteNonQuery();tx.Commit();
    }
    public IScanWorkQueue WorkQueue(string jobId) => new DurableQueue(this,jobId);
    public bool RequestJobStop(string jobId,string request)
    {
        if(request is not("paused" or "cancelled"))throw new ArgumentException("Invalid control request.");
        using var admission=AcquireMaintenanceAdmission();
        using var db=Open();using var cmd=Command(db,"""
            INSERT INTO job_controls SELECT j.id,$r FROM jobs j JOIN snapshots s ON s.id=j.snapshot_id
            WHERE j.id=$id AND j.status='running' AND ((j.kind='scan' AND s.status='running') OR (j.kind='analysis' AND $r='cancelled'))
            ON CONFLICT(job_id) DO UPDATE SET request=CASE WHEN job_controls.request='cancelled' THEN 'cancelled' ELSE excluded.request END
            """,("$r",request),("$id",jobId));return cmd.ExecuteNonQuery()>0;
    }
    public string? GetJobStop(string jobId)
    {
        using var db=Open();using var cmd=Command(db,"SELECT request FROM job_controls WHERE job_id=$id",("$id",jobId));return cmd.ExecuteScalar() as string;
    }

    public void CommitCheckpoint(string jobId,ScanCheckpointBatch checkpoint)
    {
        RequireWritable();
        using var db=Open();using var tx=db.BeginTransaction();
        var job=ReadJobState(db,tx,jobId);
        if (job.Kind != "scan" || job.Status != "running" || job.SnapshotStatus != "running")
            throw new InvalidOperationException("Only a running scan and running snapshot can accept a checkpoint.");
        ValidateSnapshotIngestion(db, tx, job.SnapshotId, checkpoint.Batch.Entries.Count != 0, checkpoint.Batch.Observations, checkpoint.Batch.Nodes);
        IngestGraph(db,tx,job.SnapshotId,checkpoint.Batch.Nodes,checkpoint.Batch.Edges,checkpoint.Batch.Observations);
        using var entry=Command(db,"INSERT OR IGNORE INTO entries VALUES($s,$p,$pp,$id,$n,$k,$l,$a,$m,$at,$lc,$o,$h)");entry.Transaction=tx;
        long added=0;
        foreach(var item in checkpoint.Batch.Entries)
        {
            if(item.SnapshotId!=job.SnapshotId)throw new ArgumentException("Checkpoint belongs to another snapshot.");
            Set(entry,("$s",item.SnapshotId),("$p",item.Path),("$pp",item.ParentPath),("$id",item.NodeId),("$n",item.Name),("$k",item.Kind),("$l",item.LogicalBytes),("$a",item.AllocatedBytes),("$m",item.ModifiedAt.ToString("O")),("$at",item.Attributes),("$lc",item.LinkCount),("$o",item.Owner),("$h",item.Hash));added+=entry.ExecuteNonQuery();
        }
        using var done=Command(db,"UPDATE scan_work SET done=1 WHERE job_id=$id AND path=$p");done.Transaction=tx;
        foreach(var path in checkpoint.CompletedPaths) {Set(done,("$id",jobId),("$p",path));done.ExecuteNonQuery();}
        using var count=Command(db,"UPDATE jobs SET entries=entries+$added WHERE id=$id",("$added",added),("$id",jobId));count.Transaction=tx;count.ExecuteNonQuery();
        tx.Commit();
    }

    private sealed class DurableQueue(MapStore owner,string jobId):IScanWorkQueue
    {
        public string? DirectoryIdentity(string path)
        {
            using var db=owner.Open();using var cmd=Command(db,"SELECT identity FROM scan_directories WHERE job_id=$id AND path=$p",("$id",jobId),("$p",path));return cmd.ExecuteScalar() as string;
        }
        public IReadOnlyList<ScanWork> Pending(int limit)
        {
            ValidatePage(0,limit);using var db=owner.Open();using var cmd=Command(db,"SELECT path,parent_path,parent_id FROM scan_work WHERE job_id=$id AND done=0 ORDER BY path LIMIT $l",("$id",jobId),("$l",limit));using var r=cmd.ExecuteReader();var result=new List<ScanWork>();
            while(r.Read())result.Add(new(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)));return result;
        }
        public void Discover(IReadOnlyList<ScanWork> paths)
        {
            using var db=owner.Open();using var tx=db.BeginTransaction();using var cmd=Command(db,"INSERT OR IGNORE INTO scan_work(job_id,path,parent_path,parent_id) VALUES($id,$p,$pp,$pi)");cmd.Transaction=tx;
            RequireRunningScan(db, tx, jobId);
            foreach(var path in paths) {Set(cmd,("$id",jobId),("$p",path.Path),("$pp",path.ParentPath),("$pi",path.ParentId));cmd.ExecuteNonQuery();}tx.Commit();
        }
        public bool EnterDirectory(string stableIdentity,string path)
        {
            using var db=owner.Open();using var tx=db.BeginTransaction();
            RequireRunningScan(db, tx, jobId);
            using(var check=Command(db,"SELECT identity FROM scan_directories WHERE job_id=$id AND path=$p",("$id",jobId),("$p",path))) {check.Transaction=tx;var prior=check.ExecuteScalar() as string;if(prior is not null&&prior!=stableIdentity)throw new IOException("Directory identity changed after checkpoint discovery; traversal is incomplete.");}
            using var insert=Command(db,"INSERT OR IGNORE INTO scan_directories VALUES($id,$identity,$p)",("$id",jobId),("$identity",stableIdentity),("$p",path));insert.Transaction=tx;insert.ExecuteNonQuery();
            using var get=Command(db,"SELECT path FROM scan_directories WHERE job_id=$id AND identity=$identity",("$id",jobId),("$identity",stableIdentity));get.Transaction=tx;var first=(string)get.ExecuteScalar()!;tx.Commit();return first==path;
        }
    }
}
