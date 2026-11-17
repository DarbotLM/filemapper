using System.Text.Json;
using FileMapper.Extract.CSharp;
using FileMapper.Storage;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "Corpus");
var models = CSharpFlowModels.Default();
models.Sinks.Add(new(new("Corpus.Api", "Sql"), "FM-CS-SQL", [0]));
models.Sinks.Add(new(new("Corpus.Api", "Deserialize"), "FM-CS-DESERIALIZE", [0]));
models.Sinks.Add(new(new("Corpus.Api", "DeleteAccount"), "FM-CS-AUTH", [0]));
models.Sinks.Add(new(new("Corpus.Api", "DeleteAllAccounts"), "FM-CS-AUTH", []));
models.Sinks.Add(new(new("Corpus.Api", "Run"), "FM-CS-COMMAND", [0]));
models.Sanitizers.Add(new(new("Corpus.Api", "AllowPath"), ["FM-CS-PATH"]));
models.Barriers.Add(new(new("Corpus.Api", "CheckPath"), ["FM-CS-PATH"], 0));
models.Barriers.Add(new(new("Corpus.Api", "RequireAuthorization"), [], Authorizes: true));
models.Barriers.Add(new(new("Corpus.Api", "RequireResource"), [], Argument: 0, Authorizes: true));
models.Propagators.Add(new(new("Corpus.Api", "Wrap"), [0]));
var timer = System.Diagnostics.Stopwatch.StartNew();
var store = new MapStore(Path.Combine(AppContext.BaseDirectory, "acceptance-" + Guid.NewGuid().ToString("N") + ".db"));
store.Initialize(); var snapshot = store.CreateSnapshot(root); store.FinishSnapshot(snapshot.Id);
var result = await new CSharpExtractor(models).ExtractAsync(snapshot.Id, root, CancellationToken.None);
store.SaveAnalysis(snapshot.Id, result);
bool persistenceValid = store.GetFindings(snapshot.Id).Count == result.Findings.Count;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var labels = JsonSerializer.Deserialize<List<Label>>(File.ReadAllText(Path.Combine(root, "labels.json")), json)!;
var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Cases.cs")), new CSharpParseOptions(LanguageVersion.CSharp15)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToDictionary(m => m.Identifier.ValueText);
var rows = labels.Select(label => { var span = methods[label.Method].GetLocation().GetLineSpan(); bool found = result.Findings.Any(f => f.RuleId == label.Rule && f.Line >= span.StartLinePosition.Line + 1 && f.Line <= span.EndLinePosition.Line + 1); return new { label.Method, label.Rule, label.Vulnerable, Found = found, Passed = found == label.Vulnerable }; }).ToArray();
var nodeIds = result.Nodes.Select(n => n.Id).ToHashSet(); var edges = result.Edges.ToDictionary(e => e.Id); var observations = result.Observations.Select(o => o.Id).ToHashSet();
var routeErrors = new List<string>();
foreach (var finding in result.Findings)
{
    if (finding.Route.Count == 0) routeErrors.Add("empty route " + finding.Id);
    for (int i = 0; i < finding.Route.Count; i++)
    {
        var step = finding.Route[i];
        if (!nodeIds.Contains(step.NodeId) || !File.Exists(step.Path) || step.Line < 1 || step.Column < 1) routeErrors.Add("invalid step " + finding.Id);
        if (i == 0 ? step.EdgeId is not null : step.EdgeId is null || !edges.TryGetValue(step.EdgeId, out var edge) || edge.FromId != finding.Route[i - 1].NodeId || edge.ToId != step.NodeId || !observations.Contains(edge.EvidenceId)) routeErrors.Add("broken route edge " + finding.Id);
    }
}
var compilerErrors = result.Observations.Where(o => o.Predicate == "compiler_diagnostic").Select(o => o.Value).ToArray();
var topLevelRoot = Path.Combine(Path.GetDirectoryName(root)!, "TopLevel");
var topLevel = await new CSharpExtractor(models).ExtractAsync("top-level", topLevelRoot, CancellationToken.None);
bool topLevelValid = topLevel.Findings.Any(f => f.RuleId == "FM-CS-COMMAND") && !topLevel.Observations.Any(o => o.Predicate == "compiler_diagnostic");
var bounded = await new CSharpExtractor(models, new(MaxOperations: 10)).ExtractAsync("bounded", root, CancellationToken.None);
var smallRoute = await new CSharpExtractor(models, new(MaxRouteLength: 3)).ExtractAsync("route-bound", root, CancellationToken.None);
var syntaxBound = await new CSharpExtractor(models, new(MaxSyntaxEntities: 5)).ExtractAsync("syntax-bound", root, CancellationToken.None);
bool budgetVisible = bounded.Observations.Any(o => o.ErrorCode == "operation_budget") && smallRoute.Findings.Any(f => f.Truncated && f.TruncationReason is not null) && syntaxBound.Observations.Any(o => o.ErrorCode == "syntax_entity_budget") && syntaxBound.Findings.All(f => f.Truncated);
bool graphValid = result.Edges.All(e => nodeIds.Contains(e.FromId) && nodeIds.Contains(e.ToId) && observations.Contains(e.EvidenceId));
bool cfgCoverage = result.Nodes.Any(n => n.Kind == "BasicBlock" && n.Properties.GetValueOrDefault("blockKind")?.ToString() == "Entry") &&
    result.Nodes.Any(n => n.Kind == "BasicBlock" && n.Properties.GetValueOrDefault("blockKind")?.ToString() == "Exit") &&
    result.Nodes.Any(n => n.Kind == "BasicBlock" && n.Properties.GetValueOrDefault("reachable") is false) &&
    result.Edges.Any(e => e.Properties?.GetValueOrDefault("flowKind")?.ToString() == "exception");
int tp = rows.Count(x => x.Vulnerable && x.Found), fp = rows.Count(x => !x.Vulnerable && x.Found), fn = rows.Count(x => x.Vulnerable && !x.Found);
var report = new { model = models.Version, roslyn = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(), language = LanguageVersion.CSharp15.ToString(), elapsedMilliseconds = timer.ElapsedMilliseconds,
    truePositive = tp, falsePositive = fp, falseNegative = fn, precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp), recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn),
    labels = rows, families = rows.GroupBy(x => x.Rule).Select(g => new { rule = g.Key, positive = g.Count(x => x.Vulnerable), negative = g.Count(x => !x.Vulnerable), passed = g.All(x => x.Passed) }),
    compilerErrors, graphValid, cfgCoverage, persistenceValid, topLevelValid, routeErrors, budgetVisible, nodes = result.Nodes.Count, edges = result.Edges.Count, findings = result.Findings.Count,
    scope = "Hand-authored labeled safe-runtime-reference fixture; host-configured mock API models. This is not a held-out production accuracy or CodeQL parity result." };
Console.WriteLine(JsonSerializer.Serialize(report, json));
if (args.Length > 1) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllText(args[1], JsonSerializer.Serialize(report, json)); }
if (rows.Any(x => !x.Passed) || compilerErrors.Length > 0 || routeErrors.Count > 0 || !graphValid || !cfgCoverage || !persistenceValid || !topLevelValid || !budgetVisible) Environment.ExitCode = 1;
record Label(string Method, string Rule, bool Vulnerable);
