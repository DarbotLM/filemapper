using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record SemanticObjectPage(IReadOnlyList<SemanticObject> Items, long Total, int Offset, int Limit,
    Snapshot Snapshot, string ModelVersion, object Limits);
public sealed record SemanticPatternMatch(SemanticObject Before, SemanticObject After, string MatchType,
    bool IdentityEqual, string PatternKey, string PatternHash, string Explanation);
public sealed record SemanticPatternPage(Snapshot Before, Snapshot After, IReadOnlyList<SemanticPatternMatch> Items,
    string MatchedBy, object Limits);
public sealed record SemanticExportResult(string Schema, string SnapshotId, string ContentSha256, long ContentBytes,
    long Objects, long Edges, long Observations, long Locations, long Findings, IReadOnlyDictionary<string, long> ObjectKinds);

/// <summary>Evidence adapters use a single pinned read-only transaction. No game input is accepted as observed fact.</summary>
public sealed class SemanticWorkspace(MapStore store)
{
    public const string ExportSchema = "filemapper.semantic-export/1";
    public const string DatasetSchema = "filemapper.observed-filename-slice/1";
    private static SemanticObject Project(Snapshot snapshot, MapStore.ObservedNode value) =>
        SemanticObjects.Create(snapshot, value.Node, value.ObservedFrom, value.ObservedTo);

    public SemanticObjectPage Objects(string snapshotId, string? kind = null, string? search = null,
        int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (kind?.Length > 128 || search?.Length > 512) throw new ArgumentException("Semantic query text exceeds its bound.");
        using var reader = store.OpenSemanticReader();
        var snapshot = reader.Snapshot(snapshotId);
        var page = reader.Nodes(snapshotId, kind, search, offset, limit, cancellationToken);
        return new(page.Items.Select(item => Project(snapshot, item)).ToArray(), page.Total, page.Offset, page.Limit,
            snapshot, SemanticObjects.ModelVersion, new { maximumPageSize = 500, maximumWordsPerObject = SemanticObjects.MaxTokens });
    }

