using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    // Only an explicit save creates this optional schema. Reading a pre-arena database remains read-only.
    public SemanticArtifact SaveSemanticArtifact(string hash, string kind, string? parentHash, string payload, string summary, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireWritable(); ArtifactHash(hash); if (parentHash is not null) ArtifactHash(parentHash);
        if (kind is not ("spatial-arena/1" or "spatial-replay/1" or "edition-practice-arena/1" or "edition-practice-episode/1")) throw new ArgumentException("Unknown semantic artifact kind.");
        if (payload is null || Encoding.UTF8.GetByteCount(payload) > 16 * 1024 * 1024 ||
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) != hash)
            throw new ArgumentException("Artifact exceeds 16 MiB or differs from its SHA256 commitment.");
        if (summary is null || Encoding.UTF8.GetByteCount(summary) > 8192) throw new ArgumentException("Artifact summary exceeds 8 KiB.");
        using var parsedPayload = JsonDocument.Parse(payload); using var parsedSummary = JsonDocument.Parse(summary);
        using var ownership = AcquireWriter(); using var db = Open(); using var tx = db.BeginTransaction();
        using (var schema = Command(db, """
            CREATE TABLE IF NOT EXISTS semantic_artifacts(
              hash TEXT PRIMARY KEY, kind TEXT NOT NULL, parent_hash TEXT, payload TEXT NOT NULL,
              summary TEXT NOT NULL, stored_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_semantic_artifacts_kind_time ON semantic_artifacts(kind,stored_at,hash);
            """)) { schema.Transaction = tx; schema.ExecuteNonQuery(); }
        if (parentHash is not null)
        {
            using var parent = Command(db, "SELECT kind FROM semantic_artifacts WHERE hash=$h", ("$h", parentHash)); parent.Transaction = tx;
            if (parent.ExecuteScalar() is not string parentKind || !((kind == "spatial-replay/1" && parentKind == "spatial-arena/1") || (kind == "edition-practice-episode/1" && parentKind == "edition-practice-arena/1")))
                throw new ArgumentException("A replay must reference a locally saved arena.");
        }
        else if (kind is "spatial-replay/1" or "edition-practice-episode/1") throw new ArgumentException("Replay parent arena is required.");
        cancellationToken.ThrowIfCancellationRequested();
        var storedAt = DateTimeOffset.UtcNow;
        using (var insert = Command(db, "INSERT OR IGNORE INTO semantic_artifacts VALUES($h,$k,$p,$v,$s,$t)",
                   ("$h", hash), ("$k", kind), ("$p", parentHash), ("$v", payload), ("$s", summary), ("$t", storedAt.ToString("O"))))
        { insert.Transaction = tx; insert.ExecuteNonQuery(); }
        using var query = Command(db, "SELECT hash,kind,parent_hash,payload,summary,stored_at FROM semantic_artifacts WHERE hash=$h", ("$h", hash)); query.Transaction = tx;
        SemanticArtifact result;
        using (var row = query.ExecuteReader()) { row.Read(); result = ReadSemanticArtifact(row); }
        if (result.Kind != kind || result.ParentHash != parentHash || result.Payload != payload || result.Summary != summary)
            throw new InvalidOperationException("An artifact hash already exists with different content or metadata.");
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit(); return result;
    }
    public SemanticArtifact? GetSemanticArtifact(string hash)
    {
        ArtifactHash(hash); using var db = OpenArtifactRead();
        if (!HasArtifactSchema(db)) return null;
        using var query = Command(db, "SELECT hash,kind,parent_hash,payload,summary,stored_at FROM semantic_artifacts WHERE hash=$h", ("$h", hash));
        using var row = query.ExecuteReader(); if (!row.Read()) return null;
        var result = ReadSemanticArtifact(row);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Payload))) != result.Hash)
            throw new InvalidOperationException("Stored semantic artifact failed its payload commitment.");
        return result;
    }
    public Page<SemanticArtifactSummary> ListSemanticArtifacts(string? kind = null, int offset = 0, int limit = 50)
    {
        if (kind is not (null or "spatial-arena/1" or "spatial-replay/1" or "edition-practice-arena/1" or "edition-practice-episode/1")) throw new ArgumentException("Unknown artifact kind.");
        ValidatePage(offset, limit); if (limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = OpenArtifactRead(); if (!HasArtifactSchema(db)) return new([], 0, offset, limit);
        using var tx = db.BeginTransaction(deferred: true);
        using var count = Command(db, "SELECT COUNT(*) FROM semantic_artifacts WHERE ($k IS NULL OR kind=$k)", ("$k", kind)); count.Transaction = tx;
        var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT hash,kind,parent_hash,summary,stored_at FROM semantic_artifacts WHERE ($k IS NULL OR kind=$k) ORDER BY stored_at DESC,hash LIMIT $l OFFSET $o",
            ("$k", kind), ("$l", limit), ("$o", offset)); query.Transaction = tx;
        using var rows = query.ExecuteReader(); var result = new List<SemanticArtifactSummary>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetString(3), Date(rows.GetString(4))));
        return new(result, total, offset, limit);
    }
    private SqliteConnection OpenArtifactRead()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private static bool HasArtifactSchema(SqliteConnection db)
    { using var query = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='semantic_artifacts'"); return (long)query.ExecuteScalar()! == 1; }
    private static SemanticArtifact ReadSemanticArtifact(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2), row.GetString(3), row.GetString(4), Date(row.GetString(5)));
    private static void ArtifactHash(string? value)
    { if (value is null || value.Length != 64 || value.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new ArgumentException("Expected a lowercase SHA256 artifact hash."); }
}
