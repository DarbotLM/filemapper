using FileMapper.Domain;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record RankItem(FileEntry Item, double Score, Dictionary<string, double> Components, string ModelVersion);
public sealed record DuplicateGroup(string Hash, long LogicalBytes, long? ReclaimableBytes, IReadOnlyList<FileEntry> Items, string Confidence);
public sealed record SnapshotDifference(string Kind, string Path, string NodeId, long BytesDelta, string Explanation);

public sealed class QueryEngine(MapStore store)
{
    public IEnumerable<FileEntry> Entries(string snapshotId)
    {
        foreach (var entry in store.StreamEntries(snapshotId)) yield return entry;
    }
    public object Rank(string id, string by, int limit, CancellationToken cancellationToken = default)
    {
        var snapshot = Require(id);
        if (by is not ("logical" or "allocated" or "age")) throw new ArgumentException("Ranking must be logical, allocated, or age.");
        var items = store.RankEntries(id, by, Math.Clamp(limit, 1, 500), cancellationToken).Select(e =>
        {
            double score = by switch { "allocated" => e.AllocatedBytes ?? -1, "age" => Math.Max(0, (snapshot.StartedAt - e.ModifiedAt).TotalDays), _ => e.LogicalBytes };
            return new RankItem(e, score, new() { [by] = score }, "storage-v1");
        }).Where(x => x.Score >= 0).OrderByDescending(x => x.Score).ThenBy(x => x.Item.Path, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 500)).ToArray();
        return new { snapshotId = id, snapshot.Completeness, truncated = false, items };
    }
    public object Hierarchy(string id, string? parent = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        var snapshot = Require(id);
        parent ??= snapshot.Root;
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var scope = store.GetDirectoryMetrics(id, parent, cancellationToken);
        var entries = store.GetEntries(id, parent: parent, offset: offset, limit: Math.Clamp(limit, 1, 1000), cancellationToken: cancellationToken);
        var items = entries.Items.Select(e => new HierarchyItem(e, e.Kind is "Directory" or "ReparsePoint" ? store.GetDirectoryMetrics(id, e.Path, cancellationToken) : null)).ToArray();
        var completeness = snapshot.Completeness == "complete" && scope.Completeness == "complete" ? "complete" : "incomplete";
        return new { snapshotId = id, completeness, scope, page = new Page<HierarchyItem>(items, entries.Total, entries.Offset, entries.Limit) };
    }
    public object Duplicates(string id, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        var snapshot = Require(id);
        var page = store.GetDuplicateGroups(id, offset, Math.Clamp(limit, 1, 500), cancellationToken: cancellationToken);
        return new { snapshotId = id, snapshot.Completeness, truncated = page.Offset + page.Items.Count < page.Total || page.Items.Any(g => g.ItemsTruncated),
            hashedFiles = store.CountHashedFiles(id, cancellationToken), totalGroups = page.Total, page.Offset, page.Limit, groups = page.Items };
    }
    public StorageBreakdown Breakdown(string id, string by = "extension", string? path = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => store.GetStorageBreakdown(id, by, path, offset, Math.Clamp(limit, 1, 1000), cancellationToken);
    public object Diff(string before, string after, int offset = 0, int limit = 1000)
    {
        var snapshot = Require(after); Require(before);
        var page = store.CompareSnapshots(before, after, offset, Math.Clamp(limit, 1, 10000));
        return new { snapshotId = after, snapshot.Completeness, before, after, scopesMatch = store.ListingScopesMatch(before, after),
            truncated = page.Offset + page.Items.Count < page.Total, totalChanges = page.Total, page.Offset, page.Limit, changes = page.Items };
    }
    public object DuplicateMembers(string id, string hash, long logicalBytes, int offset = 0, int limit = 1000, CancellationToken cancellationToken = default)
    {
        var snapshot = Require(id);
        return new { snapshotId = id, snapshot.Completeness, hash, logicalBytes, page = store.GetDuplicateMembers(id, hash, logicalBytes, offset, Math.Clamp(limit, 1, 10000), cancellationToken) };
    }
    public RouteResult Route(string id, string from, string to, int maxDepth = 12, CancellationToken cancellationToken = default)
    {
        if (maxDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth), "Route depth must be between 0 and 64.");
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = store.OpenRouteReader(id);
        var start = reader.Node(from) ?? throw new KeyNotFoundException("Node not found: " + from);
        if (reader.Node(to) == null) throw new KeyNotFoundException("Node not found: " + to);
        var queue = new Queue<(string Id, int Depth)>(); queue.Enqueue((from, 0));
        var predecessors = new Dictionary<string, MapEdge?>(StringComparer.Ordinal) { [from] = null };
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int examined = 0;
        RouteResult Result(bool found)
        {
            var steps = new List<RouteStep>();
            if (found)
            {
                var current = to;
                while (true)
                {
                    var via = predecessors[current];
                    var node = current == from ? start : reader.Node(current) ?? throw new InvalidOperationException("Observed route references a missing node: " + current);
                    steps.Add(new(node, via));
                    if (via == null) break;
                    current = via.FromId;
                }
                steps.Reverse();
            }
            return new(id, reader.Snapshot.Completeness, steps, reasons.Count > 0,
                reasons.Count > 0 ? string.Join("; ", reasons) : found ? null : "No directed route exists in the observed graph.")
                { VisitedNodes = predecessors.Count, ExaminedEdges = examined, MaxDepth = maxDepth };
        }
        if (from == to) return Result(true);
        while (queue.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Cooperative deadline between adjacency batches; SQLite CPU work is not preempted.
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) { reasons.Add("Cooperative five-second search budget reached between adjacency batches; absence is not established."); break; }
            var edges = reader.Outgoing(item.Id, 1001);
            if (edges.Count > 1000) reasons.Add("Outgoing adjacency budget of 1000 edges reached.");
            foreach (var edge in edges.Take(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (examined >= 100000) { reasons.Add("Edge budget of 100000 reached."); return Result(false); }
                examined++;
                if (predecessors.ContainsKey(edge.ToId)) continue;
                if (item.Depth >= maxDepth) { reasons.Add($"Depth budget of {maxDepth} edges reached."); continue; }
                if (predecessors.Count >= 5000) { reasons.Add("Node budget of 5000 reached."); return Result(false); }
                predecessors.Add(edge.ToId, edge);
                if (edge.ToId == to) return Result(true);
                queue.Enqueue((edge.ToId, item.Depth + 1));
            }
        }
        return Result(false);
    }
    private MapNode FindNode(string id, string nodeId)
    {
        return store.GetNode(id, nodeId) ?? throw new KeyNotFoundException("Node not found: " + nodeId);
    }
    public FindingExplanation ExplainFinding(string id, string findingId)
    {
        var snapshot = Require(id);
        var finding = store.GetFinding(id, findingId) ?? throw new KeyNotFoundException("Finding not found.");
        var evidenceIds = finding.EvidenceIds.ToHashSet(StringComparer.Ordinal);
        var steps = new List<FindingWitnessStep>();
        foreach (var location in finding.Route)
        {
            var via = location.EdgeId is null ? null : store.GetEdge(id, location.EdgeId)
                ?? throw new InvalidOperationException("Stored witness edge is missing.");
            if (via is not null) evidenceIds.Add(via.EvidenceId);
            steps.Add(new(location, FindNode(id, location.NodeId), via));
        }
        var observations = evidenceIds.Order(StringComparer.Ordinal).Select(e => store.GetObservation(id, e)
            ?? throw new InvalidOperationException("Stored finding evidence is missing.")).ToArray();
        return new(id, snapshot.Completeness, finding, steps, observations);
    }
    public ContextPackage Context(string id, string search, int budget, CancellationToken cancellationToken = default)
        => ContextBuilder.Build(store, id, search, budget, cancellationToken);
    public Snapshot Require(string id) => store.GetSnapshot(id) ?? throw new KeyNotFoundException("Snapshot not found.");
}
