using System.Collections.Immutable;
using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record PatternDatasetRequest(string[] SnapshotIds, string Console = "storage", int ObjectsPerLayer = 24,
    string GeometryVariant = "native-lift/v1", string ClearMode = "planes", int Round = 1);
public sealed record PatternDatasetObject(SpatialPiece Piece, SemanticObject Source, PatternLearningSubject LearningSubject,
    long StoredObservations, bool EvidenceTruncated)
{
    public ImmutableArray<string> SubjectEvidenceIds { get; init; } = [];
    public ImmutableArray<string> IncidentEdgeEvidenceIds { get; init; } = [];
    public string EvidenceInterpretation { get; init; } = "Subject observations remain separate from supporting incident-edge observations; no source observation is synthesized.";
}
public sealed record PatternDatasetSelectionScope(string CandidateKind, int CandidatesRead, int SelectedSourceObjects,
    int SelectedGameUnits, int SelectedSourcesBeforeUnitExpansion, int SourcesSkippedWithoutEvidence, bool GameUnitLimitReached,
    int SourcesSkippedDuringUnitExpansion, int CandidateSourcesOmitted, bool CandidateWindowTruncated);
public sealed record PatternDatasetFindingScope(long StoredFindings, int FindingsRead, int FindingsExamined,
    int AcceptedPrefixes, int TruncatedPrefixes, bool FindingsTruncated, bool StoppedAtUnitLimit);
public sealed record PatternDatasetOutgoingScope(int SourcesRead, long StoredOutgoingEdges, int EdgesRead,
    int SourcesTruncated, bool Truncated);
public sealed record PatternDatasetLayer(SpatialLayer Layer, Snapshot Snapshot, long CandidateObjects, int SelectedObjects,
    string SelectionPolicy, bool CandidatesTruncated)
{
    public PatternDatasetSelectionScope? Selection { get; init; }
    public PatternDatasetFindingScope? Findings { get; init; }
    public PatternDatasetOutgoingScope? Outgoing { get; init; }
}
public sealed record PatternDatasetEdge(string SnapshotId, MapEdge Edge);
public sealed record PatternDatasetWitnessPrefix(string SnapshotId, string FindingId, string RuleId, int StoredRouteSteps,
    int RetainedSteps, bool PrefixTruncated, bool FindingDeclaredTruncated, string FindingCompleteness,
    bool Accepted, string? PatternId, string? RejectionReason, bool PatternRetained = false);
public sealed record PatternDatasetPatternScope(string PatternId, int MergedCandidates, int DistinctEvidenceReferences,
    int RetainedEvidenceReferences, int DistinctEdgeReferences, int RetainedEdgeReferences, bool EvidenceTruncated, bool EdgesTruncated);
public sealed record PatternDatasetBounds(int CandidateSourcesPerLayer = 500, int PieceEvidenceReferences = 32,
    int FindingsPerLayer = 500, int FindingPrefixNodes = 8, int OutgoingEdgesPerSource = 100,
    int Patterns = 256, int EvidenceReferencesPerPattern = 64, int EdgeReferencesPerPattern = 64,
    int MaximumReturnedEdges = 16384, int MaximumReturnedEvidence = 20480);
public sealed record ObservedPatternDataset(string Schema, PatternDatasetRequest Request,
    ImmutableArray<PatternDatasetLayer> Layers, ImmutableArray<PatternDatasetObject> Objects,
    ImmutableArray<SpatialPattern> Patterns, ImmutableArray<PatternDatasetEdge> Edges, ImmutableArray<Evidence> Evidence,
    int PatternCandidates, bool PatternsTruncated, string DatasetHash)
{
    public PatternDatasetBounds Bounds { get; init; } = new();
    public ImmutableArray<PatternDatasetWitnessPrefix> WitnessPrefixes { get; init; } = [];
    public ImmutableArray<PatternDatasetPatternScope> PatternScopes { get; init; } = [];
    public int UnmergedPatternCandidates { get; init; }
}

