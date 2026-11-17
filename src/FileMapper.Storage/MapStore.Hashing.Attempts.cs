using FileMapper.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FileMapper.Storage;

public sealed class HashingStorageBudgetExceededException(string message, string kind = "chunks") : IOException(message)
{
    public string Kind { get; } = kind;
}

public sealed partial class MapStore
{
    public string BeginHashingAttempt(string runId, string nodeId, string stage)
    {
        RequireWritable(); stage = HashingPendingStage(stage);
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction();
        var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
        if (run.StopRequest is not null) throw new OperationCanceledException("A durable hashing stop has been requested.");
        var work = RequireHashingWork(db, tx, runId, nodeId);
        if (work.Stage != stage || work.ActiveAttemptId is not null) throw new InvalidOperationException("The candidate stage is already owned or has advanced.");
        var id = Guid.NewGuid().ToString("N");
        HashingExecute(db, tx, "INSERT INTO hashing_attempts(id,run_id,node_id,stage,status,started_at) VALUES($id,$r,$n,$stage,'reading',$at)",
            ("$id", id), ("$r", runId), ("$n", nodeId), ("$stage", HashingReadStage(stage)), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        if (HashingExecute(db, tx, "UPDATE hashing_work SET active_attempt_id=$id WHERE run_id=$r AND node_id=$n AND stage=$stage AND active_attempt_id IS NULL",
            ("$id", id), ("$r", runId), ("$n", nodeId), ("$stage", stage)) != 1) throw new InvalidOperationException("Hashing candidate ownership changed.");
        tx.Commit(); return id;
    }

    public void ReserveHashingAttemptBytes(string runId, string nodeId, string attemptId, long maximumBytes)
    {
        RequireWritable(); if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction();
        var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
        var work = RequireHashingWork(db, tx, runId, nodeId); RequireHashingAttempt(work, attemptId);
        var upper = work.Stage == "sample-pending" ? Math.Min(work.LogicalBytes, checked(3L * run.Options.SampleWindowBytes)) : work.LogicalBytes;
        if (maximumBytes != upper) throw new ArgumentException("Read reservation must cover the complete admitted stage upper bound.");
        var prior = HashingLong(db, tx, "SELECT byte_reserved FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
        if (prior != 0) { if (prior == maximumBytes) return; throw new InvalidOperationException("An attempt reservation is immutable."); }
        var reserved = HashingLong(db, tx, "SELECT bytes_reserved FROM hashing_runs WHERE id=$r", ("$r", runId));
        if (maximumBytes > run.Options.MaxBytesRead - run.Coverage.BudgetChargedBytes - reserved)
            throw new HashingStorageBudgetExceededException("The cumulative content-read reservation budget was reached.", "bytes");
        HashingExecute(db, tx, "UPDATE hashing_attempts SET byte_reserved=$bytes WHERE id=$id AND status='reading'", ("$bytes", maximumBytes), ("$id", attemptId));
        HashingExecute(db, tx, "UPDATE hashing_runs SET bytes_reserved=bytes_reserved+$bytes WHERE id=$r", ("$bytes", maximumBytes), ("$r", runId)); tx.Commit();
    }

    public void AppendHashingChunks(string runId, string nodeId, string attemptId, IReadOnlyList<FileContentChunk> chunks, CancellationToken cancellationToken = default)
    {
        RequireWritable(); ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count is < 1 or > 128) throw new ArgumentException("Chunk checkpoint batches must contain 1–128 records.");
        using var db = Open(); RequireHashingSchema(db); using var cancel = new SourceSqliteCancellation(db, cancellationToken);
        cancel.Run(() =>
        {
            using var tx = db.BeginTransaction(); var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
            var work = RequireHashingWork(db, tx, runId, nodeId); RequireHashingAttempt(work, attemptId);
            if (work.Stage != "full-pending" || run.Options.Mode is not ("partial" or "all")) throw new InvalidOperationException("This attempt does not admit CDC records.");
            var count = HashingLong(db, tx, "SELECT chunk_count FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
            var length = HashingLong(db, tx, "SELECT chunk_bytes FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
            var reserved = HashingLong(db, tx, "SELECT chunk_reserved FROM hashing_runs WHERE id=$r", ("$r", runId));
            if (chunks.Count > run.Options.MaxChunksPerFile - count || chunks.Count > run.Options.MaxChunkRecords - reserved)
                throw new HashingStorageBudgetExceededException("The configured retained chunk-record budget was reached.");
            using var insert = HashingCommand(db, tx, "INSERT INTO hashing_chunks VALUES($id,$ordinal,$offset,$length,$sha)");
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested(); HashingDigest(chunk.Sha256);
                if (chunk.Ordinal != count || chunk.Offset != length || chunk.Length < 1 || chunk.Length > run.Options.ChunkMaximumBytes || chunk.Length > work.LogicalBytes - length)
                    throw new ArgumentException("Chunk records must be contiguous, ordered and within the admitted file and chunk bounds.");
                Set(insert, ("$id", attemptId), ("$ordinal", chunk.Ordinal), ("$offset", chunk.Offset), ("$length", chunk.Length), ("$sha", chunk.Sha256)); insert.ExecuteNonQuery();
                count++; length += chunk.Length;
            }
            HashingExecute(db, tx, "UPDATE hashing_attempts SET chunk_count=$count,chunk_bytes=$bytes WHERE id=$id AND status='reading'",
                ("$count", count), ("$bytes", length), ("$id", attemptId));
            HashingExecute(db, tx, "UPDATE hashing_runs SET chunk_reserved=chunk_reserved+$n WHERE id=$r", ("$n", chunks.Count), ("$r", runId)); tx.Commit();
        });
    }

    public void CompleteHashingAttempt(string runId, string nodeId, string attemptId, HashingEvidence evidence, string nextStage, string? error = null)
    {
        RequireWritable(); ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(evidence.Receipt);
        if (evidence.Id != attemptId || evidence.RunId != runId || evidence.NodeId != nodeId) throw new ArgumentException("Hashing receipt identity does not match its admitted attempt.");
        if (nextStage is not ("sampled" or "done" or "invalidated" or "error" or "limited" or "sample-pending" or "full-pending")) throw new ArgumentException("Unknown hashing continuation stage.");
        var payload = HashingJson(evidence); var receipt = evidence.Receipt;
        if (receipt.BytesRead < 0 || receipt.ChunkCount < 0 || receipt.ChunkBytes < 0 || receipt.FinishedAt < receipt.StartedAt)
            throw new ArgumentException("Hashing receipt counters or interval are invalid.");
        HashingText(receipt.PolicyHash, 256, "policyHash");
        using var db = Open(); RequireHashingSchema(db); using var tx = db.BeginTransaction();
        var run = RequireHashingRun(db, tx, runId); RequireHashingMutable(run);
        var work = RequireHashingWork(db, tx, runId, nodeId);
        if (work.ActiveAttemptId != attemptId)
        {
            using var duplicate = HashingCommand(db, tx, "SELECT payload FROM hashing_evidence WHERE id=$id", ("$id", attemptId));
            if (duplicate.ExecuteScalar() as string == payload) return;
            throw new InvalidOperationException("Attempt is no longer active; conflicting completion is rejected.");
        }
        var stage = HashingReadStage(work.Stage);
        var policy = stage == "sample" ? new FileSamplePolicy(run.Options.SampleWindowBytes).PolicyHash
            : run.Options.Mode is "partial" or "all" ? new FileChunkPolicy(run.Options.ChunkMinimumBytes, run.Options.ChunkAverageBytes, run.Options.ChunkMaximumBytes, run.Options.MaxChunksPerFile).PolicyHash
            : Identity.For("full-policy", "sha256-unnamed-stream/v1");
        var stageMatches = receipt.Stage == stage || stage == "full" && run.Options.Mode is "partial" or "all" && receipt.Stage == "full-and-chunks";
        if (!stageMatches || receipt.PolicyHash != policy || receipt.StartedAt < run.StartedAt)
            throw new ArgumentException("Receipt stage, policy or observation interval differs from the admitted run.");
        var complete = receipt.Status == "complete";
        if (complete != (nextStage is "sampled" or "done") || complete && nextStage != (stage == "sample" ? "sampled" : "done"))
            throw new ArgumentException("Only a successful complete receipt can advance a verified stage.");
        if (nextStage.EndsWith("-pending", StringComparison.Ordinal) && nextStage != work.Stage) throw new ArgumentException("Interrupted reads must retry their original stage.");
        var provisionalCount = HashingLong(db, tx, "SELECT chunk_count FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
        var provisionalBytes = HashingLong(db, tx, "SELECT chunk_bytes FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
        var reservedBytes = HashingLong(db, tx, "SELECT byte_reserved FROM hashing_attempts WHERE id=$id AND status='reading'", ("$id", attemptId));
        if (receipt.BytesRead > reservedBytes || evidence.CacheHit && reservedBytes != 0)
            throw new ArgumentException("Actual content reads must fit the durable pre-read reservation; cache hits require zero reserved/read bytes.");
        string? chunkEvidence = null;
        if (complete)
        {
            ValidateHashingSuccess(work, receipt);
            if (stage == "full" && work.SampleEvidenceId is not null)
            {
                var sample = RequireHashingEvidence(db, tx, work.SampleEvidenceId);
                if (sample.Receipt.After?.VersionKey != receipt.Before!.VersionKey) throw new ArgumentException("Full hashing cannot promote a different version from the retained sample.");
            }
            if (evidence.CacheHit)
            {
                if (!run.Options.ReuseCache || evidence.CacheSourceEvidenceId is null || provisionalCount != 0 || receipt.BytesRead != 0 ||
                    !HashingCacheEligible(receipt.Before!) || !HashingCacheEligible(receipt.After!)) throw new ArgumentException("Cache reuse lacks a current token-bound validation.");
                var source = RequireHashingEvidence(db, tx, evidence.CacheSourceEvidenceId);
                if (source.CacheHit || source.NodeId != nodeId || source.Receipt.Status != "complete" || source.Receipt.PolicyHash != receipt.PolicyHash ||
                    source.Receipt.After?.VersionKey != receipt.After!.VersionKey || source.Receipt.Digest != receipt.Digest ||
                    source.Receipt.ChunkCount != receipt.ChunkCount || source.Receipt.ChunkBytes != receipt.ChunkBytes || source.Receipt.ChunksComplete != receipt.ChunksComplete ||
                    source.Receipt.Before is null || !HashingCacheEligible(source.Receipt.Before) || !HashingCacheEligible(source.Receipt.After!))
                    throw new ArgumentException("Cache reuse must reference a matching direct original read, never another cache hit.");
                if (HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_chunks WHERE attempt_id=$id", ("$id", source.Id)) != source.Receipt.ChunkCount ||
                    HashingLong(db, tx, "SELECT COALESCE(SUM(length),0) FROM hashing_chunks WHERE attempt_id=$id", ("$id", source.Id)) != source.Receipt.ChunkBytes)
                    throw new InvalidOperationException("Original cache chunk evidence does not match its complete receipt.");
                var reserved = HashingLong(db, tx, "SELECT chunk_reserved FROM hashing_runs WHERE id=$r", ("$r", runId));
                if (receipt.ChunkCount > run.Options.MaxChunkRecords - reserved) throw new HashingStorageBudgetExceededException("Referenced cache chunks exceed this run's logical chunk budget.");
                HashingExecute(db, tx, "UPDATE hashing_runs SET chunk_reserved=chunk_reserved+$count WHERE id=$r", ("$count", receipt.ChunkCount), ("$r", runId));
                chunkEvidence = source.Id;
            }
            else
            {
                if (evidence.CacheSourceEvidenceId is not null || receipt.ChunkCount != provisionalCount || receipt.ChunkBytes != provisionalBytes)
                    throw new ArgumentException("Receipt chunk counters must match the provisionally retained read.");
                if (stage == "full" && receipt.BytesRead != work.LogicalBytes) throw new ArgumentException("A direct complete full hash must account for the complete unnamed stream.");
                chunkEvidence = attemptId;
            }
            if (stage == "sample" && (receipt.ChunkCount != 0 || receipt.ChunkBytes != 0)) throw new ArgumentException("Sample receipts cannot contain full-file chunks.");
            if (stage == "full" && run.Options.Mode is "partial" or "all" && receipt.ChunksComplete && receipt.ChunkBytes != work.LogicalBytes)
                throw new ArgumentException("Complete CDC coverage must conserve the full stream length.");
        }
        else
        {
            if (evidence.CacheHit || evidence.CacheSourceEvidenceId is not null) throw new ArgumentException("Failed cache validation cannot publish a cache hit.");
            HashingExecute(db, tx, "DELETE FROM hashing_chunks WHERE attempt_id=$id", ("$id", attemptId));
            HashingExecute(db, tx, "UPDATE hashing_runs SET chunk_reserved=chunk_reserved-$count WHERE id=$r", ("$count", provisionalCount), ("$r", runId));
        }
        var after = receipt.After;
        HashingExecute(db, tx, """
            INSERT INTO hashing_evidence VALUES($id,$r,$n,$stage,$policy,$status,$digest,$version,$hit,$source,$bytes,$count,$chunkBytes,
              $complete,$allocation,$links,$ads,$pathsComplete,$validated,$payload)
            """, ("$id", evidence.Id), ("$r", runId), ("$n", nodeId), ("$stage", stage), ("$policy", receipt.PolicyHash), ("$status", receipt.Status),
            ("$digest", receipt.Digest), ("$version", after?.VersionKey), ("$hit", evidence.CacheHit ? 1 : 0), ("$source", evidence.CacheSourceEvidenceId),
            ("$bytes", receipt.BytesRead), ("$count", complete ? receipt.ChunkCount : 0), ("$chunkBytes", complete ? receipt.ChunkBytes : 0), ("$complete", complete && receipt.ChunksComplete ? 1 : 0),
            ("$allocation", after?.AllocatedBytes), ("$links", after?.LinkCount), ("$ads", after?.HasAlternateStreams is { } ads ? ads ? 1 : 0 : null),
            ("$pathsComplete", after?.PathsComplete == true ? 1 : 0), ("$validated", after?.ValidatedPathCount), ("$payload", payload));
        HashingExecute(db, tx, "UPDATE hashing_attempts SET status=$status,finished_at=$at WHERE id=$id AND status='reading'", ("$status", complete ? "complete" : "discarded"), ("$at", receipt.FinishedAt.ToString("O")), ("$id", attemptId));
        HashingExecute(db, tx, "UPDATE hashing_runs SET bytes_reserved=bytes_reserved-$bytes WHERE id=$r", ("$bytes", reservedBytes), ("$r", runId));
        if (complete && !evidence.CacheHit && HashingCacheEligible(receipt.Before!) && HashingCacheEligible(receipt.After!))
        {
            using var existing = HashingCommand(db, tx, "SELECT e.digest FROM hashing_cache c JOIN hashing_evidence e ON e.id=c.evidence_id WHERE c.node_id=$n AND c.version_key=$v AND c.policy_hash=$p", ("$n", nodeId), ("$v", after!.VersionKey), ("$p", receipt.PolicyHash));
            if (existing.ExecuteScalar() is string prior && prior != receipt.Digest) throw new InvalidOperationException("A stable version cache key produced contradictory content digests.");
            HashingExecute(db, tx, "INSERT OR IGNORE INTO hashing_cache VALUES($n,$v,$p,$e)", ("$n", nodeId), ("$v", after!.VersionKey), ("$p", receipt.PolicyHash), ("$e", evidence.Id));
        }
        if (HashingExecute(db, tx, """
            UPDATE hashing_work SET stage=$next,active_attempt_id=NULL,error=$error,
              sample_evidence_id=CASE WHEN $sample=1 THEN $id ELSE sample_evidence_id END,
              full_evidence_id=CASE WHEN $full=1 THEN $id ELSE full_evidence_id END,
              chunk_evidence_id=CASE WHEN $full=1 THEN $chunks ELSE chunk_evidence_id END
            WHERE run_id=$r AND node_id=$n AND active_attempt_id=$id
            """, ("$next", nextStage), ("$error", error), ("$sample", complete && stage == "sample" ? 1 : 0), ("$full", complete && stage == "full" ? 1 : 0),
            ("$id", attemptId), ("$chunks", chunkEvidence), ("$r", runId), ("$n", nodeId)) != 1) throw new InvalidOperationException("Hashing attempt lost its candidate ownership.");
        var c = run.Coverage;
        SaveHashingRun(db, tx, run with { Coverage = c with
        {
            SampledIdentities = c.SampledIdentities + (complete && stage == "sample" && work.SampleEvidenceId is null ? 1 : 0),
            FullyHashedIdentities = c.FullyHashedIdentities + (complete && stage == "full" && work.FullEvidenceId is null ? 1 : 0),
            ChunkedIdentities = c.ChunkedIdentities + (complete && stage == "full" && run.Options.Mode is "partial" or "all" ? 1 : 0),
            CacheHits = c.CacheHits + (evidence.CacheHit ? 1 : 0), BytesRead = checked(c.BytesRead + receipt.BytesRead),
            BudgetChargedBytes = checked(c.BudgetChargedBytes + receipt.BytesRead),
            ChunkRecords = checked(c.ChunkRecords + (complete ? receipt.ChunkCount : 0)), ChunkBytes = checked(c.ChunkBytes + (complete ? receipt.ChunkBytes : 0)),
            InvalidatedIdentities = c.InvalidatedIdentities + (nextStage == "invalidated" ? 1 : 0), ErrorIdentities = c.ErrorIdentities + (nextStage == "error" ? 1 : 0),
            SkippedIdentities = c.SkippedIdentities + (nextStage == "limited" ? 1 : 0),
            ChunkBudgetReached = c.ChunkBudgetReached || receipt.Status == "chunk-limit" || complete && stage == "full" && run.Options.Mode is "partial" or "all" && !receipt.ChunksComplete
        } });
        tx.Commit();
    }

    private static void ValidateHashingSuccess(HashingWorkItem work, FileReadReceipt receipt)
    {
        HashingDigest(receipt.Digest);
        var before = receipt.Before ?? throw new ArgumentException("A successful read requires its before version.");
        var after = receipt.After ?? throw new ArgumentException("A successful read requires its after version.");
        HashingText(before.VersionKey, 512, "versionKey"); HashingText(after.VersionKey, 512, "versionKey");
        if (before.Identity != work.NodeId || after.Identity != work.NodeId || before.Length != work.LogicalBytes || after.Length != work.LogicalBytes ||
            before.ModifiedAt != work.ModifiedAt || after.ModifiedAt != work.ModifiedAt || before.VersionKey != after.VersionKey ||
            before.VolumeId != after.VolumeId || before.CreatedAt != after.CreatedAt || before.ChangedAt != after.ChangedAt ||
            before.JournalId != after.JournalId || before.FileUsn != after.FileUsn || before.LinkCount != after.LinkCount ||
            receipt.ChunkBytes > work.LogicalBytes || before.LinkCount < 1 || after.LinkCount < 1 || after.ValidatedPathCount < 0 ||
            before.AllocatedBytes < 0 || after.AllocatedBytes < 0)
            throw new ArgumentException("A successful receipt must bind one unchanged admitted native identity and version.");
    }

    private static bool HashingCacheEligible(FileContentVersion version) => version.CacheEligible &&
        !string.IsNullOrWhiteSpace(version.JournalId) && version.FileUsn.HasValue && !string.IsNullOrWhiteSpace(version.VersionKey);
    private static void RequireHashingAttempt(HashingWorkItem work, string id)
    { if (work.ActiveAttemptId != id) throw new InvalidOperationException("The attempt does not own this hashing candidate."); }
    private static HashingEvidence RequireHashingEvidence(SqliteConnection db, SqliteTransaction? tx, string id)
    { using var command = HashingCommand(db, tx, "SELECT payload FROM hashing_evidence WHERE id=$id", ("$id", id)); return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<HashingEvidence>(json, Json)! : throw new KeyNotFoundException("Hashing evidence not found."); }

    public HashingEvidence? GetHashingEvidence(string id)
    {
        HashingText(id, 256, "evidenceId"); using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return null;
        using var query = HashingCommand(db, null, "SELECT payload FROM hashing_evidence WHERE id=$id", ("$id", id)); return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<HashingEvidence>(json, Json) : null;
    }

    public HashingCacheEntry? FindHashingCache(string nodeId, string versionKey, string policyHash)
    {
        HashingText(nodeId, 512, "nodeId"); HashingText(versionKey, 512, "versionKey"); HashingText(policyHash, 256, "policyHash");
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return null;
        using var query = HashingCommand(db, null, "SELECT e.payload FROM hashing_cache c JOIN hashing_evidence e ON e.id=c.evidence_id WHERE c.node_id=$n AND c.version_key=$v AND c.policy_hash=$p AND e.status='complete' AND e.cache_hit=0",
            ("$n", nodeId), ("$v", versionKey), ("$p", policyHash));
        if (query.ExecuteScalar() is not string json) return null;
        var evidence = JsonSerializer.Deserialize<HashingEvidence>(json, Json)!;
        return evidence.Receipt.Before is { } before && evidence.Receipt.After is { } after && HashingCacheEligible(before) && HashingCacheEligible(after) ? new(evidence) : null;
    }
}
