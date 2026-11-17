using System.Diagnostics;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
if (args.Length == 4)
{
    // Read-only regression against an existing snapshot. Never initializes or changes its database.
    var watch = Stopwatch.StartNew();
    var engine = new QueryEngine(new MapStore(args[0]));
    var bounded = engine.Route(args[1], args[2], args[3]);
    var defaultSeconds = watch.Elapsed.TotalSeconds; watch.Restart();
    var deeper = engine.Route(args[1], args[2], args[3], 64);
    Console.WriteLine(JsonSerializer.Serialize(new { defaultSeconds, defaultRoute = bounded, depth64Seconds = watch.Elapsed.TotalSeconds, depth64Route = deeper }, json));
    return;
}
if (args.Length != 1) throw new ArgumentException("Supply a fresh fixture directory, or database/snapshot/from/to for a read-only regression.");
var folder = Path.GetFullPath(args[0]);
Directory.CreateDirectory(folder);
var database = Path.Combine(folder, "route.db");
if (File.Exists(database)) throw new IOException("Fixture database already exists; use a fresh directory.");
var store = new MapStore(database); store.Initialize();
var checks = new List<object>();
void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
MapNode Node(string id) => new(id, "Fixture", id, null, []);
MapEdge Edge(string id, string from, string to) => new(id, from, to, "FLOWS_TO", "evidence:" + id);
string Graph(string name, IEnumerable<MapEdge> edges)
{
    var snapshot = store.CreateSnapshot(Path.Combine(folder, name));
    var all = edges.ToArray();
    store.Ingest(snapshot.Id, new(all.SelectMany(e => new[] { e.FromId, e.ToId }).Distinct().Select(Node).ToArray(), all, [], []));
    return snapshot.Id;
}
var engineFixture = new QueryEngine(store);
var id = Graph("direction", Enumerable.Range(0, 1100).Select(i => Edge($"incoming-{i:D4}", "noise" + i, "start"))
    .Concat([Edge("a", "start", "middle"), Edge("b", "middle", "end"), Edge("c", "middle", "start")]));
var path = engineFixture.Route(id, "start", "end");
Check("Incoming edges do not consume directed budget; ordered witness retains original evidence", !path.Truncated && path.Steps.Select(s => s.Node.Id).SequenceEqual(["start", "middle", "end"]) && path.Steps[2].Via?.EvidenceId == "evidence:b");
var reverse = engineFixture.Route(id, "end", "start");
Check("A complete directed absence differs from a truncated search", !reverse.Truncated && reverse.Steps.Count == 0);
var limited = engineFixture.Route(id, "start", "end", 1);
Check("Depth exhaustion reports incomplete search", limited.Truncated && limited.Steps.Count == 0 && limited.MaxDepth == 1);
Check("Zero-edge self route is valid at depth zero", engineFixture.Route(id, "start", "start", 0).Steps.Count == 1);
var exact = Graph("exact", Enumerable.Range(0, 1000).Select(i => Edge($"e{i:D4}", "root", "leaf" + i)));
Check("Exactly 1000 outgoing edges do not falsely truncate", !engineFixture.Route(exact, "root", "leaf999").Truncated);
var over = Graph("over", Enumerable.Range(0, 1001).Select(i => Edge($"e{i:D4}", "root", "leaf" + i)));
var overRoute = engineFixture.Route(over, "root", "leaf1000");
Check("The 1001st outgoing edge triggers explicit truncation", overRoute.Truncated && overRoute.Steps.Count == 0);
var broad = Graph("nodes", Enumerable.Range(0, 1000).Select(i => Edge($"a{i:D4}", "root", "branch" + i))
    .Concat(Enumerable.Range(0, 1000).SelectMany(i => Enumerable.Range(0, 6).Select(j => Edge($"b{i:D4}-{j}", "branch" + i, $"leaf{i}-{j}")))));
var broadRoute = engineFixture.Route(broad, "root", "leaf999-5");
Check("Node budget is enforced before insertion", broadRoute.Truncated && broadRoute.VisitedNodes == 5000);
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { engineFixture.Route(id, "start", "end", cancellationToken: cancelled.Token); Check("Cancellation", false); }
    catch (OperationCanceledException) { Check("Cancellation", true); }
}
using (var reader = store.OpenRouteReader(id))
{
    store.Ingest(id, new([Node("later")], [Edge("later-edge", "start", "later")], [], []));
    Check("One route reader pins a consistent SQLite observation", reader.Node("later") == null && !reader.Outgoing("start", 1001).Any(e => e.ToId == "later"));
}
Check("A new reader observes the committed graph", store.GetNode(id, "later") != null);
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks }, json));