/// <summary>Projects recorded source objects into an arena without treating lexical similarity as code flow.</summary>
public sealed class ObservedPatternDatasetReader(MapStore store)
{
    public const string Schema = "filemapper.observed-pattern-dataset/1";
    public static ImmutableArray<string> Consoles { get; } = ["storage", "words", "csharp", "powershell"];

    public ObservedPatternDataset Read(PatternDatasetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SnapshotIds is null || request.SnapshotIds.Length is < 1 or > 4 ||
            request.SnapshotIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) ||
            request.SnapshotIds.Distinct(StringComparer.Ordinal).Count() != request.SnapshotIds.Length)
            throw new ArgumentException("Choose one to four distinct recorded snapshots.");
        request = request with { SnapshotIds = (string[])request.SnapshotIds.Clone() };
        if (!Consoles.Contains(request.Console, StringComparer.Ordinal)) throw new ArgumentException("Unknown Tetrid console.");
        if (request.ObjectsPerLayer is < 2 or > 32 || request.Round is < 1 or > 3)
            throw new ArgumentException("Objects per layer must be 2–32 and round must be 1–3.");
        if (request.ClearMode is not ("planes" or "lines")) throw new ArgumentException("Clear mode must be planes or lines.");
        _ = SpatialGameGeometry.Get("I", request.GeometryVariant);
        using var reader = store.OpenSemanticReader();
        var layers = ImmutableArray.CreateBuilder<PatternDatasetLayer>();
        var objects = ImmutableArray.CreateBuilder<PatternDatasetObject>();
        var patterns = ImmutableArray.CreateBuilder<SpatialPattern>();
        var sourceEdges = new Dictionary<string, PatternDatasetEdge>(StringComparer.Ordinal);
        var sourceEvidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        var witnessPrefixes = ImmutableArray.CreateBuilder<PatternDatasetWitnessPrefix>();
        // Every layer and every lookup below shares this read transaction.
        foreach (var snapshotId in request.SnapshotIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = reader.Snapshot(snapshotId);
            if (snapshot.FinishedAt is null) throw new InvalidOperationException("Finish or stop collection before freezing an arena.");
            var layerId = "layer:" + snapshotId;
            var selected = new Dictionary<string, PatternDatasetObject>(StringComparer.Ordinal);
            var language = request.Console == "csharp" ? "C#" : "PowerShell";
            var candidates = request.Console is "storage" or "words"
                ? reader.Nodes(snapshotId, "File", limit: 500, cancellationToken: cancellationToken)
                : reader.NodesForLanguage(snapshotId, language, limit: 500, cancellationToken: cancellationToken);
            var candidateValues = candidates.Items;
            var skippedWithoutEvidence = 0; long storedFindings = 0; var findingsRead = 0; var examinedFindings = 0;
            var acceptedPrefixes = 0; var truncatedPrefixes = 0; var stoppedAtUnitLimit = false;
            var outgoingSources = 0; long storedOutgoing = 0; var outgoingRead = 0; var outgoingTruncatedSources = 0;

            PatternDatasetObject? Add(MapStore.ObservedNode observed, Evidence? requiredSubject = null, IReadOnlyList<Evidence>? incidentEvidence = null)
            {
                if (selected.TryGetValue(observed.Node.Id, out var found)) return found;
                if (selected.Count == request.ObjectsPerLayer) return null;
                var source = SemanticObjects.Create(snapshot, observed.Node, observed.ObservedFrom, observed.ObservedTo);
                var subject = reader.SubjectObservations(snapshotId, observed.Node.Id, 32, cancellationToken).ToList();
                if (requiredSubject is not null && subject.All(e => e.Id != requiredSubject.Id)) subject.Insert(0, requiredSubject);
                var incident = (incidentEvidence ?? []).Where(e => e.SnapshotId == snapshotId).DistinctBy(e => e.Id, StringComparer.Ordinal).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
                var evidence = incident.Concat(subject).DistinctBy(e => e.Id, StringComparer.Ordinal).Take(32).ToArray();
                if (evidence.Length == 0) { skippedWithoutEvidence++; return null; }
                foreach (var item in evidence) sourceEvidence[snapshotId + ":" + item.Id] = item;
                var pieceId = "spatial-piece:" + CanonicalJson.Sha256(new { layerId, source.SomId, Unit = "object" });
                var piece = new SpatialPiece(pieceId, source.SomId, source.ObservationId, observed.Node.Id,
                    layerId, source.ShapeType, request.GeometryVariant, Clip(source.Label, 512),
                    RetainTokens(SemanticObjects.Tokenize(source.Label)).Tokens,
                    evidence.Select(e => e.Id).ToImmutableArray());
                found = new(piece, source, LearningSubject(snapshot, observed, source), observed.ObservationCount,
                    observed.ObservationCount > evidence.Count(e => e.SubjectId == observed.Node.Id))
                {
                    SubjectEvidenceIds = evidence.Where(e => e.SubjectId == observed.Node.Id).Select(e => e.Id).ToImmutableArray(),
                    IncidentEdgeEvidenceIds = evidence.Where(e => incident.Any(i => i.Id == e.Id)).Select(e => e.Id).ToImmutableArray()
                };
                selected.Add(observed.Node.Id, found);
                return found;
            }

            if (request.Console is "storage" or "words")
            {
                // Grouping is based on exact recorded parent paths, not an inferred physical directory identity.
                var ordered = candidateValues.Where(value => value.Node.Path is not null)
                    .GroupBy(value => Parent(value.Node.Path!), StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count() >= 2).ThenBy(group => group.Key, StringComparer.Ordinal);
                foreach (var group in ordered)
                {
                    foreach (var value in group.OrderBy(v => v.Node.Id, StringComparer.Ordinal))
                    {
                        if (selected.Count == request.ObjectsPerLayer) break;
                        var metadata = reader.MatchingFilesystemMetadata(snapshotId, value.Node.Id, value.Node.Path!, cancellationToken);
                        if (metadata is not null) _ = Add(value, metadata);
                    }
                    if (selected.Count == request.ObjectsPerLayer) break;
                }
            }
            else
            {
                // Prefer stored witness paths. Ordered endpoints and every cited edge/evidence are checked.
                var findingPage = reader.FindingPrefixes(snapshotId, limit: 500, maximumSteps: 8, cancellationToken: cancellationToken);
                storedFindings = findingPage.Total; findingsRead = findingPage.Items.Count;
                foreach (var finding in findingPage.Items)
                {
                    if (selected.Count == request.ObjectsPerLayer) { stoppedAtUnitLimit = true; break; }
                    examinedFindings++;
                    var expectedCollector = request.Console == "csharp" ? "roslyn-safe" : "powershell-parser";
                    var route = finding.Steps;
                    var prefixTruncated = finding.StoredRouteSteps > route.Count;
                    if (prefixTruncated) truncatedPrefixes++;
                    void RecordPrefix(bool accepted, string? patternId, string? reason) => witnessPrefixes.Add(new(snapshotId,
                        finding.Id, finding.RuleId, finding.StoredRouteSteps, route.Count, prefixTruncated,
                        finding.DeclaredTruncated, finding.Completeness, accepted, patternId, reason));
                    if (route.Count < 2 || route[0].EdgeId is not null) { RecordPrefix(false, null, "Witness has fewer than two steps or an invalid initial edge."); continue; }
                    var valid = true;
                    var routeNodes = new List<MapStore.ObservedNode>();
                    var routeEdges = new List<MapEdge>();
                    var evidenceIds = new List<string>();
                    for (var index = 0; index < route.Count; index++)
                    {
                        var node = reader.Node(snapshotId, route[index].NodeId, cancellationToken);
                        if (node is null) { valid = false; break; }
                        routeNodes.Add(node);
                        if (index == 0) continue;
                        var edge = route[index].EdgeId is { } edgeId ? reader.Edge(snapshotId, edgeId) : null;
                        var evidence = edge is null ? null : reader.Observation(snapshotId, edge.EvidenceId);
                        if (edge is null || edge.FromId != route[index - 1].NodeId || edge.ToId != route[index].NodeId ||
                            evidence is null || evidence.SnapshotId != snapshotId || evidence.SourceKind != expectedCollector ||
                            evidence.ErrorCode is not null) { valid = false; break; }
                        routeEdges.Add(edge); evidenceIds.Add(evidence.Id);
                    }
                    if (!valid || routeNodes.Select(n => n.Node.Id).Distinct(StringComparer.Ordinal).Count() != routeNodes.Count)
                    { RecordPrefix(false, null, "Missing, repeated, mismatched or unsupported recorded witness node/edge/evidence."); continue; }
                    if (selected.Count + routeNodes.Count(n => !selected.ContainsKey(n.Node.Id)) > request.ObjectsPerLayer)
                    { RecordPrefix(false, null, "The bounded selected-unit set cannot hold this whole prefix."); continue; }
                    var members = routeNodes.Select(value => Add(value, incidentEvidence: routeEdges
                        .Where(e => e.FromId == value.Node.Id || e.ToId == value.Node.Id)
                        .Select(e => reader.Observation(snapshotId, e.EvidenceId)!).ToArray())!.Piece.Id).ToImmutableArray();
                    foreach (var edge in routeEdges)
                    {
                        sourceEdges[snapshotId + ":" + edge.Id] = new(snapshotId, edge);
                        var evidence = reader.Observation(snapshotId, edge.EvidenceId)!;
                        sourceEvidence[snapshotId + ":" + evidence.Id] = evidence;
                    }
                    var patternId = PatternId(layerId, "code", members);
                    patterns.Add(new(patternId,
                        layerId, "code", members, evidenceIds.Distinct(StringComparer.Ordinal).ToImmutableArray(),
                        routeEdges.Select(edge => edge.Id).ToImmutableArray()));
                    acceptedPrefixes++; RecordPrefix(true, patternId, null);
                }
                foreach (var value in candidateValues)
                {
                    if (selected.Count == request.ObjectsPerLayer) break;
                    _ = Add(value);
                }
                foreach (var value in selected.Values.ToArray())
                {
                    var outgoing = reader.OutgoingPage(snapshotId, value.Piece.SourceNodeId, limit: 100, cancellationToken: cancellationToken);
                    outgoingSources++; storedOutgoing += outgoing.Total; outgoingRead += outgoing.Items.Count;
                    if (outgoing.Total > outgoing.Items.Count) outgoingTruncatedSources++;
                    foreach (var edge in outgoing.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!selected.TryGetValue(edge.ToId, out var target) || edge.FromId == edge.ToId) continue;
                        var evidence = reader.Observation(snapshotId, edge.EvidenceId);
                        if (evidence is null || evidence.SnapshotId != snapshotId || evidence.ErrorCode is not null ||
                            evidence.SourceKind != (request.Console == "csharp" ? "roslyn-safe" : "powershell-parser")) continue;
                        sourceEdges[snapshotId + ":" + edge.Id] = new(snapshotId, edge);
                        sourceEvidence[snapshotId + ":" + evidence.Id] = evidence;
                        patterns.Add(new(PatternId(layerId, "code", [value.Piece.Id, target.Piece.Id]), layerId, "code",
                            [value.Piece.Id, target.Piece.Id], [evidence.Id], [edge.Id]));
                    }
                }
            }

            var layerObjects = selected.Values.OrderBy(value => value.Piece.SourceNodeId, StringComparer.Ordinal).ToList();
            if (request.Console == "words")
            {
                layerObjects.Clear();
                foreach (var value in selected.Values.OrderBy(v => v.Piece.SourceNodeId, StringComparer.Ordinal))
                {
                    var tokens = SemanticObjects.Tokenize(value.Source.Label);
                    if (tokens.Length is < 2 or > 8 || tokens.Any(token => token.Length > 128) ||
                        layerObjects.Count + tokens.Length > request.ObjectsPerLayer ||
                        tokens.Any(token => !value.Source.SomeShapes.Any(word => word.Token == token))) continue;
                    var members = ImmutableArray.CreateBuilder<string>();
                    for (var index = 0; index < tokens.Length; index++)
                    {
                        var word = value.Source.SomeShapes.FirstOrDefault(w => w.Token == tokens[index]);
                        if (word is null) throw new InvalidOperationException("Native filename word is absent from its semantic projection.");
                        var pieceId = "spatial-word:" + CanonicalJson.Sha256(new { layerId, value.Source.SomId, Index = index, word.WordId });
                        var piece = value.Piece with { Id = pieceId, SomId = word.WordId, BaseShape = word.ShapeType,
                            Label = word.Token, Tokens = [word.Token], UnitKind = "word", SourceSomId = value.Source.SomId };
                        var wordSubject = value.LearningSubject with
                        {
                            SomId = word.WordId,
                            ObjectHash = CanonicalJson.Sha256(new { SourceObjectHash = value.Source.Hashes.ObjectHash, Word = word }),
                            ShapeType = word.ShapeType,
                            GeometryHash = CanonicalJson.Sha256(new { Model = "native-word-geometry/v1", word.ShapeType, Cells = ShapeCatalog.Shapes[word.ShapeType] }),
                            Words = new("observed", "recorded", [word.Token], ["native semantic word shape from recorded filename"])
                        };
                        layerObjects.Add(value with { Piece = piece, LearningSubject = wordSubject }); members.Add(pieceId);
                    }
                    patterns.Add(new(PatternId(layerId, "filename", members.ToImmutable()),
                        layerId, "filename", members.ToImmutable(), value.Piece.EvidenceIds, []));
                }
            }
            else if (request.Console == "storage")
            {
                // Equal recorded names across folders are a lexical pattern, never a claim of
                // equal bytes or physical identity. Keep this separate from exact-parent paths.
                foreach (var group in layerObjects.GroupBy(value => value.Source.Label, StringComparer.Ordinal))
                foreach (var chunk in group.Chunk(8).Where(chunk => chunk.Length >= 2))
                {
                    var members = chunk.Select(value => value.Piece.Id).ToImmutableArray();
                    patterns.Add(new(PatternId(layerId, "filename", members), layerId, "filename", members,
                        chunk.SelectMany(value => value.Piece.EvidenceIds).Distinct(StringComparer.Ordinal).ToImmutableArray(), []));
                }
                foreach (var group in layerObjects.GroupBy(value => Parent(value.Source.SourceReference.Path!), StringComparer.Ordinal))
                foreach (var chunk in group.Chunk(8).Where(chunk => chunk.Length >= 2))
                {
                    var members = chunk.Select(value => value.Piece.Id).ToImmutableArray();
                    patterns.Add(new(PatternId(layerId, "path", members),
                        layerId, "path", members, chunk.SelectMany(value => value.Piece.EvidenceIds).Distinct(StringComparer.Ordinal).ToImmutableArray(), []));
                }
            }
            objects.AddRange(layerObjects);
            var actualFrom = layerObjects.Where(o => o.Source.ObservedWindow.From is not null).Select(o => o.Source.ObservedWindow.From).Min();
            var actualTo = layerObjects.Where(o => o.Source.ObservedWindow.To is not null).Select(o => o.Source.ObservedWindow.To).Max();
            var selectedSourceIds = layerObjects.Select(o => o.Piece.SourceNodeId).ToHashSet(StringComparer.Ordinal);
            var omittedCandidates = candidateValues.Count(v => !selectedSourceIds.Contains(v.Node.Id));
            layers.Add(new(new(layerId, snapshotId, actualFrom, actualTo, snapshot.Completeness), snapshot,
                candidates.Total, layerObjects.Count, request.Console is "storage" or "words"
                    ? "first-500-files-by-node-id; exact-parent paths and equal recorded filename groups (lexical, not equal bytes or identity); complete matching filesystem metadata; word expansion requires 2–8 native filename tokens and fits the game-unit budget"
                    : "first 500 recorded findings by id; checked witness prefixes up to 8 nodes; then first 500 explicitly language-labeled functions; bounded directed edges",
                candidates.Total > candidates.Items.Count || omittedCandidates > 0)
            {
                Selection = new(request.Console is "storage" or "words" ? "File" : "Function with recorded language " + language,
                    candidates.Items.Count, selectedSourceIds.Count, layerObjects.Count, selected.Count, skippedWithoutEvidence,
                    layerObjects.Count == request.ObjectsPerLayer, selected.Count - selectedSourceIds.Count, omittedCandidates, candidates.Total > candidates.Items.Count),
                Findings = new(storedFindings, findingsRead, examinedFindings, acceptedPrefixes, truncatedPrefixes,
                    storedFindings > examinedFindings, stoppedAtUnitLimit),
                Outgoing = new(outgoingSources, storedOutgoing, outgoingRead, outgoingTruncatedSources, outgoingTruncatedSources > 0)
            });
        }
        if (objects.Count == 0) throw new InvalidOperationException("No eligible recorded objects exist for this console in the selected snapshots.");
        var finalObjects = objects.ToImmutable();
        if (request.Round == 2) finalObjects = finalObjects.OrderBy(value => CanonicalJson.Sha256(new { Round = 2, value.Piece.Id }), StringComparer.Ordinal).ToImmutableArray();
        else if (request.Round == 3) finalObjects = finalObjects.Reverse().ToImmutableArray();
        var finalLayers = layers.ToImmutable();
        var snapshotsByLayer = finalLayers.ToDictionary(l => l.Layer.Id, l => l.Snapshot.Id, StringComparer.Ordinal);
        var merged = patterns.GroupBy(p => PatternId(p.LayerId, p.Kind, p.MemberPieceIds), StringComparer.Ordinal).Select(group =>
        {
            var sample = group.First(); var snapshotId = snapshotsByLayer[sample.LayerId];
            var allEvidence = group.SelectMany(p => p.EvidenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var allEdges = group.SelectMany(p => p.SourceEdgeIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var retainedEdges = allEdges.Take(64).ToImmutableArray();
            // Keep evidence for every retained edge before filling the remaining reference allowance.
            var requiredEvidence = retainedEdges.Select(id => sourceEdges[snapshotId + ":" + id].Edge.EvidenceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var retainedEvidence = requiredEvidence.Concat(allEvidence).Distinct(StringComparer.Ordinal).Take(64).Order(StringComparer.Ordinal).ToImmutableArray();
            var pattern = new SpatialPattern(group.Key, sample.LayerId, sample.Kind, sample.MemberPieceIds.Order(StringComparer.Ordinal).ToImmutableArray(), retainedEvidence, retainedEdges);
            return (Pattern: pattern, Scope: new PatternDatasetPatternScope(group.Key, group.Count(), allEvidence.Length, retainedEvidence.Length,
                allEdges.Length, retainedEdges.Length, allEvidence.Length > retainedEvidence.Length, allEdges.Length > retainedEdges.Length));
        }).OrderBy(value => value.Pattern.Id, StringComparer.Ordinal).ToArray();
        var retained = merged.Take(256).ToArray();
        var finalPatterns = retained.Select(p => p.Pattern).ToImmutableArray();
        var patternScopes = retained.Select(p => p.Scope).ToImmutableArray();
        var retainedIds = finalPatterns.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var finalWitnesses = witnessPrefixes.Select(p => p with { PatternRetained = p.PatternId is not null && retainedIds.Contains(p.PatternId) }).ToImmutableArray();
        var edgeKeys = finalPatterns.SelectMany(p => p.SourceEdgeIds.Select(id => snapshotsByLayer[p.LayerId] + ":" + id)).ToHashSet(StringComparer.Ordinal);
        var evidenceKeys = finalObjects.SelectMany(o => o.Piece.EvidenceIds.Select(id => o.Source.SourceReference.SnapshotId + ":" + id))
            .Concat(finalPatterns.SelectMany(p => p.EvidenceIds.Select(id => snapshotsByLayer[p.LayerId] + ":" + id))).ToHashSet(StringComparer.Ordinal);
        var finalEdges = edgeKeys.Order(StringComparer.Ordinal).Select(key => sourceEdges[key]).ToImmutableArray();
        var finalEvidence = evidenceKeys.Order(StringComparer.Ordinal).Select(key => sourceEvidence[key]).ToImmutableArray();
        var bounds = new PatternDatasetBounds();
        var hash = CanonicalJson.Sha256(new { Schema, Request = request, Layers = finalLayers, Objects = finalObjects, Patterns = finalPatterns,
            Edges = finalEdges, Evidence = finalEvidence, PatternCandidates = merged.Length, PatternsTruncated = merged.Length > 256,
            Bounds = bounds, WitnessPrefixes = finalWitnesses, PatternScopes = patternScopes, UnmergedPatternCandidates = patterns.Count });
        return new(Schema, request, finalLayers, finalObjects, finalPatterns, finalEdges, finalEvidence,
            merged.Length, merged.Length > 256, hash)
        { Bounds = bounds, WitnessPrefixes = finalWitnesses, PatternScopes = patternScopes, UnmergedPatternCandidates = patterns.Count };
    }

    public static PatternLearningSubject LearningSubject(Snapshot snapshot, MapStore.ObservedNode observed, SemanticObject? source = null)
    {
        source ??= SemanticObjects.Create(snapshot, observed.Node, observed.ObservedFrom, observed.ObservedTo);
        var name = RetainTokens(SemanticObjects.Tokenize(observed.Node.Name));
        var words = new PatternLearningTokens("observed", "recorded", name.Tokens, ["native-node.name"], name.Truncated);
        var path = PatternLearningTokens.Unknown;
        if (observed.Node.Path is { } fullPath)
        {
            var root = snapshot.Root.Replace('\\', '/').TrimEnd('/') + "/";
            var normalized = fullPath.Replace('\\', '/');
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var tokens = RetainTokens(SemanticObjects.Tokenize(normalized[root.Length..]));
                path = new("observed", "recorded", tokens.Tokens, ["native-node.path; root-relative lexical tokens"], tokens.Truncated);
            }
        }
        var command = PatternLearningTokens.Unknown;
        if (TextProperty(observed.Node, "language") == "PowerShell" && TextProperty(observed.Node, "symbolKind") == "command")
            command = new("observed", "recorded", name.Tokens, ["native-node.name; PowerShell command AST; not execution history"], name.Truncated);
        return new(source.SomId, source.Hashes.ObjectHash, source.ObservationId, observed.Node.Id, snapshot.Id,
            source.ShapeType, source.Hashes.GeometryHash, words, path, command, observed.ObservedFrom, observed.ObservedTo);
    }
    private static (ImmutableArray<string> Tokens, bool Truncated) RetainTokens(string[] tokens)
    {
        var retained = tokens.Where(token => token.Length <= 128).Take(64).ToImmutableArray();
        return (retained, retained.Length != tokens.Length);
    }
    private static string? TextProperty(MapNode node, string key) => node.Properties.TryGetValue(key, out var value)
        ? value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json ? json.GetString() : value as string : null;
    private static string Parent(string path) { var normalized = path.Replace('\\', '/'); var index = normalized.LastIndexOf('/'); return index < 0 ? "" : normalized[..index]; }
    private static string PatternId(string layerId, string kind, IEnumerable<string> members) => "spatial-pattern:" +
        CanonicalJson.Sha256(new { LayerId = layerId, Kind = kind, Members = members.Order(StringComparer.Ordinal).ToArray() });
    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..(char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum)];
}
