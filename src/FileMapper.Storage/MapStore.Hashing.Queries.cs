using System.Text.Json;
using FileMapper.Domain;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public Page<HashingGroup> GetHashingGroups(string runId, string? mode = null, int offset = 0, int limit = 100)
    {
        HashingText(runId, 256, "runId"); HashingPage(offset, limit);
        if (mode is not (null or "all" or "exact" or "probable")) throw new ArgumentException("Group mode must be exact, probable or all.");
        if (mode == "all") mode = null;
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true);
        const string where = "g.run_id=$r AND ($mode IS NULL OR g.mode=$mode) AND EXISTS(SELECT 1 FROM hashing_runs WHERE id=$r AND result_hash IS NOT NULL)";
        var values = new (string, object?)[] { ("$r", runId), ("$mode", mode) };
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_groups g WHERE " + where, values);
        using var query = HashingCommand(db, tx, "SELECT g.* FROM hashing_groups g WHERE " + where + " ORDER BY g.logical_bytes DESC,g.id LIMIT $l OFFSET $o", [..values, ("$l", limit), ("$o", offset)]);
        using var rows = query.ExecuteReader(); var items = new List<HashingGroup>();
        while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt64(4), rows.GetInt64(5), rows.GetInt64(6), rows.IsDBNull(7) ? null : rows.GetInt64(7), rows.GetString(8), rows.GetString(9)));
        return new(items, total, offset, limit);
    }

    public Page<HashingMember> GetHashingMembers(string runId, string groupId, int offset = 0, int limit = 100)
    {
        HashingText(runId, 256, "runId"); HashingText(groupId, 256, "groupId"); HashingPage(offset, limit);
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true);
        const string where = "m.run_id=$r AND m.group_id=$g AND EXISTS(SELECT 1 FROM hashing_runs WHERE id=$r AND result_hash IS NOT NULL)";
        var values = new (string, object?)[] { ("$r", runId), ("$g", groupId) };
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_members m WHERE " + where, values);
        using var query = HashingCommand(db, tx, "SELECT w.node_id,w.path,w.logical_bytes,w.path_count,e.links,e.allocation,e.id,e.cache_hit FROM hashing_members m JOIN hashing_work w ON w.run_id=m.run_id AND w.node_id=m.node_id JOIN hashing_evidence e ON e.id=m.evidence_id WHERE " + where + " ORDER BY w.node_id LIMIT $l OFFSET $o", [..values, ("$l", limit), ("$o", offset)]);
        using var rows = query.ExecuteReader(); var items = new List<HashingMember>();
        while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3), rows.GetInt64(4), rows.IsDBNull(5) ? null : rows.GetInt64(5), rows.GetString(6), rows.GetInt64(7) != 0));
        return new(items, total, offset, limit);
    }

    public Page<HashingOverlap> GetHashingOverlaps(string runId, string? nodeId = null, int offset = 0, int limit = 100)
    {
        HashingText(runId, 256, "runId"); if (nodeId is not null) HashingText(nodeId, 512, "nodeId"); HashingPage(offset, limit);
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true);
        const string where = "o.run_id=$r AND ($n IS NULL OR o.left_id=$n OR o.right_id=$n) AND EXISTS(SELECT 1 FROM hashing_runs WHERE id=$r AND result_hash IS NOT NULL)";
        var values = new (string, object?)[] { ("$r", runId), ("$n", nodeId) };
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_overlaps o WHERE " + where, values);
        using var query = HashingCommand(db, tx, "SELECT o.left_id,o.right_id,l.path,r.path,l.logical_bytes,r.logical_bytes,o.shared_bytes,o.shared_occurrences,o.completeness FROM hashing_overlaps o JOIN hashing_work l ON l.run_id=o.run_id AND l.node_id=o.left_id JOIN hashing_work r ON r.run_id=o.run_id AND r.node_id=o.right_id WHERE " + where + " ORDER BY o.left_id,o.right_id LIMIT $l OFFSET $offset", [..values, ("$l", limit), ("$offset", offset)]);
        using var rows = query.ExecuteReader(); var items = new List<HashingOverlap>();
        while (rows.Read()) { var left = rows.GetInt64(4); var right = rows.GetInt64(5); var shared = rows.GetInt64(6); items.Add(new(runId, rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), left, right, shared, rows.GetInt64(7), left == 0 ? 0 : (double)shared / left, right == 0 ? 0 : (double)shared / right, rows.GetString(8))); }
        return new(items, total, offset, limit);
    }

    public Page<HashingEvidence> GetHashingEvidencePage(string runId, string? nodeId = null, int offset = 0, int limit = 100)
    {
        HashingText(runId, 256, "runId"); if (nodeId is not null) HashingText(nodeId, 512, "nodeId"); HashingPage(offset, limit);
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true); var values = new (string, object?)[] { ("$r", runId), ("$n", nodeId) };
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_evidence WHERE run_id=$r AND ($n IS NULL OR node_id=$n)", values);
        using var query = HashingCommand(db, tx, "SELECT payload FROM hashing_evidence WHERE run_id=$r AND ($n IS NULL OR node_id=$n) ORDER BY node_id,id LIMIT $l OFFSET $o", [..values, ("$l", limit), ("$o", offset)]);
        using var rows = query.ExecuteReader(); var items = new List<HashingEvidence>(); while (rows.Read()) items.Add(JsonSerializer.Deserialize<HashingEvidence>(rows.GetString(0), Json)!);
        return new(items, total, offset, limit);
    }

    public Page<FileContentChunk> GetHashingChunks(string evidenceId, int offset = 0, int limit = 100)
    {
        HashingText(evidenceId, 256, "evidenceId"); HashingPage(offset, limit, 1024);
        using var db = OpenHashingRead(); if (!HasHashingSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true); string? source;
        using (var resolve = HashingCommand(db, tx, "SELECT CASE WHEN cache_hit=1 THEN cache_source_id ELSE id END FROM hashing_evidence WHERE id=$id AND status='complete'", ("$id", evidenceId))) source = resolve.ExecuteScalar() as string;
        if (source is null) return new([], 0, offset, limit);
        var total = HashingLong(db, tx, "SELECT COUNT(*) FROM hashing_chunks c JOIN hashing_attempts a ON a.id=c.attempt_id AND a.status='complete' WHERE c.attempt_id=$id", ("$id", source));
        using var query = HashingCommand(db, tx, "SELECT ordinal,offset,length,sha256 FROM hashing_chunks c JOIN hashing_attempts a ON a.id=c.attempt_id AND a.status='complete' WHERE c.attempt_id=$id ORDER BY ordinal LIMIT $l OFFSET $o", ("$id", source), ("$l", limit), ("$o", offset));
        using var rows = query.ExecuteReader(); var items = new List<FileContentChunk>(); while (rows.Read()) items.Add(new(rows.GetInt32(0), rows.GetInt64(1), rows.GetInt32(2), rows.GetString(3)));
        return new(items, total, offset, limit);
    }
}
