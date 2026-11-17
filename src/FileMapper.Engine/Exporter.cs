using System.Globalization;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Storage;

namespace FileMapper.Engine;

public static class Exporter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static async Task WriteAsync(MapStore store, string id, string format, Stream output, CancellationToken token)
    {
        var query = new QueryEngine(store); var snapshot = query.Require(id);
        if (format == "sarif")
        {
            var findings = store.GetFindings(id);
            await JsonSerializer.SerializeAsync(output, new Dictionary<string, object?>
            {
                ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json", ["version"] = "2.1.0",
                ["runs"] = new[] { new { tool = new { driver = new { name = "FileMapper", version = typeof(Exporter).Assembly.GetName().Version?.ToString(3) ?? "unknown" } },
                    properties = new { snapshotId = id, snapshot.Completeness },
                    results = findings.Select(SarifResult) } }
            }, Json, token); return;
        }
        if (format == "json")
        {
            await output.WriteAsync("{\"schemaVersion\":1,\"snapshot\":"u8.ToArray(), token);
            await JsonSerializer.SerializeAsync(output, snapshot, Json, token);
            await output.WriteAsync(",\"truncated\":false,\"entries\":["u8.ToArray(), token);
            bool first = true;
            foreach (var entry in store.StreamEntries(id, token))
            {
                if (!first) await output.WriteAsync(","u8.ToArray(), token);
                first = false;
                await JsonSerializer.SerializeAsync(output, entry, Json, token);
            }
            await output.WriteAsync("]}"u8.ToArray(), token); return;
        }
        await using var text = new StreamWriter(output, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true);
        if (format == "csv") await text.WriteLineAsync("schema_version,snapshot_id,completeness,path,node_id,kind,logical_bytes,allocated_bytes,modified_at");
        else if (format != "ndjson") throw new ArgumentException("Export format must be json, ndjson, csv, or sarif.");
        foreach (var entry in store.StreamEntries(id, token))
        {
            token.ThrowIfCancellationRequested();
            string line = format == "ndjson" ? JsonSerializer.Serialize(new { schemaVersion = 1, snapshotId = id, snapshot.Completeness, entry }, Json)
                : string.Join(',', "1", Csv(id), Csv(snapshot.Completeness), Csv(entry.Path), Csv(entry.NodeId), Csv(entry.Kind), entry.LogicalBytes.ToString(CultureInfo.InvariantCulture), entry.AllocatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(entry.ModifiedAt.ToString("O")));
            await text.WriteLineAsync(line.AsMemory(), token);
        }
        await text.FlushAsync(token);
    }
    private static object SarifLocation(string path, int line, int column, string reason) => new
    {
        physicalLocation = new { artifactLocation = new { uri = new Uri(Path.GetFullPath(path)).AbsoluteUri },
            region = new { startLine = Math.Max(1, line), startColumn = Math.Max(1, column) } },
        message = new { text = reason }
    };
    private static object SarifResult(Finding finding)
    {
        var result = new Dictionary<string, object?>
        {
            ["ruleId"] = finding.RuleId,
            ["level"] = finding.Severity is "error" or "high" ? "error" : finding.Severity is "note" or "info" or "information" ? "note" : "warning",
            ["message"] = new { text = finding.Title + ": " + finding.Explanation },
            ["locations"] = new[] { SarifLocation(finding.Path, finding.Line, finding.Route.LastOrDefault()?.Column ?? 1, finding.Title) },
            ["partialFingerprints"] = new Dictionary<string, string> { ["filemapperFinding/v1"] = finding.Id },
            ["properties"] = new { finding.Confidence, finding.Cwe, finding.EvidenceIds, finding.Status, finding.ModelVersion,
                finding.Completeness, finding.Truncated, finding.TruncationReason, finding.Assumptions, finding.Route }
        };
        if (finding.Route.Count > 1)
            result["codeFlows"] = new[] { new { message = new { text = "Modeled witness; runtime feasibility is not established." },
                threadFlows = new[] { new { locations = finding.Route.Select((step, index) => new
                {
                    executionOrder = index + 1, location = SarifLocation(step.Path, step.Line, step.Column, step.Reason),
                    properties = new { step.NodeId, step.EdgeId, finding.ModelVersion }
                }).ToArray() } } } };
        return result;
    }
    private static string Csv(string text)
    {
        // Neutralize spreadsheet formulas when importing CSV into Excel.
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
