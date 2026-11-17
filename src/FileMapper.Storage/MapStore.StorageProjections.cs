using FileMapper.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FileMapper.Storage;

/// <summary>Cold preparation is separate from subsequent reads. Counts cover the exact requested
/// subtree's three facets and every duplicate-eligible path in the snapshot, with no dataset cap.</summary>
public sealed record StorageProjectionPreparation(string SnapshotId, string Path, string ModelVersion,
    bool FacetsBuilt, bool DuplicatesBuilt, long FacetRows, long DuplicateGroups, long DuplicateMembers,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

public sealed partial class MapStore
{
    private const string StorageProjectionModel = "filesystem-storage-projection/3";

    private static SqliteCommand ProjectionCommand(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string, object?)[] values)
    {
        var cmd = Command(db, sql, values);
        cmd.Transaction = tx;
        return cmd;
    }

    private SqliteConnection OpenStorageProjection()
    {
        var db = Open();
        try
        {
            // Sorts and intermediate relations may spill to disk. Do not retain an entire scope
            // in managed memory, or use temp_store=MEMORY on a ten-million-entry snapshot.
            using var config = Command(db, "PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA temp.cache_size=-8192; PRAGMA threads=1;");
            config.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static bool StorageProjectionSchemaExists(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = ProjectionCommand(db, tx,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='storage_projection_schema'");
        if ((long)cmd.ExecuteScalar()! == 0) return false;
        using var version = ProjectionCommand(db, tx, "SELECT model FROM storage_projection_schema WHERE version=3");
        return version.ExecuteScalar() as string == StorageProjectionModel;
    }

    private static void CreateStorageProjectionSchema(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = ProjectionCommand(db, tx, """
            CREATE TABLE IF NOT EXISTS storage_projection_schema(version INTEGER PRIMARY KEY,model TEXT NOT NULL);
            INSERT OR IGNORE INTO storage_projection_schema VALUES(3,'filesystem-storage-projection/3');
            CREATE INDEX IF NOT EXISTS ix_storage_scan_options ON observations(snapshot_id,id) WHERE predicate='scan.options';
            CREATE TABLE IF NOT EXISTS storage_facet_heads(
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id),path TEXT NOT NULL,model TEXT NOT NULL,
              started_at TEXT NOT NULL,finished_at TEXT NOT NULL,valid INTEGER NOT NULL,
              extension_count INTEGER NOT NULL,owner_count INTEGER NOT NULL,age_count INTEGER NOT NULL,
              conflicts INTEGER NOT NULL,PRIMARY KEY(snapshot_id,path));
            CREATE TABLE IF NOT EXISTS storage_facets(
              snapshot_id TEXT NOT NULL,path TEXT NOT NULL,dimension TEXT NOT NULL,facet_key TEXT NOT NULL,
              identity_count INTEGER NOT NULL,files INTEGER NOT NULL,streams INTEGER NOT NULL,names INTEGER NOT NULL,
              logical_bytes INTEGER NOT NULL,allocated INTEGER,known INTEGER NOT NULL,unknown INTEGER NOT NULL,conflicts INTEGER NOT NULL,
              PRIMARY KEY(snapshot_id,path,dimension,facet_key));
            CREATE INDEX IF NOT EXISTS ix_storage_facets_page ON storage_facets(snapshot_id,path,dimension,logical_bytes DESC,facet_key);
            CREATE TABLE IF NOT EXISTS storage_duplicate_heads(
              snapshot_id TEXT PRIMARY KEY REFERENCES snapshots(id),model TEXT NOT NULL,started_at TEXT NOT NULL,
              finished_at TEXT NOT NULL,valid INTEGER NOT NULL,group_count INTEGER NOT NULL,member_count INTEGER NOT NULL,
              hashed_files INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS storage_duplicate_groups(
              id INTEGER PRIMARY KEY,snapshot_id TEXT NOT NULL,hash TEXT NOT NULL,logical_bytes INTEGER NOT NULL,
              identity_count INTEGER NOT NULL,names INTEGER NOT NULL,reclaimable INTEGER,member_count INTEGER NOT NULL DEFAULT 0,
              UNIQUE(snapshot_id,hash,logical_bytes));
            CREATE INDEX IF NOT EXISTS ix_storage_duplicate_page ON storage_duplicate_groups(snapshot_id,logical_bytes DESC,hash);
            CREATE TABLE IF NOT EXISTS storage_duplicate_members(
              group_id INTEGER NOT NULL REFERENCES storage_duplicate_groups(id) ON DELETE CASCADE,
              path TEXT NOT NULL,PRIMARY KEY(group_id,path)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS storage_directory_metrics(
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id),path TEXT NOT NULL,model TEXT NOT NULL,
              started_at TEXT NOT NULL,finished_at TEXT NOT NULL,valid INTEGER NOT NULL,payload TEXT NOT NULL,
              PRIMARY KEY(snapshot_id,path));

            CREATE TRIGGER IF NOT EXISTS storage_projection_entry_insert AFTER INSERT ON entries BEGIN
              UPDATE storage_facet_heads SET valid=0 WHERE snapshot_id=NEW.snapshot_id AND valid=1;
              UPDATE storage_duplicate_heads SET valid=0 WHERE snapshot_id=NEW.snapshot_id AND valid=1;
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id=NEW.snapshot_id AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_entry_delete AFTER DELETE ON entries BEGIN
              UPDATE storage_facet_heads SET valid=0 WHERE snapshot_id=OLD.snapshot_id AND valid=1;
              UPDATE storage_duplicate_heads SET valid=0 WHERE snapshot_id=OLD.snapshot_id AND valid=1;
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id=OLD.snapshot_id AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_entry_update AFTER UPDATE ON entries BEGIN
              UPDATE storage_facet_heads SET valid=0 WHERE snapshot_id IN (OLD.snapshot_id,NEW.snapshot_id) AND valid=1;
              UPDATE storage_duplicate_heads SET valid=0 WHERE snapshot_id IN (OLD.snapshot_id,NEW.snapshot_id) AND valid=1;
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id IN (OLD.snapshot_id,NEW.snapshot_id) AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_snapshot_update
              AFTER UPDATE OF root,started_at,finished_at,status,completeness ON snapshots
              WHEN OLD.root IS NOT NEW.root OR OLD.started_at IS NOT NEW.started_at OR OLD.finished_at IS NOT NEW.finished_at
                OR OLD.status IS NOT NEW.status OR OLD.completeness IS NOT NEW.completeness BEGIN
              UPDATE storage_facet_heads SET valid=0 WHERE snapshot_id=NEW.id AND valid=1;
              UPDATE storage_duplicate_heads SET valid=0 WHERE snapshot_id=NEW.id AND valid=1;
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id=NEW.id AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_policy_insert AFTER INSERT ON observations
              WHEN NEW.predicate='scan.options' BEGIN
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id=NEW.snapshot_id AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_policy_update AFTER UPDATE ON observations
              WHEN OLD.predicate='scan.options' OR NEW.predicate='scan.options' BEGIN
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id IN (OLD.snapshot_id,NEW.snapshot_id) AND valid=1;
            END;
            CREATE TRIGGER IF NOT EXISTS storage_projection_policy_delete AFTER DELETE ON observations
              WHEN OLD.predicate='scan.options' BEGIN
              UPDATE storage_directory_metrics SET valid=0 WHERE snapshot_id=OLD.snapshot_id AND valid=1;
            END;
            """);
        cmd.ExecuteNonQuery();
        if (!StorageProjectionSchemaExists(db, tx)) throw new InvalidDataException("Unknown storage projection model.");
    }

    private static Snapshot ProjectionSnapshot(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var cmd = ProjectionCommand(db, tx, "SELECT * FROM snapshots WHERE id=$s", ("$s", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : throw new KeyNotFoundException("Snapshot not found.");
    }

    private static ScanOptions? ProjectionScanOptions(SqliteConnection db, SqliteTransaction tx, string id)
    {
        string index = StorageProjectionSchemaExists(db, tx) ? " INDEXED BY ix_storage_scan_options" : "";
        using var cmd = ProjectionCommand(db, tx, "SELECT o.payload,s.root FROM observations o" + index + " " + """
            JOIN snapshots s ON s.id=o.snapshot_id
            WHERE o.snapshot_id=$s AND o.predicate='scan.options' ORDER BY o.id LIMIT 2
            """, ("$s", id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        string payload = reader.GetString(0), root = reader.GetString(1);
        if (reader.Read()) return null;
        var evidence = JsonSerializer.Deserialize<Evidence>(payload, Json);
        if (evidence is null || evidence.SnapshotId != id || evidence.SubjectId != id || evidence.Predicate != "scan.options")
            return null;
        var options = JsonSerializer.Deserialize<ScanOptions>(evidence.Value, Json);
        if (options is null) return null;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? options : null;
    }

    private static string ProjectionPath(Snapshot snapshot, string? path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path ?? snapshot.Root));

    private static bool ProjectionReady(SqliteConnection db, SqliteTransaction tx, string snapshotId, string? path, string table)
    {
        if (!StorageProjectionSchemaExists(db, tx)) return false;
        // Table is an internal constant. All user values are parameters. This is the read's
        // transaction snapshot: a concurrent invalidation cannot mix old rows with new metadata.
        using var cmd = ProjectionCommand(db, tx, $"""
            SELECT COUNT(*) FROM {table} h JOIN snapshots s ON s.id=h.snapshot_id
            WHERE h.snapshot_id=$s AND h.model=$model AND h.valid=1 AND s.status='completed'
              AND h.started_at=s.started_at AND h.finished_at=s.finished_at
            """ + (path is null ? "" : " AND h.path=$p"), ("$s", snapshotId), ("$model", StorageProjectionModel), ("$p", path));
        return (long)cmd.ExecuteScalar()! == 1;
    }

    /// <summary>Explicit cold preparation. Requires a writable store and acquires normal writer
    /// plus maintenance admission. A failed/cancelled transaction publishes no partial projection.
    /// Existing valid projections are reused; running/paused snapshots are never prepared.</summary>
    public StorageProjectionPreparation PrepareStorageProjections(string snapshotId, string? path = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var ownership = AcquireWriter();
        return PrepareStorageProjectionsOwned(snapshotId, path, true, true, cancellationToken);
    }

    private StorageProjectionPreparation PrepareStorageProjectionsOwned(string snapshotId, string? path,
        bool facets, bool duplicates, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        using var db = OpenStorageProjection();
        using var cancellation = new SourceSqliteCancellation(db, token);
        return cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction(); // IMMEDIATE: source and publication share one write transaction.
            var snapshot = ProjectionSnapshot(db, tx, snapshotId);
            if (snapshot.Status != "completed" || snapshot.FinishedAt is null)
                throw new InvalidOperationException("Only completed snapshots can have persistent storage projections.");
            string scope = ProjectionPath(snapshot, path);
            CreateStorageProjectionSchema(db, tx);
            bool buildFacets = facets && !ProjectionReady(db, tx, snapshotId, scope, "storage_facet_heads");
            bool buildDuplicates = duplicates && !ProjectionReady(db, tx, snapshotId, null, "storage_duplicate_heads");
            if (buildFacets) BuildStorageFacets(db, tx, snapshot, scope);
            if (buildDuplicates) BuildStorageDuplicates(db, tx, snapshot);
            long facetRows = 0, groups = 0, members = 0;
            using (var cmd = ProjectionCommand(db, tx, """
                SELECT extension_count+owner_count+age_count FROM storage_facet_heads
                WHERE snapshot_id=$s AND path=$p AND valid=1
                """, ("$s", snapshotId), ("$p", scope)))
                if (cmd.ExecuteScalar() is long n) facetRows = n;
            using (var cmd = ProjectionCommand(db, tx,
                "SELECT group_count,member_count FROM storage_duplicate_heads WHERE snapshot_id=$s AND valid=1", ("$s", snapshotId)))
            using (var reader = cmd.ExecuteReader())
                if (reader.Read()) { groups = reader.GetInt64(0); members = reader.GetInt64(1); }
            token.ThrowIfCancellationRequested();
            tx.Commit();
            return new StorageProjectionPreparation(snapshotId, scope, StorageProjectionModel,
                buildFacets, buildDuplicates, facetRows, groups, members, started, DateTimeOffset.UtcNow);
        });
    }

    private void EnsureStorageProjection(string snapshotId, string? path, bool facets, bool duplicates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsReadOnly) return;
        using (var db = OpenStorageProjection())
        using (var cancellation = new SourceSqliteCancellation(db, token))
        {
            bool ready = cancellation.Run(() =>
            {
                using var tx = db.BeginTransaction(deferred: true);
                var snapshot = ProjectionSnapshot(db, tx, snapshotId);
                if (snapshot.Status != "completed" || snapshot.FinishedAt is null) return true;
                return (!facets || ProjectionReady(db, tx, snapshotId, ProjectionPath(snapshot, path), "storage_facet_heads"))
                    && (!duplicates || ProjectionReady(db, tx, snapshotId, null, "storage_duplicate_heads"));
            });
            if (ready) return;
        }
        IDisposable ownership;
        try { ownership = AcquireWriter(); }
        catch (InvalidOperationException) { return; } // Only admission failure is optional, never a build/SQL failure.
        using (ownership)
        {
            // A legacy snapshot may have resumed between the read check and admission. Do not
            // publish it, and let the subsequent live read observe its current state.
            using (var db = Open())
            using (var tx = db.BeginTransaction(deferred: true))
                if (ProjectionSnapshot(db, tx, snapshotId).Status != "completed") return;
            PrepareStorageProjectionsOwned(snapshotId, path, facets, duplicates, token);
        }
    }

    private static void RegisterExtensionFunction(SqliteConnection db) =>
        db.CreateFunction<string, string>("filemapper_extension", name =>
        {
            string extension = Path.GetExtension(name).ToLowerInvariant();
            return extension.Length == 0 || (name.StartsWith('.') && name.LastIndexOf('.') == 0)
                ? "(no extension)" : extension;
        }, isDeterministic: true);

    private static void CreateFacetIdentities(SqliteConnection db, SqliteTransaction tx, Snapshot snapshot, string path)
    {
        using (var exists = ProjectionCommand(db, tx,
            "SELECT 1 FROM entries WHERE snapshot_id=$s AND path=$p", ("$s", snapshot.Id), ("$p", path)))
            if (exists.ExecuteScalar() is null) throw new KeyNotFoundException("Breakdown path was not observed in this snapshot.");
        RegisterExtensionFunction(db);
        using var materialize = ProjectionCommand(db, tx, """
            DROP TABLE IF EXISTS temp.storage_facet_identities;
            CREATE TEMP TABLE storage_facet_identities AS
            WITH RECURSIVE scope(path) AS (
              SELECT path FROM entries WHERE snapshot_id=$s AND path=$p
              UNION SELECT e.path FROM scope CROSS JOIN entries e INDEXED BY ix_entries_parent
                ON e.parent_path=scope.path AND e.snapshot_id=$s
            ), selected AS (
              SELECT e.node_id,e.kind,e.logical_bytes,e.allocated_bytes,e.owner,e.modified_at,
                CASE WHEN e.kind='Stream' THEN '(alternate streams)' WHEN instr(e.name,'.')=0 THEN '(no extension)'
                  ELSE filemapper_extension(e.name) END extension
              FROM scope CROSS JOIN entries e ON e.snapshot_id=$s AND e.path=scope.path
              WHERE e.kind IN ('File','Stream')
            )
            SELECT node_id,MIN(kind) kind,COUNT(*) names,MAX(logical_bytes) logical_bytes,
              CASE WHEN COUNT(*)=COUNT(allocated_bytes) AND MIN(allocated_bytes)=MAX(allocated_bytes) THEN MAX(allocated_bytes) END allocation,
              CASE WHEN COUNT(DISTINCT extension)=1 THEN MIN(extension) ELSE '(multiple extensions)' END extension_key,
              CASE WHEN COUNT(owner)=0 THEN '(unknown owner)'
                WHEN COUNT(owner)=COUNT(*) AND COUNT(DISTINCT owner)=1 THEN MIN(owner)
                ELSE '(mixed or unknown owners)' END owner_key,
              MAX(julianday(modified_at)) newest_modified,
              CASE WHEN MIN(logical_bytes)<>MAX(logical_bytes) OR COUNT(DISTINCT allocated_bytes)>1
                OR COUNT(DISTINCT owner)>1 OR (COUNT(owner)>0 AND COUNT(owner)<COUNT(*))
                OR COUNT(DISTINCT modified_at)>1 OR COUNT(DISTINCT kind)>1 THEN 1 ELSE 0 END conflicting
            FROM selected GROUP BY node_id;
            """, ("$s", snapshot.Id), ("$p", path));
        materialize.ExecuteNonQuery();
    }

    private static string FacetAggregation(string by) => """
        SELECT
        """ + " " + (by switch
        {
            "extension" => "extension_key",
            "owner" => "owner_key",
            _ => """
                CASE WHEN newest_modified IS NULL THEN 'unknown'
                  WHEN newest_modified>julianday($asof) THEN 'future'
                  WHEN julianday($asof)-newest_modified<7 THEN '0-7d'
                  WHEN julianday($asof)-newest_modified<30 THEN '7-30d'
                  WHEN julianday($asof)-newest_modified<90 THEN '30-90d'
                  WHEN julianday($asof)-newest_modified<365 THEN '90-365d'
                  ELSE '365d+' END
                """
        }) + " " + """
          facet_key,COUNT(*) identity_count,
          SUM(CASE WHEN kind='File' THEN 1 ELSE 0 END) files,
          SUM(CASE WHEN kind='Stream' THEN 1 ELSE 0 END) streams,SUM(names) names,SUM(logical_bytes) logical_bytes,
          CASE WHEN COUNT(*)=COUNT(allocation) THEN SUM(allocation) END allocated,
          COALESCE(SUM(allocation),0) known,COUNT(*)-COUNT(allocation) unknown,SUM(conflicting) conflicts
        FROM temp.storage_facet_identities GROUP BY facet_key
        """;

    private static void BuildStorageFacets(SqliteConnection db, SqliteTransaction tx, Snapshot snapshot, string path)
    {
        CreateFacetIdentities(db, tx, snapshot, path);
        using (var clear = ProjectionCommand(db, tx,
            "DELETE FROM storage_facets WHERE snapshot_id=$s AND path=$p", ("$s", snapshot.Id), ("$p", path))) clear.ExecuteNonQuery();
        long[] counts = new long[3];
        string[] dimensions = ["extension", "owner", "age"];
        for (int i = 0; i < dimensions.Length; i++)
        {
            using (var insert = ProjectionCommand(db, tx,
                "INSERT INTO storage_facets SELECT $s,$p,$by,f.* FROM (" + FacetAggregation(dimensions[i]) + ") f",
                ("$s", snapshot.Id), ("$p", path), ("$by", dimensions[i]), ("$asof", snapshot.StartedAt.ToString("O"))))
                insert.ExecuteNonQuery();
            using var count = ProjectionCommand(db, tx,
                "SELECT COUNT(*) FROM storage_facets WHERE snapshot_id=$s AND path=$p AND dimension=$by",
                ("$s", snapshot.Id), ("$p", path), ("$by", dimensions[i]));
            counts[i] = (long)count.ExecuteScalar()!;
        }
        using (var publish = ProjectionCommand(db, tx, """
            INSERT INTO storage_facet_heads
            SELECT $s,$p,$model,s.started_at,s.finished_at,1,$extensions,$owners,$ages,
              (SELECT COALESCE(SUM(conflicting),0) FROM temp.storage_facet_identities)
            FROM snapshots s WHERE s.id=$s AND s.status='completed' AND s.finished_at IS NOT NULL
            ON CONFLICT(snapshot_id,path) DO UPDATE SET model=excluded.model,started_at=excluded.started_at,
              finished_at=excluded.finished_at,valid=1,extension_count=excluded.extension_count,
              owner_count=excluded.owner_count,age_count=excluded.age_count,conflicts=excluded.conflicts;
            DROP TABLE temp.storage_facet_identities;
            """, ("$s", snapshot.Id), ("$p", path), ("$model", StorageProjectionModel),
            ("$extensions", counts[0]), ("$owners", counts[1]), ("$ages", counts[2]))) publish.ExecuteNonQuery();
    }

    private static void CreateDuplicateIdentities(SqliteConnection db, SqliteTransaction tx, string snapshotId)
    {
        using var materialize = ProjectionCommand(db, tx, """
            DROP TABLE IF EXISTS temp.storage_duplicate_identities;
            DROP TABLE IF EXISTS temp.storage_ads_identities;
            CREATE TEMP TABLE storage_ads_identities(node_id TEXT PRIMARY KEY) WITHOUT ROWID;
            INSERT OR IGNORE INTO storage_ads_identities
              SELECT f.node_id FROM entries ads JOIN entries f ON f.snapshot_id=$s AND f.path=ads.parent_path
              WHERE ads.snapshot_id=$s AND ads.kind='Stream';
            CREATE TEMP TABLE storage_duplicate_identities AS
              SELECT node_id,MAX(hash) hash,MAX(logical_bytes) logical_bytes,
                CASE WHEN COUNT(*)=COUNT(allocated_bytes) AND MIN(allocated_bytes)=MAX(allocated_bytes) THEN MAX(allocated_bytes) END allocation,
                COUNT(*) names,
                CASE WHEN MIN(link_count)>0 AND MIN(link_count)=MAX(link_count) AND COUNT(*)=MAX(link_count)
                  AND node_id NOT LIKE 'path-fallback:%' THEN 1 ELSE 0 END links_complete
              FROM entries INDEXED BY ix_entries_identity WHERE snapshot_id=$s AND kind='File' GROUP BY node_id
              HAVING COUNT(*)=COUNT(hash) AND MIN(hash)=MAX(hash) AND MIN(logical_bytes)=MAX(logical_bytes);
            CREATE INDEX ix_storage_temp_duplicate_hash ON storage_duplicate_identities(hash,logical_bytes,node_id);
            """, ("$s", snapshotId));
        materialize.ExecuteNonQuery();
    }

    private const string DuplicateGroupAggregation = """
        SELECT i.hash,i.logical_bytes,COUNT(*) identity_count,SUM(i.names) names,
          CASE WHEN COUNT(*)=COUNT(i.allocation) AND MIN(i.links_complete)=1 AND COUNT(ads.node_id)=0
            THEN SUM(i.allocation)-MAX(i.allocation) END reclaimable
        FROM temp.storage_duplicate_identities i LEFT JOIN temp.storage_ads_identities ads ON ads.node_id=i.node_id
        GROUP BY i.hash,i.logical_bytes HAVING COUNT(*)>1
        """;

    private static void BuildStorageDuplicates(SqliteConnection db, SqliteTransaction tx, Snapshot snapshot)
    {
        CreateDuplicateIdentities(db, tx, snapshot.Id);
        using (var clear = ProjectionCommand(db, tx,
            "DELETE FROM storage_duplicate_groups WHERE snapshot_id=$s", ("$s", snapshot.Id))) clear.ExecuteNonQuery();
        using (var groups = ProjectionCommand(db, tx, """
            INSERT INTO storage_duplicate_groups(snapshot_id,hash,logical_bytes,identity_count,names,reclaimable)
            SELECT $s,g.* FROM (
            """ + DuplicateGroupAggregation + ") g", ("$s", snapshot.Id))) groups.ExecuteNonQuery();
        using (var members = ProjectionCommand(db, tx, """
            INSERT INTO storage_duplicate_members
            SELECT g.id,e.path FROM storage_duplicate_groups g
              CROSS JOIN temp.storage_duplicate_identities i INDEXED BY ix_storage_temp_duplicate_hash
                ON i.hash=g.hash AND i.logical_bytes=g.logical_bytes
              CROSS JOIN entries e INDEXED BY ix_entries_identity ON e.snapshot_id=$s AND e.node_id=i.node_id
            WHERE g.snapshot_id=$s;
            UPDATE storage_duplicate_groups SET member_count=(
              SELECT COUNT(*) FROM storage_duplicate_members m WHERE m.group_id=storage_duplicate_groups.id)
              WHERE snapshot_id=$s;
            INSERT INTO storage_duplicate_heads
            SELECT $s,$model,s.started_at,s.finished_at,1,
              (SELECT COUNT(*) FROM storage_duplicate_groups WHERE snapshot_id=$s),
              (SELECT COALESCE(SUM(member_count),0) FROM storage_duplicate_groups WHERE snapshot_id=$s),
              (SELECT COUNT(DISTINCT node_id) FROM entries INDEXED BY ix_entries_identity WHERE snapshot_id=$s AND kind='File' AND hash IS NOT NULL)
            FROM snapshots s WHERE s.id=$s AND s.status='completed' AND s.finished_at IS NOT NULL
            ON CONFLICT(snapshot_id) DO UPDATE SET model=excluded.model,started_at=excluded.started_at,
              finished_at=excluded.finished_at,valid=1,group_count=excluded.group_count,member_count=excluded.member_count,
              hashed_files=excluded.hashed_files;
            DROP TABLE temp.storage_duplicate_identities;
            DROP TABLE temp.storage_ads_identities;
            """, ("$s", snapshot.Id), ("$model", StorageProjectionModel))) members.ExecuteNonQuery();
    }
}
