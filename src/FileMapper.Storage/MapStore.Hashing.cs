using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    private const int HashingPayloadLimit = 64 * 1024;
    private static readonly string[] HashingTables = ["hashing_runs", "hashing_work", "hashing_paths", "hashing_attempts",
        "hashing_evidence", "hashing_chunks", "hashing_cache", "hashing_groups", "hashing_members", "hashing_overlaps"];

    /// <summary>Explicit additive migration. All other mutation methods require the coordinator's writer lease.</summary>
    public void InitializeHashing()
    {
        RequireWritable();
        if (HasHashingStore()) return;
        if (!File.Exists(DatabasePath)) throw new InvalidOperationException("Initialize the base map database before hashing storage.");
        using var ownership = AcquireWriter(allowInstallerSession: true);
        using var db = Open(); using var tx = db.BeginTransaction();
        using var schema = HashingCommand(db, tx, """
            CREATE TABLE IF NOT EXISTS hashing_runs(
              id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES snapshots(id),options TEXT NOT NULL,request_hash TEXT NOT NULL,
              status TEXT NOT NULL,phase TEXT NOT NULL,started_at TEXT NOT NULL,finished_at TEXT,updated_at TEXT NOT NULL,
              revision INTEGER NOT NULL,coverage TEXT NOT NULL,result_hash TEXT,error TEXT,stop_request TEXT,
              prepared INTEGER NOT NULL DEFAULT 0,chunk_reserved INTEGER NOT NULL DEFAULT 0,bytes_reserved INTEGER NOT NULL DEFAULT 0,
              baseline TEXT NOT NULL,scope_safe INTEGER NOT NULL,completeness TEXT NOT NULL DEFAULT 'incomplete');
            CREATE INDEX IF NOT EXISTS ix_hashing_runs_snapshot ON hashing_runs(snapshot_id,started_at DESC,id);
            CREATE INDEX IF NOT EXISTS ix_hashing_runs_status ON hashing_runs(status,started_at);
            CREATE TABLE IF NOT EXISTS hashing_work(
              run_id TEXT NOT NULL REFERENCES hashing_runs(id),node_id TEXT NOT NULL,path TEXT NOT NULL,
              logical_bytes INTEGER NOT NULL,modified_at TEXT NOT NULL,attributes TEXT NOT NULL,path_count INTEGER NOT NULL,
              link_count INTEGER NOT NULL,allocated_bytes INTEGER,has_ads INTEGER NOT NULL,metadata_consistent INTEGER NOT NULL,
              admission_status TEXT NOT NULL,stage TEXT NOT NULL,sample_evidence_id TEXT,full_evidence_id TEXT,error TEXT,
              active_attempt_id TEXT,chunk_evidence_id TEXT,PRIMARY KEY(run_id,node_id));
            CREATE INDEX IF NOT EXISTS ix_hashing_work_stage ON hashing_work(run_id,stage,node_id);
            CREATE INDEX IF NOT EXISTS ix_hashing_work_size ON hashing_work(run_id,logical_bytes,node_id);
            CREATE TABLE IF NOT EXISTS hashing_paths(run_id TEXT NOT NULL,node_id TEXT NOT NULL,path TEXT NOT NULL,
              PRIMARY KEY(run_id,node_id,path),FOREIGN KEY(run_id,node_id) REFERENCES hashing_work(run_id,node_id));
            CREATE TABLE IF NOT EXISTS hashing_attempts(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,node_id TEXT NOT NULL,
              stage TEXT NOT NULL,status TEXT NOT NULL,started_at TEXT NOT NULL,finished_at TEXT,
              chunk_count INTEGER NOT NULL DEFAULT 0,chunk_bytes INTEGER NOT NULL DEFAULT 0,byte_reserved INTEGER NOT NULL DEFAULT 0,
              FOREIGN KEY(run_id,node_id) REFERENCES hashing_work(run_id,node_id));
            CREATE INDEX IF NOT EXISTS ix_hashing_attempts_run ON hashing_attempts(run_id,status,id);
            CREATE TABLE IF NOT EXISTS hashing_evidence(id TEXT PRIMARY KEY REFERENCES hashing_attempts(id),run_id TEXT NOT NULL,
              node_id TEXT NOT NULL,stage TEXT NOT NULL,policy_hash TEXT NOT NULL,status TEXT NOT NULL,digest TEXT,version_key TEXT,
              cache_hit INTEGER NOT NULL,cache_source_id TEXT,bytes_read INTEGER NOT NULL,chunk_count INTEGER NOT NULL,
              chunk_bytes INTEGER NOT NULL,chunks_complete INTEGER NOT NULL,allocation INTEGER,links INTEGER,
              has_ads INTEGER,paths_complete INTEGER,validated_paths INTEGER,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_hashing_evidence_run ON hashing_evidence(run_id,node_id,id);
            CREATE INDEX IF NOT EXISTS ix_hashing_evidence_digest ON hashing_evidence(run_id,stage,status,digest,node_id);
            CREATE TABLE IF NOT EXISTS hashing_chunks(attempt_id TEXT NOT NULL REFERENCES hashing_attempts(id),
              ordinal INTEGER NOT NULL,offset INTEGER NOT NULL,length INTEGER NOT NULL,sha256 TEXT NOT NULL,
              PRIMARY KEY(attempt_id,ordinal));
            CREATE INDEX IF NOT EXISTS ix_hashing_chunks_digest ON hashing_chunks(sha256,length,attempt_id);
            CREATE TABLE IF NOT EXISTS hashing_cache(node_id TEXT NOT NULL,version_key TEXT NOT NULL,policy_hash TEXT NOT NULL,
              evidence_id TEXT NOT NULL REFERENCES hashing_evidence(id),PRIMARY KEY(node_id,version_key,policy_hash));
            CREATE TABLE IF NOT EXISTS hashing_groups(id TEXT NOT NULL,run_id TEXT NOT NULL REFERENCES hashing_runs(id),
              mode TEXT NOT NULL,digest TEXT NOT NULL,logical_bytes INTEGER NOT NULL,identity_count INTEGER NOT NULL,
              path_count INTEGER NOT NULL,reclaimable_bytes INTEGER,confidence TEXT NOT NULL,explanation TEXT NOT NULL,
              PRIMARY KEY(run_id,id));
            CREATE INDEX IF NOT EXISTS ix_hashing_groups_page ON hashing_groups(run_id,mode,logical_bytes DESC,id);
            CREATE TABLE IF NOT EXISTS hashing_members(run_id TEXT NOT NULL,group_id TEXT NOT NULL,node_id TEXT NOT NULL,
              evidence_id TEXT NOT NULL,PRIMARY KEY(run_id,group_id,node_id),
              FOREIGN KEY(run_id,group_id) REFERENCES hashing_groups(run_id,id));
            CREATE TABLE IF NOT EXISTS hashing_overlaps(run_id TEXT NOT NULL REFERENCES hashing_runs(id),left_id TEXT NOT NULL,
              right_id TEXT NOT NULL,shared_bytes INTEGER NOT NULL,shared_occurrences INTEGER NOT NULL,completeness TEXT NOT NULL,
              PRIMARY KEY(run_id,left_id,right_id));
            CREATE INDEX IF NOT EXISTS ix_hashing_overlaps_right ON hashing_overlaps(run_id,right_id,left_id);
            INSERT OR IGNORE INTO schema_versions VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """);
        schema.ExecuteNonQuery(); tx.Commit();
    }

    public bool HasHashingStore()
    {
        if (!File.Exists(DatabasePath)) return false;
        using var db = OpenHashingRead(); return HasHashingSchema(db);
    }

    public HashingRun CreateHashingRun(HashingRequest request)
    {
        RequireWritable(); ArgumentNullException.ThrowIfNull(request); HashingText(request.SnapshotId, 256, "snapshotId");
        var options = request.Options ?? new(); options.Validate();
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction();
        Snapshot baseline;
        using (var query = HashingCommand(db, tx, "SELECT * FROM snapshots WHERE id=$id", ("$id", request.SnapshotId)))
        using (var row = query.ExecuteReader()) { if (!row.Read()) throw new KeyNotFoundException("Snapshot not found."); baseline = ReadSnapshot(row); }
        if (baseline.Status != "completed" || baseline.FinishedAt is null) throw new InvalidOperationException("Hashing requires a finished baseline snapshot.");
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseline.Root));
        var scope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ScopePath ?? boundary));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + Path.DirectorySeparatorChar;
        if (!scope.Equals(boundary, comparison) && !scope.StartsWith(prefix, comparison)) throw new ArgumentException("Hashing scope must remain within the baseline root.");
        HashingText(scope, 32768, "scopePath"); options = options with { ScopePath = scope };
        ScanOptions? scanOptions = null;
        using (var policy = HashingCommand(db, tx, "SELECT payload FROM observations WHERE snapshot_id=$s AND predicate='scan.options' ORDER BY id LIMIT 1", ("$s", baseline.Id)))
            if (policy.ExecuteScalar() is string payload) scanOptions = JsonSerializer.Deserialize<ScanOptions>(JsonSerializer.Deserialize<Evidence>(payload, Json)!.Value, Json);
        var now = DateTimeOffset.UtcNow;
        var run = new HashingRun("hash-job-" + Guid.NewGuid().ToString("N"), baseline.Id, options,
            Identity.For("hashing-request", HashingJson(new HashingRequest(baseline.Id, options))), "running", "admitting", now, null, now, 0, new());
        HashingExecute(db, tx, """
            INSERT INTO hashing_runs(id,snapshot_id,options,request_hash,status,phase,started_at,updated_at,revision,coverage,baseline,scope_safe)
            VALUES($id,$s,$o,$h,$status,$phase,$at,$at,0,$c,$baseline,$safe)
            """, ("$id", run.Id), ("$s", run.SnapshotId), ("$o", HashingJson(options)), ("$h", run.RequestHash), ("$status", run.Status),
            ("$phase", run.Phase), ("$at", now.ToString("O")), ("$c", HashingJson(run.Coverage)), ("$baseline", HashingJson(baseline)),
            ("$safe", baseline.Completeness == "complete" && scanOptions?.ReparsePolicy is "skip" or "reference" ? 1 : 0));
        tx.Commit(); return run;
    }

    public void PrepareHashingCandidates(string runId, CancellationToken cancellationToken = default)
    {
        RequireWritable(); using var db = Open(); RequireHashingSchema(db); using var cancel = new SourceSqliteCancellation(db, cancellationToken);
        cancel.Run(() =>
        {
            using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
            if (HashingLong(db, tx, "SELECT prepared FROM hashing_runs WHERE id=$r", ("$r", runId)) != 0) return;
            var scope = run.Options.ScopePath!;
            var prefix = Path.EndsInDirectorySeparator(scope) ? scope : scope + Path.DirectorySeparatorChar;
            var collation = OperatingSystem.IsWindows() ? "NOCASE" : "BINARY";
            HashingExecute(db, tx, "DROP TABLE IF EXISTS temp.hashing_admission; DROP TABLE IF EXISTS temp.hashing_metadata;");
            HashingExecute(db, tx, """
                CREATE TEMP TABLE hashing_metadata AS
                WITH scoped AS (
                  SELECT e.* FROM entries e WHERE e.snapshot_id=$s AND e.kind='File'
                """ + $" AND (e.path=$scope COLLATE {collation} OR substr(e.path,1,length($prefix))=$prefix COLLATE {collation})" + """
                ), grouped AS (
                  SELECT node_id,MIN(path) path,MAX(logical_bytes) logical_bytes,MAX(modified_at) modified_at,MAX(attributes) attributes,
                    COUNT(*) path_count,MAX(link_count) link_count,
                    CASE WHEN COUNT(*)=COUNT(allocated_bytes) AND MIN(allocated_bytes)=MAX(allocated_bytes) THEN MAX(allocated_bytes) END allocated_bytes,
                    MIN(logical_bytes)=MAX(logical_bytes) AND MIN(modified_at)=MAX(modified_at) AND MIN(link_count)=MAX(link_count) AND MIN(link_count)>=1 AND MIN(attributes)=MAX(attributes) consistent,
                    MAX(CASE WHEN attributes LIKE '%ReparsePoint%' THEN 1 ELSE 0 END) reparse
                  FROM scoped GROUP BY node_id
                )
                SELECT *,CASE WHEN consistent=0 THEN 'metadata-conflict' WHEN reparse=1 THEN 'reparse'
                  WHEN node_id LIKE 'path-fallback:%' THEN 'unstable-identity' WHEN logical_bytes<$min THEN 'minimum-size'
                  ELSE 'eligible' END reason FROM grouped;
                CREATE TEMP TABLE hashing_admission AS
                WITH eligible AS (SELECT *,COUNT(*) OVER(PARTITION BY logical_bytes) size_peers FROM hashing_metadata WHERE reason='eligible')
                SELECT *,ROW_NUMBER() OVER(ORDER BY node_id) ordinal,SUM(path_count) OVER(ORDER BY node_id) cumulative_paths
                FROM eligible WHERE $all=1 OR size_peers>1
                """, ("$s", run.SnapshotId), ("$scope", scope), ("$prefix", prefix), ("$min", run.Options.MinimumBytes),
                ("$all", run.Options.Mode is "partial" or "all" ? 1 : 0));
            var scopePredicate = $"snapshot_id=$s AND kind='File' AND (path=$scope COLLATE {collation} OR substr(path,1,length($prefix))=$prefix COLLATE {collation})";
            var parameters = new (string, object?)[] { ("$s", run.SnapshotId), ("$scope", scope), ("$prefix", prefix) };
            var total = HashingLong(db, tx, "SELECT COUNT(DISTINCT node_id) FROM entries WHERE " + scopePredicate, parameters);
            var paths = HashingLong(db, tx, "SELECT COUNT(*) FROM entries WHERE " + scopePredicate, parameters);
            var candidates = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_admission");
            var eligibleCount = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_metadata WHERE reason='eligible'");
            var unsafeCount = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_metadata WHERE reason IN ('metadata-conflict','reparse','unstable-identity')");
            var minimumCount = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_metadata WHERE reason='minimum-size'");
            var reasons = new SortedDictionary<string, long>(StringComparer.Ordinal);
            using (var reasonQuery = HashingCommand(db, tx, "SELECT reason,COUNT(*) FROM hashing_metadata WHERE reason<>'eligible' GROUP BY reason ORDER BY reason"))
            using (var reasonRows = reasonQuery.ExecuteReader()) while (reasonRows.Read()) reasons.Add(reasonRows.GetString(0), reasonRows.GetInt64(1));
            HashingExecute(db, tx, """
                INSERT INTO hashing_work(run_id,node_id,path,logical_bytes,modified_at,attributes,path_count,link_count,allocated_bytes,
                  has_ads,metadata_consistent,admission_status,stage)
                SELECT $r,a.node_id,a.path,a.logical_bytes,a.modified_at,a.attributes,a.path_count,a.link_count,a.allocated_bytes,
                  EXISTS(SELECT 1 FROM entries f JOIN entries ads ON ads.snapshot_id=f.snapshot_id AND ads.parent_path=f.path AND ads.kind='Stream'
                    WHERE f.snapshot_id=$s AND f.node_id=a.node_id),1,'admitted',$stage
                FROM hashing_admission a WHERE ordinal <= $max AND cumulative_paths <= $paths
                """, ("$r", run.Id), ("$s", run.SnapshotId), ("$stage", run.Options.Mode == "partial" ? "full-pending" : "sample-pending"),
                ("$max", run.Options.MaxIdentities), ("$paths", run.Options.MaxPaths));
            HashingExecute(db, tx, "INSERT INTO hashing_paths SELECT $r,e.node_id,e.path FROM entries e JOIN hashing_work w ON w.run_id=$r AND w.node_id=e.node_id WHERE " +
                scopePredicate.Replace("snapshot_id", "e.snapshot_id", StringComparison.Ordinal).Replace("kind=", "e.kind=", StringComparison.Ordinal).Replace("path=", "e.path=", StringComparison.Ordinal).Replace("substr(path,", "substr(e.path,", StringComparison.Ordinal),
                [..parameters, ("$r", run.Id)]);
            var admitted = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_work WHERE run_id=$r", ("$r", runId));
            var admittedPaths = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_paths WHERE run_id=$r", ("$r", runId));
            reasons["unique-size"] = eligibleCount - candidates; reasons["admission-limit"] = candidates - admitted; reasons["admitted"] = admitted;
            SaveHashingRun(db, tx, run with { Phase = run.Options.Mode == "partial" ? "full" : "sampling", Coverage = run.Coverage with
            { TotalIdentities = total, TotalPaths = paths, CandidateIdentities = candidates, AdmittedIdentities = admitted,
                AdmittedPaths = admittedPaths, SkippedIdentities = total - admitted, AdmissionTruncated = admitted < candidates,
                UnsafeExcludedIdentities = unsafeCount, PolicyExcludedIdentities = minimumCount + eligibleCount - candidates, AdmissionReasons = reasons } });
            HashingExecute(db, tx, "UPDATE hashing_runs SET prepared=1 WHERE id=$r", ("$r", runId));
            HashingExecute(db, tx, "DROP TABLE temp.hashing_admission; DROP TABLE temp.hashing_metadata;"); tx.Commit();
        });
    }

    public HashingRun? GetHashingRun(string runId)
    {
        HashingText(runId, 256, "runId"); using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return null;
        return ReadHashingRun(db, null, runId) is { } run ? WithHashingControl(run) : null;
    }

    public IReadOnlyList<HashingRun> ListHashingRuns(string? snapshotId = null, int limit = 100)
    {
        HashingPage(0, limit, 1000); if (snapshotId is not null) HashingText(snapshotId, 256, "snapshotId");
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return [];
        using var query = HashingCommand(db, null, "SELECT * FROM hashing_runs WHERE ($s IS NULL OR snapshot_id=$s) ORDER BY CASE WHEN status='running' THEN 0 ELSE 1 END,started_at DESC,id LIMIT $l", ("$s", snapshotId), ("$l", limit));
        using var rows = query.ExecuteReader(); var items = new List<HashingRun>(); while (rows.Read()) items.Add(WithHashingControl(ReadHashingRun(rows))); return items;
    }

    public IReadOnlyList<HashingWorkItem> GetHashingWork(string runId, string stage, string? afterNodeId = null, int limit = 128)
    {
        HashingText(runId, 256, "runId"); HashingPage(0, limit, 1024); stage = HashingPendingStage(stage);
        if (afterNodeId is not null) HashingText(afterNodeId, 512, "afterNodeId");
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return [];
        using var query = HashingCommand(db, null, "SELECT * FROM hashing_work WHERE run_id=$r AND stage=$stage AND active_attempt_id IS NULL AND ($after IS NULL OR node_id>$after) ORDER BY node_id LIMIT $l",
            ("$r", runId), ("$stage", stage), ("$after", afterNodeId), ("$l", limit)); using var rows = query.ExecuteReader();
        var items = new List<HashingWorkItem>(); while (rows.Read()) items.Add(ReadHashingWork(rows)); return items;
    }

    public Page<string> GetHashingPaths(string runId, string nodeId, int limit = 1024, int offset = 0)
    {
        HashingText(runId, 256, "runId"); HashingText(nodeId, 512, "nodeId"); HashingPage(offset, limit, 1024);
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true); var values = new (string, object?)[] { ("$r", runId), ("$n", nodeId) };
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_paths WHERE run_id=$r AND node_id=$n", values);
        using var query = HashingCommand(db, tx, "SELECT path FROM hashing_paths WHERE run_id=$r AND node_id=$n ORDER BY path LIMIT $l OFFSET $o", [..values, ("$l", limit), ("$o", offset)]);
        using var rows = query.ExecuteReader(); var items = new List<string>(); while (rows.Read()) items.Add(rows.GetString(0)); return new(items, total, offset, limit);
    }

    private SqliteConnection OpenHashingRead()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { db.Open(); using var cmd = Command(db, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;"); cmd.ExecuteNonQuery(); return db; } catch { db.Dispose(); throw; }
    }
    private static bool HasHashingSchema(SqliteConnection db)
    {
        using var query = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (" + string.Join(',', HashingTables.Select(name => "'" + name + "'")) + ")");
        if ((long)query.ExecuteScalar()! != HashingTables.Length) return false;
        using var version = Command(db, "SELECT COUNT(*) FROM schema_versions WHERE version=3"); return (long)version.ExecuteScalar()! == 1;
    }
    private static void RequireHashingSchema(SqliteConnection db) { if (!HasHashingSchema(db)) throw new InvalidOperationException("Hashing storage has not been initialized explicitly."); }
    private static SqliteCommand HashingCommand(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { var command = Command(db, sql, values); command.Transaction = tx; return command; }
    private static int HashingExecute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var command = HashingCommand(db, tx, sql, values); return command.ExecuteNonQuery(); }
    private static long HashingLong(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var command = HashingCommand(db, tx, sql, values); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static string HashingJson<T>(T value)
    { var json = JsonSerializer.Serialize(value, Json); if (Encoding.UTF8.GetByteCount(json) > HashingPayloadLimit) throw new ArgumentException("Hashing record exceeds 64 KiB."); return json; }
    private static void HashingText(string? value, int maximum, string name)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Invalid bounded " + name + "."); }
    private static void HashingDigest(string? value)
    { if (value is null || value.Length != 64 || value.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new ArgumentException("A full lowercase SHA-256 digest is required."); }
    private static void HashingPage(int offset, int limit, int maximum = 200)
    { if (offset < 0 || limit < 1 || limit > maximum) throw new ArgumentOutOfRangeException(nameof(limit), "Hashing page exceeds its explicit row bound."); }
    private static string HashingPendingStage(string value) => value switch { "sample" or "sample-pending" => "sample-pending", "full" or "full-pending" => "full-pending", _ => throw new ArgumentException("Unknown hashing work stage.") };
    private static string HashingReadStage(string value) => HashingPendingStage(value) == "sample-pending" ? "sample" : "full";
    private static HashingRun? ReadHashingRun(SqliteConnection db, SqliteTransaction? tx, string id)
    { using var query = HashingCommand(db, tx, "SELECT * FROM hashing_runs WHERE id=$id", ("$id", id)); using var row = query.ExecuteReader(); return row.Read() ? ReadHashingRun(row) : null; }
    private static HashingRun RequireHashingRun(SqliteConnection db, SqliteTransaction? tx, string id) => ReadHashingRun(db, tx, id) ?? throw new KeyNotFoundException("Hashing run not found.");
    private static HashingRun ReadHashingRun(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), JsonSerializer.Deserialize<HashingOptions>(row.GetString(2), Json)!, row.GetString(3), row.GetString(4), row.GetString(5), Date(row.GetString(6)), row.IsDBNull(7) ? null : Date(row.GetString(7)), Date(row.GetString(8)), row.GetInt64(9), JsonSerializer.Deserialize<HashingCoverage>(row.GetString(10), Json)!, row.IsDBNull(11) ? null : row.GetString(11), row.IsDBNull(12) ? null : row.GetString(12), row.IsDBNull(13) ? null : row.GetString(13)) { Completeness = row.GetString(row.GetOrdinal("completeness")) };
    private static HashingWorkItem ReadHashingWork(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), Date(row.GetString(4)), row.GetString(5), row.GetInt64(6), row.GetInt64(7), row.IsDBNull(8) ? null : row.GetInt64(8), row.GetInt64(9) != 0, row.GetInt64(10) != 0, row.GetString(11), row.GetString(12), row.IsDBNull(13) ? null : row.GetString(13), row.IsDBNull(14) ? null : row.GetString(14), row.IsDBNull(15) ? null : row.GetString(15), row.IsDBNull(16) ? null : row.GetString(16));
    private static HashingWorkItem RequireHashingWork(SqliteConnection db, SqliteTransaction tx, string runId, string nodeId)
    { using var query = HashingCommand(db, tx, "SELECT * FROM hashing_work WHERE run_id=$r AND node_id=$n", ("$r", runId), ("$n", nodeId)); using var row = query.ExecuteReader(); return row.Read() ? ReadHashingWork(row) : throw new KeyNotFoundException("Hashing candidate not found."); }
    private static void RequireHashingMutable(HashingRun run)
    { if (run.ResultHash is not null || run.Status != "running") throw new InvalidOperationException("Hashing work requires an active unsealed run."); }
    private static void SaveHashingRun(SqliteConnection db, SqliteTransaction tx, HashingRun run)
    {
        if (HashingExecute(db, tx, "UPDATE hashing_runs SET status=$status,phase=$phase,finished_at=$finished,updated_at=$now,revision=revision+1,coverage=$c,result_hash=$hash,error=$error,stop_request=$stop,completeness=$completeness WHERE id=$id",
            ("$id", run.Id), ("$status", run.Status), ("$phase", run.Phase), ("$finished", run.FinishedAt?.ToString("O")), ("$now", DateTimeOffset.UtcNow.ToString("O")),
            ("$c", HashingJson(run.Coverage)), ("$hash", run.ResultHash), ("$error", run.Error), ("$stop", run.StopRequest), ("$completeness", run.Completeness)) != 1) throw new KeyNotFoundException("Hashing run not found.");
    }
}
