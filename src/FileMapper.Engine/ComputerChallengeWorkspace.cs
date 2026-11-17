using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed record ComputerProtectionRegistry(string RegistryId, string[] ValidationEditions, string[] TestEditions, string[]? TrainingEditions = null);
public sealed record ComputerDeckPolicy(int WordLimit, int PathLimit, int CandidateLimit,
    string Schema = "filemapper.computer-deck-quotas/1");
public sealed record ComputerFreezeRequest(string[] Editions, int SelectedEdition, int Pieces = 16, string Seed = "filemapper-computer-v1",
    ComputerProtectionRegistry? Protection = null, ComputerDeckPolicy? DeckPolicy = null);
public sealed record ComputerEditionBinding(string Path, string FileHash, string RepositoryFamily, string RevisionFamily, string Partition);
public sealed record ComputerDeckSummary(int Words, int PathComponents, int Candidates, long AvailableWords,
    long AvailablePathComponents, long AvailableCandidates, ImmutableArray<string> TruncationReasons);
public sealed record ComputerFrozenChallenge(ComputerFreezeRequest Request, ImmutableArray<ComputerEditionBinding> Editions,
    GamePartitionManifest Manifest, GameEpisodeSource Source, SpatialChallenge Challenge,
    string Schema = "filemapper.computer-challenge/1", ComputerDeckSummary? DeckSummary = null);

