using System.Globalization;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

/// <summary>Immutable edition files, built from a verified source index; lexical observations make no runtime or parser claims.</summary>
public static partial class PublicWordLibrary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Owners = new(StringComparer.OrdinalIgnoreCase) { "darbotlabs", "DarbotLM", "dayour" };

    public static async Task<PublicWordLibraryReceipt> PublishAsync(ISourceHarvestReader source, string captureRoot,
        string destination, PublicRepositoryEvidence evidence, string evidenceJsonPath, PublicHeadEvidence headEvidence, string headReceiptPath, string headStdoutPath, PublicWordLibraryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(evidence); options ??= new();
        if (options.MaximumEditionBytes < 1048576 || options.MinimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (source is not CompactSourceHarvestIndex || !source.IsReadOnly) throw new ArgumentException("A sealed compact source index is required for bounded streaming.");
        var publicIdentity = await ValidateEvidenceAsync(evidence, evidenceJsonPath, cancellationToken);
        await using var pinnedIndex = new FileStream(source.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var indexSha256 = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(pinnedIndex, cancellationToken));
        var store = new GitSourceCapture(captureRoot, readOnly: true);
        var capture = store.Read(evidence.CaptureId).Manifest;
        await ValidateHeadAsync(capture, evidence, headEvidence, headReceiptPath, headStdoutPath, cancellationToken);
        if (capture.CanonicalUri != evidence.RepositoryUrl || capture.ProviderStableId != evidence.ProviderStableId ||
            capture.Commit.Length is not (40 or 64) || !capture.Commit.All(Uri.IsHexDigit))
            throw new InvalidDataException("Public evidence does not match the immutable capture identity.");
        var counts = source.Counts(evidence.CaptureId, cancellationToken);
        if (!counts.ExtractionSealed || counts.AssociatedEntries != capture.Coverage.TreeEntries)
            throw new InvalidDataException("A sealed extraction and complete indexed entry enumeration are required.");
        var full = Path.GetFullPath(destination);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("Editions are immutable and cannot overwrite an existing path.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var stage = full + "." + Guid.NewGuid().ToString("N") + ".building";
        long enumerated = 0, admitted = 0, excluded = 0, words = 0;
        void Budget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = 0;
            foreach (var candidate in new[] { stage, stage + "-journal", stage + "-wal" }) if (File.Exists(candidate)) size = checked(size + new FileInfo(candidate).Length);
            if (size > options.MaximumEditionBytes) throw new IOException("Edition exceeded its explicit database and journal byte budget.");
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.AvailableFreeSpace < options.MinimumFreeBytes) throw new IOException("Free space fell below the edition publication floor.");
        }
        Budget();
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stage, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                db.Open(); Execute(db, null, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE documents(entry_id TEXT PRIMARY KEY, document_id TEXT, path TEXT NOT NULL, source_sha256 TEXT, status TEXT NOT NULL, lexical_status TEXT, complete INTEGER, exclusion_reason TEXT); CREATE TABLE words(term_id TEXT NOT NULL, normalized_term TEXT NOT NULL, shape TEXT NOT NULL, document_id TEXT NOT NULL, path TEXT NOT NULL, path_bytes TEXT NOT NULL, source_sha256 TEXT NOT NULL, occurrence_id TEXT NOT NULL, byte_start INTEGER NOT NULL, byte_length INTEGER NOT NULL, utf16_start INTEGER NOT NULL, utf16_length INTEGER NOT NULL, line INTEGER NOT NULL, column_no INTEGER NOT NULL, spelling TEXT NOT NULL, lexical_status TEXT NOT NULL, complete INTEGER NOT NULL, PRIMARY KEY(document_id,occurrence_id)); CREATE TABLE lexical_shapes(entry_id TEXT NOT NULL, document_id TEXT NOT NULL, source_sha256 TEXT NOT NULL, occurrence_id TEXT NOT NULL, kind TEXT NOT NULL, shape_id TEXT NOT NULL, shape TEXT NOT NULL, text TEXT NOT NULL, byte_start INTEGER NOT NULL, byte_length INTEGER NOT NULL, utf16_start INTEGER NOT NULL, utf16_length INTEGER NOT NULL, line INTEGER NOT NULL, column_no INTEGER NOT NULL, evidence_level TEXT NOT NULL, PRIMARY KEY(entry_id,occurrence_id,kind)); CREATE TABLE path_shapes(entry_id TEXT NOT NULL, ordinal INTEGER NOT NULL, raw_path_base64 TEXT NOT NULL, byte_start INTEGER NOT NULL, byte_length INTEGER NOT NULL, component_base64 TEXT NOT NULL, shape_id TEXT NOT NULL, shape TEXT NOT NULL, evidence_level TEXT NOT NULL, PRIMARY KEY(entry_id,ordinal)); CREATE INDEX words_term ON words(term_id); CREATE INDEX words_partition ON words(document_id);");
                using var tx = db.BeginTransaction();
                void Meta(string key, object? value) => Execute(db, tx, "INSERT INTO metadata VALUES($k,$v)", ("$k", key), ("$v", JsonSerializer.Serialize(value, Json)));
                Meta("schema", "filemapper.public-word-library/4"); Meta("evidence", evidence); Meta("capture", capture); Meta("headEvidence", headEvidence); Meta("compactIndexSha256", indexSha256);
                Meta("evidenceLevel", "lexical observation only; no parsed semantics or observed behavior"); Meta("shapeMapping", "sha256-first-byte modulo 9, ordered I O T L J S Z pillar stair; display geometry bucket only, collisions do not imply equality or similarity; canonical source IDs remain authoritative");
                Meta("repositoryFamily", publicIdentity ? null : capture.RepositoryId); Meta("forkFamilyStatus", publicIdentity ? "unknown-excluded-from-heldout" : "non-fork-self"); Meta("revisionFamily", CanonicalJson.Sha256(new { capture.RepositoryId, capture.Commit }));
                for (var offset = 0; ; offset = checked(offset + 256))
                {
                    cancellationToken.ThrowIfCancellationRequested(); var page = source.Documents(evidence.CaptureId, offset: offset, limit: 256, cancellationToken: cancellationToken);
                    foreach (var d in page.Items)
                    {
                        enumerated++; if (enumerated % 256 == 0) Budget(); var ok = d.CaptureStatus == "captured" && d.DocumentId is not null && d.SourceSha256 is not null && d.LexicalStatus is not null;
                        var reason = ok ? null : d.CaptureStatus != "captured" ? d.CaptureStatus : "lexicon-unavailable";
                        if (ok) admitted++; else excluded++;
                        Execute(db, tx, "INSERT INTO documents VALUES($e,$d,$p,$h,$s,$l,$c,$r)", ("$e", d.EntryId), ("$d", d.DocumentId), ("$p", d.RepositoryPath), ("$h", d.SourceSha256), ("$s", d.CaptureStatus), ("$l", d.LexicalStatus), ("$c", d.LexicallyComplete is null ? null : d.LexicallyComplete.Value ? 1 : 0), ("$r", reason));
                    }
                    if (offset + page.Items.Count >= page.Total) break;
                    if (page.Items.Count == 0) throw new InvalidDataException("Source document pagination ended early.");
                }
                string? policyHash = null; string? extractionId = null;
                for (var offset = 0; ; offset = checked(offset + 256))
                {
                    var page = source.ListCaptures(offset, 256, cancellationToken);
                    var found = page.Items.FirstOrDefault(x => x.CaptureId == evidence.CaptureId);
                    if (found is not null) { policyHash = found.ExtractionPolicyHash; extractionId = found.ExtractionHash; break; }
                    if (offset + page.Items.Count >= page.Total || page.Items.Count == 0) break;
                }
                if (policyHash is null || !Hash(policyHash)) throw new InvalidDataException("Sealed extraction policy is unavailable."); Meta("lexicalPolicyHash", policyHash); if (extractionId is null || !Hash(extractionId)) throw new InvalidDataException("Sealed extraction ID is unavailable."); Meta("extractionId", extractionId);
                long pathEntries = 0;
                await foreach (var entry in store.EntriesAsync(evidence.CaptureId, cancellationToken))
                {
                    pathEntries++; var pathBytes = Convert.FromBase64String(entry.Entry.RepositoryPathBytes);
                    if (entry.Status == "captured" && entry.Blob is not null && entry.DocumentId is not null)
                    {
                        var basePath = Path.Combine(store.RootDirectory, "lexicons", policyHash, entry.Blob.Sha256);
                        var metadataPath = basePath + ".json";
                        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 65536) throw new InvalidDataException("Lexicon metadata is unavailable or too large.");
                        var lexicon = JsonSerializer.Deserialize<SourceLexiconResult>(await File.ReadAllBytesAsync(metadataPath, cancellationToken), Json) ?? throw new InvalidDataException("Invalid lexicon metadata.");
                        if (lexicon.SourceSha256 != entry.Blob.Sha256 || lexicon.Policy.PolicyHash != policyHash) throw new InvalidDataException("Lexicon source or policy differs.");
                        if (lexicon.OutputSha256 is not null)
                        {
                            var lexiconPath = basePath + ".ndjson.gz";
                            await using (var bytes = new FileStream(lexiconPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                                if (Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(bytes, cancellationToken)) != lexicon.OutputSha256) throw new InvalidDataException("Lexical artifact hash differs.");
                            SourceLexicalOccurrence? previous = null; long activeLine = 0; bool onlyIndent = true; long units = 0;
                            var extension = Path.GetExtension(entry.Entry.RepositoryPath);
                            var shell = extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) || extension.Equals(".psm1", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bash", StringComparison.OrdinalIgnoreCase);
                            await foreach (var unit in SourceLexicon.ReadAsync(lexiconPath, cancellationToken))
                            {
                                units++; if (units % 4096 == 0) Budget();
                                if (unit.SourceSha256 != entry.Blob.Sha256 || unit.PolicyHash != policyHash) throw new InvalidDataException("Lexical stream identity differs.");
                                if (unit.StartLine != activeLine) { activeLine = unit.StartLine; onlyIndent = true; }
                                string? kind = null;
                                if (unit.Kind == "word" && !unit.Fragmented && unit.Text is not null)
                                {
                                    if (previous is not null && previous.ByteStart + previous.ByteLength == unit.ByteStart && previous.Text?.EndsWith('$') == true) kind = "variable-prefix-candidate";
                                    else if (shell && onlyIndent) kind = "line-initial-shell-word-candidate";
                                }
                                else if (unit.Kind is "punctuation" or "symbol" && unit.Text is not null && unit.Text.Any(c => "=+-*/%<>!&|^".Contains(c))) kind = "operator-character-run";
                                if (kind is not null)
                                {
                                    var shapeId = "lexical-shape:" + CanonicalJson.Sha256(new { Kind = kind, Text = unit.Text });
                                    Execute(db, tx, "INSERT INTO lexical_shapes VALUES($e,$d,$h,$i,$k,$id,$shape,$text,$bs,$bl,$us,$ul,$line,$col,$level)", ("$e", entry.Entry.EntryId), ("$d", entry.DocumentId), ("$h", entry.Blob.Sha256), ("$i", unit.OccurrenceId), ("$k", kind), ("$id", shapeId), ("$shape", Geometry(shapeId)), ("$text", unit.Text), ("$bs", unit.ByteStart), ("$bl", unit.ByteLength), ("$us", unit.Utf16Start), ("$ul", unit.Utf16Length), ("$line", unit.StartLine), ("$col", unit.StartColumn), ("$level", "lexical-candidate-not-parsed-or-executed"));
                                }
                                if (unit.Kind != "whitespace" && unit.Kind != "bom") onlyIndent = false;
                                previous = unit;
                            }
                            if (units != lexicon.Occurrences) throw new InvalidDataException("Lexical artifact count differs.");
                        }
                    }
                    var start = 0; var ordinal = 0;
                    for (var i = 0; i <= pathBytes.Length; i++)
                    {
                        if (i != pathBytes.Length && pathBytes[i] != (byte)'/') continue;
                        if (i == start) throw new InvalidDataException("Empty Git path component.");
                        var component = pathBytes.AsSpan(start, i - start).ToArray();
                        var id = "path-component:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(component));
                        Execute(db, tx, "INSERT INTO path_shapes VALUES($e,$o,$raw,$start,$length,$part,$id,$shape,$level)", ("$e", entry.Entry.EntryId), ("$o", ordinal++), ("$raw", entry.Entry.RepositoryPathBytes), ("$start", start), ("$length", component.Length), ("$part", Convert.ToBase64String(component)), ("$id", id), ("$shape", Geometry(id)), ("$level", "lexical-git-path-bytes"));
                        start = i + 1;
                    }
                }
                if (pathEntries != enumerated) throw new InvalidDataException("Capture path enumeration differs from source index.");
                // Stream the compact projection once in capture/lexicon order; do not issue one query per distinct term.
                using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    input.Open(); using var query = input.CreateCommand();
                    query.CommandText = """
                        SELECT e.document_id,e.path,e.path_bytes,l.source_hash,p.hash,w.ordinal,w.byte_start,w.byte_length,
                          w.utf16_start,w.utf16_length,w.start_line,w.start_column,s.text,t.hash,t.term,l.status,l.complete
                        FROM captures c JOIN entries e ON e.capture_id=c.id
                        JOIN lexicons l ON l.id=e.lexicon_id AND l.ready=1 JOIN policies p ON p.id=l.policy_id
                        JOIN words w ON w.lexicon_id=l.id JOIN terms t ON t.id=w.term_id JOIN spellings s ON s.id=w.spelling_id
                        WHERE c.capture_id=$capture AND c.ready=1 ORDER BY e.ordinal,w.ordinal
                        """;
                    query.Parameters.AddWithValue("$capture", evidence.CaptureId);
                    using var wordInsert = db.CreateCommand(); wordInsert.Transaction = tx;
                    wordInsert.CommandText = "INSERT INTO words VALUES($t,$n,$shape,$d,$p,$pb,$h,$i,$bs,$bl,$us,$ul,$line,$col,$text,$status,$complete)";
                    foreach (var name in new[] { "$t", "$n", "$shape", "$d", "$p", "$pb", "$h", "$i", "$text", "$status" }) wordInsert.Parameters.Add(name, SqliteType.Text);
                    foreach (var name in new[] { "$bs", "$bl", "$us", "$ul", "$line", "$col", "$complete" }) wordInsert.Parameters.Add(name, SqliteType.Integer);
                    wordInsert.Prepare();
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourceHash = Convert.ToHexStringLower((byte[])reader.GetValue(3));
                        var policy = Convert.ToHexStringLower((byte[])reader.GetValue(4));
                        if (policy != policyHash) throw new InvalidDataException("Compact occurrence policy differs from extraction.");
                        var ordinal = reader.GetInt64(5); var startByte = reader.GetInt64(6); var byteLength = reader.GetInt64(7);
                        var termId = "source-term:" + Convert.ToHexStringLower((byte[])reader.GetValue(13));
                        var occurrenceId = "source-occurrence:" + CanonicalJson.Sha256(new { Model = SourceLexicon.ContractId, PolicyHash = policy, Source = sourceHash, Ordinal = ordinal, Kind = "word", ByteStart = startByte, ByteLength = byteLength });
                        wordInsert.Parameters["$t"].Value = termId; wordInsert.Parameters["$n"].Value = reader.GetString(14);
                        wordInsert.Parameters["$shape"].Value = Geometry(termId); wordInsert.Parameters["$d"].Value = reader.GetString(0);
                        wordInsert.Parameters["$p"].Value = reader.GetString(1); wordInsert.Parameters["$pb"].Value = reader.GetString(2);
                        wordInsert.Parameters["$h"].Value = sourceHash; wordInsert.Parameters["$i"].Value = occurrenceId;
                        wordInsert.Parameters["$bs"].Value = startByte; wordInsert.Parameters["$bl"].Value = byteLength;
                        wordInsert.Parameters["$us"].Value = reader.GetInt64(8); wordInsert.Parameters["$ul"].Value = reader.GetInt64(9);
                        wordInsert.Parameters["$line"].Value = reader.GetInt64(10); wordInsert.Parameters["$col"].Value = reader.GetInt64(11);
                        wordInsert.Parameters["$text"].Value = reader.GetString(12); wordInsert.Parameters["$status"].Value = reader.GetString(15);
                        wordInsert.Parameters["$complete"].Value = reader.GetInt64(16); wordInsert.ExecuteNonQuery();
                        words++; if (words % 4096 == 0) Budget();
                    }
                }
                if (enumerated != capture.Coverage.TreeEntries || words != counts.WordOccurrences) throw new InvalidDataException("Edition coverage differs from pinned index.");
                Budget(); var published = DateTimeOffset.UtcNow; Meta("publishedAt", published); Meta("coverage", new { enumerated, admitted, excluded, words, capture.Coverage.ProviderEnumerationComplete });
                var edition = CanonicalJson.Sha256(new { Schema = "filemapper.public-word-library/4", Evidence = evidence, HeadEvidence = headEvidence, CaptureId = evidence.CaptureId, capture.Commit, Counts = counts, PublishedAt = published });
                Meta("editionId", edition); tx.Commit(); tx.Dispose(); db.Close();
                cancellationToken.ThrowIfCancellationRequested(); File.Move(stage, full);
                return new(edition, full, enumerated, admitted, excluded, words, published);
            }
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static async Task<bool> ValidateEvidenceAsync(PublicRepositoryEvidence evidence, string evidenceJsonPath, CancellationToken cancellationToken)
    {
        if (evidence.Visibility != "public" || string.IsNullOrWhiteSpace(evidence.ProviderStableId) || evidence.ObservedAt == default ||
            !Uri.TryCreate(evidence.RepositoryUrl, UriKind.Absolute, out var repo) || repo.Scheme != "https" || repo.Host != "github.com" ||
            !string.IsNullOrEmpty(repo.Query) || !string.IsNullOrEmpty(repo.Fragment) || !string.IsNullOrEmpty(repo.UserInfo)) throw new ArgumentException("Explicit public GitHub visibility evidence is required.");
        var parts = repo.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Owners.Contains(parts[0]) || repo.AbsolutePath != "/" + parts[0] + "/" + parts[1] ||
            !Uri.TryCreate(evidence.EvidenceUri, UriKind.Absolute, out var proof) || proof.Scheme != "https" || proof.Host != "api.github.com" || (proof.AbsolutePath != "/repos/" + parts[0] + "/" + parts[1] && proof.AbsolutePath != "/users/" + parts[0] + "/repos" && proof.AbsolutePath != "/orgs/" + parts[0] + "/repos") || !string.IsNullOrEmpty(proof.Query) || !string.IsNullOrEmpty(proof.Fragment)) throw new ArgumentException("Evidence must identify an allowed owner and a GitHub API observation.");
                if (!Hash(evidence.CaptureId) || !Hash(evidence.EvidenceSha256)) throw new ArgumentException("Expected lowercase SHA-256 commitments.");
        var info = new FileInfo(evidenceJsonPath);
        if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidDataException("Public provider evidence is missing or exceeds one MiB.");
        var bytes = await File.ReadAllBytesAsync(evidenceJsonPath, cancellationToken);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != evidence.EvidenceSha256) throw new InvalidDataException("Provider evidence hash differs.");
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        var matches = new List<JsonElement>();
        void Consider(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) && id.ToString() == evidence.ProviderStableId) matches.Add(value);
        }
        if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) Consider(item); else Consider(root);
        if (matches.Count != 1) throw new InvalidDataException("Provider page must contain exactly one matching repository ID.");
        var selected = matches[0];
        if (!selected.TryGetProperty("private", out var visibility) || visibility.ValueKind != JsonValueKind.False ||
            !selected.TryGetProperty("visibility", out var explicitVisibility) || explicitVisibility.GetString() != "public" ||
            !selected.TryGetProperty("html_url", out var url) || url.GetString() != evidence.RepositoryUrl ||
            !selected.TryGetProperty("full_name", out var fullName) || fullName.GetString() != parts[0] + "/" + parts[1] ||
            !selected.TryGetProperty("owner", out var owner) || !owner.TryGetProperty("login", out var login) || login.GetString() != parts[0] ||
            !selected.TryGetProperty("fork", out var fork) || fork.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Provider page does not attest this exact repository as public.");
        return fork.GetBoolean();
    }
    private static string Geometry(string identity)
    {
        // These nine catalog shapes are a stable display bucket, never a semantic identity or similarity score.
        string[] shapes = ["I", "O", "T", "L", "J", "S", "Z", "pillar", "stair"];
        var hex = identity[(identity.LastIndexOf(':') + 1)..];
        if (!Hash(hex)) throw new InvalidDataException("Shape identity is not SHA-256 based.");
        return shapes[Convert.ToByte(hex[..2], 16) % shapes.Length];
    }
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
