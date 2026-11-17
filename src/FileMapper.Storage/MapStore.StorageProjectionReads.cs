using FileMapper.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    /// <summary>Installs only the projection schema under writer/maintenance ownership.
    /// No snapshot is prepared here; cold preparation remains separately observable.</summary>
    public void InitializeProjections()
    {
        using var ownership = AcquireWriter();
        using var db = OpenStorageProjection();
        using var tx = db.BeginTransaction();
        CreateStorageProjectionSchema(db, tx);
        tx.Commit();
    }

    public StorageBreakdown GetStorageBreakdown(string snapshotId, string by = "extension", string? path = null,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, limit);
        by = by == "type" ? "extension" : by;
        if (by is not ("extension" or "owner" or "age"))
            throw new ArgumentException("Breakdown must be extension, type, owner, or age.");
        EnsureStorageProjection(snapshotId, path, true, false, cancellationToken);
        using var db = OpenStorageProjection();
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var snapshot = ProjectionSnapshot(db, tx, snapshotId);
            string scope = ProjectionPath(snapshot, path);
            bool persisted = ProjectionReady(db, tx, snapshotId, scope, "storage_facet_heads");
            long total, conflicts;
            string source;
            if (persisted)
            {
                string countColumn = by switch { "extension" => "extension_count", "owner" => "owner_count", _ => "age_count" };
                using var count = ProjectionCommand(db, tx,
                    $"SELECT {countColumn},conflicts FROM storage_facet_heads WHERE snapshot_id=$s AND path=$p",
                    ("$s", snapshotId), ("$p", scope));
                using var reader = count.ExecuteReader();
                reader.Read(); total = reader.GetInt64(0); conflicts = reader.GetInt64(1);
                source = "storage_facets WHERE snapshot_id=$s AND path=$p AND dimension=$by";
            }
            else
            {
                CreateFacetIdentities(db, tx, snapshot, scope);
                using (var materialize = ProjectionCommand(db, tx, """
                    DROP TABLE IF EXISTS temp.storage_live_facets;
                    CREATE TEMP TABLE storage_live_facets AS
                    """ + " " + FacetAggregation(by), ("$asof", snapshot.StartedAt.ToString("O")))) materialize.ExecuteNonQuery();
                using (var count = ProjectionCommand(db, tx, "SELECT COUNT(*),COALESCE(SUM(conflicts),0) FROM temp.storage_live_facets"))
                using (var reader = count.ExecuteReader())
                { reader.Read(); total = reader.GetInt64(0); conflicts = reader.GetInt64(1); }
                source = "temp.storage_live_facets";
            }
            var items = new List<StorageFacet>();
            using (var cmd = ProjectionCommand(db, tx, """
                SELECT facet_key,identity_count,files,streams,names,logical_bytes,allocated,known,unknown,conflicts FROM
                """ + " " + source + " ORDER BY logical_bytes DESC,facet_key LIMIT $l OFFSET $o",
                ("$s", snapshotId), ("$p", scope), ("$by", by), ("$l", limit), ("$o", offset)))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    string label = by != "age" ? key : key switch
                    {
                        "unknown" => "Unknown modification date", "future" => "Modified after snapshot start",
                        "0-7d" => "Less than 7 days", "7-30d" => "7 to less than 30 days",
                        "30-90d" => "30 to less than 90 days", "90-365d" => "90 to less than 365 days",
                        _ => "365 days or older"
                    };
                    items.Add(new(key, label, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                        reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                        snapshot.Completeness == "complete" && reader.GetInt64(9) == 0 ? "complete" : "incomplete"));
                }
            if (!persisted)
            {
                using var cleanup = ProjectionCommand(db, tx, "DROP TABLE temp.storage_live_facets; DROP TABLE temp.storage_facet_identities;");
                cleanup.ExecuteNonQuery();
            }
            string strategy = "Files and alternate-stream identities only; each physical identity contributes bytes once and pathEntries counts all observed aliases in this scope. " + (by switch
            {
                "extension" => "Aliases with the same case-normalized final extension share its bucket; identities with differing alias extensions use (multiple extensions). Dotfiles without a later dot use (no extension). Alternate streams have their own bucket.",
                "owner" => "All aliases must agree on a non-null owner to enter that owner bucket; missing owners use (unknown owner), and disagreement or partial owner metadata uses (mixed or unknown owners).",
                _ => "Each identity uses its newest observed modification timestamp, measured relative to snapshot start. Conflicting timestamps are marked incomplete. Future and unknown dates have separate buckets."
            });
            return new StorageBreakdown(snapshotId, snapshot.Completeness == "complete" && conflicts == 0 ? "complete" : "incomplete",
                scope, by, snapshot.StartedAt, strategy, new(items, total, offset, limit));
        });
    }

    public Page<DuplicateProjection> GetDuplicateGroups(string snapshotId, int offset = 0, int limit = 100,
        int memberLimit = 1000, CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, limit); ValidatePage(0, memberLimit);
        EnsureStorageProjection(snapshotId, null, false, true, cancellationToken);
        using var db = OpenStorageProjection();
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var snapshot = ProjectionSnapshot(db, tx, snapshotId);
            var options = ProjectionScanOptions(db, tx, snapshotId);
            bool persisted = ProjectionReady(db, tx, snapshotId, null, "storage_duplicate_heads");
            string source;
            long total;
            if (persisted)
            {
                source = "storage_duplicate_groups WHERE snapshot_id=$s";
                using var count = ProjectionCommand(db, tx,
                    "SELECT group_count FROM storage_duplicate_heads WHERE snapshot_id=$s", ("$s", snapshotId));
                total = (long)count.ExecuteScalar()!;
            }
            else
            {
                CreateDuplicateIdentities(db, tx, snapshotId);
                using (var create = ProjectionCommand(db, tx, """
                    DROP TABLE IF EXISTS temp.storage_live_duplicate_groups;
                    CREATE TEMP TABLE storage_live_duplicate_groups AS
                    """ + " " + DuplicateGroupAggregation)) create.ExecuteNonQuery();
                source = "temp.storage_live_duplicate_groups";
                using var count = ProjectionCommand(db, tx, "SELECT COUNT(*) FROM temp.storage_live_duplicate_groups");
                total = (long)count.ExecuteScalar()!;
            }
            var keys = new List<(long Id, string Hash, long Size, long Identities, long Names, long? Savings)>();
            using (var cmd = ProjectionCommand(db, tx,
                "SELECT " + (persisted ? "id" : "rowid") + ",hash,logical_bytes,identity_count,names,reclaimable FROM " + source +
                " ORDER BY logical_bytes DESC,hash LIMIT $l OFFSET $o", ("$s", snapshotId), ("$l", limit), ("$o", offset)))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) keys.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5)));
            var results = new List<DuplicateProjection>();
            bool scopeSafe = snapshot.Completeness == "complete" && options?.ReparsePolicy is "skip" or "reference";
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = ReadDuplicateMemberPage(db, tx, snapshotId, persisted, key.Id, key.Hash, key.Size, 0, memberLimit);
                long? reclaimable = scopeSafe ? key.Savings : null;
                results.Add(new(key.Hash, key.Size, reclaimable, items, "sha256-unnamed-stream-at-observation",
                    key.Identities, key.Names, items.Count < key.Names,
                    reclaimable.HasValue
                        ? "Estimate assumes every hardlink name of redundant identities is removed and the identity with largest allocation is retained; no action is authorized."
                        : "Savings unknown: incomplete scope, alternate streams, unknown allocation, inconsistent or external hardlinks, or reparse aliases."));
            }
            if (!persisted) CleanupLiveDuplicates(db, tx, true);
            return new Page<DuplicateProjection>(results, total, offset, limit);
        });
    }

    private static List<FileEntry> ReadDuplicateMemberPage(SqliteConnection db, SqliteTransaction tx, string snapshotId,
        bool persisted, long groupId, string hash, long size, int offset, int limit)
    {
        string selection = persisted ? """
            FROM storage_duplicate_members m CROSS JOIN entries e
              ON e.snapshot_id=$s AND e.path=m.path WHERE m.group_id=$g
            ORDER BY m.path
            """ : """
            FROM temp.storage_duplicate_identities i CROSS JOIN entries e INDEXED BY ix_entries_identity
              ON e.snapshot_id=$s AND e.node_id=i.node_id
            WHERE i.hash=$h AND i.logical_bytes=$z ORDER BY e.path
            """;
        using var cmd = ProjectionCommand(db, tx, "SELECT e.* " + selection + " LIMIT $l OFFSET $o",
            ("$s", snapshotId), ("$g", groupId), ("$h", hash), ("$z", size), ("$l", limit), ("$o", offset));
        var items = new List<FileEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add(ReadEntry(reader));
        return items;
    }

    private static void CleanupLiveDuplicates(SqliteConnection db, SqliteTransaction tx, bool groups)
    {
        using var cleanup = ProjectionCommand(db, tx, """
            DROP TABLE temp.storage_duplicate_identities; DROP TABLE temp.storage_ads_identities;
            """ + (groups ? "DROP TABLE temp.storage_live_duplicate_groups;" : ""));
        cleanup.ExecuteNonQuery();
    }

    public Page<FileEntry> GetDuplicateMembers(string snapshotId, string hash, long logicalBytes, int offset = 0,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, limit);
        if (logicalBytes < 0 || string.IsNullOrWhiteSpace(hash) || hash.Length > 256)
            throw new ArgumentException("A hash and nonnegative logical size are required.");
        EnsureStorageProjection(snapshotId, null, false, true, cancellationToken);
        using var db = OpenStorageProjection();
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            _ = ProjectionSnapshot(db, tx, snapshotId);
            bool persisted = ProjectionReady(db, tx, snapshotId, null, "storage_duplicate_heads");
            long total = 0, groupId = 0;
            if (persisted)
            {
                using var cmd = ProjectionCommand(db, tx, """
                    SELECT id,member_count FROM storage_duplicate_groups WHERE snapshot_id=$s AND hash=$h AND logical_bytes=$z
                    """, ("$s", snapshotId), ("$h", hash), ("$z", logicalBytes));
                using var reader = cmd.ExecuteReader();
                if (reader.Read()) { groupId = reader.GetInt64(0); total = reader.GetInt64(1); }
            }
            else
            {
                CreateDuplicateIdentities(db, tx, snapshotId);
                using var cmd = ProjectionCommand(db, tx, """
                    SELECT COUNT(*) FROM temp.storage_duplicate_identities i CROSS JOIN entries e INDEXED BY ix_entries_identity
                      ON e.snapshot_id=$s AND e.node_id=i.node_id WHERE i.hash=$h AND i.logical_bytes=$z
                      AND (SELECT COUNT(*) FROM temp.storage_duplicate_identities WHERE hash=$h AND logical_bytes=$z)>1
                    """, ("$s", snapshotId), ("$h", hash), ("$z", logicalBytes));
                total = (long)cmd.ExecuteScalar()!;
            }
            var items = total == 0 ? [] : ReadDuplicateMemberPage(db, tx, snapshotId, persisted, groupId, hash, logicalBytes, offset, limit);
            if (!persisted) CleanupLiveDuplicates(db, tx, false);
            return new Page<FileEntry>(items, total, offset, limit);
        });
    }

    public long CountHashedFiles(string snapshotId, CancellationToken cancellationToken = default)
    {
        using var db = OpenStorageProjection();
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            bool persisted = ProjectionReady(db, tx, snapshotId, null, "storage_duplicate_heads");
            using var cmd = ProjectionCommand(db, tx, persisted
                ? "SELECT hashed_files FROM storage_duplicate_heads WHERE snapshot_id=$s"
                : "SELECT COUNT(DISTINCT node_id) FROM entries INDEXED BY ix_entries_identity WHERE snapshot_id=$s AND kind='File' AND hash IS NOT NULL",
                ("$s", snapshotId));
            return (long)cmd.ExecuteScalar()!;
        });
    }

    public DirectoryMetrics GetDirectoryMetrics(string snapshotId, string path, CancellationToken cancellationToken = default)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        cancellationToken.ThrowIfCancellationRequested();
        using (var db = OpenStorageProjection())
        using (var cancellation = new SourceSqliteCancellation(db, cancellationToken))
        {
            var cached = cancellation.Run(() =>
            {
                using var tx = db.BeginTransaction(deferred: true);
                _ = ProjectionSnapshot(db, tx, snapshotId);
                return CachedDirectoryMetrics(db, tx, snapshotId, path);
            });
            if (cached is not null) return cached;
        }
        IDisposable? ownership = null;
        if (!IsReadOnly)
            try { ownership = AcquireWriter(); }
            catch (InvalidOperationException) { /* Live fallback during writer/maintenance ownership. */ }
        using (ownership)
        using (var db = OpenStorageProjection())
        using (var cancellation = new SourceSqliteCancellation(db, cancellationToken))
            return cancellation.Run(() =>
            {
                using var tx = db.BeginTransaction(deferred: ownership is null);
                var snapshot = ProjectionSnapshot(db, tx, snapshotId);
                bool publish = ownership is not null && snapshot.Status == "completed" && snapshot.FinishedAt.HasValue;
                if (publish)
                {
                    CreateStorageProjectionSchema(db, tx);
                    if (CachedDirectoryMetrics(db, tx, snapshotId, path) is { } cached) return cached;
                }
                var metrics = ComputeDirectoryMetrics(db, tx, snapshot, path);
                if (publish)
                {
                    using var insert = ProjectionCommand(db, tx, """
                        INSERT INTO storage_directory_metrics
                        SELECT $s,$p,$model,started_at,finished_at,1,$payload FROM snapshots WHERE id=$s
                        ON CONFLICT(snapshot_id,path) DO UPDATE SET model=excluded.model,started_at=excluded.started_at,
                          finished_at=excluded.finished_at,valid=1,payload=excluded.payload
                        """, ("$s", snapshotId), ("$p", path), ("$model", StorageProjectionModel),
                        ("$payload", JsonSerializer.Serialize(metrics, Json)));
                    insert.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();
                return metrics;
            });
    }

    private static DirectoryMetrics? CachedDirectoryMetrics(SqliteConnection db, SqliteTransaction tx, string id, string path)
    {
        if (!ProjectionReady(db, tx, id, path, "storage_directory_metrics")) return null;
        using var cmd = ProjectionCommand(db, tx,
            "SELECT payload FROM storage_directory_metrics WHERE snapshot_id=$s AND path=$p", ("$s", id), ("$p", path));
        return JsonSerializer.Deserialize<DirectoryMetrics>((string)cmd.ExecuteScalar()!, Json)!;
    }

    private static DirectoryMetrics ComputeDirectoryMetrics(SqliteConnection db, SqliteTransaction tx, Snapshot snapshot, string path)
    {
        using var cmd = ProjectionCommand(db, tx, """
            WITH RECURSIVE scope(path) AS (
              SELECT path FROM entries WHERE snapshot_id=$s AND path=$p
              UNION SELECT e.path FROM scope CROSS JOIN entries e INDEXED BY ix_entries_parent
                ON e.parent_path=scope.path AND e.snapshot_id=$s
            ), selected AS MATERIALIZED (
              SELECT e.node_id,e.kind,e.logical_bytes,e.allocated_bytes,e.link_count
              FROM scope CROSS JOIN entries e ON e.path=scope.path AND e.snapshot_id=$s
            ), identities AS (
              SELECT node_id,MAX(logical_bytes) logical_bytes,
                CASE WHEN COUNT(*)=COUNT(allocated_bytes) AND MIN(allocated_bytes)=MAX(allocated_bytes) THEN MAX(allocated_bytes) END allocated_bytes,
                COUNT(*) names,MAX(link_count) links,MIN(link_count) min_links,
                CASE WHEN MIN(logical_bytes)<>MAX(logical_bytes) OR COUNT(DISTINCT allocated_bytes)>1 THEN 1 ELSE 0 END conflicting
              FROM selected WHERE kind IN ('File','Stream') GROUP BY node_id
            )
            SELECT (SELECT COUNT(*) FROM selected),
              (SELECT COUNT(DISTINCT node_id) FROM selected WHERE kind='File'),
              (SELECT COUNT(DISTINCT node_id) FROM selected WHERE kind='Directory'),
              COALESCE(SUM(logical_bytes),0),
              CASE WHEN COUNT(*)=COUNT(allocated_bytes) THEN COALESCE(SUM(allocated_bytes),0) END,
              CASE WHEN COUNT(*)=COUNT(allocated_bytes)
                AND COALESCE(SUM(CASE WHEN node_id LIKE 'path-fallback:%' OR min_links<=0 OR min_links<>links OR names>links THEN 1 ELSE 0 END),0)=0
                THEN COALESCE(SUM(CASE WHEN names=links THEN allocated_bytes ELSE 0 END),0) END,
              COUNT(*)-COUNT(allocated_bytes),
              COALESCE(SUM(conflicting),0)
            FROM identities
            """, ("$s", snapshot.Id), ("$p", path));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) == 0) throw new KeyNotFoundException("Hierarchy path was not observed in this snapshot.");
        var policy = ProjectionScanOptions(db, tx, snapshot.Id);
        var limitations = new List<string>();
        bool ambiguousNames = policy is null || policy.ReparsePolicy is not ("skip" or "reference");
        if (ambiguousNames) limitations.Add("Exclusive allocation is unknown when reparse aliases may count the same hardlink name more than once, or scan policy is unavailable.");
        if (reader.GetInt64(6) > 0) limitations.Add("Some physical allocations are unknown; logical bytes are measured independently.");
        if (reader.GetInt64(7) > 0) limitations.Add("One physical identity has inconsistent size or allocation observations across locations.");
        if (reader.IsDBNull(5)) limitations.Add("Exclusive allocation is unknown for uncertain physical identities, allocations, or inconsistent hardlink counts.");
        if (snapshot.Completeness != "complete") limitations.Add("Snapshot is incomplete; totals cover observed entries only and exclusive allocation is unknown.");
        return new(path, reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            snapshot.Completeness != "complete" || reader.GetInt64(7) > 0 || ambiguousNames || reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(6),
            snapshot.Completeness == "complete" && reader.GetInt64(7) == 0 ? "complete" : "incomplete",
            "filesystem-scope-v2", limitations.ToArray());
    }
}
