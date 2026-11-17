using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

namespace FileMapper.Acceptance;

internal static class SemanticIntegration
{
    public static async Task Run(string directory, AcceptanceReport report, CancellationToken token)
    {
        var sourceRoot = Path.Combine(directory, "source"); Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "Flow.cs");
        await File.WriteAllTextAsync(source, """
            using System;
            using System.Diagnostics;
            public static class Flow
            {
                public static void Unsafe()
                {
                    var command = Console.ReadLine();
                    var copy = command;
                    Process.Start(copy!);
                }
                public static void Safe()
                {
                    var command = Console.ReadLine();
                    command = "fixed-program";
                    Process.Start(command);
                }
            }
            """, token);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Flow.ps1"), "$value = Read-Host\n$copy = $value\nInvoke-Expression $copy\n$value = 'Get-Date'\nInvoke-Expression $value\n", token);
        var store = new MapStore(Path.Combine(directory, "semantic.db")); store.Initialize();
        await using var scans = new ScanCoordinator(store);
        var scan = scans.StartScan(new(sourceRoot, HashFiles: true)); scan = await scans.WaitAsync(scan.Id).WaitAsync(token);
        if (scan.Status != "completed") throw new InvalidOperationException(scan.Error);
        var job = scans.StartAnalysis(scan.SnapshotId); job = await scans.WaitAsync(job.Id).WaitAsync(token);
        report.Check("engine-analysis-commit", job.Status == "completed", $"snapshot={scan.SnapshotId}; status={job.Status}; error={job.Error}");
        var findings = store.GetFindings(scan.SnapshotId);
        report.Check("default-model-safe-and-vulnerable", findings.Count == 2
            && findings.Count(f => f.RuleId == "FM-CS-COMMAND") == 1 && findings.Count(f => f.RuleId == "FM-PS-0001") == 1,
            "Actual rules: " + string.Join(",", findings.Select(f => f.RuleId)));
        var query = new QueryEngine(store);
        var explanations = findings.Select(f => query.ExplainFinding(scan.SnapshotId, f.Id)).ToArray();
        report.Check("saved-witness-explanation", explanations.Length == 2 && explanations.All(e => e.Steps.Count >= 3
            && e.Observations.Count > 0 && e.ContentTrust == "untrusted-data" && e.Finding.Assumptions.Count > 0
            && e.Steps.Skip(1).All(s => s.Via is not null && e.Observations.Any(o => o.Id == s.Via.EvidenceId))),
            "Source, propagation and sink steps resolve to persisted graph/evidence in both languages.");
        await File.WriteAllTextAsync(Path.Combine(directory, "explanations.json"), JsonSerializer.Serialize(explanations, Exporter.Json), token);
        var sarifPath = Path.Combine(directory, "findings.sarif.json");
        await using (var output = File.Create(sarifPath)) await Exporter.WriteAsync(store, scan.SnapshotId, "sarif", output, token);
        using (var sarif = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath, token)))
        {
            var results = sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToArray();
            report.Check("sarif-ordered-flow", results.Length == 2 && results.All(r =>
                r.GetProperty("codeFlows")[0].GetProperty("threadFlows")[0].GetProperty("locations").GetArrayLength() >= 3
                && r.GetProperty("properties").GetProperty("assumptions").GetArrayLength() > 0), "Two SARIF results retain ordered source locations and model assumptions.");
        }
        bool immutable = false;
        try { scans.StartAnalysis(scan.SnapshotId); } catch (InvalidOperationException) { immutable = true; }
        report.Check("immutable-analysis", immutable, "A completed analysis cannot be silently replaced.");
        var changed = scans.StartScan(new(sourceRoot, HashFiles: true)); changed = await scans.WaitAsync(changed.Id).WaitAsync(token);
        await File.AppendAllTextAsync(source, "\n// changed after scan\n", token);
        var stale = scans.StartAnalysis(changed.SnapshotId); stale = await scans.WaitAsync(stale.Id).WaitAsync(token);
        report.Check("stale-source-rejected", stale.Status == "failed" && store.GetFindings(changed.SnapshotId).Count == 0
            && !store.GetObservations(changed.SnapshotId).Any(e => e.Predicate == "analysis.committed"), "Post-scan source mutation cannot publish findings or a successful analysis marker.");
        using (store.AcquireWriter()) ValidateRollback(store, source, report);
        report.Measurements["snapshotId"] = scan.SnapshotId;
        report.Measurements["findingIds"] = findings.Select(f => f.Id).ToArray();
        report.Limitations.Add("This checks production ingestion/explanation/export boundaries with two modeled flows. Language accuracy and provenance acceptance use separate labeled corpora. No fixture source is executed.");
    }

    private static void ValidateRollback(MapStore store, string path, AcceptanceReport report)
    {
        foreach (var fault in new[] { "edge-direction", "missing-evidence", "node-location", "truncated-complete" })
        {
            var snapshot = store.CreateSnapshot(Path.GetDirectoryName(path)!); store.FinishSnapshot(snapshot.Id);
            MapNode[] nodes = [new("source", "FlowOperation", "source", path, new() { ["line"] = 1 }), new("sink", "FlowOperation", "sink", path, new() { ["line"] = 2 })];
            Evidence[] evidence = [new("fact", snapshot.Id, "sink", "flow", "modeled", "acceptance", path, DateTimeOffset.UtcNow),
                new("marker", snapshot.Id, snapshot.Id, "analysis.committed", "transaction test", "acceptance", path, DateTimeOffset.UtcNow)];
            MapEdge[] edges = [new("edge", fault == "edge-direction" ? "sink" : "source", "sink", "FLOWS_TO", fault == "missing-evidence" ? "absent" : "fact")];
            var finding = new Finding("finding", "acceptance", "warning", "rollback", path, 2, "test", .8, ["fact"])
            {
                Status = "modeled-flow", ModelVersion = "acceptance-v1", Completeness = fault == "truncated-complete" ? "complete" : "partial",
                Truncated = fault == "truncated-complete", TruncationReason = fault == "truncated-complete" ? "budget" : null,
                Route = [new("source", null, path, fault == "node-location" ? 9 : 1, "source"), new("sink", "edge", path, 2, "sink")]
            };
            bool rejected = false;
            try { store.SaveAnalysis(snapshot.Id, new(nodes, edges, evidence, [finding])); } catch (ArgumentException) { rejected = true; }
            report.Check("atomic-rejection-" + fault, rejected && store.GetNodes(snapshot.Id).Total == 0 && store.GetEdges(snapshot.Id).Count == 0
                && store.GetObservations(snapshot.Id).Count == 0 && store.GetFindings(snapshot.Id).Count == 0, "Invalid witness leaves no graph, evidence, finding or analysis marker behind.");
        }
    }
}
