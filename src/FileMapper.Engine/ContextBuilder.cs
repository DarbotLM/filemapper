using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileMapper.Domain;
using FileMapper.Storage;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

/// <summary>Deterministic metadata retrieval with recorded graph connections, not a program-flow proof.</summary>
public static partial class ContextBuilder
{
    private sealed record Candidate(ContextNode Node, Dictionary<string, int> Components, string[] Reasons, ContextStep[] Path)
    { public int Score => Components.Values.Sum(); }
    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Terms();

    public static ContextPackage Build(MapStore store, string snapshotId, string search, int byteBudget,
        CancellationToken cancellationToken = default)
    {
        if (byteBudget is < 256 or > 100000) throw new ArgumentOutOfRangeException(nameof(byteBudget), "Context byte budget must be 256–100,000.");
        ArgumentNullException.ThrowIfNull(search);
        if (search.EnumerateRunes().Count() > 512 || search.Any(char.IsControl)) throw new ArgumentException("Context search must be at most 512 Unicode code points with no control characters.", nameof(search));
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = store.OpenSemanticReader();
        reader.SetContextCancellation(cancellationToken);
        try { return BuildPinned(reader, snapshotId, search.Trim(), byteBudget, cancellationToken); }
        catch (SqliteException error) when (error.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Context query was cancelled.", error, cancellationToken); }
    }

    private static ContextPackage BuildPinned(MapStore.SemanticReader reader, string id, string phrase, int budget, CancellationToken token)
    {
        var snapshot = reader.Snapshot(id);
        var allTerms = Terms().Matches(phrase).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var terms = allTerms.Take(8).ToArray();
        var limits = new SortedSet<string>(StringComparer.Ordinal);
        if (allTerms.Length > 8) limits.Add("query-terms:8");
        var package = new ContextPackage(id, snapshot.Completeness, budget)
        {
            QueryTerms = terms,
            Scope = [
                "Ranking units are graph node identities, not distinct physical files or verified relevance labels.",
                "Literal name/path/ID retrieval; SQLite candidate case folding is ASCII. At most 64 seeds, 256 candidate nodes, two connection hops, 12 expanded nodes per hop and 32 incident edges per direction/node. CONTAINS is not expanded.",
                "Connections retain their recorded direction but are traversed both ways for context. They are not execution, taint, verified ownership, or content-equality proofs. Test-like names are a disclosed heuristic.",
                "Up to three latest recorded subject observations and 512-character value previews. Full object properties and source files are not read. No current source is substituted for historical evidence.",
                "Budget covers the complete compact UTF-8 context JSON, excluding transport framing. It is not a model-token budget. No top-20 precision or ten-million-node latency acceptance is implied."
            ]
        };
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        if (phrase.Length > 0)
        {
            var seeds = reader.ContextSeeds(id, phrase, terms, 65, token);
            if (seeds.Count > 64) limits.Add("literal-seeds:64");
            foreach (var node in seeds.Take(64))
            {
                var components = Match(node, phrase, terms); components["literalSeed"] = 100;
                candidates.Add(node.Id, new(node, components, ["Recorded identity/name/path matched the literal query."], [new(node, null)]));
            }
        }
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var edgesSeen = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < 2; depth++)
        {
            var frontier = Ordered(candidates.Values.Where(c => c.Path.Length == depth + 1 && !expanded.Contains(c.Node.Id))).ToArray();
            if (frontier.Length > 12) limits.Add("expanded-nodes-per-hop:12");
            foreach (var parent in frontier.Take(12))
            {
                token.ThrowIfCancellationRequested(); expanded.Add(parent.Node.Id);
                foreach (bool incoming in new[] { false, true })
                {
                    var incident = reader.ContextIncident(id, parent.Node.Id, incoming, 33, token);
                    if (incident.Count > 32) limits.Add("incident-edges-per-direction:32");
                    foreach (var edge in incident.Take(32))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!edgesSeen.Add(edge.Id)) continue;
                        string other = incoming ? edge.FromId : edge.ToId;
                        if (parent.Path.Any(step => step.Node.Id == other)) continue;
                        if (!candidates.ContainsKey(other) && candidates.Count >= 256) { limits.Add("candidate-nodes:256"); continue; }
                        var node = reader.ContextNodeById(id, other, token);
                        var evidence = reader.ContextEvidenceById(id, edge.EvidenceId, token);
                        if (node is null || evidence is null) { limits.Add("missing-node-or-edge-evidence"); continue; }
                        if (evidence.ValueTruncated) limits.Add("evidence-value-preview:512");
                        var components = Match(node, phrase, terms);
                        components["parentRelevance"] = parent.Score / 2;
                        components["recordedRelationship"] = edge.Kind switch
                        { "OWNS" => 35, "MODIFIED_BY" or "CHANGED_BY" => 30, "TESTS" => 35, "GENERATED_FROM" or "DEPENDS_ON" => 25, _ => 10 };
                        components["connectionDepthPenalty"] = -(depth + 1) * 5;
                        bool testLike = node.Name.Contains("test", StringComparison.OrdinalIgnoreCase) || node.Name.Contains("spec", StringComparison.OrdinalIgnoreCase);
                        if (testLike) components["testNameHeuristic"] = 8;
                        var reasons = new List<string> { $"Recorded {edge.Kind} connection to {parent.Node.Id}; {(incoming ? "incoming" : "outgoing")} edge, not inferred program flow." };
                        if (testLike) reasons.Add("Test/spec-like name is a naming heuristic, not proof this node tests the task.");
                        var connection = new ContextConnection(edge.Id, edge.FromId, edge.ToId, edge.Kind, edge.Confidence, evidence);
                        var candidate = new Candidate(node, components, reasons.ToArray(), [..parent.Path, new(node, connection)]);
                        if (!candidates.TryGetValue(other, out var previous) || candidate.Score > previous.Score) candidates[other] = candidate;
                    }
                }
            }
        }
        if (candidates.Values.Any(c => c.Path.Length == 3)) limits.Add("connection-depth:2");
        package.CandidatesConsidered = candidates.Count;
        var items = new List<ContextItem>();
        foreach (var candidate in Ordered(candidates.Values))
        {
            token.ThrowIfCancellationRequested();
            var observations = reader.ContextObservations(id, candidate.Node.Id, token);
            if (observations.Count > 3) limits.Add("subject-observations:3");
            if (observations.Take(3).Any(e => e.ValueTruncated)) limits.Add("evidence-value-preview:512");
            items.Add(new(candidate.Node, candidate.Score, candidate.Components, candidate.Reasons, candidate.Path, observations.Take(3).ToArray()));
        }
        // Reserve truthful truncation metadata before packing, then measure the final envelope again.
        package.Truncated = true; package.TruncationReasons = [..limits, "byte-budget"];
        bool omitted = false;
        foreach (var item in items)
        {
            package.Items.Add(item);
            if (Size(package) + 1 > budget) { package.Items.RemoveAt(package.Items.Count - 1); omitted = true; }
        }
        package.Truncated = omitted || limits.Count > 0;
        package.TruncationReasons = omitted ? [..limits, "byte-budget"] : [..limits];
        if (Size(package) > budget)
        {
            package.Items.Clear(); package.Scope = null; package.QueryTerms = null; package.CandidatesConsidered = null;
            package.Truncated = true; package.TruncationReasons = ["byte-budget"];
        }
        if (Size(package) > budget) throw new ArgumentException("Byte budget is too small for the snapshot's minimal context envelope.", nameof(budget));
        token.ThrowIfCancellationRequested();
        return package;
    }

    private static int Size(ContextPackage package) => JsonSerializer.SerializeToUtf8Bytes(package, Exporter.Json).Length;
    private static IOrderedEnumerable<Candidate> Ordered(IEnumerable<Candidate> values) => values.OrderByDescending(c => c.Score).ThenBy(c => c.Node.Id, StringComparer.Ordinal);
    private static Dictionary<string, int> Match(ContextNode node, string phrase, IReadOnlyList<string> terms) => new()
    {
        ["exactIdentity"] = node.Id == phrase ? 500 : 0,
        ["exactNameOrPath"] = node.Name.Equals(phrase, StringComparison.OrdinalIgnoreCase) || string.Equals(node.Path, phrase, StringComparison.OrdinalIgnoreCase) ? 120 : 0,
        ["nameTerms"] = terms.Count(term => node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) * 20,
        ["pathTerms"] = terms.Count(term => node.Path?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) * 5
    };
}
