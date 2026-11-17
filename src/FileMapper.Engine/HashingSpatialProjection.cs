using FileMapper.Domain;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

public sealed record HashingSpatialPage(string RunId, string SnapshotId, long RunRevision,
    string Completeness, string? ResultHash, Page<SemanticObject> Page, string ModelVersion,
    string ProjectionModel, string Scope);

/// <summary>Read-only canonical SOM adapter over a bounded page of committed content-read evidence.
/// Its synthetic observation envelope identifies the hashing session, never a filesystem snapshot.
/// Geometry encodes the native record kind; no distance, volume or learned score is asserted.</summary>
public sealed class HashingSpatialProjection(MapStore store)
{
    public const string ProjectionModel = "hashing-evidence-som/v1";
    public HashingSpatialPage Objects(string runId, string? nodeId = null, int offset = 0, int limit = 24,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit), "Spatial pages require nonnegative offset and 1–50 objects.");
        cancellationToken.ThrowIfCancellationRequested();
        var run = store.GetHashingRun(runId) ?? throw new KeyNotFoundException("Content analysis not found.");
        var records = store.GetHashingEvidencePage(runId, nodeId, offset, limit);
        // Only window fields enter SemanticObjects.Create. The temporary envelope is not persisted
        // and deliberately has a hashing-session URI instead of a filesystem root.
        var session = new Snapshot(run.Id, "filemapper://hashing/" + Uri.EscapeDataString(run.Id), run.StartedAt,
            run.FinishedAt, run.Status, run.Completeness, 0, 0, 0, 0, null, 0);
        var objects = new List<SemanticObject>(records.Items.Count);
        foreach (var record in records.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.RunId != run.Id) throw new InvalidDataException("Content evidence escaped its run scope.");
            HashingEvidence? original = null;
            if (record.CacheHit)
            {
                original = record.CacheSourceEvidenceId is { } sourceId ? store.GetHashingEvidence(sourceId) : null;
                if (original is null || original.CacheHit || original.NodeId != record.NodeId || original.Receipt.Status != "complete" ||
                    original.Receipt.Digest != record.Receipt.Digest || original.Receipt.PolicyHash != record.Receipt.PolicyHash)
                    throw new InvalidDataException("Cached evidence has no consistent original content-read receipt.");
            }
            var readInterval = original?.Receipt ?? record.Receipt;
            var uri = session.Root + "/evidence/" + Uri.EscapeDataString(record.Id);
            var kind = record.Receipt.Stage == "sample" ? "ContentSampleEvidence" : "ContentFullEvidence";
            var source = new MapNode(Identity.For("hashing-evidence-node", record.Id), kind,
                record.Receipt.Stage + " · " + record.NodeId, uri, new()
                {
                    ["projectionModel"] = ProjectionModel, ["hashingRunId"] = run.Id,
                    ["baselineSnapshotId"] = run.SnapshotId, ["hashingEvidenceId"] = record.Id,
                    ["sourceNodeId"] = record.NodeId,
                    ["evidenceIds"] = original is null ? new[] { record.Id } : new[] { record.Id, original.Id },
                    ["hashingEvidence"] = record, ["cacheSourceEvidence"] = original,
                    ["scope"] = "Committed unnamed-stream read evidence. Cache association does not assert a new content read; consult the original cache source receipt."
                });
            objects.Add(SemanticObjects.Create(session, source, readInterval.StartedAt, readInterval.FinishedAt));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(run.Id, run.SnapshotId, run.Revision, run.Completeness, run.ResultHash,
            new(objects, records.Total, records.Offset, records.Limit), SemanticObjects.ModelVersion, ProjectionModel,
            "Bounded page of committed read evidence observed alongside the supplied run revision; an active run may advance. Canonical native geometry encodes category only. Session/observation times are recorded intervals, not an atomic filesystem snapshot or inferred temporal movement.");
    }
}
