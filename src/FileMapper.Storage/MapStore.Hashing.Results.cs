using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public void SealHashingRun(string runId, CancellationToken cancellationToken = default)
    {
        RequireWritable(); using var db = Open(); RequireHashingSchema(db); using var cancel = new SourceSqliteCancellation(db, cancellationToken);
        cancel.Run(() =>
        {
            using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId);
            if (run.ResultHash is not null) return; RequireHashingMutable(run);
            if (HashingLong(db, tx, "SELECT prepared FROM hashing_runs WHERE id=$r", ("$r", runId)) == 0 ||
                HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_work WHERE run_id=$r AND (active_attempt_id IS NOT NULL OR stage IN ('sample-pending','full-pending','sampled'))", ("$r", runId)) != 0)
                throw new InvalidOperationException("Only admitted work with closed attempts and stages can publish an immutable result.");
            var safe = HashingLong(db, tx, "SELECT scope_safe FROM hashing_runs WHERE id=$r", ("$r", runId)) != 0;
            SealHashingGroups(db, tx, run, safe, cancellationToken);
            var coverage = run.Options.Mode is "partial" or "all" ? SealHashingOverlaps(db, tx, run, cancellationToken) : run.Coverage;
            var limited = coverage.AdmissionTruncated || coverage.ByteBudgetReached || coverage.ChunkBudgetReached || coverage.OverlapPairsTruncated ||
                coverage.PairComparisonBudgetReached || coverage.SkippedHighFanoutChunks > 0;
            var incomplete = coverage.UnsafeExcludedIdentities > 0 || coverage.InvalidatedIdentities > 0 || coverage.ErrorIdentities > 0 || coverage.BytesReadAreLowerBound;
            var baseline = HashingBaseline(db, tx, runId);
            var completeness = limited ? "limited" : incomplete || baseline.Completeness != "complete" ? "incomplete" : "complete-for-policy";
            run = run with { Coverage = coverage, Completeness = completeness, Phase = "sealed" };
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AddHashingDigest(digest, HashingJson(new { Schema = "filemapper.hashing-result/1", run.Id, run.SnapshotId, run.RequestHash,
                run.ModelVersion, run.Options, run.StartedAt, run.Coverage, run.Completeness, Baseline = baseline }));
            foreach (var (name, sql) in new[]
            {
                ("work", "SELECT node_id,path,logical_bytes,modified_at,attributes,path_count,link_count,allocated_bytes,has_ads,metadata_consistent,admission_status,stage,sample_evidence_id,full_evidence_id,error,chunk_evidence_id FROM hashing_work WHERE run_id=$r ORDER BY node_id"),
                ("paths", "SELECT node_id,path FROM hashing_paths WHERE run_id=$r ORDER BY node_id,path"),
                ("evidence", "SELECT id,payload FROM hashing_evidence WHERE run_id=$r OR id IN (SELECT cache_source_id FROM hashing_evidence WHERE run_id=$r AND cache_source_id IS NOT NULL) ORDER BY id"),
                ("chunks", "SELECT w.node_id,c.ordinal,c.offset,c.length,c.sha256 FROM hashing_work w JOIN hashing_chunks c ON c.attempt_id=w.chunk_evidence_id WHERE w.run_id=$r ORDER BY w.node_id,c.ordinal"),
                ("groups", "SELECT id,mode,digest,logical_bytes,identity_count,path_count,reclaimable_bytes,confidence,explanation FROM hashing_groups WHERE run_id=$r ORDER BY id"),
                ("members", "SELECT group_id,node_id,evidence_id FROM hashing_members WHERE run_id=$r ORDER BY group_id,node_id"),
                ("overlaps", "SELECT left_id,right_id,shared_bytes,shared_occurrences,completeness FROM hashing_overlaps WHERE run_id=$r ORDER BY left_id,right_id")
            })
            {
                AddHashingDigest(digest, name); using var query = HashingCommand(db, tx, sql, ("$r", runId)); using var rows = query.ExecuteReader();
                while (rows.Read()) { cancellationToken.ThrowIfCancellationRequested(); for (var i = 0; i < rows.FieldCount; i++) AddHashingDigest(digest, rows.IsDBNull(i) ? null : Convert.ToString(rows.GetValue(i), CultureInfo.InvariantCulture)); }
            }
            // Linearize sealing against stop acceptance. A control writer only reads the main WAL;
            // it never waits for this main write transaction, and cannot slip in after this check.
            using var control = OpenHashingControl(writable: true); using var controlTx = control.BeginTransaction();
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadHashingControl(control, controlTx, run) is not null)
                throw new OperationCanceledException("A durable stop request prevents result publication.", cancellationToken);
            SaveHashingRun(db, tx, run with { ResultHash = Convert.ToHexStringLower(digest.GetHashAndReset()) }); tx.Commit(); controlTx.Commit();
        });
    }

    private static Snapshot HashingBaseline(SqliteConnection db, SqliteTransaction tx, string runId)
    { using var query = HashingCommand(db, tx, "SELECT baseline FROM hashing_runs WHERE id=$r", ("$r", runId)); return JsonSerializer.Deserialize<Snapshot>((string)query.ExecuteScalar()!, Json)!; }
    private static void AddHashingDigest(IncrementalHash hash, string? value)
    {
        if (value is null) { hash.AppendData("-1:"u8); return; }
        var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":")); hash.AppendData(bytes);
    }

    private static void SealHashingGroups(SqliteConnection db, SqliteTransaction tx, HashingRun run, bool scopeSafe, CancellationToken token)
    {
        // One row per physical identity. Paths never multiply content groups or allocation.
        foreach (var mode in new[] { "exact", "probable" })
        {
            if (mode == "exact" && run.Options.Mode == "probable" || mode == "probable" && run.Options.Mode == "partial") continue;
            var field = mode == "exact" ? "full_evidence_id" : "sample_evidence_id";
            var fullConstraint = mode == "probable" ? " AND (w.full_evidence_id IS NULL OR f.status='complete')" : "";
            var having = mode == "probable" ? " AND COUNT(DISTINCT f.digest)<=1 AND COUNT(f.digest)<COUNT(*)" : "";
            using var groups = HashingCommand(db, tx, $"""
                SELECT e.digest,w.logical_bytes,COUNT(*),SUM(w.path_count),
                  CASE WHEN $safe=1 AND $exact=1 AND COUNT(*)=COUNT(e.allocation) AND MIN(e.paths_complete)=1
                    AND MIN(CASE WHEN e.has_ads=0 AND w.has_ads=0 AND e.links=w.path_count AND e.validated_paths=w.path_count THEN 1 ELSE 0 END)=1
                    THEN SUM(e.allocation)-MAX(e.allocation) END
                FROM hashing_work w JOIN hashing_evidence e ON e.id=w.{field}
                LEFT JOIN hashing_evidence f ON f.id=w.full_evidence_id AND f.status='complete'
                WHERE w.run_id=$r AND e.status='complete' AND w.stage<>'invalidated'{fullConstraint}
                GROUP BY e.digest,w.logical_bytes HAVING COUNT(*)>1{having}
                ORDER BY e.digest,w.logical_bytes
                """, ("$r", run.Id), ("$safe", scopeSafe ? 1 : 0), ("$exact", mode == "exact" ? 1 : 0));
            using var rows = groups.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested(); var digest = rows.GetString(0); var length = rows.GetInt64(1);
                var id = Identity.For("hash-group", run.Id + "|" + mode + "|" + length.ToString(CultureInfo.InvariantCulture) + "|" + digest);
                long? reclaimable = rows.IsDBNull(4) ? null : rows.GetInt64(4);
                var confidence = mode == "exact" ? "full-sha256-unnamed-stream-at-recorded-observation" : "equal-size-and-sampled-sha256; full equality unverified";
                var explanation = mode == "probable" ? "Sample equality does not establish full equality; independently reclaimable bytes are unknown."
                    : reclaimable.HasValue ? "Conservative allocation estimate at validated observations: retain the largest allocation and remove every verified alias of each redundant identity; no action is authorized."
                    : "Savings unknown: baseline scope, allocation, alternate streams or complete current alias coverage is unavailable.";
                HashingExecute(db, tx, "INSERT INTO hashing_groups VALUES($id,$r,$mode,$digest,$length,$identities,$paths,$bytes,$confidence,$explanation)",
                    ("$id", id), ("$r", run.Id), ("$mode", mode), ("$digest", digest), ("$length", length), ("$identities", rows.GetInt64(2)),
                    ("$paths", rows.GetInt64(3)), ("$bytes", reclaimable), ("$confidence", confidence), ("$explanation", explanation));
                HashingExecute(db, tx, $"""
                    INSERT INTO hashing_members SELECT $r,$g,w.node_id,e.id FROM hashing_work w JOIN hashing_evidence e ON e.id=w.{field}
                    WHERE w.run_id=$r AND w.logical_bytes=$length AND e.digest=$digest AND e.status='complete' AND w.stage<>'invalidated'
                    """, ("$r", run.Id), ("$g", id), ("$length", length), ("$digest", digest));
            }
        }
    }

    private static HashingCoverage SealHashingOverlaps(SqliteConnection db, SqliteTransaction tx, HashingRun run, CancellationToken token)
    {
        HashingExecute(db, tx, "DROP TABLE IF EXISTS temp.hashing_chunk_counts; DROP TABLE IF EXISTS temp.hashing_chunk_keys; DROP TABLE IF EXISTS temp.hashing_pairs;");
        HashingExecute(db, tx, """
            CREATE TEMP TABLE hashing_chunk_counts AS
              SELECT w.node_id,c.sha256,c.length,COUNT(*) multiplicity
              FROM hashing_work w JOIN hashing_evidence e ON e.id=w.full_evidence_id AND e.status='complete'
              JOIN hashing_chunks c ON c.attempt_id=w.chunk_evidence_id WHERE w.run_id=$r
              GROUP BY w.node_id,c.sha256,c.length;
            CREATE UNIQUE INDEX ix_hcc_node ON hashing_chunk_counts(node_id,sha256,length);
            CREATE INDEX ix_hcc_key ON hashing_chunk_counts(sha256,length,node_id);
            CREATE TEMP TABLE hashing_chunk_keys AS SELECT sha256,length,COUNT(*) fanout FROM hashing_chunk_counts GROUP BY sha256,length;
            CREATE TEMP TABLE hashing_pairs(left_id TEXT NOT NULL,right_id TEXT NOT NULL,PRIMARY KEY(left_id,right_id)) WITHOUT ROWID;
            """, ("$r", run.Id));
        var highFanout = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_chunk_keys WHERE fanout>$max", ("$max", run.Options.MaxChunkFanout));
        long comparisons = 0, pairCount = 0; bool pairsLimited = false, comparisonsLimited = false, stop = false;
        using (var keys = HashingCommand(db, tx, "SELECT sha256,length FROM hashing_chunk_keys WHERE fanout BETWEEN 2 AND $max ORDER BY sha256,length", ("$max", run.Options.MaxChunkFanout)))
        using (var rows = keys.ExecuteReader())
        using (var insert = HashingCommand(db, tx, "INSERT OR IGNORE INTO hashing_pairs VALUES($left,$right)"))
        using (var exists = HashingCommand(db, tx, "SELECT COUNT(*) FROM hashing_pairs WHERE left_id=$left AND right_id=$right"))
        {
            while (!stop && rows.Read())
            {
                token.ThrowIfCancellationRequested(); var nodes = new List<string>();
                using (var members = HashingCommand(db, tx, "SELECT node_id FROM hashing_chunk_counts WHERE sha256=$sha AND length=$length ORDER BY node_id", ("$sha", rows.GetString(0)), ("$length", rows.GetInt64(1))))
                using (var items = members.ExecuteReader()) while (items.Read()) nodes.Add(items.GetString(0));
                for (var left = 0; !stop && left < nodes.Count; left++)
                for (var right = left + 1; right < nodes.Count; right++)
                {
                    token.ThrowIfCancellationRequested();
                    if (comparisons >= run.Options.MaxPairComparisons) { comparisonsLimited = true; stop = true; break; }
                    comparisons++;
                    if (pairCount >= run.Options.MaxOverlapPairs)
                    {
                        Set(exists, ("$left", nodes[left]), ("$right", nodes[right]));
                        if ((long)exists.ExecuteScalar()! == 0) { pairsLimited = true; stop = true; break; }
                    }
                    else { Set(insert, ("$left", nodes[left]), ("$right", nodes[right])); pairCount += insert.ExecuteNonQuery(); }
                }
            }
        }
        var discoveryLimited = highFanout > 0 || pairsLimited || comparisonsLimited;
        HashingExecute(db, tx, """
            INSERT INTO hashing_overlaps
            SELECT $r,p.left_id,p.right_id,SUM(MIN(a.multiplicity,b.multiplicity)*a.length),SUM(MIN(a.multiplicity,b.multiplicity)),
              CASE WHEN MIN(le.chunks_complete)=1 AND MIN(re.chunks_complete)=1 THEN 'complete-multiset-for-recorded-streams'
                ELSE 'partial-cdc-coverage; full-stream denominators' END || CASE WHEN $limited=1 THEN '; pair-discovery-limited' ELSE '' END
            FROM hashing_pairs p JOIN hashing_chunk_counts a ON a.node_id=p.left_id
            JOIN hashing_chunk_counts b ON b.node_id=p.right_id AND a.sha256=b.sha256 AND a.length=b.length
            JOIN hashing_work lw ON lw.run_id=$r AND lw.node_id=p.left_id JOIN hashing_work rw ON rw.run_id=$r AND rw.node_id=p.right_id
            JOIN hashing_evidence le ON le.id=lw.full_evidence_id JOIN hashing_evidence re ON re.id=rw.full_evidence_id
            GROUP BY p.left_id,p.right_id
            """, ("$r", run.Id), ("$limited", discoveryLimited ? 1 : 0));
        if (HashingLong(db, tx, """
            SELECT COUNT(*) FROM hashing_overlaps o JOIN hashing_work l ON l.run_id=o.run_id AND l.node_id=o.left_id
            JOIN hashing_work r ON r.run_id=o.run_id AND r.node_id=o.right_id
            WHERE o.run_id=$run AND (o.shared_bytes>l.logical_bytes OR o.shared_bytes>r.logical_bytes OR o.shared_bytes<0)
            """, ("$run", run.Id)) != 0) throw new InvalidOperationException("Chunk multiset intersection violated stream-byte conservation.");
        var actualPairs = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_overlaps WHERE run_id=$r", ("$r", run.Id));
        HashingExecute(db, tx, "DROP TABLE temp.hashing_chunk_counts; DROP TABLE temp.hashing_chunk_keys; DROP TABLE temp.hashing_pairs;");
        return run.Coverage with { OverlapPairs = actualPairs, PairComparisons = comparisons, SkippedHighFanoutChunks = highFanout,
            OverlapPairsTruncated = pairsLimited || comparisonsLimited, PairComparisonBudgetReached = comparisonsLimited };
    }
}
