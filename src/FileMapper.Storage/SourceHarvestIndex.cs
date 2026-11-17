using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed record SourceIndexPage<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit);
public sealed record SourceDocumentAssociation(CapturedSourceEntry Entry, SourceLexiconResult? Lexicon);
public sealed record SourceIndexedCapture(string CaptureId, string RepositoryId, string DisplayName, string Commit,
    string State, string? ExtractionPolicyHash, string? ExtractionHash, SourceCaptureCoverage Coverage,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string CanonicalUri);
public sealed record SourceTermCount(string TermId, string Term, long Occurrences, long Documents, long PartialDocuments);
public sealed record SourceIndexedOccurrence(string DocumentId, string RepositoryPath, string RepositoryPathBytes,
    string SourceSha256, SourceLexicalOccurrence Unit, string LexicalStatus, bool LexicallyComplete);
public sealed record SourceIndexCounts(string CaptureId, long TreeEntries, long CapturedDocuments, long AssociatedEntries,
    long ProcessedDocuments, long UnprocessedDocuments, long UniqueProcessedBlobs, long Occurrences, long WordOccurrences,
    long DistinctTerms, bool ExtractionSealed, long LexicallyCompleteDocuments, long IncompleteLexicalDocuments,
    long LexicalErrorDocuments, long UnavailableEntries, long PolicyExcludedEntries);

