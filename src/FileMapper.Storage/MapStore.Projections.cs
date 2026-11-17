using FileMapper.Domain;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    public bool ListingScopesMatch(string before, string after)
    {
        var a = GetScanOptions(before); var b = GetScanOptions(after);
        if (a is null || b is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a.Root), System.IO.Path.TrimEndingDirectorySeparator(b.Root), comparison)
            && a.ReparsePolicy == b.ReparsePolicy && (a.Excludes ?? []).Order(StringComparer.Ordinal).SequenceEqual((b.Excludes ?? []).Order(StringComparer.Ordinal));
    }

    public Page<DifferenceProjection> CompareSnapshots(string before, string after, int offset = 0, int limit = 1000)
    {
        ValidatePage(offset, limit);
        var a = GetSnapshot(before) ?? throw new KeyNotFoundException("Baseline snapshot not found.");
        var b = GetSnapshot(after) ?? throw new KeyNotFoundException("Current snapshot not found.");
        if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a.Root), System.IO.Path.TrimEndingDirectorySeparator(b.Root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Compare snapshots of the same root.");
        bool scope = ListingScopesMatch(before, after);
        string addition = scope && a.Completeness == "complete" ? "added" : "newly_observed";
        string removal = scope && b.Completeness == "complete" ? "removed" : "unobserved";
        // Move inference requires exactly one path per physical identity on both sides: hardlink additions are never renamed files.
        string sql = """
            WITH old AS NOT MATERIALIZED (SELECT * FROM diff_old),
            current AS NOT MATERIALIZED (SELECT * FROM diff_current),
            moves AS MATERIALIZED (
              SELECT o.path previous_path,c.path,c.node_id,c.logical_bytes-o.logical_bytes delta
              FROM old o JOIN current c ON o.node_id=c.node_id AND o.path<>c.path
              WHERE NOT EXISTS(SELECT 1 FROM current WHERE path=o.path) AND NOT EXISTS(SELECT 1 FROM old WHERE path=c.path)
                AND (SELECT COUNT(*) FROM old WHERE node_id=o.node_id)=1 AND (SELECT COUNT(*) FROM current WHERE node_id=c.node_id)=1
                AND o.node_id NOT LIKE 'path-fallback:%'
            ), changes(kind,path,node_id,delta,explanation,previous_path) AS (
              SELECT $moved,path,node_id,delta,'Unique stable identity changed observed location; complete matching coverage establishes a move.',previous_path FROM moves
              UNION ALL SELECT $added,c.path,c.node_id,c.logical_bytes,'Path newly observed; creation is proven only with a complete matching baseline.',NULL FROM current c
                WHERE NOT EXISTS(SELECT 1 FROM old WHERE path=c.path) AND NOT EXISTS(SELECT 1 FROM moves WHERE path=c.path)
              UNION ALL SELECT $removed,o.path,o.node_id,CASE WHEN $removed='removed' THEN -o.logical_bytes ELSE 0 END,'Path absent; deletion is proven only with complete matching current coverage.',NULL FROM old o
                WHERE NOT EXISTS(SELECT 1 FROM current WHERE path=o.path) AND NOT EXISTS(SELECT 1 FROM moves WHERE previous_path=o.path)
              UNION ALL SELECT 'replaced',c.path,c.node_id,c.logical_bytes-o.logical_bytes,'The identity at this path changed.',o.path FROM old o JOIN current c ON o.path=c.path WHERE o.node_id<>c.node_id
              UNION ALL SELECT CASE WHEN c.logical_bytes>o.logical_bytes THEN 'grown' ELSE 'shrunk' END,c.path,c.node_id,c.logical_bytes-o.logical_bytes,'Observed logical byte size changed.',NULL
                FROM old o JOIN current c ON o.path=c.path AND o.node_id=c.node_id WHERE o.logical_bytes<>c.logical_bytes
              UNION ALL SELECT 'changed',c.path,c.node_id,0,'Content hash, modification timestamp, attributes, or allocation changed.',NULL
                FROM old o JOIN current c ON o.path=c.path AND o.node_id=c.node_id
                WHERE o.logical_bytes=c.logical_bytes AND (o.modified_at<>c.modified_at OR o.attributes<>c.attributes OR o.allocated_bytes IS NOT c.allocated_bytes OR (o.hash IS NOT NULL AND c.hash IS NOT NULL AND o.hash<>c.hash))
              UNION ALL SELECT 'ownership_changed',c.path,c.node_id,0,'Observed owner identity changed.',NULL
                FROM old o JOIN current c ON o.path=c.path AND o.node_id=c.node_id WHERE o.owner IS NOT NULL AND c.owner IS NOT NULL AND o.owner<>c.owner
              UNION ALL SELECT 'permissions_changed',c.path,c.node_id,0,'Observed security descriptor changed.',NULL
                FROM old o JOIN current c ON o.path=c.path AND o.node_id=c.node_id WHERE o.security IS NOT NULL AND c.security IS NOT NULL AND o.security<>c.security
            )
            """;
        using var db = Open();
        using (var reset = Command(db, "DROP TABLE IF EXISTS temp.diff_old; DROP TABLE IF EXISTS temp.diff_current; DROP TABLE IF EXISTS temp.diff_changes;")) reset.ExecuteNonQuery();
        using (var input = Command(db, """
            CREATE TEMP TABLE diff_old AS SELECT e.*,json_extract(n.properties,'$.securityDescriptor') security
              FROM entries e LEFT JOIN nodes n ON n.snapshot_id=e.snapshot_id AND n.id=e.node_id WHERE e.snapshot_id=$a;
            CREATE TEMP TABLE diff_current AS SELECT e.*,json_extract(n.properties,'$.securityDescriptor') security
              FROM entries e LEFT JOIN nodes n ON n.snapshot_id=e.snapshot_id AND n.id=e.node_id WHERE e.snapshot_id=$b;
            CREATE INDEX ix_temp_diff_old_path ON diff_old(path);
            CREATE INDEX ix_temp_diff_old_identity ON diff_old(node_id);
            CREATE INDEX ix_temp_diff_current_path ON diff_current(path);
            CREATE INDEX ix_temp_diff_current_identity ON diff_current(node_id);
            """, ("$a", before), ("$b", after))) input.ExecuteNonQuery();
        var movement = scope && a.Completeness == "complete" && b.Completeness == "complete" ? "moved" : "relocated_observation";
        using (var project = Command(db, "CREATE TEMP TABLE diff_changes AS " + sql + "SELECT * FROM changes", ("$added", addition), ("$removed", removal), ("$moved", movement))) project.ExecuteNonQuery();
        using var count = Command(db, "SELECT COUNT(*) FROM diff_changes");
        long total = (long)count.ExecuteScalar()!;
        using var cmd = Command(db, "SELECT * FROM diff_changes ORDER BY path,kind,node_id LIMIT $l OFFSET $o", ("$l", limit), ("$o", offset));
        var changes = new List<DifferenceProjection>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) changes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        using (var cleanup = Command(db, "DROP TABLE temp.diff_old; DROP TABLE temp.diff_current; DROP TABLE temp.diff_changes;")) cleanup.ExecuteNonQuery();
        return new(changes, total, offset, limit);
    }
}
