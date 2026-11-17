using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

/// <summary>A disposable, compact query projection of pinned immutable source captures and extractions.
/// All ordered units remain in the committed gzip corpus; only searchable, unfragmented word positions are indexed.</summary>
public sealed class CompactSourceHarvestIndex : ISourceHarvestReader
{
    private const int ApplicationId = 0x53484332;
    private const int Version = 1;
    private const string Format = "filemapper.compact-source-index/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string DatabasePath { get; }
    public bool IsReadOnly => true;

    public CompactSourceHarvestIndex(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath); CheckPath(DatabasePath);
        using var db = OpenReader(DatabasePath);
    }

    public SourceIndexPage<SourceIndexedCapture> ListCaptures(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); cancellationToken.ThrowIfCancellationRequested();
        return Read<SourceIndexPage<SourceIndexedCapture>>(cancellationToken, db =>
        {
        using var tx = db.BeginTransaction(deferred: true);
        using var count = Command(db, "SELECT COUNT(*) FROM captures WHERE ready=1", tx); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT capture_id,manifest,extraction_id,extraction FROM captures WHERE ready=1 ORDER BY capture_id LIMIT $l OFFSET $o", tx, ("$l", limit), ("$o", offset));
        using var r = query.ExecuteReader(); var items = new List<SourceIndexedCapture>();
        while (r.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); var m = Deserialize<SourceCaptureManifest>(r.GetString(1));
            var extraction = r.IsDBNull(3) ? null : Deserialize<CompactExtractionManifest>(r.GetString(3));
            items.Add(new(r.GetString(0), m.RepositoryId, m.DisplayName, m.Commit, m.State, extraction?.Policy.PolicyHash, NullableString(r, 2), m.Coverage, m.StartedAt, m.FinishedAt, m.CanonicalUri));
        }
        return new(items, total, offset, limit);
        });
    }

    public SourceIndexCounts Counts(string captureId, CancellationToken cancellationToken = default)
    {
        Hash(captureId); cancellationToken.ThrowIfCancellationRequested();
        return Read<SourceIndexCounts>(cancellationToken, db =>
        {
        using var tx = db.BeginTransaction(deferred: true);
        var capture = ReadCapture(db, tx, captureId);
        using var query = Command(db, """
            SELECT COUNT(*),COUNT(l.id),COUNT(DISTINCT l.id),COALESCE(SUM(l.units),0),COALESCE(SUM(l.words),0),
              COALESCE(SUM(CASE WHEN l.complete=1 THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN l.complete=0 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN l.status IN ('invalid-encoding','source-length-mismatch','source-hash-mismatch') THEN 1 ELSE 0 END),0)
            FROM entries e LEFT JOIN lexicons l ON l.id=e.lexicon_id AND l.ready=1 WHERE e.capture_id=$c
            """, tx, ("$c", capture.Key));
        long entries, processed, unique, units, words, complete, incomplete, errors;
        using (var r = query.ExecuteReader()) { r.Read(); entries = r.GetInt64(0); processed = r.GetInt64(1); unique = r.GetInt64(2); units = r.GetInt64(3); words = r.GetInt64(4); complete = r.GetInt64(5); incomplete = r.GetInt64(6); errors = r.GetInt64(7); }
        using var terms = Command(db, "SELECT COUNT(DISTINCT b.term_id) FROM entries e JOIN lexicon_terms b ON b.lexicon_id=e.lexicon_id WHERE e.capture_id=$c", tx, ("$c", capture.Key));
        var distinct = (long)terms.ExecuteScalar()!; cancellationToken.ThrowIfCancellationRequested();
        return new(captureId, capture.Manifest.Coverage.TreeEntries, capture.Manifest.Coverage.CapturedEntries, entries, processed, capture.Manifest.Coverage.CapturedEntries - processed,
            unique, units, words, distinct, capture.Extraction is not null, complete, incomplete, errors, capture.Manifest.Coverage.MissingEntries, capture.Manifest.Coverage.PolicyExcludedEntries);
        });
    }

    public SourceIndexPage<SourceIndexedDocument> Documents(string captureId, string? search = null, bool supportedOnly = false,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); Hash(captureId); cancellationToken.ThrowIfCancellationRequested();
        if (search?.Length > 4096) throw new ArgumentException("Search is limited to 4096 UTF-16 units.", nameof(search));
        return Read<SourceIndexPage<SourceIndexedDocument>>(cancellationToken, db =>
        {
            using var tx = db.BeginTransaction(deferred: true); var capture = ReadCapture(db, tx, captureId);
            const string joins = " FROM entries e LEFT JOIN lexicons l ON l.id=e.lexicon_id AND l.ready=1 WHERE e.capture_id=$c ";
            var filter = joins + SourceIndexedDocument.Filter;
            using var count = Command(db, "SELECT COUNT(*)" + filter, tx, ("$c", capture.Key), ("$q", Like(search)), ("$supported", supportedOnly ? 1 : 0));
            var total = (long)count.ExecuteScalar()!;
            using var query = Command(db, "SELECT e.entry_content,l.status,l.complete,length(e.entry_content)" + filter + " ORDER BY e.ordinal LIMIT $l OFFSET $o", tx,
                ("$c", capture.Key), ("$q", Like(search)), ("$supported", supportedOnly ? 1 : 0), ("$l", limit), ("$o", offset));
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
        return Read<SourceIndexPage<SourceTermCount>>(cancellationToken, db =>
        {
        using var tx = db.BeginTransaction(deferred: true); var capture = ReadCapture(db, tx, captureId);
        const string joins = " FROM entries e JOIN lexicons l ON l.id=e.lexicon_id AND l.ready=1 JOIN lexicon_terms b ON b.lexicon_id=l.id JOIN terms t ON t.id=b.term_id WHERE e.capture_id=$c AND t.term LIKE $q ESCAPE char(92)";
        using var count = Command(db, "SELECT COUNT(DISTINCT t.id)" + joins, tx, ("$c", capture.Key), ("$q", Like(search))); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT t.hash,t.term,SUM(b.occurrences),COUNT(*),SUM(CASE WHEN l.complete=0 THEN 1 ELSE 0 END)" + joins + " GROUP BY t.id ORDER BY SUM(b.occurrences) DESC,t.term,t.hash LIMIT $l OFFSET $o", tx,
            ("$c", capture.Key), ("$q", Like(search)), ("$l", limit), ("$o", offset));
        using var r = query.ExecuteReader(); var items = new List<SourceTermCount>();
        while (r.Read()) { cancellationToken.ThrowIfCancellationRequested(); items.Add(new("source-term:" + Hex(r, 0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4))); }
        return new(items, total, offset, limit);
        });
    }

    public SourceIndexPage<SourceIndexedOccurrence> Occurrences(string captureId, string termId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        Page(offset, limit); Hash(captureId); cancellationToken.ThrowIfCancellationRequested();
        if (!termId.StartsWith("source-term:", StringComparison.Ordinal)) throw new ArgumentException("A source term ID is required.", nameof(termId)); Hash(termId[12..]);
        return Read<SourceIndexPage<SourceIndexedOccurrence>>(cancellationToken, db =>
        {
        using var tx = db.BeginTransaction(deferred: true); var capture = ReadCapture(db, tx, captureId);
        const string joins = " FROM entries e JOIN lexicons l ON l.id=e.lexicon_id AND l.ready=1 JOIN policies p ON p.id=l.policy_id JOIN words w ON w.lexicon_id=l.id JOIN terms t ON t.id=w.term_id JOIN spellings s ON s.id=w.spelling_id WHERE e.capture_id=$c AND t.hash=$t";
        using var count = Command(db, "SELECT COUNT(*)" + joins, tx, ("$c", capture.Key), ("$t", Convert.FromHexString(termId[12..]))); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT e.document_id,e.path,e.path_bytes,l.source_hash,p.hash,w.ordinal,w.byte_start,w.byte_length,w.utf16_start,w.utf16_length,w.start_line,w.start_column,s.text,t.term,l.status,l.complete" + joins +
            " ORDER BY e.path_bytes,e.document_id,w.ordinal LIMIT $l OFFSET $o", tx, ("$c", capture.Key), ("$t", Convert.FromHexString(termId[12..])), ("$l", limit), ("$o", offset));
        using var r = query.ExecuteReader(); var items = new List<SourceIndexedOccurrence>();
        while (r.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); var source = Hex(r, 3); var policy = Hex(r, 4); var ordinal = r.GetInt64(5); var byteStart = r.GetInt64(6); var byteLength = r.GetInt64(7);
            var id = "source-occurrence:" + CanonicalJson.Sha256(new { Model = SourceLexicon.ContractId, PolicyHash = policy, Source = source, Ordinal = ordinal, Kind = "word", ByteStart = byteStart, ByteLength = byteLength });
            var unit = new SourceLexicalOccurrence(SourceLexicon.ContractId, policy, source, id, ordinal, "word", byteStart, byteLength,
                r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), "utf16-code-unit", r.GetString(12), r.GetString(13), termId, false, null, 0, false, false);
            items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), source, unit, r.GetString(14), r.GetInt64(15) == 1));
        }
        return new(items, total, offset, limit);
        });
    }

    public static async Task<CompactSourceIndexBuildReceipt> RebuildAsync(string workspaceRoot, string destinationDatabasePath,
        IReadOnlyList<CompactSourceIndexInput> inputs, CompactSourceIndexBuildOptions? options = null,
        IProgress<CompactSourceIndexBuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); ArgumentNullException.ThrowIfNull(inputs); cancellationToken.ThrowIfCancellationRequested();
        if (options.MaximumDatabaseBytes < 1024 * 1024 || options.MaximumDatabaseBytes > 1024L * 1024 * 1024 * 1024 ||
            options.MinimumFreeBytes < 0 || options.MaximumEntries < 0 || options.MaximumUnits < 0 || options.MaximumCaptures is < 1 or > 100_000 ||
            inputs.Count is < 1 || inputs.Count > options.MaximumCaptures) throw new ArgumentOutOfRangeException(nameof(options));
        var pinned = new CompactSourceIndexInput[inputs.Count];
        for (var i = 0; i < pinned.Length; i++) { pinned[i] = inputs[i]; Hash(pinned[i].CaptureId); if (pinned[i].ExtractionId is { } e) Hash(e); }
        if (pinned.Select(i => i.CaptureId).Distinct(StringComparer.Ordinal).Count() != pinned.Length) throw new ArgumentException("Select each capture exactly once.");
        Array.Sort(pinned, (a, b) => StringComparer.Ordinal.Compare(a.CaptureId, b.CaptureId));
        var root = Path.GetFullPath(workspaceRoot); CheckPath(root);
        var destination = Path.GetFullPath(destinationDatabasePath); CheckPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Compact publication never overwrites an existing destination.");
        var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent); CheckPath(parent);
        var request = CanonicalJson.Serialize(new { Format, WorkspaceRoot = root, Destination = destination, Inputs = pinned, Options = options with { ResumeDatabasePath = null } });
        var commitment = CanonicalJson.HashUtf8(request); var started = DateTimeOffset.UtcNow;
        var stage = options.ResumeDatabasePath is null ? Path.Combine(parent, ".compact-source-" + Guid.NewGuid().ToString("N") + ".building.db") : Path.GetFullPath(options.ResumeDatabasePath);
        CheckPath(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), parent, StringComparison.OrdinalIgnoreCase) || stage == destination ||
            !Path.GetFileName(stage).StartsWith(".compact-source-", StringComparison.Ordinal) || !stage.EndsWith(".building.db", StringComparison.Ordinal))
            throw new ArgumentException("Resume requires an owned compact staging database beside the destination.");
        EnsureFree(parent, options.MinimumFreeBytes);
        var captureStore = new GitSourceCapture(root, readOnly: true);
        CheckPath(stage + ".lock");
        using var lease = new FileStream(stage + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using (var builder = new Builder(stage, options, request, options.ResumeDatabasePath is not null, cancellationToken))
            {
                foreach (var input in pinned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await builder.CaptureAsync(captureStore, input, progress, cancellationToken);
                }
                builder.Finish(pinned.Length);
            }
            cancellationToken.ThrowIfCancellationRequested(); CheckPath(destination); CheckPath(stage);
            await using (var file = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            string databaseHash;
            await using (var file = OpenFile(stage)) databaseHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            long captures, entries, lexicons, units, words;
            using (var db = OpenReader(stage))
            using (var cmd = Command(db, "SELECT (SELECT COUNT(*) FROM captures),(SELECT COUNT(*) FROM entries),(SELECT COUNT(*) FROM lexicons WHERE ready=1),COALESCE(SUM(units),0),COALESCE(SUM(words),0) FROM lexicons WHERE ready=1"))
            using (var r = cmd.ExecuteReader()) { r.Read(); captures = r.GetInt64(0); entries = r.GetInt64(1); lexicons = r.GetInt64(2); units = r.GetInt64(3); words = r.GetInt64(4); }
            var bytes = new FileInfo(stage).Length;
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, destination, overwrite: false);
            return new(Format, destination, databaseHash, commitment, bytes, captures, entries, lexicons, units, words, started, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var failure = new { schema = Format, state = cancellationToken.IsCancellationRequested ? "canceled" : "failed", stagingDatabasePath = stage,
                destinationDatabasePath = destination, inputCommitment = commitment, startedAt = started, finishedAt = DateTimeOffset.UtcNow,
                error = error.GetType().Name, detail = error.Message, recovery = "Retain staging evidence; explicitly retry with the same inputs/options and ResumeDatabasePath. No destination was overwritten." };
            try { await File.WriteAllTextAsync(stage + ".failure-" + Guid.NewGuid().ToString("N") + ".json", CanonicalJson.Serialize(failure), CancellationToken.None); } catch (IOException) { }
            var translated = SourceSqliteCancellation.Translate(error, cancellationToken);
            if (!ReferenceEquals(error, translated)) throw translated;
            throw;
        }
    }

    private const string SchemaSql = """
        PRAGMA application_id=1397244722;
        PRAGMA user_version=1;
        CREATE TABLE build(singleton INTEGER PRIMARY KEY CHECK(singleton=1),request TEXT NOT NULL,state TEXT NOT NULL);
        CREATE TABLE captures(id INTEGER PRIMARY KEY,capture_id TEXT NOT NULL UNIQUE,manifest TEXT NOT NULL,extraction_id TEXT,extraction TEXT,ready INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE policies(id INTEGER PRIMARY KEY,hash BLOB NOT NULL UNIQUE,content TEXT NOT NULL);
        CREATE TABLE lexicons(id INTEGER PRIMARY KEY,source_hash BLOB NOT NULL,policy_id INTEGER NOT NULL REFERENCES policies(id),
          result TEXT NOT NULL,commitment BLOB NOT NULL,source_bytes INTEGER NOT NULL,status TEXT NOT NULL,complete INTEGER NOT NULL,
          units INTEGER NOT NULL,words INTEGER NOT NULL,ready INTEGER NOT NULL DEFAULT 0,UNIQUE(source_hash,policy_id));
        CREATE TABLE terms(id INTEGER PRIMARY KEY,hash BLOB NOT NULL UNIQUE,term TEXT NOT NULL);
        CREATE TABLE spellings(id INTEGER PRIMARY KEY,term_id INTEGER NOT NULL REFERENCES terms(id),text TEXT NOT NULL,UNIQUE(term_id,text));
        CREATE TABLE words(lexicon_id INTEGER NOT NULL REFERENCES lexicons(id),ordinal INTEGER NOT NULL,term_id INTEGER NOT NULL REFERENCES terms(id),
          spelling_id INTEGER NOT NULL REFERENCES spellings(id),byte_start INTEGER NOT NULL,byte_length INTEGER NOT NULL,
          utf16_start INTEGER NOT NULL,utf16_length INTEGER NOT NULL,start_line INTEGER NOT NULL,start_column INTEGER NOT NULL,
          PRIMARY KEY(lexicon_id,ordinal)) WITHOUT ROWID;
        CREATE INDEX words_by_term ON words(term_id,lexicon_id,ordinal);
        CREATE TABLE lexicon_terms(lexicon_id INTEGER NOT NULL REFERENCES lexicons(id),term_id INTEGER NOT NULL REFERENCES terms(id),occurrences INTEGER NOT NULL,
          PRIMARY KEY(lexicon_id,term_id)) WITHOUT ROWID;
        CREATE TABLE entries(capture_id INTEGER NOT NULL REFERENCES captures(id),ordinal INTEGER NOT NULL,entry_id TEXT NOT NULL,entry_content TEXT NOT NULL,
          document_id TEXT,path TEXT,path_bytes TEXT,source_hash BLOB,source_bytes INTEGER,status TEXT NOT NULL,lexicon_id INTEGER REFERENCES lexicons(id),
          PRIMARY KEY(capture_id,ordinal),UNIQUE(capture_id,entry_id));
        CREATE INDEX entries_by_lexicon ON entries(capture_id,lexicon_id);
        CREATE INDEX entries_by_source ON entries(capture_id,source_hash,source_bytes);
        """;

    private sealed class Builder : IDisposable
    {
        private readonly SqliteConnection db;
        private readonly SourceSqliteCancellation? cancellation;
        private readonly CompactSourceIndexBuildOptions options;
        private readonly string stage;
        private readonly CancellationToken ct;
        private long totalEntries, totalUnits, totalWords, completedCaptures, completedLexicons;
        public Builder(string path, CompactSourceIndexBuildOptions limits, string request, bool resume, CancellationToken token)
        {
            stage = path; options = limits; ct = token;
            if (!resume) { using var reserve = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            else if (!File.Exists(stage)) throw new FileNotFoundException("The explicit staging database does not exist.", stage);
            db = new(new SqliteConnectionStringBuilder { DataSource = stage, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            try
            {
                cancellation = new SourceSqliteCancellation(db, token);
                if (resume)
                {
                    VerifyFormat(db, allowBuilding: true);
                    using var prior = Command(db, "SELECT request FROM build WHERE singleton=1");
                    if ((string?)prior.ExecuteScalar() != request) throw new InvalidDataException("Staging request differs from the exact pinned build inputs/options.");
                }
                using (var configure = Command(db, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-16384; PRAGMA temp_store=MEMORY;")) configure.ExecuteNonQuery();
                using (var size = Command(db, "PRAGMA page_size"))
                using (var limit = Command(db, "PRAGMA max_page_count=" + (options.MaximumDatabaseBytes / (long)size.ExecuteScalar()!).ToString(CultureInfo.InvariantCulture))) limit.ExecuteScalar();
                if (!resume)
                {
                    using var tx = db.BeginTransaction(); using var schema = Command(db, SchemaSql, tx); schema.ExecuteNonQuery();
                    using var add = Command(db, "INSERT INTO build VALUES(1,$request,'building')", tx, ("$request", request)); add.ExecuteNonQuery(); tx.Commit();
                }
                using var totals = Command(db, "SELECT (SELECT COUNT(*) FROM entries),(SELECT COUNT(*) FROM captures WHERE ready=1),COUNT(*),COALESCE(SUM(units),0),COALESCE(SUM(words),0) FROM lexicons WHERE ready=1");
                using var reader = totals.ExecuteReader(); reader.Read(); totalEntries = reader.GetInt64(0); completedCaptures = reader.GetInt64(1); completedLexicons = reader.GetInt64(2); totalUnits = reader.GetInt64(3); totalWords = reader.GetInt64(4);
            }
            catch { cancellation?.Dispose(); db.Dispose(); throw; }
        }
        public void Dispose() { cancellation?.Dispose(); db.Dispose(); }
        private void Budget()
        {
            ct.ThrowIfCancellationRequested(); EnsureFree(Path.GetDirectoryName(stage)!, options.MinimumFreeBytes);
            if (new FileInfo(stage).Length > options.MaximumDatabaseBytes || totalEntries > options.MaximumEntries || totalUnits > options.MaximumUnits)
                throw new IOException("Compact index resource budget exceeded; staged work is retained and not published.");
        }
        private void Report(string phase, IProgress<CompactSourceIndexBuildProgress>? progress) =>
            progress?.Report(new(phase, stage, completedCaptures, totalEntries, completedLexicons, totalUnits, totalWords, new FileInfo(stage).Length));

        public async Task CaptureAsync(GitSourceCapture store, CompactSourceIndexInput input,
            IProgress<CompactSourceIndexBuildProgress>? progress, CancellationToken token)
        {
            Budget(); var receipt = store.Read(input.CaptureId); var manifest = receipt.Manifest;
            if (manifest.Schema != "filemapper.source-capture/1" || CanonicalJson.Sha256(manifest.Policy) != manifest.PolicyHash ||
                manifest.State is not ("sealed-complete-for-policy" or "sealed-partial")) throw new InvalidDataException("Unsupported capture commitment or state.");
            ValidateCoverage(manifest.Coverage);
            if (manifest.Coverage.TreeEntries > options.MaximumEntries - totalEntries) // Existing resumed entries are removed below.
            {
                using var oldCount = Command(db, "SELECT COUNT(*) FROM entries e JOIN captures c ON c.id=e.capture_id WHERE c.capture_id=$c", null, ("$c", input.CaptureId));
                if (manifest.Coverage.TreeEntries > options.MaximumEntries - totalEntries + (long)oldCount.ExecuteScalar()!) throw new IOException("Pinned capture exceeds the entry budget.");
            }
            await using (var rawTree = await VerifiedBlobAsync(store.Blobs, manifest.RawTree, token)) { }
            CompactExtractionManifest? extraction = null;
            if (input.ExtractionId is { } extractionId)
            {
                extraction = await ReadManifestAsync<CompactExtractionManifest>(Path.Combine(store.RootDirectory, "extractions", extractionId + ".json"), extractionId, token);
                if (extraction.Schema != "filemapper.source-extraction/1" || extraction.CaptureId != input.CaptureId ||
                    extraction.State is not ("sealed-complete-for-contract" or "sealed-partial") ||
                    CanonicalJson.Serialize(extraction.Policy) != CanonicalJson.Serialize(SourceLexicon.Describe(extraction.Policy.Options)))
                    throw new InvalidDataException("Extraction identity, state, or exact lexer runtime contract differs.");
            }
            var canonical = CanonicalJson.Serialize(manifest); var extractionJson = extraction is null ? null : CanonicalJson.Serialize(extraction);
            long captureKey;
            using (var tx = db.BeginTransaction())
            {
                using var old = Command(db, "SELECT id,manifest,extraction_id,extraction,ready FROM captures WHERE capture_id=$c", tx, ("$c", input.CaptureId));
                using (var r = old.ExecuteReader())
                {
                    if (r.Read())
                    {
                        captureKey = r.GetInt64(0);
                        if (r.GetString(1) != canonical || NullableString(r, 2) != input.ExtractionId || NullableString(r, 3) != extractionJson)
                            throw new InvalidDataException("Staged capture identity conflicts with the pinned manifests.");
                        if (r.GetInt64(4) == 1) completedCaptures--;
                    }
                    else captureKey = 0;
                }
                if (captureKey == 0)
                {
                    using var add = Command(db, "INSERT INTO captures(capture_id,manifest,extraction_id,extraction) VALUES($c,$m,$e,$x) RETURNING id", tx,
                        ("$c", input.CaptureId), ("$m", canonical), ("$e", input.ExtractionId), ("$x", extractionJson)); captureKey = (long)add.ExecuteScalar()!;
                }
                using var remove = Command(db, "DELETE FROM entries WHERE capture_id=$c", tx, ("$c", captureKey)); totalEntries -= remove.ExecuteNonQuery();
                using var pending = Command(db, "UPDATE captures SET ready=0 WHERE id=$c", tx, ("$c", captureKey)); pending.ExecuteNonQuery(); tx.Commit();
            }
            await using var captureBytes = await VerifiedBlobAsync(store.Blobs, manifest.Entries, token);
            await using var extractionBytes = extraction is null ? null : await VerifiedBlobAsync(store.Blobs, extraction.Entries, token);
            await using var extracted = extractionBytes is null ? null : ReadRecordsAsync<CompactExtractionEntry>(extractionBytes, token).GetAsyncEnumerator(token);
            var pendingEntries = new List<(CapturedSourceEntry Entry, string Canonical, long? Lexicon)>(256); long pendingBytes = 0;
            long count = 0, captured = 0, missing = 0, excluded = 0, submodules = 0, errors = 0, associatedBytes = 0;
            long complete = 0, incomplete = 0, units = 0, words = 0;
            await foreach (var entry in ReadRecordsAsync<CapturedSourceEntry>(captureBytes, token))
            {
                token.ThrowIfCancellationRequested(); ValidateEntry(manifest, entry);
                if (++count > manifest.Coverage.TreeEntries) throw new InvalidDataException("Capture entries exceed their committed count.");
                CompactExtractionEntry? expected = null;
                if (extracted is not null)
                {
                    if (!await extracted.MoveNextAsync()) throw new InvalidDataException("Extraction entries end before capture entries."); expected = extracted.Current;
                    if (expected.EntryId != entry.Entry.EntryId || expected.DocumentId != entry.DocumentId || expected.SourceSha256 != entry.Blob?.Sha256 || expected.CaptureStatus != entry.Status)
                        throw new InvalidDataException("Extraction entries do not match capture order and document identities.");
                }
                long? lexicon = null;
                switch (entry.Status)
                {
                    case "captured":
                        captured++; associatedBytes = checked(associatedBytes + entry.Blob!.ByteLength);
                        if (extraction is null) { await using var proof = await VerifiedBlobAsync(store.Blobs, entry.Blob, token); }
                        else
                        {
                            var policyHash = extraction.Policy.PolicyHash;
                            var metadataPath = Path.Combine(store.RootDirectory, "lexicons", policyHash, entry.Blob.Sha256 + ".json");
                            var result = await ReadBoundedJsonAsync<SourceLexiconResult>(metadataPath, 65536, token);
                            if (result.SourceSha256 != entry.Blob.Sha256 || result.SourceBytes != entry.Blob.ByteLength || CanonicalJson.Serialize(result.Policy) != CanonicalJson.Serialize(extraction.Policy) ||
                                expected!.LexicalStatus != result.Status || expected.OutputSha256 != result.OutputSha256 || expected.CanonicalNdjsonSha256 != result.CanonicalNdjsonSha256 ||
                                expected.Occurrences != result.Occurrences || expected.WordOccurrences != result.WordOccurrences || expected.Complete != result.Complete)
                                throw new InvalidDataException("Lexicon metadata differs from the sealed extraction entry.");
                            // Resolve immutable artifacts within this corpus, never through a supplied metadata path.
                            if (result.OutputPath is not null) result = result with { OutputPath = Path.Combine(store.RootDirectory, "lexicons", policyHash, entry.Blob.Sha256 + ".ndjson.gz") };
                            lexicon = await ImportAsync(store.Blobs, result, progress, token);
                            if (result.Complete) complete++; else incomplete++;
                            units = checked(units + result.Occurrences); words = checked(words + result.WordOccurrences);
                        }
                        break;
                    case "unavailable": missing++; break;
                    case "excluded-size-policy": case "excluded-capture-budget": excluded++; break;
                    case "submodule-reference": submodules++; break;
                    case "unsupported-object-type": errors++; break;
                    default: throw new InvalidDataException("Unknown capture entry status.");
                }
                if (entry.Status != "captured" && expected is not null && (expected.LexicalStatus != "not-captured" || expected.OutputSha256 is not null || expected.CanonicalNdjsonSha256 is not null || expected.Occurrences != 0 || expected.WordOccurrences != 0 || expected.Complete))
                    throw new InvalidDataException("An unavailable entry cannot claim lexical observations.");
                var entryJson = CanonicalJson.Serialize(entry); pendingEntries.Add((entry, entryJson, lexicon)); pendingBytes += Encoding.UTF8.GetByteCount(entryJson);
                if (pendingEntries.Count == 256 || pendingBytes >= 4 * 1024 * 1024)
                { AppendEntries(captureKey, count - pendingEntries.Count, pendingEntries); pendingEntries.Clear(); pendingBytes = 0; Budget(); Report("associating", progress); }
            }
            if (pendingEntries.Count != 0) AppendEntries(captureKey, count - pendingEntries.Count, pendingEntries);
            if (extracted is not null && await extracted.MoveNextAsync()) throw new InvalidDataException("Extraction entries exceed capture entries.");
            var coverage = manifest.Coverage;
            using var unique = Command(db, "SELECT COUNT(*),COALESCE(SUM(source_bytes),0) FROM (SELECT source_hash,source_bytes FROM entries WHERE capture_id=$c AND source_hash IS NOT NULL GROUP BY source_hash,source_bytes)", null, ("$c", captureKey));
            long uniqueBlobs, uniqueBytes;
            using (var r = unique.ExecuteReader()) { r.Read(); uniqueBlobs = r.GetInt64(0); uniqueBytes = r.GetInt64(1); }
            if (count != coverage.TreeEntries || captured != coverage.CapturedEntries || missing != coverage.MissingEntries || excluded != coverage.PolicyExcludedEntries ||
                submodules != coverage.SubmoduleReferences || errors != coverage.ErrorEntries || associatedBytes != coverage.AssociatedSourceBytes || uniqueBlobs != coverage.UniqueRetainedBlobs || uniqueBytes != coverage.UniqueRetainedBytes)
                throw new InvalidDataException("Capture coverage differs from the complete retained entry stream.");
            if (extraction is not null)
            {
                using var distinct = Command(db, "SELECT COUNT(DISTINCT lexicon_id) FROM entries WHERE capture_id=$c", null, ("$c", captureKey));
                var actual = new CompactExtractionCoverage(count, captured, complete, incomplete, count - captured, units, words, (long)distinct.ExecuteScalar()!);
                if (actual != extraction.Coverage || extraction.State != (incomplete == 0 && manifest.State == "sealed-complete-for-policy" ? "sealed-complete-for-contract" : "sealed-partial"))
                    throw new InvalidDataException("Extraction coverage or completeness differs from verified documents.");
            }
            using var ready = Command(db, "UPDATE captures SET ready=1 WHERE id=$c", null, ("$c", captureKey)); ready.ExecuteNonQuery(); completedCaptures++;
            Budget(); Report("capture-complete", progress);
        }

        private void AppendEntries(long capture, long ordinal, IReadOnlyList<(CapturedSourceEntry Entry, string Canonical, long? Lexicon)> values)
        {
            using var tx = db.BeginTransaction();
            using var add = Command(db, "INSERT INTO entries VALUES($c,$o,$e,$content,$d,$path,$raw,$s,$bytes,$status,$l)", tx);
            foreach (var (entry, canonical, lexicon) in values)
            {
                ct.ThrowIfCancellationRequested();
                Set(add, ("$c", capture), ("$o", ordinal++), ("$e", entry.Entry.EntryId), ("$content", canonical), ("$d", entry.DocumentId),
                    ("$path", entry.Entry.RepositoryPath), ("$raw", entry.Entry.RepositoryPathBytes), ("$s", entry.Blob is null ? null : Convert.FromHexString(entry.Blob.Sha256)),
                    ("$bytes", entry.Blob?.ByteLength), ("$status", entry.Status), ("$l", lexicon)); add.ExecuteNonQuery();
            }
            tx.Commit(); totalEntries += values.Count;
        }

        private readonly HashSet<long> verifiedLexicons = [];
        private async Task<long> ImportAsync(SourceBlobStore store, SourceLexiconResult result,
            IProgress<CompactSourceIndexBuildProgress>? progress, CancellationToken token)
        {
            ValidateResult(result); var policyText = CanonicalJson.Serialize(result.Policy); long policy;
            using (var add = Command(db, "INSERT INTO policies(hash,content) VALUES($h,$c) ON CONFLICT(hash) DO NOTHING", null, ("$h", Convert.FromHexString(result.Policy.PolicyHash)), ("$c", policyText))) add.ExecuteNonQuery();
            using (var find = Command(db, "SELECT id,content FROM policies WHERE hash=$h", null, ("$h", Convert.FromHexString(result.Policy.PolicyHash))))
            using (var r = find.ExecuteReader()) { r.Read(); policy = r.GetInt64(0); if (r.GetString(1) != policyText) throw new InvalidDataException("Policy identity conflict."); }
            var content = CanonicalJson.Serialize(result); var commitment = SHA256.HashData(Encoding.UTF8.GetBytes(LexiconCommitment(result)));
            long key = 0; bool ready = false;
            using (var find = Command(db, "SELECT id,commitment,ready FROM lexicons WHERE source_hash=$s AND policy_id=$p", null, ("$s", Convert.FromHexString(result.SourceSha256)), ("$p", policy)))
            using (var r = find.ExecuteReader()) if (r.Read()) { key = r.GetInt64(0); ready = r.GetInt64(2) == 1; if (!((byte[])r.GetValue(1)).AsSpan().SequenceEqual(commitment)) throw new InvalidDataException("Lexicon identity conflict."); }
            if (ready && verifiedLexicons.Contains(key)) return key;
            if (!ready && result.Occurrences > options.MaximumUnits - totalUnits) throw new IOException("Lexical unit budget exceeded before import.");
            await using var source = await VerifiedBlobAsync(store, new(result.SourceSha256, result.SourceBytes, ""), token);
            if (key == 0)
            {
                using var add = Command(db, "INSERT INTO lexicons(source_hash,policy_id,result,commitment,source_bytes,status,complete,units,words) VALUES($s,$p,$r,$c,$b,$status,$complete,$u,$w) RETURNING id", null,
                    ("$s", Convert.FromHexString(result.SourceSha256)), ("$p", policy), ("$r", content), ("$c", commitment), ("$b", result.SourceBytes), ("$status", result.Status),
                    ("$complete", result.Complete ? 1 : 0), ("$u", result.Occurrences), ("$w", result.WordOccurrences)); key = (long)add.ExecuteScalar()!;
            }
            if (!ready)
            {
                using var tx = db.BeginTransaction(); using var reset = Command(db, "DELETE FROM words WHERE lexicon_id=$l; DELETE FROM lexicon_terms WHERE lexicon_id=$l", tx, ("$l", key)); reset.ExecuteNonQuery(); tx.Commit();
            }
            if (result.OutputPath is not null)
            {
                await using var artifact = OpenFile(result.OutputPath);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(artifact, token)) != result.OutputSha256) throw new InvalidDataException("Stored lexical artifact hash mismatch.");
                artifact.Position = 0;
                using (var decoded = new GZipStream(artifact, CompressionMode.Decompress, leaveOpen: true))
                    if (await BoundedHashAsync(decoded, checked(result.Occurrences * 524289), token) != result.CanonicalNdjsonSha256) throw new InvalidDataException("Canonical lexical artifact hash mismatch.");
                using var canonicalDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var sourceDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var validation = new UnitValidation(result, sourceDigest, source); var buffer = new List<SourceLexicalOccurrence>(4096); long bufferBytes = 0;
                await foreach (var unit in SourceLexicon.ReadAsync(result.OutputPath, token))
                {
                    validation.Add(unit); canonicalDigest.AppendData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(unit) + "\n"));
                    if (!ready && unit.TermId is not null) { buffer.Add(unit); bufferBytes += 512L + 2L * (unit.Text!.Length + unit.NormalizedTerm!.Length); }
                    if (buffer.Count == 4096 || bufferBytes >= 4 * 1024 * 1024) { AppendWords(key, buffer); buffer.Clear(); bufferBytes = 0; Budget(); Report("indexing-words", progress); }
                }
                validation.Finish();
                if (Convert.ToHexStringLower(canonicalDigest.GetHashAndReset()) != result.CanonicalNdjsonSha256) throw new InvalidDataException("Lexical records are noncanonical or differ from their commitment.");
                if (buffer.Count != 0) AppendWords(key, buffer);
            }
            using (var count = Command(db, "SELECT COUNT(*) FROM words WHERE lexicon_id=$l", null, ("$l", key)))
                if ((long)count.ExecuteScalar()! != result.WordOccurrences) throw new InvalidDataException("Compact word count differs from verified lexical evidence.");
            if (!ready)
            {
                using var done = Command(db, "UPDATE lexicons SET ready=1 WHERE id=$l", null, ("$l", key)); done.ExecuteNonQuery();
                completedLexicons++; totalUnits += result.Occurrences; totalWords += result.WordOccurrences;
            }
            if (verifiedLexicons.Count >= 8192) verifiedLexicons.Clear(); verifiedLexicons.Add(key);
            Budget(); return key;
        }

        private readonly Dictionary<string, long> termKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<(long, string), long> spellingKeys = [];
        private long spellingCacheCharacters;
        private void AppendWords(long lexicon, IReadOnlyList<SourceLexicalOccurrence> values)
        {
            using var tx = db.BeginTransaction();
            using var termAdd = Command(db, "INSERT INTO terms(hash,term) VALUES($h,$t) ON CONFLICT(hash) DO NOTHING", tx);
            using var termFind = Command(db, "SELECT id,term FROM terms WHERE hash=$h", tx);
            using var spellingAdd = Command(db, "INSERT INTO spellings(term_id,text) VALUES($t,$s) ON CONFLICT(term_id,text) DO NOTHING", tx);
            using var spellingFind = Command(db, "SELECT id FROM spellings WHERE term_id=$t AND text=$s", tx);
            using var wordAdd = Command(db, "INSERT INTO words VALUES($l,$o,$t,$s,$bs,$bl,$us,$ul,$line,$col)", tx);
            using var aggregate = Command(db, "INSERT INTO lexicon_terms VALUES($l,$t,1) ON CONFLICT(lexicon_id,term_id) DO UPDATE SET occurrences=occurrences+1", tx);
            try
            {
                foreach (var unit in values)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!termKeys.TryGetValue(unit.TermId!, out var term))
                    {
                        var hash = Convert.FromHexString(unit.TermId![12..]); Set(termAdd, ("$h", hash), ("$t", unit.NormalizedTerm)); termAdd.ExecuteNonQuery();
                        Set(termFind, ("$h", hash)); using (var r = termFind.ExecuteReader()) { r.Read(); term = r.GetInt64(0); if (r.GetString(1) != unit.NormalizedTerm) throw new InvalidDataException("Term identity conflict."); }
                        if (termKeys.Count >= 8192) termKeys.Clear(); termKeys.Add(unit.TermId, term);
                    }
                    if (!spellingKeys.TryGetValue((term, unit.Text!), out var spelling))
                    {
                        Set(spellingAdd, ("$t", term), ("$s", unit.Text)); spellingAdd.ExecuteNonQuery(); Set(spellingFind, ("$t", term), ("$s", unit.Text)); spelling = (long)spellingFind.ExecuteScalar()!;
                        if (spellingKeys.Count >= 8192 || spellingCacheCharacters + unit.Text!.Length > 2 * 1024 * 1024) { spellingKeys.Clear(); spellingCacheCharacters = 0; }
                        spellingKeys.Add((term, unit.Text!), spelling); spellingCacheCharacters += unit.Text!.Length;
                    }
                    Set(wordAdd, ("$l", lexicon), ("$o", unit.Ordinal), ("$t", term), ("$s", spelling), ("$bs", unit.ByteStart), ("$bl", unit.ByteLength),
                        ("$us", unit.Utf16Start), ("$ul", unit.Utf16Length), ("$line", unit.StartLine), ("$col", unit.StartColumn)); wordAdd.ExecuteNonQuery();
                    Set(aggregate, ("$l", lexicon), ("$t", term)); aggregate.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { termKeys.Clear(); spellingKeys.Clear(); spellingCacheCharacters = 0; throw; }
        }
        public void Finish(int expectedCaptures)
        {
            Budget();
            using var check = Command(db, "SELECT COUNT(*) FROM captures WHERE ready=1");
            if ((long)check.ExecuteScalar()! != expectedCaptures) throw new InvalidDataException("Compact build did not complete every pinned capture.");
            using var integrity = Command(db, "PRAGMA quick_check");
            if ((string?)integrity.ExecuteScalar() != "ok") throw new InvalidDataException("Compact index integrity check failed.");
            using var foreign = Command(db, "PRAGMA foreign_key_check"); using (var r = foreign.ExecuteReader()) if (r.Read()) throw new InvalidDataException("Compact index foreign key check failed.");
            using var done = Command(db, "UPDATE build SET state='complete' WHERE singleton=1"); done.ExecuteNonQuery();
        }
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

    private static SqliteConnection OpenReader(string path)
    {
        CheckPath(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { db.Open(); using var settings = Command(db, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;"); settings.ExecuteNonQuery(); VerifyFormat(db, false); return db; }
        catch { db.Dispose(); throw; }
    }
    private static void VerifyFormat(SqliteConnection db, bool allowBuilding)
    {
        using var app = Command(db, "PRAGMA application_id"); using var version = Command(db, "PRAGMA user_version");
        if ((long)app.ExecuteScalar()! != ApplicationId || (long)version.ExecuteScalar()! != Version) throw new InvalidDataException("This database is not a supported compact source projection.");
        using var state = Command(db, "SELECT state FROM build WHERE singleton=1");
        var value = state.ExecuteScalar() as string;
        if (value != "complete" && !(allowBuilding && value == "building")) throw new InvalidDataException("The compact source projection is not completely published.");
    }
    private static (long Key, SourceCaptureManifest Manifest, string? Extraction) ReadCapture(SqliteConnection db, SqliteTransaction tx, string captureId)
    {
        using var query = Command(db, "SELECT id,manifest,extraction_id FROM captures WHERE capture_id=$c AND ready=1", tx, ("$c", captureId)); using var r = query.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Source capture is not included in this compact projection.");
        return (r.GetInt64(0), Deserialize<SourceCaptureManifest>(r.GetString(1)), NullableString(r, 2));
    }
    private static async Task<FileStream> VerifiedBlobAsync(SourceBlobStore store, SourceBlobDescriptor blob, CancellationToken ct)
    {
        Hash(blob.Sha256); if (blob.ByteLength < 0 || blob.ByteLength > store.MaxDocumentBytes) throw new InvalidDataException("Retained manifest/source exceeds its declared bound.");
        var stream = store.OpenRead(blob.Sha256);
        try
        {
            if (stream.Length != blob.ByteLength || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != blob.Sha256) throw new InvalidDataException("Retained source or manifest byte commitment mismatch.");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private static async Task<T> ReadBoundedJsonAsync<T>(string path, long maximumBytes, CancellationToken ct)
    {
        await using var stream = OpenFile(path); if (stream.Length > maximumBytes) throw new InvalidDataException("JSON metadata exceeds its bound.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Null JSON metadata.");
    }
    private static async Task<T> ReadManifestAsync<T>(string path, string expectedHash, CancellationToken ct)
    {
        var value = await ReadBoundedJsonAsync<T>(path, 4 * 1024 * 1024, ct);
        if (CanonicalJson.Sha256(value) != expectedHash) throw new InvalidDataException("Sealed manifest identity mismatch.");
        return value;
    }
    private static async IAsyncEnumerable<T> ReadRecordsAsync<T>(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        var buffer = new char[8192]; var line = new StringBuilder();
        while (true)
        {
            var length = await reader.ReadAsync(buffer, ct); if (length == 0) break;
            for (var i = 0; i < length; i++)
            {
                if (buffer[i] != '\n') { if (line.Length >= 4 * 1024 * 1024) throw new InvalidDataException("Manifest entry exceeds the bounded record size."); line.Append(buffer[i]); continue; }
                ct.ThrowIfCancellationRequested(); var text = line.ToString(); line.Clear(); var value = Deserialize<T>(text);
                if (CanonicalJson.Serialize(value) != text) throw new InvalidDataException("Manifest entry is noncanonical or contains unrecognized fields.");
                yield return value;
            }
        }
        if (line.Length != 0) throw new InvalidDataException("Manifest entries end without the committed newline.");
    }
    private static async Task<string> BoundedHashAsync(Stream stream, long maximumBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct); if (read == 0) break; total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("Decompressed lexical output exceeds the committed record bound."); hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static FileStream OpenFile(string path) { CheckPath(path); return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
    private static void EnsureFree(string parent, long minimum)
    { var drive = new DriveInfo(Path.GetPathRoot(parent)!); if (drive.AvailableFreeSpace < minimum) throw new IOException("Compact build stopped at its reserved free-space floor."); }
    private static void CheckPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("Alternate-stream compact index paths are not accepted.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point compact index paths are not accepted.");
    }
    private static T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Json) ?? throw new InvalidDataException("Null source metadata.");
    private static string Hex(SqliteDataReader r, int ordinal) => Convert.ToHexStringLower((byte[])r.GetValue(ordinal));
    private static string? NullableString(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    private static void Hash(string value) { if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("A lowercase SHA-256 value is required."); }
    private static void Page(int offset, int limit) { if (offset < 0 || limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and limit 1–500."); }
    private static string Like(string? text) => "%" + (text ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    private T Read<T>(CancellationToken token, Func<SqliteConnection, T> body)
    {
        using var db = OpenReader(DatabasePath); using var cancellation = new SourceSqliteCancellation(db, token);
        return cancellation.Run(() => body(db));
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string, object?)[] values)
    { var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx; Set(cmd, values); return cmd; }
    private static void Set(SqliteCommand cmd, params (string, object?)[] values)
    { foreach (var (name, value) in values) { if (cmd.Parameters.Contains(name)) cmd.Parameters[name].Value = value ?? DBNull.Value; else cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); } }
}