    public SemanticPatternPage Patterns(string beforeId, string afterId, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        using var reader = store.OpenSemanticReader();
        var before = reader.Snapshot(beforeId); var after = reader.Snapshot(afterId);
        // Explicitly bounded file candidates, never presented as an exhaustive temporal join.
        var a = reader.Nodes(beforeId, "File", limit: 500, cancellationToken: cancellationToken);
        var b = reader.Nodes(afterId, "File", limit: 500, cancellationToken: cancellationToken);
        var byId = b.Items.ToDictionary(item => item.Node.Id, StringComparer.Ordinal);
        var byPath = b.Items.Select(item => (Item: item, Key: PatternKey(after, item.Node)))
            .Where(pair => pair.Key is not null).GroupBy(pair => pair.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item).ToArray(), StringComparer.Ordinal);
        var matches = new List<SemanticPatternMatch>();
        foreach (var item in a.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count == limit) break;
            var pattern = PatternKey(before, item.Node);
            if (byId.TryGetValue(item.Node.Id, out var equal))
            {
                var key = pattern ?? "node:" + item.Node.Id;
                matches.Add(new(Project(before, item), Project(after, equal), "stable-identity", true, key,
                    CanonicalJson.Sha256(new { Model = "schema-path/v1", Key = key }),
                    "The collector node ID is equal in both snapshots. Path-fallback identities cannot establish physical continuity."));
            }
            else if (pattern is not null && byPath.TryGetValue(pattern, out var candidates))
            {
                foreach (var candidate in candidates.Take(limit - matches.Count))
                    matches.Add(new(Project(before, item), Project(after, candidate), "schema-path-pattern", false, pattern,
                        CanonicalJson.Sha256(new { Model = "schema-path/v1", Key = pattern }),
                        "Root-relative path segments have equal NFKC lowercase lexical tokens. Similarity is not identity, a rename, or movement."));
            }
        }
        return new(before, after, matches, "stable node identity or explicit normalized lexical path pattern; no inferred movement",
            new { maximumItems = limit, candidateObjectsPerSnapshot = 500, candidatesTruncated = a.Total > 500 || b.Total > 500,
                minimumLexicalSegments = 2, candidateKind = "File", pathPolicy = "root-relative; slash-separated; NFKC lowercase token arrays; case-insensitive lexical feature only" });
    }

    private static string? PatternKey(Snapshot snapshot, MapNode node)
    {
        if (node.Path is null) return null;
        var root = snapshot.Root.Replace('\\', '/').TrimEnd('/') + "/";
        var path = node.Path.Replace('\\', '/');
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var segments = path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        return CanonicalJson.Serialize(segments.Select(SemanticObjects.Tokenize).ToArray());
    }

    public GameChallenge Challenge(string snapshotId, int round = 1, CancellationToken cancellationToken = default)
    {
        if (round is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(round));
        using var reader = store.OpenSemanticReader();
        var snapshot = reader.Snapshot(snapshotId);
        var candidates = reader.Nodes(snapshotId, "File", limit: 500, cancellationToken: cancellationToken);
        var selected = new List<(SemanticObject Object, string[] Tokens, Evidence Evidence)>();
        var patternsSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = SemanticObjects.Tokenize(candidate.Node.Name);
            if (tokens.Length is < 2 or > 4 || tokens.Any(t => t.Length > 128) || candidate.Node.Name.Any(char.IsControl)) continue;
            var patternKey = CanonicalJson.Serialize(tokens);
            if (patternsSeen.Contains(patternKey)) continue;
            var obj = Project(snapshot, candidate);
            var evidence = obj.EvidenceRefs.Select(reference => reader.Observation(snapshotId, reference.Id))
                .FirstOrDefault(value => value is { Predicate: "filesystem.metadata", Completeness: "complete", ErrorCode: null }
                    && value.SnapshotId == snapshotId && value.SubjectId == candidate.Node.Id
                    && string.Equals(value.SourceUri, candidate.Node.Path, StringComparison.Ordinal));
            if (evidence is null || tokens.Any(token => !obj.SomeShapes.Any(word => word.Token == token))) continue;
            selected.Add((obj, tokens, evidence)); patternsSeen.Add(patternKey);
            if (selected.Count == 12) break;
        }
        if (selected.Count == 0) throw new InvalidOperationException("This snapshot has no eligible observed filenames in its first 500 file nodes. A challenge requires a 2–4 token filename and matching filesystem evidence.");
        var deck = ImmutableArray.CreateBuilder<GamePiece>();
        var patterns = ImmutableArray.CreateBuilder<GamePattern>();
        var associations = new Dictionary<(string First, string Second, string Kind), SortedSet<string>>();
        void Associate(string left, string right, string kind, params string[] evidence)
        {
            if (left == right) return;
            var pair = string.CompareOrdinal(left, right) < 0 ? (left, right, kind) : (right, left, kind);
            if (!associations.TryGetValue(pair, out var ids)) associations.Add(pair, ids = new(StringComparer.Ordinal));
            foreach (var id in evidence) ids.Add(id);
        }
        foreach (var value in selected)
        {
            var words = value.Tokens.Select(token => value.Object.SomeShapes.Single(word => word.Token == token)).ToArray();
            for (var index = 0; index < words.Length; index++)
            {
                var word = words[index];
                deck.Add(new("piece:" + CanonicalJson.Sha256(new { value.Object.SomId, Index = index }), word.WordId,
                    word.Token, [word.Token], word.ShapeType));
            }
            patterns.Add(new("pattern:" + CanonicalJson.Sha256(value.Tokens), value.Tokens.ToImmutableArray(), "filename", [value.Evidence.Id]));
            for (var first = 0; first < words.Length; first++)
            for (var second = first + 1; second < words.Length; second++)
                Associate(words[first].WordId, words[second].WordId, "same-source", value.Evidence.Id);
        }
        for (var first = 0; first < selected.Count; first++)
        for (var second = first + 1; second < selected.Count; second++)
        {
            var left = selected[first]; var right = selected[second];
            var leftPath = left.Object.SourceReference.Path!.Replace('\\', '/');
            var rightPath = right.Object.SourceReference.Path!.Replace('\\', '/');
            // Exact observed parent path, not an inferred directory identity.
            if (!string.Equals(leftPath[..leftPath.LastIndexOf('/')], rightPath[..rightPath.LastIndexOf('/')], StringComparison.Ordinal)) continue;
            foreach (var leftToken in left.Tokens)
            foreach (var rightToken in right.Tokens)
                Associate(left.Object.SomeShapes.Single(word => word.Token == leftToken).WordId,
                    right.Object.SomeShapes.Single(word => word.Token == rightToken).WordId, "path", left.Evidence.Id, right.Evidence.Id);
        }
        var links = associations.OrderBy(pair => pair.Key.First, StringComparer.Ordinal).ThenBy(pair => pair.Key.Second, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal).Select(pair => new GameAssociation(
                "association:" + CanonicalJson.Sha256(new { pair.Key.First, pair.Key.Second, pair.Key.Kind }),
                pair.Key.First, pair.Key.Second, pair.Key.Kind, pair.Value.ToImmutableArray())).ToImmutableArray();
        var datasetHash = CanonicalJson.Sha256(new { Schema = DatasetSchema, Snapshot = snapshot,
            Selection = "first-500-files-by-ordinal-id; first-12-distinct-eligible-filename-token-sequences", CandidateCount = candidates.Total,
            Objects = selected.Select(value => value.Object), Evidence = selected.Select(value => value.Evidence) });
        var challenge = new GameChallenge("snapshot:" + snapshotId, datasetHash, DatasetSchema, SemanticObjects.ModelVersion,
            deck.ToImmutable(), links, patterns.ToImmutable());
        return SemanticGame.CreateCompetitionRounds(challenge)[round - 1].Challenge;
    }

    public async Task<SemanticExportResult> ExportAsync(string snapshotId, Stream destination, CancellationToken cancellationToken = default)
    {
        using var export = PrepareExport(snapshotId, cancellationToken);
        return await export.WriteAsync(destination, cancellationToken);
    }

    /// <summary>Validates and pins the snapshot before a transport starts its response.</summary>
    public PreparedExport PrepareExport(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        if (snapshotId.Length > 256 || snapshotId.Any(char.IsControl)) throw new ArgumentException("Invalid snapshot identifier.", nameof(snapshotId));
        cancellationToken.ThrowIfCancellationRequested();
        var reader = store.OpenSemanticReader();
        try
        {
            var snapshot = reader.Snapshot(snapshotId);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedExport(reader, snapshot);
        }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>A single-use, pinned export. Keep alive until WriteAsync completes, then dispose.</summary>
    public sealed class PreparedExport : IDisposable
    {
        private readonly MapStore.SemanticReader reader;
        private readonly Snapshot snapshot;
        private int started;
        private bool disposed;

        internal PreparedExport(MapStore.SemanticReader reader, Snapshot snapshot)
        { this.reader = reader; this.snapshot = snapshot; }

        public Task<SemanticExportResult> WriteAsync(Stream destination, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite) throw new ArgumentException("The export destination must be writable.", nameof(destination));
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A prepared export can be written only once.");
            return WriteExportAsync(reader, snapshot, destination, cancellationToken);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            reader.Dispose();
        }
    }

    private static async Task<SemanticExportResult> WriteExportAsync(MapStore.SemanticReader reader, Snapshot snapshot,
        Stream destination, CancellationToken cancellationToken)
    {
        var snapshotId = snapshot.Id;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0, objects = 0, edges = 0, observations = 0, locations = 0, findings = 0;
        var kinds = new SortedDictionary<string, long>(StringComparer.Ordinal);
        async Task Write(object value, bool hashed = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value) + "\n");
            await destination.WriteAsync(line, cancellationToken);
            if (hashed) { digest.AppendData(line); bytes += line.Length; }
        }
        await Write(new { Type = "header", Schema = ExportSchema, ModelVersion = SemanticObjects.ModelVersion, Snapshot = snapshot,
            HashScope = "Exact UTF-8 canonical NDJSON bytes through the record before footer, including LF; footer excluded",
            Coverage = "All stored nodes, edges, observations, file locations and findings for one pinned snapshot; unobserved dimensions stay unknown" });
        foreach (var value in reader.AllNodes(snapshotId, cancellationToken))
        {
            await Write(new { Type = "object", Item = Project(snapshot, value) }); objects++;
            kinds[value.Node.Kind] = kinds.GetValueOrDefault(value.Node.Kind) + 1;
        }
        foreach (var value in reader.Edges(snapshotId, cancellationToken))
        { await Write(new { Type = "edge", FromSomId = SemanticObjects.SomIdFor(value.FromId), ToSomId = SemanticObjects.SomIdFor(value.ToId), Item = value }); edges++; }
        foreach (var value in reader.Observations(snapshotId, cancellationToken))
        { await Write(new { Type = "observation", SubjectSomId = SemanticObjects.SomIdFor(value.SubjectId), Item = value }); observations++; }
        foreach (var value in reader.Entries(snapshotId, cancellationToken))
        { await Write(new { Type = "location", SomId = SemanticObjects.SomIdFor(value.NodeId), Item = value }); locations++; }
        foreach (var value in reader.Findings(snapshotId, cancellationToken))
        { await Write(new { Type = "finding", Item = value }); findings++; }
        var result = new SemanticExportResult(ExportSchema, snapshotId, Convert.ToHexStringLower(digest.GetHashAndReset()), bytes,
            objects, edges, observations, locations, findings, kinds);
        await Write(new { Type = "footer", Complete = true, Result = result }, false);
        await destination.FlushAsync(cancellationToken);
        return result;
    }
}
