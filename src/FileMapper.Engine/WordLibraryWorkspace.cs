using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed record WordLibraryPin(string Path, string Sha256);
public sealed record WordLibraryCatalog(string Schema, DateTimeOffset ConnectedAt, WordLibraryPin[] Editions);
public sealed record WordLibraryCapture(string RepositoryId, string CanonicalUri, string Commit, string? FinishedAt, string? FinishedUtcTicks, string? CaptureId, string? State);
public sealed record WordLibraryEdition(string Sha256, WordLibraryCapture Capture, JsonElement Evidence, JsonElement HeadEvidence, JsonElement EditionId, long Documents, long Excluded, long Words);

// A catalog is an explicit, privileged selection. Read requests never accept filesystem paths.
public sealed class WordLibraryWorkspace : IDisposable
{
    const string Schema = "filemapper.word-library-catalog/1";
    readonly string catalogPath;
    readonly object gate = new();
    sealed class Session(WordLibraryCatalog catalog, List<FileStream> handles, List<WordLibraryEdition> editions)
    {
        public WordLibraryCatalog Catalog = catalog; public List<FileStream> Handles = handles; public List<WordLibraryEdition> Editions = editions;
        public int Leases; public bool Retired;
        public void Close() { foreach (var h in Handles) h.Dispose(); Handles.Clear(); }
    }
    readonly CancellationTokenSource lifetime = new();
    Session? session; bool loading, disposed; string? failure;
    long bytesDone, bytesTotal; int editionsDone, editionsTotal;
    public WordLibraryWorkspace(string dataDirectory) { catalogPath = Path.Combine(dataDirectory, "word-library-catalog.json"); }
    public object Status() { lock (gate) { EnsureLoaded(); return new { schema = Schema, connected = session != null, loading, failure, connectedAt = session?.Catalog.ConnectedAt, total = session?.Editions.Count ?? 0, editionsDone, editionsTotal, bytesDone = Interlocked.Read(ref bytesDone), bytesTotal = Interlocked.Read(ref bytesTotal) }; } }
    public object Page(int offset, int limit) { lock (gate) { EnsureLoaded(); Ready(); offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 100); var list = session?.Editions ?? []; return new { total = list.Count, offset, items = list.Skip(offset).Take(limit).ToArray() }; } }
    void Retire(Session? old) { if (old == null) return; old.Retired = true; if (old.Leases == 0) old.Close(); }
    Session Acquire() { lock (gate) { EnsureLoaded(); Ready(); if (session == null) throw new InvalidOperationException("Connect a public edition catalog first."); session.Leases++; return session; } }
    void Release(Session s) { lock (gate) { s.Leases--; if (s.Retired && s.Leases == 0) s.Close(); } }
    void Begin() { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (loading) throw new InvalidOperationException("Catalog verification is already running."); loading = true; failure = null; editionsDone = editionsTotal = 0; Interlocked.Exchange(ref bytesDone, 0); Interlocked.Exchange(ref bytesTotal, 0); } }
    void Publish(WordLibraryCatalog c, (List<FileStream> Guards, List<WordLibraryEdition> Editions) opened) { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); var old = session; session = new Session(c, opened.Guards, opened.Editions); Retire(old); } }
    public async Task ConnectAsync(string root, CancellationToken ct)
    {
        Begin(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        try {
        root = SourceHarvestConnectionStore.NormalizePath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Edition directory is unavailable.");
        var pins = new List<WordLibraryPin>();
        foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); SourceHarvestConnectionStore.NormalizePath(dir);
            var receipt = Path.Combine(dir, "complete.json"); var db = Path.Combine(dir, "edition.db");
            SourceHarvestConnectionStore.NormalizePath(receipt); SourceHarvestConnectionStore.NormalizePath(db);
            if (!File.Exists(receipt) || !File.Exists(db)) continue;
            if (new FileInfo(receipt).Length > 65536) throw new InvalidDataException("Edition receipt is too large.");
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt, ct));
            var sha = json.RootElement.GetProperty("sha256").GetString() ?? "";
            pins.Add(new(db, sha));
            if (pins.Count > 1000) throw new ArgumentException("Catalog limit is 1000 editions.");
        }
        if (pins.Count == 0) throw new ArgumentException("No completed editions were found in immediate child directories.");
        var candidate = new WordLibraryCatalog(Schema, DateTimeOffset.UtcNow, pins.ToArray());
        {
            var opened = await Task.Run(() => Open(candidate, ct), ct);
            try
            {
                var tmp = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                try {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(candidate));
                    if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
                    SourceHarvestConnectionStore.NormalizePath(tmp);
                    using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); }
                    var writerPath = catalogPath + ".writer.lock"; SourceHarvestConnectionStore.NormalizePath(writerPath);
                    using var writer = new FileStream(writerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    SourceHarvestConnectionStore.NormalizePath(catalogPath);
                    lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested(); File.Move(tmp, catalogPath, true); Publish(candidate, opened); }
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            catch { foreach (var g in opened.Guards) g.Dispose(); throw; }
        }
        } finally { lock (gate) loading = false; }
    }
    public object Search(string word, long offset, int limit, CancellationToken ct) => SearchKind("word", word, offset, limit, ct);
    public object SearchKind(string kind, string text, long offset, int limit, CancellationToken ct, string mode = "occurrences")
    {
        if (mode is not ("occurrences" or "compare")) throw new ArgumentException("Unsupported search mode.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) throw new ArgumentException("Enter an exact value of up to 256 characters.");
        if (kind is not ("word" or "variable" or "command" or "operator" or "path")) throw new ArgumentException("Unsupported observation kind.");
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var term = "source-term:" + CanonicalJson.Sha256(new { Model = SourceLexicon.TermContractId, NormalizedText = normalized });
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be nonnegative."); limit = Math.Clamp(limit, 1, 100);
        var selected = Acquire(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        try {
            var rows = new List<object>(); long total = 0, occurrenceTotal = 0, matchingEditions = 0;
            for (int i = 0; i < selected.Editions.Count; i++) {
                ct.ThrowIfCancellationRequested(); var pin = selected.Catalog.Editions[i]; CheckPath(pin.Path);
                using var db = Db(pin.Path); var handle = db.Handle;
                using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
                SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => ct.IsCancellationRequested ? 1 : 0, null);
                try {
                    using var cmd = db.CreateCommand();
                    var where = kind == "word" ? "w.term_id=$value" : kind == "path" ? "w.component_base64=$value" : "w.kind=$kind AND w.text=$value";
                    var table = kind == "word" ? "words w" : kind == "path" ? "path_shapes w" : "lexical_shapes w";
                    cmd.Parameters.AddWithValue("$value", kind == "word" ? term : kind == "path" ? Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) : text);
                    if (kind is "variable" or "command" or "operator") cmd.Parameters.AddWithValue("$kind", kind switch { "variable" => "variable-prefix-candidate", "command" => "line-initial-shell-word-candidate", _ => "operator-character-run" });
                    cmd.CommandText = $"SELECT count(*) FROM {table} WHERE {where}";
                    long count = (long)cmd.ExecuteScalar()!; occurrenceTotal = checked(occurrenceTotal + count); if (count > 0) matchingEditions++;
                    long pageCount = mode == "compare" ? (count > 0 ? 1 : 0) : count;
                    long skip = offset > total ? offset - total : 0; total = checked(total + pageCount);
                    if (skip >= pageCount || rows.Count >= limit) continue;
                    var cols = kind == "word" ? "w.shape,w.document_id,w.path,w.path_bytes,w.source_sha256,w.occurrence_id,w.byte_start,w.byte_length,w.utf16_start,w.utf16_length,w.line,w.column_no,w.spelling,w.lexical_status,w.complete,NULL,NULL,NULL" : kind == "path" ? "w.shape,d.document_id,d.path,w.raw_path_base64,d.source_sha256,NULL,w.byte_start,w.byte_length,NULL,NULL,NULL,NULL,w.component_base64,w.evidence_level,d.complete,w.shape_id,w.entry_id,w.ordinal" : "w.shape,w.document_id,d.path,NULL,w.source_sha256,w.occurrence_id,w.byte_start,w.byte_length,w.utf16_start,w.utf16_length,w.line,w.column_no,w.text,w.evidence_level,d.complete,w.shape_id,w.entry_id,NULL";
                    var join = kind == "word" ? "" : " LEFT JOIN documents d ON d.entry_id=w.entry_id";
                    cmd.CommandText = $"SELECT {cols} FROM {table}{join} WHERE {where} ORDER BY w.rowid LIMIT $limit OFFSET $offset";
                    cmd.Parameters.AddWithValue("$limit", mode == "compare" ? 1 : limit - rows.Count); cmd.Parameters.AddWithValue("$offset", mode == "compare" ? 0 : skip);
                    using var r = cmd.ExecuteReader(); string? Str(int n) => r.IsDBNull(n) ? null : r.GetString(n); long? Num(int n) => r.IsDBNull(n) ? null : r.GetInt64(n);
                                        while (r.Read()) {
                        ct.ThrowIfCancellationRequested(); var documentId = Str(1); var occurrenceId = Str(5); var entryId = Str(16); var ordinal = Num(17);
                        var sourceObservationId = kind == "path" ? "path-observation:" + CanonicalJson.Sha256(new { EntryId = entryId, Ordinal = ordinal }) : occurrenceId;
                        var viewObservationId = "word-library-view:" + CanonicalJson.Sha256(new { EditionSha256 = pin.Sha256, Kind = kind, DocumentId = documentId, EntryId = entryId, SourceObservationId = sourceObservationId });
                        rows.Add(new { kind, editionSha256 = pin.Sha256, editionId = selected.Editions[i].EditionId, capture = selected.Editions[i].Capture, termId = kind == "word" ? term : null, shape = Str(0), documentId, path = Str(2), pathBytes = Str(3), sourceSha256 = Str(4), occurrenceId, sourceObservationId, viewObservationId, pathOrdinal = ordinal, byteStart = Num(6), byteLength = Num(7), utf16Start = Num(8), utf16Length = Num(9), line = Num(10), column = Num(11), spelling = kind == "path" ? text : Str(12), lexicalStatus = Str(13), complete = Num(14) is long complete ? complete == 1 : (bool?)null, shapeId = Str(15), entryId });
                    }
                } catch (SqliteException e) when (ct.IsCancellationRequested && e.SqliteErrorCode == 9) { throw new OperationCanceledException("Library search canceled.", e, ct); }
                finally { registration.Dispose(); SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
            }
            return new { kind, mode, termId = kind == "word" ? term : null, normalizedTerm = kind == "word" ? normalized : null, total, occurrenceTotal, matchingEditions, offset, items = rows };
        } finally { Release(selected); }
    }
    public object PracticePage(int offset, int limit, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(offset));
        var selected = Acquire(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            var items = new List<object>();
            for (var i = offset; i < Math.Min((long)selected.Editions.Count, (long)offset + limit); i++)
            {
                linked.Token.ThrowIfCancellationRequested(); var pin = selected.Catalog.Editions[i]; CheckPath(pin.Path);
                var reason = PracticeIneligibility(pin, linked.Token);
                items.Add(new { edition = selected.Editions[i], eligible = reason is null, reason });
            }
            return new { total = selected.Editions.Count, offset, items };
        }
        finally { Release(selected); }
    }
    internal static string? PracticeIneligibility(WordLibraryPin pin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(pin.Path).Length > 4L * 1024 * 1024 * 1024) return "Edition exceeds the 4 GiB computer limit.";
        using var db = Db(pin.Path); var handle = db.Handle!;
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
        try
        {
            using var c=db.CreateCommand(); c.CommandText="SELECT value FROM metadata WHERE key='forkFamilyStatus'";
            var raw=c.ExecuteScalar() as string;
            if (raw is null) return "Non-fork family evidence is missing.";
            using var value=JsonDocument.Parse(raw);
            return value.RootElement.GetString() == "non-fork-self" ? null : "Edition lacks established non-fork family eligibility.";
        }
        catch (SqliteException e) when (token.IsCancellationRequested && e.SqliteErrorCode == 9) { throw new OperationCanceledException("Eligibility check canceled.", e, token); }
        finally { registration.Dispose(); SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
    }
    // Keep the selected catalog session (and its file read guards) alive across reconnect.
    internal T WithPinnedEdition<T>(string sha256, Func<WordLibraryPin, WordLibraryEdition, CancellationToken, T> action, CancellationToken ct)
    {
        if (sha256 is null || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A lowercase SHA-256 edition pin is required.");
        var selected = Acquire();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            var index = Array.FindIndex(selected.Catalog.Editions, p => p.Sha256 == sha256);
            if (index < 0) throw new ArgumentException("Edition is not in the connected catalog.");
            var pin = selected.Catalog.Editions[index]; CheckPath(pin.Path);
            return action(pin, selected.Editions[index], linked.Token);
        }
        finally { Release(selected); }
    }
    void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (session != null || loading || failure != null || !File.Exists(catalogPath)) return;
        Begin();
        _ = Task.Run(() => {
            try {
                SourceHarvestConnectionStore.NormalizePath(catalogPath);
                if (new FileInfo(catalogPath).Length > 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
                var saved = JsonSerializer.Deserialize<WordLibraryCatalog>(File.ReadAllBytes(catalogPath)) ?? throw new InvalidDataException("Invalid word library catalog.");
                var opened = Open(saved, lifetime.Token);
                try { Publish(saved, opened); } catch { foreach (var h in opened.Guards) h.Dispose(); throw; }
            } catch (Exception ex) { lock (gate) { if (!disposed) failure = ex.Message; } }
            finally { lock (gate) loading = false; }
        });
    }
    void Ready() { ObjectDisposedException.ThrowIf(disposed, this); if (session == null && loading) throw new InvalidOperationException("Catalog verification is in progress. Try again shortly."); if (session == null && failure != null) throw new InvalidOperationException("Catalog verification failed: " + failure); }
    (List<FileStream> Guards, List<WordLibraryEdition> Editions) Open(WordLibraryCatalog candidate, CancellationToken ct)
    {
        if (candidate.Schema != Schema || candidate.Editions == null || candidate.Editions.Length is < 1 or > 1000) throw new InvalidDataException("Unsupported word library catalog.");
        lock (gate) editionsTotal = candidate.Editions.Length;
        long expectedBytes = 0; foreach (var pin in candidate.Editions) { ct.ThrowIfCancellationRequested(); CheckPath(pin.Path); expectedBytes = checked(expectedBytes + new FileInfo(pin.Path).Length); }
        Interlocked.Exchange(ref bytesTotal, expectedBytes);
        var handles = new List<FileStream>(); var result = new List<WordLibraryEdition>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        try { foreach (var pin in candidate.Editions) {
            ct.ThrowIfCancellationRequested(); CheckPath(pin.Path);
            if (pin.Sha256.Length != 64 || pin.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)) || !seen.Add(pin.Sha256)) throw new InvalidDataException("Invalid or duplicate edition pin.");
            var stream = new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read); handles.Add(stream);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read; while ((read = stream.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); Interlocked.Add(ref bytesDone, read); }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != pin.Sha256) throw new InvalidDataException("Edition differs from its SHA-256 pin.");
            using var db = Db(pin.Path);
            JsonElement Meta(string key) { using var c = db.CreateCommand(); c.CommandText = "SELECT value FROM metadata WHERE key=$key"; c.Parameters.AddWithValue("$key", key); using var j = JsonDocument.Parse((string?)c.ExecuteScalar() ?? throw new InvalidDataException("Missing edition metadata.")); return j.RootElement.Clone(); }
            if (Meta("schema").GetString() != "filemapper.public-word-library/4") throw new InvalidDataException("Unsupported word edition.");
            var capture = Meta("capture"); var evidence = Meta("evidence");
            RequirePublicRepositoryUri(capture.GetProperty("canonicalUri").GetString());
            if (evidence.GetProperty("visibility").GetString() != "public" || evidence.GetProperty("repositoryUrl").GetString() != capture.GetProperty("canonicalUri").GetString() || evidence.GetProperty("providerStableId").GetString() != capture.GetProperty("providerStableId").GetString()) throw new InvalidDataException("Edition does not carry matching public repository evidence.");
            var coverage = Meta("coverage");
            var finished = capture.GetProperty("finishedAt").GetString();
            var ticks = DateTimeOffset.TryParse(finished, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var observed) ? observed.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture) : null;
            var summary = new WordLibraryCapture(capture.GetProperty("repositoryId").GetString()!, capture.GetProperty("canonicalUri").GetString()!, capture.GetProperty("commit").GetString()!, finished, ticks, evidence.GetProperty("captureId").GetString(), capture.GetProperty("state").GetString());
            result.Add(new(pin.Sha256, summary, evidence, Meta("headEvidence"), Meta("editionId"), coverage.GetProperty("enumerated").GetInt64(), coverage.GetProperty("excluded").GetInt64(), coverage.GetProperty("words").GetInt64()));
            lock (gate) editionsDone++;
        } return (handles, result); } catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    static void RequirePublicRepositoryUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Edition repository URI is not an allowed HTTPS GitHub repository.");
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 3 || parts[0].Length != 0 || parts[1].Length == 0 || parts[2].Length == 0 || parts[2] is "." or ".." ||
            !(new[] { "darbotlabs", "darbotlm", "dayour" }).Contains(parts[1], StringComparer.OrdinalIgnoreCase) ||
            parts.Skip(1).Any(p => p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) || value != "https://github.com/" + parts[1] + "/" + parts[2])
            throw new InvalidDataException("Edition repository owner or path is not allowed.");
    }
    internal static void CheckPath(string path) { if (SourceHarvestConnectionStore.NormalizePath(path) != path) throw new InvalidDataException("Edition path is not normalized."); foreach (var suffix in new[]{"-wal", "-shm", "-journal"}) { SourceHarvestConnectionStore.NormalizePath(path + suffix); if (File.Exists(path + suffix) || Directory.Exists(path + suffix)) throw new InvalidDataException("Edition has SQLite sidecars."); } }
    static SqliteConnection Db(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open(); using var c = db.CreateCommand(); c.CommandText = "PRAGMA query_only=ON"; c.ExecuteNonQuery(); return db; }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); Retire(session); session = null; } }
}
