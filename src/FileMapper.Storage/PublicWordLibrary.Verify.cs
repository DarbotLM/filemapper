using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public static partial class PublicWordLibrary
{
    /// <summary>Verify an already published edition against the exact capture, provider page and compact-index bytes.</summary>
    public static async Task<PublicWordLibraryReceipt> VerifyAsync(CompactSourceHarvestIndex source, string captureRoot,
        string editionPath, PublicRepositoryEvidence evidence, string evidenceJsonPath, PublicHeadEvidence headEvidence, string headReceiptPath, string headStdoutPath, CancellationToken cancellationToken = default)
    {
        await ValidateEvidenceAsync(evidence, evidenceJsonPath, cancellationToken);
        var capture = new GitSourceCapture(captureRoot, readOnly: true).Read(evidence.CaptureId).Manifest;
        await ValidateHeadAsync(capture, evidence, headEvidence, headReceiptPath, headStdoutPath, cancellationToken);
        if (capture.CanonicalUri != evidence.RepositoryUrl || capture.ProviderStableId != evidence.ProviderStableId) throw new InvalidDataException("Capture and public evidence differ.");
        await using var sourceFile = new FileStream(source.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceFile, cancellationToken));
        await using var editionFile = new FileStream(editionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(editionPath), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        JsonElement Meta(string key)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM metadata WHERE key=$k"; cmd.Parameters.AddWithValue("$k", key);
            var raw = cmd.ExecuteScalar() as string ?? throw new InvalidDataException("Edition metadata missing: " + key);
            using var doc = JsonDocument.Parse(raw); return doc.RootElement.Clone();
        }
        long Scalar(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
        using (var check = db.CreateCommand()) { check.CommandText = "PRAGMA quick_check"; if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException("Edition integrity check failed."); }
        if (Meta("schema").GetString() != "filemapper.public-word-library/4" || Meta("compactIndexSha256").GetString() != sourceHash ||
            CanonicalJson.Serialize(Meta("capture")) != CanonicalJson.Serialize(capture) || CanonicalJson.Serialize(Meta("evidence")) != CanonicalJson.Serialize(evidence) || CanonicalJson.Serialize(Meta("headEvidence")) != CanonicalJson.Serialize(headEvidence))
            throw new InvalidDataException("Edition input commitments differ from the exact supplied inputs.");
        var indexed = source.Counts(evidence.CaptureId, cancellationToken); var coverage = Meta("coverage");
        long documents = Scalar("SELECT COUNT(*) FROM documents"), words = Scalar("SELECT COUNT(*) FROM words");
        var admitted = Scalar("SELECT COUNT(*) FROM documents WHERE exclusion_reason IS NULL"); var excluded = documents - admitted;
        if (!indexed.ExtractionSealed || documents != capture.Coverage.TreeEntries || documents != indexed.AssociatedEntries || words != indexed.WordOccurrences ||
            coverage.GetProperty("enumerated").GetInt64() != documents || coverage.GetProperty("admitted").GetInt64() != admitted ||
            coverage.GetProperty("excluded").GetInt64() != excluded || coverage.GetProperty("words").GetInt64() != words)
            throw new InvalidDataException("Edition coverage differs from committed inputs.");
        var id = Meta("editionId").GetString() ?? throw new InvalidDataException("Missing edition ID.");
        var published = Meta("publishedAt").GetDateTimeOffset();
        return new(id, Path.GetFullPath(editionPath), documents, admitted, excluded, words, published);
    }
    private static async Task ValidateHeadAsync(SourceCaptureManifest capture, PublicRepositoryEvidence publicEvidence,
        PublicHeadEvidence headEvidence, string receiptPath, string stdoutPath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(headEvidence);
        if (!Hash(headEvidence.ReceiptSha256) || !Hash(headEvidence.StdoutSha256) || headEvidence.ObservedAt == default) throw new ArgumentException("HEAD evidence commitments are required.");
        var info = new FileInfo(receiptPath); if (!info.Exists || info.Length > 4 * 1024 * 1024) throw new InvalidDataException("HEAD receipt missing or too large.");
        var bytes = await File.ReadAllBytesAsync(receiptPath, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != headEvidence.ReceiptSha256) throw new InvalidDataException("HEAD receipt hash differs.");
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        if (root.GetProperty("schema").GetString() != "filemapper.public-word-library-heads/1" || root.GetProperty("authenticated").GetBoolean()) throw new InvalidDataException("Unsupported or authenticated HEAD observation.");
        var matches = root.GetProperty("repositories").EnumerateArray().Where(x => x.GetProperty("repositoryId").ToString() == publicEvidence.ProviderStableId).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("HEAD receipt must contain exactly one matching repository.");
        var selected = matches[0]; var commit = selected.GetProperty("commit").GetString(); var reference = selected.GetProperty("ref").GetString();
        if (selected.GetProperty("state").GetString() != "observed" || selected.GetProperty("exitCode").GetInt32() != 0 || selected.GetProperty("url").GetString() != publicEvidence.RepositoryUrl ||
            commit != capture.Commit || selected.GetProperty("finishedAt").GetDateTimeOffset() != headEvidence.ObservedAt ||
            !string.Equals(Path.GetFileName(stdoutPath), selected.GetProperty("stdout").GetString(), StringComparison.Ordinal) || reference is null || !reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new InvalidDataException("HEAD observation does not bind this public capture and revision.");
        var outputInfo = new FileInfo(stdoutPath); if (!outputInfo.Exists || outputInfo.Length > 65536) throw new InvalidDataException("HEAD output missing or too large.");
        var output = await File.ReadAllBytesAsync(stdoutPath, token);
        if (Convert.ToHexStringLower(SHA256.HashData(output)) != headEvidence.StdoutSha256) throw new InvalidDataException("HEAD stdout hash differs.");
        var lines = System.Text.Encoding.UTF8.GetString(output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2 || lines[0] != "ref: " + reference + "\tHEAD" || lines[1] != commit + "\tHEAD") throw new InvalidDataException("HEAD output does not match receipt.");
    }
}