/// <summary>Separate additive source index. It never opens the map database or updates model/learner state.</summary>
public sealed class SourceHarvestIndex : ISourceHarvestReader
{
    private const int ApplicationId = 0x53484958;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string DatabasePath { get; }
    public bool IsReadOnly { get; }
    public SourceHarvestIndex(string databasePath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath); IsReadOnly = readOnly;
        CheckPath(DatabasePath);
        if (readOnly && !File.Exists(DatabasePath)) throw new FileNotFoundException("Read-only source index must exist.", DatabasePath);
    }
    private void Writable() { if (IsReadOnly) throw new InvalidOperationException("The source index is read-only."); }
    private SqliteConnection Open(bool initialize = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckPath(DatabasePath);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false,
            Mode = IsReadOnly ? SqliteOpenMode.ReadOnly : initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            DefaultTimeout = 30 }.ToString());
        db.Open();
        try
        {
            using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
            return cancellation.Run(() =>
            {
                using var configuration = Command(db, IsReadOnly ? "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;" : "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;");
                configuration.ExecuteNonQuery();
                if (!initialize)
                {
                    using var identity = Command(db, "PRAGMA application_id;");
                    if (Convert.ToInt32(identity.ExecuteScalar(), CultureInfo.InvariantCulture) != ApplicationId)
                        throw new InvalidDataException("This file is not a FileMapper source-harvest index. Initialize a separate database explicitly.");
                }
                return db;
            });
        }
        catch { db.Dispose(); throw; }
    }
    public void Initialize()
    {
        Writable(); Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); CheckPath(DatabasePath);
        using var db = Open(initialize: true);
        using (var identity = Command(db, "PRAGMA application_id;"))
        {
            var id = Convert.ToInt32(identity.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (id != 0 && id != ApplicationId) throw new InvalidDataException("Refusing to initialize an unrelated database.");
            if (id == 0)
            {
                using var existing = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
                if ((long)existing.ExecuteScalar()! != 0) throw new InvalidDataException("Use a separate empty source-harvest database; existing map or dataset tables will not be changed.");
            }
        }
        using (var wal = Command(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;")) wal.ExecuteNonQuery();
        using var tx = db.BeginTransaction();
        using var schema = Command(db, """
            PRAGMA application_id=1397246296;
            CREATE TABLE IF NOT EXISTS sh_schema(version INTEGER PRIMARY KEY CHECK(version=1));
            INSERT OR IGNORE INTO sh_schema VALUES(1);
            CREATE TABLE IF NOT EXISTS sh_captures(
              capture_id TEXT PRIMARY KEY,manifest TEXT NOT NULL,manifest_path TEXT NOT NULL,
              repository_id TEXT NOT NULL,commit_id TEXT NOT NULL,policy_hash TEXT,extraction_hash TEXT);
            CREATE TABLE IF NOT EXISTS sh_lexicons(
              source_sha256 TEXT NOT NULL,policy_hash TEXT NOT NULL,commitment TEXT NOT NULL,result TEXT NOT NULL,
              source_bytes INTEGER NOT NULL,status TEXT NOT NULL,complete INTEGER NOT NULL,
              occurrences INTEGER NOT NULL,words INTEGER NOT NULL,PRIMARY KEY(source_sha256,policy_hash));
            CREATE TABLE IF NOT EXISTS sh_units(
              source_sha256 TEXT NOT NULL,policy_hash TEXT NOT NULL,ordinal INTEGER NOT NULL,
              occurrence_id TEXT NOT NULL,kind TEXT NOT NULL,term_id TEXT,byte_start INTEGER NOT NULL,
              byte_length INTEGER NOT NULL,utf16_start INTEGER NOT NULL,utf16_length INTEGER NOT NULL,payload TEXT NOT NULL,
              PRIMARY KEY(source_sha256,policy_hash,ordinal),UNIQUE(occurrence_id),
              FOREIGN KEY(source_sha256,policy_hash) REFERENCES sh_lexicons(source_sha256,policy_hash));
            CREATE INDEX IF NOT EXISTS sh_units_term ON sh_units(source_sha256,policy_hash,term_id,ordinal);
            CREATE TABLE IF NOT EXISTS sh_terms(term_id TEXT PRIMARY KEY,term TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sh_blob_terms(
              source_sha256 TEXT NOT NULL,policy_hash TEXT NOT NULL,term_id TEXT NOT NULL REFERENCES sh_terms(term_id),
              occurrences INTEGER NOT NULL,PRIMARY KEY(source_sha256,policy_hash,term_id),
              FOREIGN KEY(source_sha256,policy_hash) REFERENCES sh_lexicons(source_sha256,policy_hash));
            CREATE TABLE IF NOT EXISTS sh_documents(
              document_id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,commit_id TEXT NOT NULL,
              path TEXT NOT NULL,path_bytes TEXT NOT NULL,mode TEXT NOT NULL,source_sha256 TEXT NOT NULL,
              source_bytes INTEGER NOT NULL,identity_content TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sh_capture_entries(
              capture_id TEXT NOT NULL REFERENCES sh_captures(capture_id),entry_id TEXT NOT NULL,ordinal INTEGER NOT NULL,
              document_id TEXT REFERENCES sh_documents(document_id),status TEXT NOT NULL,entry_content TEXT NOT NULL,
              source_sha256 TEXT,policy_hash TEXT,PRIMARY KEY(capture_id,entry_id),UNIQUE(capture_id,ordinal),
              FOREIGN KEY(source_sha256,policy_hash) REFERENCES sh_lexicons(source_sha256,policy_hash));
            CREATE INDEX IF NOT EXISTS sh_capture_documents ON sh_capture_entries(capture_id,document_id);
            CREATE INDEX IF NOT EXISTS sh_capture_lexicons ON sh_capture_entries(capture_id,source_sha256,policy_hash);
            """, tx);
        schema.ExecuteNonQuery(); tx.Commit();
    }

    public void RegisterCapture(SourceCaptureReceipt receipt)
    {
        Writable(); ArgumentNullException.ThrowIfNull(receipt); Hash(receipt.CaptureId);
        var manifest = receipt.Manifest; var canonical = CanonicalJson.Serialize(manifest);
        if (CanonicalJson.HashUtf8(canonical) != receipt.CaptureId || manifest.Schema != "filemapper.source-capture/1" ||
            CanonicalJson.Sha256(manifest.Policy) != manifest.PolicyHash) throw new InvalidDataException("Capture or capture-policy commitment mismatch.");
        ValidateCoverage(manifest.Coverage); Hash(manifest.RawTree.Sha256); Hash(manifest.Entries.Sha256);
        if (manifest.RawTree.ByteLength < 0 || manifest.Entries.ByteLength < 0 || manifest.FinishedAt < manifest.StartedAt)
            throw new InvalidDataException("Invalid capture bounds.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var prior = Command(db, "SELECT manifest FROM sh_captures WHERE capture_id=$id", tx, ("$id", receipt.CaptureId));
        if (prior.ExecuteScalar() is string old)
        { if (old != canonical) throw new InvalidDataException("Capture identity has conflicting canonical content."); return; }
        using var insert = Command(db, "INSERT INTO sh_captures(capture_id,manifest,manifest_path,repository_id,commit_id) VALUES($id,$m,$p,$r,$c)", tx,
            ("$id", receipt.CaptureId), ("$m", canonical), ("$p", Path.GetFullPath(receipt.ManifestPath)), ("$r", manifest.RepositoryId), ("$c", manifest.Commit));
        insert.ExecuteNonQuery(); tx.Commit();
    }

    public SourceLexiconResult? TryGetLexicon(string blobSha256, string policyHash, CancellationToken cancellationToken = default)
    {
        Hash(blobSha256); Hash(policyHash); using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceLexiconResult?>(() =>
        {
            using var cmd = Command(db, "SELECT result FROM sh_lexicons WHERE source_sha256=$s AND policy_hash=$p", null, ("$s", blobSha256), ("$p", policyHash));
            cancellationToken.ThrowIfCancellationRequested();
            return cmd.ExecuteScalar() is string text ? Deserialize<SourceLexiconResult>(text) : null;
        });
    }

    public Task ImportLexiconAsync(SourceLexiconResult result, CancellationToken cancellationToken = default) =>
        ImportLexiconCoreAsync(result, null, cancellationToken);

    public Task ImportLexiconAsync(SourceLexiconResult result, SourceBlobStore source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ImportLexiconCoreAsync(result, source, cancellationToken);
    }

    private async Task ImportLexiconCoreAsync(SourceLexiconResult result, SourceBlobStore? source, CancellationToken cancellationToken)
    {
        Writable(); ValidateResult(result); cancellationToken.ThrowIfCancellationRequested();
        if (!result.Complete && result.OutputPath is not null && source is null)
            throw new InvalidDataException("Partial lexical output requires the retained source blob to verify its byte spans.");
        // Keep the same verified source handle open throughout import; a prefix cannot prove its own source SHA.
        await using var sourceProof = result.OutputPath is not null && source is not null ? source.OpenRead(result.SourceSha256) : null;
        if (sourceProof is not null)
        {
            if (sourceProof.Length != result.SourceBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceProof, cancellationToken)) != result.SourceSha256)
                throw new InvalidDataException("Retained source blob length or SHA-256 commitment mismatch.");
            sourceProof.Position = 0;
        }
        var commitment = LexiconCommitment(result);
        using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        await cancellation.RunAsync(async () =>
        {
            using var tx = db.BeginTransaction();
            using var prior = Command(db, "SELECT commitment FROM sh_lexicons WHERE source_sha256=$s AND policy_hash=$p", tx,
                ("$s", result.SourceSha256), ("$p", result.Policy.PolicyHash));
            var existing = prior.ExecuteScalar() as string;
            if (existing is not null && existing != commitment) throw new InvalidDataException("Blob/policy identity has conflicting lexical content.");
            using var insert = Command(db, "INSERT INTO sh_lexicons VALUES($s,$p,$c,$r,$bytes,$status,$complete,$occ,$words)", tx,
                ("$s", result.SourceSha256), ("$p", result.Policy.PolicyHash), ("$c", commitment), ("$r", CanonicalJson.Serialize(result)),
                ("$bytes", result.SourceBytes), ("$status", result.Status), ("$complete", result.Complete ? 1 : 0), ("$occ", result.Occurrences), ("$words", result.WordOccurrences));
            if (existing is null) insert.ExecuteNonQuery();
            if (result.OutputPath is null) { cancellationToken.ThrowIfCancellationRequested(); tx.Commit(); return; }
            CheckPath(result.OutputPath);
            // Hold the artifact against writes/deletion throughout both independent hash passes and streamed import.
            await using var artifact = new FileStream(result.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var storedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(artifact, cancellationToken));
            if (storedHash != result.OutputSha256) throw new InvalidDataException("Stored lexical artifact hash mismatch.");
            artifact.Position = 0;
            using (var decoded = result.OutputFormat == "ndjson-gzip" ? new GZipStream(artifact, CompressionMode.Decompress, leaveOpen: true) : null)
            {
                var canonicalHash = await BoundedHashAsync(decoded ?? (Stream)artifact, checked(result.Occurrences * 524289), cancellationToken);
                if (canonicalHash != result.CanonicalNdjsonSha256) throw new InvalidDataException("Decompressed lexical NDJSON commitment mismatch.");
            }
            using var canonicalDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sourceDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var validation = new UnitValidation(result, sourceDigest, sourceProof);
            using var unitInsert = Command(db, "INSERT INTO sh_units VALUES($s,$p,$o,$id,$k,$t,$bs,$bl,$us,$ul,$payload)", tx);
            using var termInsert = Command(db, "INSERT INTO sh_terms VALUES($id,$term) ON CONFLICT(term_id) DO NOTHING", tx);
            using var termCheck = Command(db, "SELECT term FROM sh_terms WHERE term_id=$id", tx);
            using var aggregate = Command(db, "INSERT INTO sh_blob_terms VALUES($s,$p,$t,1) ON CONFLICT(source_sha256,policy_hash,term_id) DO UPDATE SET occurrences=occurrences+1", tx);
            await foreach (var unit in SourceLexicon.ReadAsync(result.OutputPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested(); validation.Add(unit);
                var payload = CanonicalJson.Serialize(unit); canonicalDigest.AppendData(Encoding.UTF8.GetBytes(payload + "\n"));
                if (existing is not null) continue;
                Set(unitInsert, ("$s", result.SourceSha256), ("$p", result.Policy.PolicyHash), ("$o", unit.Ordinal), ("$id", unit.OccurrenceId),
                    ("$k", unit.Kind), ("$t", unit.TermId), ("$bs", unit.ByteStart), ("$bl", unit.ByteLength), ("$us", unit.Utf16Start), ("$ul", unit.Utf16Length), ("$payload", payload));
                unitInsert.ExecuteNonQuery();
                if (unit.TermId is not null)
                {
                    Set(termInsert, ("$id", unit.TermId), ("$term", unit.NormalizedTerm)); termInsert.ExecuteNonQuery();
                    Set(termCheck, ("$id", unit.TermId));
                    if ((string?)termCheck.ExecuteScalar() != unit.NormalizedTerm) throw new InvalidDataException("Term identity collision.");
                    Set(aggregate, ("$s", result.SourceSha256), ("$p", result.Policy.PolicyHash), ("$t", unit.TermId)); aggregate.ExecuteNonQuery();
                }
            }
            validation.Finish();
            if (Convert.ToHexStringLower(canonicalDigest.GetHashAndReset()) != result.CanonicalNdjsonSha256)
                throw new InvalidDataException("Lexical NDJSON contains noncanonical or uncommitted record bytes.");
            cancellationToken.ThrowIfCancellationRequested(); tx.Commit();
        });
    }

    public void AssociateDocument(string captureId, CapturedSourceEntry entry, SourceLexiconResult? lexicon,
        CancellationToken cancellationToken = default) => AssociateDocuments(captureId, [new(entry, lexicon)], cancellationToken);

    /// <summary>Atomically associates at most 512 entries. Exact retries preserve any existing lexical enrichment.</summary>
    public void AssociateDocuments(string captureId, IReadOnlyList<SourceDocumentAssociation> items, CancellationToken cancellationToken = default)
    {
        Writable(); Hash(captureId); ArgumentNullException.ThrowIfNull(items); cancellationToken.ThrowIfCancellationRequested();
        if (items.Count > 512) throw new ArgumentOutOfRangeException(nameof(items), "An association transaction accepts at most 512 entries.");
        var batch = items.ToArray();
        if (batch.Length == 0) return;
        using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        cancellation.Run(() =>
        {
            using var tx = db.BeginTransaction();
            var capture = ReadCapture(db, tx, captureId); var currentPolicy = capture.Policy;
            using var ordinalQuery = Command(db, "SELECT COALESCE(MAX(ordinal)+1,0) FROM sh_capture_entries WHERE capture_id=$c", tx, ("$c", captureId));

            var nextOrdinal = (long)ordinalQuery.ExecuteScalar()!;
            using var cached = Prepared(db, "SELECT commitment FROM sh_lexicons WHERE source_sha256=$s AND policy_hash=$p", tx, "$s", "$p");
            using var old = Prepared(db, "SELECT entry_content,policy_hash FROM sh_capture_entries WHERE capture_id=$c AND entry_id=$e", tx, "$c", "$e");
            using var prior = Prepared(db, "SELECT identity_content FROM sh_documents WHERE document_id=$d", tx, "$d");
            using var document = Prepared(db, "INSERT INTO sh_documents VALUES($d,$r,$c,$path,$raw,$mode,$s,$bytes,$identity) ON CONFLICT(document_id) DO NOTHING", tx, "$d", "$r", "$c", "$path", "$raw", "$mode", "$s", "$bytes", "$identity");
            using var add = Prepared(db, "INSERT INTO sh_capture_entries VALUES($c,$e,$o,$d,$status,$entry,$s,$p)", tx, "$c", "$e", "$o", "$d", "$status", "$entry", "$s", "$p");
            using var update = Prepared(db, "UPDATE sh_capture_entries SET source_sha256=$s,policy_hash=$p WHERE capture_id=$c AND entry_id=$e", tx, "$s", "$p", "$c", "$e");
            using var policy = Prepared(db, "UPDATE sh_captures SET policy_hash=$p WHERE capture_id=$c", tx, "$p", "$c");



            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(item.Entry);
                var entry = item.Entry; var lexicon = item.Lexicon; ValidateEntry(capture.Manifest, entry);
                if (lexicon is not null)
                {
                    ValidateResult(lexicon);
                    if (entry.Status != "captured" || entry.Blob?.Sha256 != lexicon.SourceSha256 || entry.Blob.ByteLength != lexicon.SourceBytes)
                        throw new InvalidDataException("Lexicon does not describe this captured document.");
                    Set(cached, ("$s", lexicon.SourceSha256), ("$p", lexicon.Policy.PolicyHash));
                    if ((string?)cached.ExecuteScalar() != LexiconCommitment(lexicon)) throw new InvalidDataException("Import and verify this lexicon before associating it.");
                    if (currentPolicy is not null && currentPolicy != lexicon.Policy.PolicyHash) throw new InvalidDataException("A capture cannot mix extraction policies.");
                }
                var canonical = CanonicalJson.Serialize(entry); string? oldEntry = null, oldPolicy = null;
                Set(old, ("$c", captureId), ("$e", entry.Entry.EntryId));
                using (var reader = old.ExecuteReader()) if (reader.Read()) { oldEntry = reader.GetString(0); oldPolicy = NullableString(reader, 1); }
                if (oldEntry is not null && oldEntry != canonical) throw new InvalidDataException("Capture entry identity has conflicting canonical content.");
                if (oldPolicy is not null && lexicon is not null && oldPolicy != lexicon.Policy.PolicyHash) throw new InvalidDataException("A document already has a different extraction policy.");
                var enrich = oldEntry is not null && oldPolicy is null && lexicon is not null;
                if (oldEntry is not null && !enrich) continue;
                if (capture.Extraction is not null) throw new InvalidOperationException("A sealed extraction cannot be changed.");
                if (entry.DocumentId is not null)
                {
                    var identity = CanonicalJson.Serialize(new { capture.Manifest.RepositoryId, capture.Manifest.Commit, entry.Entry.RepositoryPathBytes, entry.Entry.Mode, entry.Blob!.Sha256, entry.Blob.ByteLength });
                    Set(prior, ("$d", entry.DocumentId));
                    if (prior.ExecuteScalar() is string existing && existing != identity) throw new InvalidDataException("Document occurrence identity collision.");
                    Set(document, ("$d", entry.DocumentId), ("$r", capture.Manifest.RepositoryId), ("$c", capture.Manifest.Commit), ("$path", entry.Entry.RepositoryPath),
                        ("$raw", entry.Entry.RepositoryPathBytes), ("$mode", entry.Entry.Mode), ("$s", entry.Blob.Sha256), ("$bytes", entry.Blob.ByteLength), ("$identity", identity));
                    document.ExecuteNonQuery();
                }
                if (oldEntry is null)
                {
                    if (nextOrdinal >= capture.Manifest.Coverage.TreeEntries) throw new InvalidDataException("Association exceeds the committed tree entry count.");
                    Set(add, ("$c", captureId), ("$e", entry.Entry.EntryId), ("$o", nextOrdinal++), ("$d", entry.DocumentId), ("$status", entry.Status), ("$entry", canonical),
                        ("$s", lexicon?.SourceSha256), ("$p", lexicon?.Policy.PolicyHash)); add.ExecuteNonQuery();
                }
                else
                {
                    Set(update, ("$s", lexicon!.SourceSha256), ("$p", lexicon.Policy.PolicyHash), ("$c", captureId), ("$e", entry.Entry.EntryId)); update.ExecuteNonQuery();
                }
                if (lexicon is not null) currentPolicy = lexicon.Policy.PolicyHash;
            }
            if (currentPolicy != capture.Policy)
            { Set(policy, ("$p", currentPolicy), ("$c", captureId)); policy.ExecuteNonQuery(); }
            cancellationToken.ThrowIfCancellationRequested(); tx.Commit();
        });
    }

    public void SealExtraction(string captureId, string policyHash, string extractionHash)
    {
        Writable(); Hash(captureId); Hash(policyHash); Hash(extractionHash);
        using var db = Open(); using var tx = db.BeginTransaction(); var capture = ReadCapture(db, tx, captureId);
        if (capture.Policy is not null && capture.Policy != policyHash || capture.Extraction is not null && capture.Extraction != extractionHash)
            throw new InvalidDataException("Sealed extraction identity or policy conflict.");
        if (capture.Extraction == extractionHash) return;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0, bytes = 0, pending = 0;
        using (var query = Command(db, "SELECT entry_content,document_id,policy_hash FROM sh_capture_entries WHERE capture_id=$c ORDER BY ordinal", tx, ("$c", captureId)))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                var encoded = Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"); digest.AppendData(encoded); bytes = checked(bytes + encoded.Length); count++;
                if (!reader.IsDBNull(1) && reader.IsDBNull(2)) pending++;
            }
        }
        if (count != capture.Manifest.Coverage.TreeEntries || pending != 0 || bytes != capture.Manifest.Entries.ByteLength ||
            Convert.ToHexStringLower(digest.GetHashAndReset()) != capture.Manifest.Entries.Sha256)
            throw new InvalidDataException("Capture entries are missing, reordered, unprocessed, or differ from the committed entries manifest.");
        using var seal = Command(db, "UPDATE sh_captures SET policy_hash=$p,extraction_hash=$e WHERE capture_id=$c", tx,
            ("$p", policyHash), ("$e", extractionHash), ("$c", captureId)); seal.ExecuteNonQuery(); tx.Commit();
    }

    public SourceIndexPage<SourceIndexedCapture> ListCaptures(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); cancellationToken.ThrowIfCancellationRequested(); using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceIndexPage<SourceIndexedCapture>>(() =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            using var count = Command(db, "SELECT COUNT(*) FROM sh_captures", tx);
            var total = (long)count.ExecuteScalar()!;
            using var cmd = Command(db, "SELECT capture_id,manifest,policy_hash,extraction_hash FROM sh_captures ORDER BY capture_id LIMIT $l OFFSET $o", tx, ("$l", limit), ("$o", offset));

            using var reader = cmd.ExecuteReader(); var results = new List<SourceIndexedCapture>();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); var m = Deserialize<SourceCaptureManifest>(reader.GetString(1)); results.Add(new(reader.GetString(0), m.RepositoryId, m.DisplayName, m.Commit, m.State, NullableString(reader, 2), NullableString(reader, 3), m.Coverage, m.StartedAt, m.FinishedAt, m.CanonicalUri)); }
            cancellationToken.ThrowIfCancellationRequested(); return new(results, total, offset, limit);
        });
    }
    public SourceIndexPage<SourceIndexedDocument> Documents(string captureId, string? search = null, bool supportedOnly = false,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); Hash(captureId); cancellationToken.ThrowIfCancellationRequested();
        if (search?.Length > 4096) throw new ArgumentException("Search is limited to 4096 UTF-16 units.", nameof(search));
        using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceIndexPage<SourceIndexedDocument>>(() =>
        {
            using var tx = db.BeginTransaction(deferred: true); _ = ReadCapture(db, tx, captureId);
            const string joins = " FROM sh_capture_entries e LEFT JOIN sh_lexicons l ON l.source_sha256=e.source_sha256 AND l.policy_hash=e.policy_hash WHERE e.capture_id=$c ";
            var filter = joins + SourceIndexedDocument.Filter;
            using var count = Command(db, "SELECT COUNT(*)" + filter, tx, ("$c", captureId), ("$q", Like(search)), ("$supported", supportedOnly ? 1 : 0));
            var total = (long)count.ExecuteScalar()!;
            using var query = Command(db, "SELECT e.entry_content,l.status,l.complete,length(e.entry_content)" + filter + " ORDER BY e.ordinal LIMIT $l OFFSET $o", tx,
                ("$c", captureId), ("$q", Like(search)), ("$supported", supportedOnly ? 1 : 0), ("$l", limit), ("$o", offset));
            using var reader = query.ExecuteReader(); var items = new List<SourceIndexedDocument>(); var bytes = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.GetInt64(3) > 64 * 1024) throw new InvalidDataException("Source entry metadata exceeds its query bound.");
                var text = reader.GetString(0); bytes = checked(bytes + Encoding.UTF8.GetByteCount(text));
                if (bytes > 4 * 1024 * 1024) throw new InvalidDataException("Source document page exceeds its byte bound; use a smaller page.");
                items.Add(SourceIndexedDocument.From(Deserialize<CapturedSourceEntry>(text), NullableString(reader, 1), reader.IsDBNull(2) ? null : reader.GetInt64(2) == 1));
            }
            return new(items, total, offset, limit);
        });
    }

    public SourceIndexPage<SourceTermCount> Terms(string captureId, string? search, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); Hash(captureId); cancellationToken.ThrowIfCancellationRequested(); if (search?.Length > 4096) throw new ArgumentException("Search is limited to 4096 UTF-16 units.", nameof(search));
        using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceIndexPage<SourceTermCount>>(() =>
        {
            using var tx = db.BeginTransaction(deferred: true); _ = ReadCapture(db, tx, captureId);
            const string joins = " FROM sh_capture_entries e JOIN sh_lexicons l ON l.source_sha256=e.source_sha256 AND l.policy_hash=e.policy_hash JOIN sh_blob_terms b ON b.source_sha256=l.source_sha256 AND b.policy_hash=l.policy_hash JOIN sh_terms t ON t.term_id=b.term_id WHERE e.capture_id=$c AND t.term LIKE $q ESCAPE char(92)";
            using var count = Command(db, "SELECT COUNT(DISTINCT t.term_id)" + joins, tx, ("$c", captureId), ("$q", Like(search)));

            var total = (long)count.ExecuteScalar()!;
            using var cmd = Command(db, "SELECT t.term_id,t.term,SUM(b.occurrences),COUNT(*),SUM(CASE WHEN l.complete=0 THEN 1 ELSE 0 END)" + joins + " GROUP BY t.term_id,t.term ORDER BY SUM(b.occurrences) DESC,t.term,t.term_id LIMIT $l OFFSET $o", tx,
                ("$c", captureId), ("$q", Like(search)), ("$l", limit), ("$o", offset));

            using var reader = cmd.ExecuteReader(); var items = new List<SourceTermCount>();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4))); }
            cancellationToken.ThrowIfCancellationRequested(); return new(items, total, offset, limit);
        });
    }
    public SourceIndexPage<SourceIndexedOccurrence> Occurrences(string captureId, string termId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); Hash(captureId); cancellationToken.ThrowIfCancellationRequested(); if (!termId.StartsWith("source-term:", StringComparison.Ordinal)) throw new ArgumentException("A source term ID is required.", nameof(termId)); Hash(termId[12..]);
        using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceIndexPage<SourceIndexedOccurrence>>(() =>
        {
            using var tx = db.BeginTransaction(deferred: true); _ = ReadCapture(db, tx, captureId);
            const string joins = " FROM sh_capture_entries e JOIN sh_documents d ON d.document_id=e.document_id JOIN sh_lexicons l ON l.source_sha256=e.source_sha256 AND l.policy_hash=e.policy_hash JOIN sh_units u ON u.source_sha256=l.source_sha256 AND u.policy_hash=l.policy_hash WHERE e.capture_id=$c AND u.term_id=$t";
            using var count = Command(db, "SELECT COUNT(*)" + joins, tx, ("$c", captureId), ("$t", termId));  var total = (long)count.ExecuteScalar()!;
            using var cmd = Command(db, "SELECT d.document_id,d.path,d.path_bytes,d.source_sha256,u.payload,l.status,l.complete" + joins + " ORDER BY d.path_bytes,d.document_id,u.ordinal LIMIT $l OFFSET $o", tx,
                ("$c", captureId), ("$t", termId), ("$l", limit), ("$o", offset));

            using var reader = cmd.ExecuteReader(); var items = new List<SourceIndexedOccurrence>();
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Deserialize<SourceLexicalOccurrence>(reader.GetString(4)), reader.GetString(5), reader.GetInt64(6) == 1)); }
            cancellationToken.ThrowIfCancellationRequested(); return new(items, total, offset, limit);
        });
    }
    public SourceIndexCounts Counts(string captureId, CancellationToken cancellationToken = default)
    {
        Hash(captureId); cancellationToken.ThrowIfCancellationRequested(); using var db = Open(cancellationToken: cancellationToken);
        using var cancellation = new SourceSqliteCancellation(db, cancellationToken);
        return cancellation.Run<SourceIndexCounts>(() =>
        {
            using var tx = db.BeginTransaction(deferred: true); var capture = ReadCapture(db, tx, captureId);
            using var cmd = Command(db, """
                SELECT COUNT(*),COUNT(e.document_id),COUNT(l.source_sha256),COUNT(DISTINCT l.source_sha256),
                  COALESCE(SUM(l.occurrences),0),COALESCE(SUM(l.words),0),
                  COALESCE(SUM(CASE WHEN l.complete=1 THEN 1 ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN l.complete=0 THEN 1 ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN l.status IN ('invalid-encoding','source-length-mismatch','source-hash-mismatch') THEN 1 ELSE 0 END),0)
                FROM sh_capture_entries e LEFT JOIN sh_lexicons l ON l.source_sha256=e.source_sha256 AND l.policy_hash=e.policy_hash WHERE e.capture_id=$c
                """, tx, ("$c", captureId));

            long entries, processed, unique, occurrences, words, complete, partial, errors;
            using (var r = cmd.ExecuteReader()) { r.Read(); entries = r.GetInt64(0); processed = r.GetInt64(2); unique = r.GetInt64(3); occurrences = r.GetInt64(4); words = r.GetInt64(5); complete = r.GetInt64(6); partial = r.GetInt64(7); errors = r.GetInt64(8); }
            using var termCount = Command(db, "SELECT COUNT(DISTINCT b.term_id) FROM sh_capture_entries e JOIN sh_blob_terms b ON b.source_sha256=e.source_sha256 AND b.policy_hash=e.policy_hash WHERE e.capture_id=$c", tx, ("$c", captureId));
             cancellationToken.ThrowIfCancellationRequested();
            var terms = (long)termCount.ExecuteScalar()!; cancellationToken.ThrowIfCancellationRequested();
            return new(captureId, capture.Manifest.Coverage.TreeEntries, capture.Manifest.Coverage.CapturedEntries, entries, processed, capture.Manifest.Coverage.CapturedEntries - processed, unique,
                occurrences, words, terms, capture.Extraction is not null, complete, partial, errors,
                capture.Manifest.Coverage.MissingEntries, capture.Manifest.Coverage.PolicyExcludedEntries);
        });
    }

    private static string LexiconCommitment(SourceLexiconResult result) => CanonicalJson.Serialize(result with { OutputPath = null, OutputSha256 = null, OutputFormat = "canonical-ndjson" });
    private static void ValidateResult(SourceLexiconResult r)
    {
        ArgumentNullException.ThrowIfNull(r); Hash(r.SourceSha256); Hash(r.Policy.PolicyHash);
        if (r.Schema != SourceLexicon.ContractId || CanonicalJson.Serialize(r.Policy) != CanonicalJson.Serialize(SourceLexicon.Describe(r.Policy.Options)))
            throw new InvalidDataException("Unsupported lexical contract, runtime binding, or policy commitment.");
        if (r.SourceBytes < 0 || r.ProcessedBytes < 0 || r.ProcessedBytes > r.SourceBytes || r.DecodedUtf16Units < 0 || r.Occurrences < 0 || r.Occurrences > r.ProcessedBytes || r.WordOccurrences < 0 || r.WordOccurrences > r.Occurrences ||
            r.Complete != (r.Status == "complete" && r.ProcessedBytes == r.SourceBytes)) throw new InvalidDataException("Invalid lexical result counts or completeness.");
        if (r.Status is not ("complete" or "binary" or "unsupported-encoding" or "invalid-encoding" or "too-large" or "source-length-mismatch" or "source-hash-mismatch"))
            throw new InvalidDataException("Unknown lexical status.");
        if (r.OutputPath is null)
        {
            if (r.Complete || r.Occurrences != 0 || r.WordOccurrences != 0 || r.ProcessedBytes != 0 || r.DecodedUtf16Units != 0 || r.OutputSha256 is not null || r.CanonicalNdjsonSha256 is not null)
                throw new InvalidDataException("Absent lexical output cannot claim processed units.");
        }
        else
        {
            Hash(r.OutputSha256!); Hash(r.CanonicalNdjsonSha256!);
            if (r.SourceBytes > r.Policy.Options.MaxDocumentBytes || r.Encoding is not ("utf-8" or "utf-16le" or "utf-16be" or "binary" or "utf-32") ||
                r.Complete && r.Encoding is not ("utf-8" or "utf-16le" or "utf-16be")) throw new InvalidDataException("Invalid lexical encoding or source bound.");
            if (r.OutputFormat is not ("ndjson" or "ndjson-gzip") || r.OutputPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) != (r.OutputFormat == "ndjson-gzip"))
                throw new InvalidDataException("Lexical artifact format/path disagreement.");
        }
    }
    private sealed class UnitValidation(SourceLexiconResult result, IncrementalHash sourceHash, Stream? sourceProof)
    {
        private long ordinal, words, bytes, utf16, line = 1, column = 1;
        private bool priorCr;
        private string? fragmentGroup, fragmentKind; private int fragmentIndex;
        private readonly Encoding encoding = result.Encoding switch { "utf-8" => new UTF8Encoding(false, true), "utf-16le" => new UnicodeEncoding(false, false, true), "utf-16be" => new UnicodeEncoding(true, false, true), _ => new UTF8Encoding(false, true) };
        public void Add(SourceLexicalOccurrence u)
        {
            if (u.Schema != SourceLexicon.ContractId || u.PolicyHash != result.Policy.PolicyHash || u.SourceSha256 != result.SourceSha256 || u.Ordinal != ordinal ||
                u.ByteStart != bytes || u.Utf16Start != utf16 || u.ByteLength <= 0 || u.Utf16Length < 0 || u.StartLine != line || u.StartColumn != column || u.ColumnUnit != "utf16-code-unit")
                throw new InvalidDataException("Noncontiguous or inconsistent lexical occurrence coordinates.");
            if (u.Kind is not ("word" or "whitespace" or "punctuation" or "symbol" or "control" or "bom" or "invalid-byte-sequence" or "binary-marker" or "unsupported-encoding")) throw new InvalidDataException("Unknown lexical unit kind.");
            var expected = "source-occurrence:" + CanonicalJson.Sha256(new { Model = SourceLexicon.ContractId, result.Policy.PolicyHash, Source = result.SourceSha256, Ordinal = ordinal, u.Kind, u.ByteStart, u.ByteLength });
            if (expected != u.OccurrenceId || u.Text?.Length > result.Policy.Options.MaxTokenUtf16Length || u.Utf16Length != (u.Text?.Length ?? 0)) throw new InvalidDataException("Lexical occurrence identity or UTF-16 length mismatch.");
            byte[] raw;
            if (u.RawBytesBase64 is not null)
            {
                if (u.RawBytesBase64.Length > 32 || u.Kind is not ("bom" or "invalid-byte-sequence" or "binary-marker" or "unsupported-encoding")) throw new InvalidDataException("Invalid raw lexical marker.");
                raw = Convert.FromBase64String(u.RawBytesBase64);
                if (Convert.ToBase64String(raw) != u.RawBytesBase64) throw new InvalidDataException("Noncanonical raw marker encoding.");
            }
            else { if (u.Text is null) throw new InvalidDataException("Missing lexical source text."); raw = encoding.GetBytes(u.Text); }
            if (raw.LongLength != u.ByteLength) throw new InvalidDataException("Lexical raw byte length mismatch.");
            // Raw markers carry bytes, not arbitrary decoded display text. Keep their forms bound to the producer contract.
            var markerValid = u.Kind switch
            {
                "bom" => u.Text == "" && u.RawBytesBase64 is not null && u.Ordinal == 0 && !u.Fragmented &&
                    (result.Encoding == "utf-8" && raw.AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) ||
                     result.Encoding == "utf-16le" && raw.AsSpan().SequenceEqual(new byte[] { 0xff, 0xfe }) ||
                     result.Encoding == "utf-16be" && raw.AsSpan().SequenceEqual(new byte[] { 0xfe, 0xff })),
                "invalid-byte-sequence" => u.Text is null && u.RawBytesBase64 is not null && raw.Length <= 4 && !u.Fragmented &&
                    result.Status == "invalid-encoding" && u.Ordinal == result.Occurrences - 1,
                "unsupported-encoding" => u.Text is null && u.RawBytesBase64 is not null && !u.Fragmented && u.Ordinal == 0 &&
                    result.Status == "unsupported-encoding" && result.Encoding == "utf-32" && result.Occurrences == 1 && raw.Length == Math.Min(8, result.SourceBytes),
                "binary-marker" => !u.Fragmented && result.Status == "binary" && u.Ordinal == result.Occurrences - 1 &&
                    (u.RawBytesBase64 is null && u.Text == "\0" && result.Encoding is "utf-8" or "utf-16le" or "utf-16be" ||
                     u.RawBytesBase64 is not null && u.Text is null && result.Encoding == "binary" && u.Ordinal == 0 && raw.Length == Math.Min(8, result.SourceBytes)),
                _ => true
            };
            if (!markerValid) throw new InvalidDataException("Lexical marker text, raw bytes, or terminal form differs from the source decoder contract.");
            if (sourceProof is not null)
            {
                if (sourceProof.Position != u.ByteStart) throw new InvalidDataException("Source proof span is not contiguous.");
                Span<byte> proofBuffer = stackalloc byte[4096];
                for (var start = 0; start < raw.Length; start += proofBuffer.Length)
                {
                    var count = Math.Min(proofBuffer.Length, raw.Length - start);
                    sourceProof.ReadExactly(proofBuffer[..count]);
                    if (!proofBuffer[..count].SequenceEqual(raw.AsSpan(start, count)))
                        throw new InvalidDataException("Lexical unit bytes do not match the retained source blob.");
                }
            }
            sourceHash.AppendData(raw);
            var term = u.Kind == "word" && !u.Fragmented ? u.Text!.Normalize(NormalizationForm.FormKC).ToLowerInvariant() : null;
            var termId = term is null ? null : "source-term:" + CanonicalJson.Sha256(new { Model = SourceLexicon.TermContractId, NormalizedText = term });
            if (u.NormalizedTerm != term || u.TermId != termId) throw new InvalidDataException("Lexical term normalization or identity mismatch.");
            if (u.Fragmented)
            {
                if (fragmentGroup is null)
                {
                    fragmentGroup = "source-fragments:" + CanonicalJson.Sha256(new { Model = SourceLexicon.ContractId, result.Policy.PolicyHash, Source = result.SourceSha256, u.Kind, u.ByteStart }); fragmentKind = u.Kind; fragmentIndex = 0;
                }
                if (u.FragmentGroupId != fragmentGroup || u.Kind != fragmentKind || u.FragmentIndex != fragmentIndex || u.ContinuedFromPrevious != (fragmentIndex > 0) || fragmentIndex == 0 && !u.ContinuesToNext)
                    throw new InvalidDataException("Invalid lexical fragment chain.");
                if (u.ContinuesToNext) fragmentIndex++; else { fragmentGroup = null; fragmentKind = null; fragmentIndex = 0; }
            }
            else if (fragmentGroup is not null || u.FragmentGroupId is not null || u.FragmentIndex != 0 || u.ContinuedFromPrevious || u.ContinuesToNext) throw new InvalidDataException("Unmarked lexical fragment boundary.");
            if (u.Text is not null)
            {
                var attachedToWord = u.ContinuedFromPrevious && u.Kind == "word";
                foreach (var rune in u.Text.EnumerateRunes())
                {
                    if (u.Kind is "word" or "whitespace" or "punctuation" or "symbol" or "control")
                    {
                        var category = Rune.GetUnicodeCategory(rune);
                        var kind = Rune.IsLetterOrDigit(rune) || attachedToWord && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                            ? "word" : Rune.IsWhiteSpace(rune) ? "whitespace" : Rune.IsPunctuation(rune) ? "punctuation" : Rune.IsControl(rune) ? "control" : "symbol";
                        if (kind != u.Kind) throw new InvalidDataException("Lexical kind disagrees with its Unicode scalar classification.");
                        attachedToWord = kind == "word";
                    }
                    if (rune.Value == '\r') { line++; column = 1; priorCr = true; }
                    else if (rune.Value == '\n') { if (!priorCr) line++; column = 1; priorCr = false; }
                    else { column = checked(column + rune.Utf16SequenceLength); priorCr = false; }
                }
            }
            bytes = checked(bytes + u.ByteLength); utf16 = checked(utf16 + u.Utf16Length); ordinal++; if (termId is not null) words++;
            if (bytes > result.ProcessedBytes || utf16 > result.DecodedUtf16Units || ordinal > result.Occurrences || words > result.WordOccurrences) throw new InvalidDataException("Lexical stream exceeds its declared result counts.");
        }
        public void Finish()
        {
            if (ordinal != result.Occurrences || words != result.WordOccurrences || bytes != result.ProcessedBytes || utf16 != result.DecodedUtf16Units || fragmentGroup is not null)
                throw new InvalidDataException("Lexical stream is truncated or has inconsistent totals.");
            if (result.Complete && Convert.ToHexStringLower(sourceHash.GetHashAndReset()) != result.SourceSha256) throw new InvalidDataException("Reconstructed lexical bytes do not match the source commitment.");
        }
    }
    private static void ValidateEntry(SourceCaptureManifest capture, CapturedSourceEntry entry)
    {
        var e = entry.Entry; var raw = Convert.FromBase64String(e.RepositoryPathBytes);
        if (raw.Length == 0 || raw.Length > 1024 * 1024 || raw.Contains((byte)0) || raw[0] == '/') throw new InvalidDataException("Invalid raw repository path.");
        string display; string encoding;
        try { display = new UTF8Encoding(false, true).GetString(raw); encoding = "utf-8"; } catch (DecoderFallbackException) { display = Encoding.UTF8.GetString(raw); encoding = "opaque-bytes"; }
        if (e.RepositoryPath != display || e.PathEncoding != encoding) throw new InvalidDataException("Repository path display is inconsistent with its raw bytes.");
        var expectedEntry = "entry:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-tree-entry/1", capture.RepositoryId, Revision = capture.Commit, PathBytes = e.RepositoryPathBytes, e.Mode, ObjectId = e.GitObjectId });
        if (e.EntryId != expectedEntry) throw new InvalidDataException("Tree entry identity mismatch.");
        if (entry.Status == "captured")
        {
            if (entry.Blob is null || entry.DocumentId is null || entry.Blob.ByteLength < 0 || entry.ReportedBlobBytes != entry.Blob.ByteLength || e.ObjectType != "blob") throw new InvalidDataException("Invalid captured source descriptor.");
            Hash(entry.Blob.Sha256);
            var document = "document:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-document-occurrence/1", capture.RepositoryId, Revision = capture.Commit, e.RepositoryPathBytes, e.Mode, BlobSha256 = entry.Blob.Sha256 });
            if (entry.DocumentId != document) throw new InvalidDataException("Source document occurrence identity mismatch.");
        }
        else if (entry.DocumentId is not null || entry.Blob is not null) throw new InvalidDataException("An uncaptured entry cannot claim a retained source document.");
    }
    private static void ValidateCoverage(SourceCaptureCoverage c)
    {
        if (c.TreeEntries < 0 || c.CapturedEntries < 0 || c.MissingEntries < 0 || c.PolicyExcludedEntries < 0 || c.SubmoduleReferences < 0 || c.ErrorEntries < 0 || c.AssociatedSourceBytes < 0 || c.UniqueRetainedBytes < 0 || c.UniqueRetainedBlobs < 0 ||
            checked(c.CapturedEntries + c.MissingEntries + c.PolicyExcludedEntries + c.SubmoduleReferences + c.ErrorEntries) != c.TreeEntries) throw new InvalidDataException("Invalid capture coverage accounting.");
    }
    private static (SourceCaptureManifest Manifest, string? Policy, string? Extraction) ReadCapture(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var cmd = Command(db, "SELECT manifest,policy_hash,extraction_hash FROM sh_captures WHERE capture_id=$c", tx, ("$c", id)); using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Source capture is not registered.");
        return (Deserialize<SourceCaptureManifest>(r.GetString(0)), NullableString(r, 1), NullableString(r, 2));
    }
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Source index contains a null payload.");
    private static string? NullableString(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
    private static void Hash(string value) { if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("A lowercase SHA-256 value is required."); }
    private static async Task<string> BoundedHashAsync(Stream stream, long maximumBytes, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken); if (count == 0) break;
            total = checked(total + count); if (total > maximumBytes) throw new InvalidDataException("Decompressed lexical output exceeds its committed record bound.");
            digest.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    private static void Page(int offset, int limit) { if (offset < 0 || limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and limit 1–500."); }
    private static string Like(string? text) => "%" + (text ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    { var cmd = db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql; Set(cmd, values); return cmd; }
    private static SqliteCommand Prepared(SqliteConnection db, string sql, SqliteTransaction transaction, params string[] parameters)
    {
        var cmd = Command(db, sql, transaction);
        try { foreach (var name in parameters) cmd.Parameters.AddWithValue(name, DBNull.Value); cmd.Prepare(); return cmd; }
        catch { cmd.Dispose(); throw; }
    }
    private static void Set(SqliteCommand cmd, params (string, object?)[] values)
    { foreach (var (key, value) in values) { if (cmd.Parameters.Contains(key)) cmd.Parameters[key].Value = value ?? DBNull.Value; else cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); } }
    private static void CheckPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("Alternate-stream source index paths are not accepted.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse-point source index paths are not accepted.");
    }
}