/// <summary>Builds challenges from byte-committed public editions; all source content joins the family split.</summary>
public static class ComputerChallengeWorkspace
{
    private sealed record Edition(string Path, string Hash, string Repo, string Revision, string Snapshot, string[] Content);
    public static ComputerFrozenChallenge Freeze(ComputerFreezeRequest request, CancellationToken token = default)
    {
        if (request?.Editions is null || request.Editions.Length is < 1 or > 256 || request.SelectedEdition < 0 || request.SelectedEdition >= request.Editions.Length
            || request.Pieces is < 2 or > 128 || string.IsNullOrWhiteSpace(request.Seed) || request.Seed.Length > 128) throw new ArgumentException("Invalid bounded cohort request.");
        var policy = request.DeckPolicy;
        if (policy is not null && (policy.Schema != "filemapper.computer-deck-quotas/1" || policy.WordLimit is < 2 or > 128
            || policy.PathLimit is < 0 or > 128 || policy.CandidateLimit is < 0 or > 128
            || (long)policy.WordLimit + policy.PathLimit + policy.CandidateLimit > SpatialGame.MaximumDeckLength))
            throw new ArgumentException("Invalid versioned deck quotas; total must not exceed 128.");
        var paths = request.Editions.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new ArgumentException("Duplicate edition path.");
        var protection = request.Protection;
        if (protection is not null && (string.IsNullOrWhiteSpace(protection.RegistryId) || protection.RegistryId.Length > 128
            || protection.ValidationEditions is null || protection.TestEditions is null || protection.ValidationEditions.Length + protection.TestEditions.Length + (protection.TrainingEditions?.Length ?? 0) > 256))
            throw new ArgumentException("Invalid protected-family registry.");
        var protectedPaths = protection is null ? Array.Empty<(string Path, string Partition)>() : protection.ValidationEditions.Select(p => (Path: Path.GetFullPath(p), Partition: "validation"))
            .Concat(protection.TestEditions.Select(p => (Path: Path.GetFullPath(p), Partition: "test")))
            .Concat((protection.TrainingEditions ?? []).Select(p => (Path: Path.GetFullPath(p), Partition: "train"))).ToArray();
        using var guards = new EditionGuards(paths.Concat(protectedPaths.Select(p => p.Path)));
        var cohort = paths.Select(p => ReadEdition(p, token)).ToArray();
        if (cohort.Select(e => e.Hash).Distinct(StringComparer.Ordinal).Count() != cohort.Length) throw new ArgumentException("Duplicate edition bytes.");
        var protectedEditions = protectedPaths.Select(p => ReadEdition(p.Path, token)).ToArray();
        var editions = cohort.Concat(protectedEditions).ToArray();
        var parent = Enumerable.Range(0, editions.Length).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a,b)] = Math.Min(a,b); }
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < editions.Length; i++)
            foreach (var key in editions[i].Content.Select(c => "content:" + c).Append("repo:" + editions[i].Repo).Append("revision:" + editions[i].Revision))
                if (keys.TryGetValue(key, out var j)) Union(i, j); else keys[key] = i;
        var groups = Enumerable.Range(0, editions.Length).GroupBy(Find).ToDictionary(g => g.Key,
            g => CanonicalJson.Sha256(new { request.Seed, Editions = g.Select(i => editions[i].Hash).Order(StringComparer.Ordinal).ToArray() }));
        var protectedGroups = new Dictionary<int, string>();
        for (var i = 0; i < protectedPaths.Length; i++)
        {
            var group = Find(cohort.Length + i); var partition = protectedPaths[i].Partition;
            if (protectedGroups.TryGetValue(group, out var prior) && prior != partition)
                throw new ArgumentException("A shared protected family crosses reserved partitions.");
            protectedGroups[group] = partition;
        }
        string Partition(int i) { if (protectedGroups.TryGetValue(Find(i), out var reserved)) return reserved;
            var v = Convert.ToInt32(groups[Find(i)][..4], 16) % 10; return v < 7 ? "train" : v < 8 ? "validation" : "test"; }
        var bindings = cohort.Select((e, i) => new ComputerEditionBinding(e.Path, e.Hash, e.Repo, e.Revision, Partition(i))).ToImmutableArray();
        var cohortMembers = bindings.Select(e => new { e.FileHash, e.RepositoryFamily, e.RevisionFamily, e.Partition }).ToArray();
        var cohortHash = protection is null ? CanonicalJson.Sha256(cohortMembers) : CanonicalJson.Sha256(new { Members = cohortMembers,
            protection.RegistryId, Protected = protectedPaths.Select((p, i) => new { protectedEditions[i].Hash, p.Partition }).ToArray() });
        var manifest = new GamePartitionManifest(bindings.Select(e => new GameEpisodeAssignment(e.RepositoryFamily, e.RevisionFamily, e.Partition)).ToImmutableArray(), cohortHash);
        GamePartitions.Validate(manifest);
        var selected = cohort[request.SelectedEdition]; var source = new GameEpisodeSource(selected.Repo, selected.Revision, Partition(request.SelectedEdition));
        var deck = ImmutableArray.CreateBuilder<SpatialPiece>();
        var lexical = new List<(string Id, string Document, long Line, long Start, long Length, string Occurrence)>();
        var pathPieces = new List<(string Id, string Document, long Ordinal, long Start, long Length)>();
        using (var db = Open(selected.Path))
        using (var cancellation = new SqlCancellation(db, token))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT occurrence_id,document_id,normalized_term,shape,source_sha256,line,byte_start,byte_length FROM words WHERE complete=1 ORDER BY document_id,byte_start,occurrence_id LIMIT $n";
            cmd.Parameters.AddWithValue("$n", policy?.WordLimit ?? request.Pieces);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var occurrence = reader.GetString(0); var document = reader.GetString(1); var term = reader.GetString(2); var shape = reader.GetString(3); var content = reader.GetString(4);
                var id = "piece:" + CanonicalJson.Sha256(new { selected.Hash, document, occurrence });
                lexical.Add((id, document, reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), occurrence));
                deck.Add(new(id, "term:" + CanonicalJson.Sha256(term), occurrence, document, "layer:source", shape, "native-lift/v1", term,
                    [term], ["content:" + content, "occurrence:" + occurrence], "word", document));
            }
        }
        using (var db = Open(selected.Path))
        using (var cancellation = new SqlCancellation(db, token))
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='path_shapes'";
            if (Convert.ToInt64(exists.ExecuteScalar()) != 0)
            {
                foreach (var document in lexical.Select(x => x.Document).Distinct(StringComparer.Ordinal))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT p.ordinal,p.byte_start,p.byte_length,p.component_base64,p.shape_id,p.shape,p.entry_id FROM path_shapes p JOIN documents d ON d.entry_id=p.entry_id WHERE d.document_id=$d ORDER BY p.entry_id,p.ordinal";
                    cmd.Parameters.AddWithValue("$d", document); using var reader = cmd.ExecuteReader();
                    while (deck.Count < SpatialGame.MaximumDeckLength && pathPieces.Count < (policy?.PathLimit ?? SpatialGame.MaximumDeckLength) && reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var ordinal = reader.GetInt64(0); var start = reader.GetInt64(1); var length = reader.GetInt64(2);
                        var component = reader.GetString(3); var shapeId = reader.GetString(4); var shape = reader.GetString(5); var entry = reader.GetString(6);
                        var id = "path-piece:" + CanonicalJson.Sha256(new { selected.Hash, document, entry, ordinal, component });
                        pathPieces.Add((id, entry, ordinal, start, length));
                        deck.Add(new(id, shapeId, "path-occurrence:" + CanonicalJson.Sha256(new { selected.Hash, document, entry, ordinal }), document,
                            "layer:source", shape, "native-lift/v1", component, [], [$"git-path-byte-span:{start}:{length}"]));
                    }
                }
            }
        }
        var candidateCount = 0;
        using (var db = Open(selected.Path))
        using (var cancellation = new SqlCancellation(db, token))
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='lexical_shapes'";
            if (Convert.ToInt64(exists.ExecuteScalar()) != 0)
            foreach (var document in lexical.Select(x => x.Document).Distinct(StringComparer.Ordinal))
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT occurrence_id,kind,shape_id,shape,text,byte_start,byte_length FROM lexical_shapes WHERE document_id=$d ORDER BY byte_start,occurrence_id,kind";
                cmd.Parameters.AddWithValue("$d", document); using var reader = cmd.ExecuteReader();
                while (deck.Count < SpatialGame.MaximumDeckLength && candidateCount < (policy?.CandidateLimit ?? SpatialGame.MaximumDeckLength) && reader.Read())
                {
                    candidateCount++;
                    token.ThrowIfCancellationRequested();
                    var occurrence = reader.GetString(0); var kind = reader.GetString(1); var shapeId = reader.GetString(2);
                    var text = reader.GetString(4); var start = reader.GetInt64(5); var length = reader.GetInt64(6);
                    var id = "candidate-piece:" + CanonicalJson.Sha256(new { selected.Hash, document, occurrence, kind });
                    deck.Add(new(id, shapeId, occurrence, document, "layer:source", reader.GetString(3), "native-lift/v1",
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))[..Math.Min(512, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).Length)], [], ["lexical-candidate-not-parsed-or-executed", $"byte-span:{start}:{length}"]));
                }
            }
        }
        if (deck.Count < 2) throw new InvalidDataException("Selected edition has fewer than two complete word occurrences.");
        var patterns = ImmutableArray.CreateBuilder<SpatialPattern>();
        for (var i = 1; i < lexical.Count; i++)
        {
            var first = lexical[i - 1]; var second = lexical[i];
            if (first.Document != second.Document || first.Line != second.Line) continue;
            var evidence = ImmutableArray.Create("occurrence:" + first.Occurrence, "occurrence:" + second.Occurrence,
                "source-spans:" + CanonicalJson.Sha256(new { first.Document, first.Line, FirstStart = first.Start, FirstLength = first.Length, SecondStart = second.Start, SecondLength = second.Length }), $"byte-span:{first.Start}:{first.Length}:line:{first.Line}", $"byte-span:{second.Start}:{second.Length}:line:{second.Line}");
            patterns.Add(new("lexical:" + CanonicalJson.Sha256(new { selected.Hash, FirstId = first.Id, SecondId = second.Id }), "layer:source", "lexical-ordered-x/v1", [first.Id, second.Id], evidence, []));
        }
        for (var i = 1; i < pathPieces.Count; i++)
        {
            var first = pathPieces[i - 1]; var second = pathPieces[i];
            if (first.Document != second.Document || second.Ordinal != first.Ordinal + 1) continue;
            patterns.Add(new("path:" + CanonicalJson.Sha256(new { selected.Hash, FirstId = first.Id, SecondId = second.Id }),
                "layer:source", "path-ordered-x/v1", [first.Id, second.Id], [$"git-path-byte-span:{first.Start}:{first.Length}", $"git-path-byte-span:{second.Start}:{second.Length}", "document:" + first.Document], []));
        }
        var datasetHash = CanonicalJson.Sha256(new { selected.Hash, request.Pieces, Deck = deck.ToImmutable(), Patterns = patterns.ToImmutable() });
        var challenge = SpatialGame.Start(new("computer:" + datasetHash, datasetHash, CanonicalJson.Sha256("no-semantic-model"),
            [new("layer:source", selected.Snapshot, null, null, "lexical-observations")], deck.ToImmutable(), patterns.ToImmutable(), [], new(), "lines")).Challenge;
        ComputerDeckSummary? summary = null;
        if (policy is not null)
        {
            long wordsAvailable; long pathsAvailable = 0, candidatesAvailable = 0;
            using var db = Open(selected.Path); using var cancellation = new SqlCancellation(db, token); using var wordCount = db.CreateCommand();
            wordCount.CommandText = "SELECT count(*) FROM words WHERE complete=1"; wordsAvailable = Convert.ToInt64(wordCount.ExecuteScalar());
            using var tables = db.CreateCommand(); tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('path_shapes','lexical_shapes')";
            var availableTables = new HashSet<string>(StringComparer.Ordinal); using (var tableReader = tables.ExecuteReader()) while (tableReader.Read()) availableTables.Add(tableReader.GetString(0));
            foreach (var document in lexical.Select(x => x.Document).Distinct(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (availableTables.Contains("path_shapes")) { using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM path_shapes p JOIN documents d ON d.entry_id=p.entry_id WHERE d.document_id=$d"; c.Parameters.AddWithValue("$d", document); pathsAvailable += Convert.ToInt64(c.ExecuteScalar()); }
                if (availableTables.Contains("lexical_shapes")) { using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM (SELECT DISTINCT occurrence_id,kind,shape_id,shape,text,byte_start,byte_length FROM lexical_shapes WHERE document_id=$d)"; c.Parameters.AddWithValue("$d", document); candidatesAvailable += Convert.ToInt64(c.ExecuteScalar()); }
            }
            var reasons = ImmutableArray.CreateBuilder<string>();
            if (wordsAvailable > lexical.Count) reasons.Add("word quota");
            if (pathsAvailable > pathPieces.Count) reasons.Add("path quota");
            if (candidatesAvailable > candidateCount) reasons.Add("candidate quota");
            if (lexical.Count < policy.WordLimit) reasons.Add("fewer eligible words than quota");
            if (pathPieces.Count < policy.PathLimit) reasons.Add("fewer eligible path components than quota");
            if (candidateCount < policy.CandidateLimit) reasons.Add("fewer eligible candidates than quota");
            summary = new(lexical.Count, pathPieces.Count, candidateCount, wordsAvailable, pathsAvailable, candidatesAvailable, reasons.ToImmutable());
        }
        guards.CheckSidecars();
        return new(request with { Editions = paths }, bindings, manifest, source, challenge, DeckSummary: summary);
    }
    public static void Verify(ComputerFrozenChallenge frozen, CancellationToken token = default)
    {
        if (frozen is null || frozen.Schema != "filemapper.computer-challenge/1") throw new ArgumentException("Invalid frozen challenge.");
        var expected = Freeze(frozen.Request, token);
        if (CanonicalJson.Serialize(expected) != CanonicalJson.Serialize(frozen)) throw new InvalidDataException("Frozen challenge, source bytes or partition bindings differ.");
    }
    // Windows share guards deny write/delete during hashing and all subsequent SQLite reads.
    // Sidecar journals are not included in an edition's byte hash and must not be present.
    private sealed class EditionGuards : IDisposable
    {
        private readonly string[] paths;
        private readonly List<FileStream> handles = [];
        public EditionGuards(IEnumerable<string> source)
        {
            paths = source.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            try
            {
                CheckSidecars();
                foreach (var path in paths) handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                CheckSidecars();
            }
            catch { Dispose(); throw; }
        }
        public void CheckSidecars()
        {
            foreach (var path in paths)
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (File.Exists(path + suffix)) throw new InvalidDataException("Edition has a SQLite sidecar: " + suffix);
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }
    private static Edition ReadEdition(string path, CancellationToken token)
    {
        var info = new FileInfo(path); if (!info.Exists || info.Length > 4L * 1024 * 1024 * 1024) throw new ArgumentException("Edition missing or exceeds 4 GiB.");
        token.ThrowIfCancellationRequested(); string hash;
        using (var stream = File.OpenRead(path))
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
            hash = Convert.ToHexStringLower(digest.GetHashAndReset());
        }
        using var db = Open(path); using var cancellation = new SqlCancellation(db, token); var meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand()) { cmd.CommandText = "SELECT key,value FROM metadata"; using var r = cmd.ExecuteReader(); while (r.Read()) meta.Add(r.GetString(0), JsonDocument.Parse(r.GetString(1)).RootElement.Clone()); }
        if (meta["schema"].GetString() is not ("filemapper.public-word-library/1" or "filemapper.public-word-library/2" or "filemapper.public-word-library/3" or "filemapper.public-word-library/4") || meta["forkFamilyStatus"].GetString() != "non-fork-self") throw new InvalidDataException("Only editions with established non-fork family are eligible.");
        var repo = meta["repositoryFamily"].GetString() ?? throw new InvalidDataException("Repository family missing.");
        var revision = meta["revisionFamily"].GetString() ?? throw new InvalidDataException("Revision family missing.");
        var snapshot = meta["editionId"].GetString() ?? throw new InvalidDataException("Edition ID missing.");
        using var query = db.CreateCommand(); query.CommandText = "SELECT DISTINCT source_sha256 FROM documents WHERE source_sha256 IS NOT NULL ORDER BY source_sha256";
        using var reader = query.ExecuteReader(); var content = new List<string>(); while (reader.Read()) { token.ThrowIfCancellationRequested(); content.Add(reader.GetString(0)); if (content.Count > 1000000) throw new InvalidDataException("Edition content family limit exceeded."); }
        return new(path, hash, repo, revision, snapshot, content.ToArray());
    }
    private static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(path).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open(); return db; }
    private sealed class SqlCancellation : IDisposable
    {
        private readonly SQLitePCL.sqlite3 handle;
        private readonly CancellationTokenRegistration registration;
        public SqlCancellation(SqliteConnection db, CancellationToken token)
        {
            handle = db.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
            registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
        }
        public void Dispose() { registration.Dispose(); SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
    }
}
