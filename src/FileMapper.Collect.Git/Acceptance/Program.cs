using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Collect.Git;
using FileMapper.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var outputRoot = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "filemapper-provenance"));
Directory.CreateDirectory(outputRoot);
var root = Path.Combine(outputRoot, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var git = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe") : "/usr/bin/git";
if (!File.Exists(git)) throw new InvalidOperationException("Acceptance requires a host Git installation: " + git);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var results = new List<object>(); var failures = new List<string>();
var stopwatch = Stopwatch.StartNew();
void Check(string name, bool passed) { results.Add(new { name, passed }); if (!passed) failures.Add(name); }
bool RejectsPath(Func<string> resolve)
{
    try { resolve(); return false; }
    catch (InvalidDataException) { return true; }
}
bool ResolvesTo(Func<string> resolve, string expected)
{
    try { return resolve() == expected; }
    catch (InvalidDataException) { return false; }
}
// These are lexical boundary checks: no drive-root or UNC fixture is read or written.
var boundaryRoot = Path.Combine(outputRoot, "boundary");
var boundary = new ProvenanceGraph("boundary", boundaryRoot + Path.DirectorySeparatorChar, new());
Check("directory boundary accepts root and descendants", boundary.Contains(boundaryRoot) && boundary.Contains(Path.Combine(boundaryRoot, "child", "file.cs")));
Check("directory boundary rejects sibling prefix", !boundary.Contains(boundaryRoot + "-sibling") && !boundary.Contains(Path.Combine(boundaryRoot + "-sibling", "file.cs")));
Check("relative resolution normalizes inside boundary", boundary.Resolve(Path.Combine(boundaryRoot, "child"), "../file.cs") == Path.Combine(boundaryRoot, "file.cs"));
Check("relative resolution rejects parent escape", RejectsPath(() => boundary.Resolve(boundaryRoot, "../outside.cs")));
if (OperatingSystem.IsWindows())
{
    var drive = new ProvenanceGraph("boundary", @"D:\", new());
    Check("drive root accepts descendant without double separator", drive.Contains(@"D:\") && drive.Contains(@"d:\folder\file.cs"));
    Check("drive root resolves relative and normalized paths", ResolvesTo(() => drive.Resolve(@"D:\", "folder/file.cs"), @"D:\folder\file.cs") && ResolvesTo(() => drive.Resolve(@"D:\folder", "../file.cs"), @"D:\file.cs"));
    Check("drive root rejects different drive", !drive.Contains(@"E:\folder\file.cs") && RejectsPath(() => drive.Resolve(@"D:\", @"E:\file.cs")));
    foreach (var uncRoot in new[] { @"\\fixture.invalid\share", @"\\fixture.invalid\share\" })
    {
        var unc = new ProvenanceGraph("boundary", uncRoot, new());
        Check("UNC boundary accepts share and child " + uncRoot, unc.Contains(@"\\fixture.invalid\share") && unc.Contains(@"\\fixture.invalid\share\folder\file.cs") && unc.Resolve(uncRoot, "folder/file.cs") == @"\\fixture.invalid\share\folder\file.cs");
        Check("UNC boundary rejects other share and server " + uncRoot, !unc.Contains(@"\\fixture.invalid\share-other\file.cs") && !unc.Contains(@"\\other.invalid\share\file.cs") && RejectsPath(() => unc.Resolve(uncRoot, @"\\fixture.invalid\other\file.cs")));
    }
}
else
{
    var volume = new ProvenanceGraph("boundary", "/", new());
    Check("POSIX volume root accepts descendants", volume.Contains("/folder/file.cs") && volume.Resolve("/", "folder/file.cs") == "/folder/file.cs");
}
var gitWithinRootRejected = false;
try { await new GitReader(new() { GitExecutable = git }, Path.GetPathRoot(git)!).Text(outputRoot, ["--version"], CancellationToken.None); }
catch (GitReadException ex) when (ex.Message.StartsWith("No trusted absolute Git executable", StringComparison.Ordinal)) { gitWithinRootRejected = true; }
Check("Git executable within analysis volume root is refused", gitWithinRootRejected);
void Put(string relative, string content)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}
void Json(string relative, object value) => Put(relative, JsonSerializer.Serialize(value, jsonOptions));
string Hash(string relative) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))));
async Task<string> Git(string[] arguments, string? input = null)
{
    var start = new ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(arguments)) start.ArgumentList.Add(argument);
    start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    start.Environment["GIT_AUTHOR_DATE"] = "2026-01-01T12:00:00+00:00"; start.Environment["GIT_COMMITTER_DATE"] = "2026-01-01T12:00:00+00:00";
    using var process = Process.Start(start)!;
    if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(budget.Token); }
    finally { if (!process.HasExited) process.Kill(true); }
    var text = await stdout; if (process.ExitCode != 0) throw new InvalidOperationException(await stderr); return text.TrimEnd('\r', '\n');
}
async Task Commit(string person, string message)
{
    await Git(["add", "--all"]);
    await Git(["-c", "user.name=" + person, "-c", "user.email=" + person.ToLowerInvariant() + "@fixture.invalid", "commit", "-m", message]);
}
await Git(["init", "--initial-branch=main"]);
Put("src/Old.cs", "namespace Fixture;\npublic static class Entry\n{\n    public static int Value() => 42;\n    public static string Name() => \"fixture\";\n}\n");
Put("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net11.0;net10.0</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include=\"Fixture.A\" Version=\"[1.0,3.0)\"/><ProjectReference Include=\"lib/Helper.csproj\" Condition=\"'$(TargetFramework)' == 'net11.0'\"/></ItemGroup><Target Name=\"NeverExecute\" BeforeTargets=\"Build\"><Exec Command=\"echo unsafe &gt; msbuild-ran.marker\"/></Target></Project>");
Put("lib/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
Put("Fixture.slnx", "<Solution><Project Path=\"Fixture.csproj\"/><Project Path=\"lib/Helper.csproj\"/></Solution>");
Put(".github/CODEOWNERS", "* @all\n/src/** @core\n/src/Main.cs @override @team\n/src/Copy.cs\n[unsupported] @nobody\n");
Put("CODEOWNERS", "* @ignored-root-policy\n");
await Commit("Alice", "initial source and policy");
File.Move(Path.Combine(root, "src", "Old.cs"), Path.Combine(root, "src", "Main.cs"));
await Commit("Bob", "rename source");
Put("src/Copy.cs", File.ReadAllText(Path.Combine(root, "src", "Main.cs")).Replace("class Entry", "class CopyEntry", StringComparison.Ordinal));
await Commit("Carol", "copy source with renamed type");
Put("src/Main.cs", File.ReadAllText(Path.Combine(root, "src", "Main.cs")).Replace("=> 42", "=> 43", StringComparison.Ordinal));
await Commit("Alice", "update value");
var previous = await Git(["rev-parse", "HEAD"]); var tree = await Git(["rev-parse", "HEAD^{tree}"]);
var signatureObject = $"tree {tree}\nparent {previous}\nauthor Fixture Signer <signer@fixture.invalid> 1767268800 +0000\ncommitter Fixture Signer <signer@fixture.invalid> 1767268800 +0000\ngpgsig -----BEGIN PGP SIGNATURE-----\n deliberately-invalid-fixture-signature\n -----END PGP SIGNATURE-----\n\nsignature presence fixture; never verified\n";
var head = await Git(["hash-object", "-t", "commit", "-w", "--stdin"], signatureObject);
await Git(["update-ref", "refs/heads/main", head]);
await Git(["-c", "user.name=Fixture", "-c", "user.email=fixture@fixture.invalid", "tag", "-a", "v-fixture", "-m", "fixture tag"]);
var blobTag = await Git(["rev-parse", "HEAD:src/Main.cs"]); await Git(["tag", "blob-fixture", blobTag]);

object LockPackage(string version, string type, Dictionary<string, string>? dependencies = null) => new { type, resolved = version, contentHash = Convert.ToBase64String(new byte[64]), dependencies = dependencies ?? [] };
Json("packages.lock.json", new { version = 1, dependencies = new Dictionary<string, object>
{
    ["net11.0"] = new Dictionary<string, object> { ["Fixture.A"] = LockPackage("1.2.0", "Direct", new() { ["Fixture.B"] = "2.0.0" }), ["Fixture.B"] = LockPackage("2.0.0", "Transitive") },
    ["net10.0"] = new Dictionary<string, object> { ["Fixture.A"] = LockPackage("1.1.0", "Direct", new() { ["Fixture.B"] = "1.0.0" }), ["Fixture.B"] = LockPackage("1.0.0", "Transitive") }
} });
Json("obj/project.assets.json", new
{
    version = 3,
    project = new { restore = new { projectPath = Path.Combine(root, "Fixture.csproj") }, frameworks = new Dictionary<string, object> { ["net11.0"] = new { dependencies = new Dictionary<string, object> { ["Fixture.A"] = new { target = "Package", version = "[1.0, 3.0)" } } }, ["net10.0"] = new { dependencies = new Dictionary<string, object> { ["Fixture.A"] = new { target = "Package", version = "[1.0, 3.0)" } } } } },
    targets = new Dictionary<string, object>
    {
        ["net11.0"] = new Dictionary<string, object> { ["Fixture.A/1.2.0"] = new { type = "package", dependencies = new Dictionary<string, string> { ["Fixture.B"] = "2.0.0" } }, ["Fixture.B/2.0.0"] = new { type = "package" }, ["Helper/1.0.0"] = new { type = "project" } },
        ["net10.0"] = new Dictionary<string, object> { ["Fixture.A/1.1.0"] = new { type = "package", dependencies = new Dictionary<string, string> { ["Fixture.B"] = "1.0.0" } }, ["Fixture.B/1.0.0"] = new { type = "package" } }
    },
    libraries = new Dictionary<string, object> { ["Fixture.A/1.2.0"] = new { type = "package", sha512 = Convert.ToBase64String(new byte[64]) }, ["Fixture.A/1.1.0"] = new { type = "package", sha512 = Convert.ToBase64String(new byte[64]) }, ["Helper/1.0.0"] = new { type = "project", msbuildProject = "lib/Helper.csproj", path = "lib/Helper.csproj" } }
});
var sourcePaths = new[] { "src/Main.cs", "src/Copy.cs" };
var syntax = sourcePaths.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, p)), path: p)).ToArray();
var compilation = CSharpCompilation.Create("Fixture", syntax, [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
Directory.CreateDirectory(Path.Combine(root, "out"));
using (var assembly = File.Create(Path.Combine(root, "out", "Fixture.dll")))
{
    var emitted = compilation.Emit(assembly); if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
}
var artifactHash = Hash("out/Fixture.dll");
object Spec(string path) => new { path, sha256 = Hash(path), length = new FileInfo(Path.Combine(root, path)).Length };
var invocation = new { api = "CSharpCompilation.Create/Emit", arguments = new[] { "deterministic=true", "OutputKind=DynamicallyLinkedLibrary" }, environment = new { runtime = Environment.Version.ToString() } };
Json("build.provenance.json", new { kind = "filemapper-build-provenance", schemaVersion = 1, project = "Fixture.csproj", targetFramework = "net11.0", commit = head, timestamp = "2026-01-01T12:00:00Z", tool = new { name = "controlled Roslyn acceptance compiler", version = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString() }, invocation, inputs = sourcePaths.Select(Spec).ToArray(), outputs = new[] { Spec("out/Fixture.dll") } });
Json("bad-hash.provenance.json", new { kind = "filemapper-build-provenance", schemaVersion = 1, invocation, inputs = new[] { new { path = "src/Main.cs", sha256 = new string('0', 64) } }, outputs = new[] { Spec("out/Fixture.dll") } });
Json("escape.provenance.json", new { kind = "filemapper-build-provenance", schemaVersion = 1, invocation, inputs = new[] { new { path = "../outside-secret.txt", sha256 = new string('a', 64) } }, outputs = new[] { Spec("out/Fixture.dll") } });
Json("bom.cdx.json", new
{
    bomFormat = "CycloneDX", specVersion = "1.6", version = 1,
    components = new[]
    {
        new Dictionary<string, object> { ["type"] = "file", ["bom-ref"] = "artifact", ["name"] = "out/Fixture.dll", ["hashes"] = new[] { new { alg = "SHA-256", content = artifactHash } } },
        new Dictionary<string, object> { ["type"] = "library", ["bom-ref"] = "package-a", ["name"] = "Fixture.A", ["version"] = "1.2.0", ["purl"] = "pkg:nuget/Fixture.A@1.2.0", ["licenses"] = new[] { new { license = new { id = "MIT" } } } }
    }, dependencies = new[] { new Dictionary<string, object> { ["ref"] = "artifact", ["dependsOn"] = new[] { "package-a" } } }
});
Json("fixture.spdx.json", new Dictionary<string, object>
{
    ["spdxVersion"] = "SPDX-2.3", ["SPDXID"] = "SPDXRef-DOCUMENT", ["documentNamespace"] = "https://fixture.invalid/sbom/1", ["name"] = "Fixture",
    ["packages"] = new[] { new Dictionary<string, object> { ["SPDXID"] = "SPDXRef-Package", ["name"] = "Fixture.A", ["versionInfo"] = "1.2.0", ["licenseDeclared"] = "MIT", ["externalRefs"] = new[] { new { referenceType = "purl", referenceLocator = "pkg:nuget/Fixture.A@1.2.0" } } } },
    ["files"] = new[] { new Dictionary<string, object> { ["SPDXID"] = "SPDXRef-Artifact", ["fileName"] = "out/Fixture.dll", ["checksums"] = new[] { new { algorithm = "SHA256", checksumValue = artifactHash } } }, new Dictionary<string, object> { ["SPDXID"] = "SPDXRef-Source", ["fileName"] = "src/Main.cs", ["checksums"] = new[] { new { algorithm = "SHA256", checksumValue = Hash("src/Main.cs") } } } },
    ["relationships"] = new[] { new { spdxElementId = "SPDXRef-Artifact", relationshipType = "GENERATED_FROM", relatedSpdxElement = "SPDXRef-Source" }, new { spdxElementId = "SPDXRef-Artifact", relationshipType = "DEPENDS_ON", relatedSpdxElement = "SPDXRef-Package" } }
});
Put("malformed-bom.json", "{ definitely not JSON }");
Put("external-entity.csproj", "<!DOCTYPE Project [<!ENTITY xxe SYSTEM 'file:///outside-secret.txt'>]><Project>&xxe;</Project>");
Put(".gitattributes", "*.cs filter=unsafe diff=unsafe\n");
Put("unsafe-helper.sh", "#!/bin/sh\nprintf unsafe > '" + Path.Combine(root, "repository-code-ran.marker").Replace('\\', '/') + "'\ncat\n");
var helper = "sh '" + Path.Combine(root, "unsafe-helper.sh").Replace('\\', '/') + "'";
foreach (var setting in new[] { "filter.unsafe.clean", "filter.unsafe.process", "diff.unsafe.command", "diff.unsafe.textconv", "core.fsmonitor", "gpg.program" }) await Git(["config", setting, helper]);
await Git(["config", "filter.unsafe.required", "true"]); await Git(["config", "log.showSignature", "true"]);
Put(OperatingSystem.IsWindows() ? "git.exe" : "git", "This repository-local executable must never be selected.\n");
var oldDirectory = Environment.CurrentDirectory; Environment.CurrentDirectory = root;
AnalysisResult result;
try { result = await new GitCollector(new() { GitExecutable = git, MaxChangeCommits = 20, MaxBlameFiles = 32 }).ExtractAsync("provenance-acceptance", root, CancellationToken.None); }
finally { Environment.CurrentDirectory = oldDirectory; }
var nodes = result.Nodes.ToDictionary(n => n.Id); var observations = result.Observations.ToDictionary(o => o.Id);
Check("graph references exist", result.Edges.All(e => nodes.ContainsKey(e.FromId) && nodes.ContainsKey(e.ToId) && observations.ContainsKey(e.EvidenceId)));
Check("derived evidence contributions exist", result.Observations.All(o => o.ContributingObservationIds.All(observations.ContainsKey)));
Check("repository hooks filters helpers and MSBuild never executed", !File.Exists(Path.Combine(root, "repository-code-ran.marker")) && !File.Exists(Path.Combine(root, "msbuild-ran.marker")));
Check("commit signature presence is not verification", result.Nodes.Any(n => n.Kind == "Commit" && n.Properties.GetValueOrDefault("signaturePresent") is true && Equals(n.Properties.GetValueOrDefault("signatureVerification"), "not-performed")));
Check("rename detected with threshold evidence", result.Edges.Any(e => e.Kind == "RENAMED_FROM" && Equals(e.Properties?.GetValueOrDefault("thresholdPercent"), 50)));
Check("copy detected with explicit uncertainty", result.Edges.Any(e => e.Kind == "COPIED_FROM" && Equals(e.Properties?.GetValueOrDefault("identityProof"), false)));
var main = result.Nodes.Single(n => n.Kind == "SourceDocument" && n.Path == Path.Combine(root, "src", "Main.cs"));
var copy = result.Nodes.Single(n => n.Kind == "SourceDocument" && n.Path == Path.Combine(root, "src", "Copy.cs"));
Check("CODEOWNERS file and last-rule precedence", result.Edges.Any(e => e.FromId == main.Id && e.Kind == "ATTRIBUTED_TO" && nodes[e.ToId].Name == "@override") && result.Nodes.All(n => n.Kind != "Owner" || n.Name != "@ignored-root-policy"));
Check("CODEOWNERS empty owner rule clears declaration", !result.Edges.Any(e => e.ToId == copy.Id && e.Kind == "OWNS"));
Check("CODEOWNERS conflict explanation", result.Observations.Any(o => o.SubjectId == main.Id && o.Predicate == "ownership_policy_resolution" && o.Value.Contains("\"conflictingOwners\":true", StringComparison.Ordinal)));
Check("blame remains advisory lower priority", result.Edges.Any(e => e.FromId == main.Id && e.Kind == "ATTRIBUTED_TO" && Equals(e.Properties?.GetValueOrDefault("priority"), 10) && Equals(e.Properties?.GetValueOrDefault("advisory"), true)));
var artifact = result.Nodes.First(n => n.Kind == "Artifact" && Equals(n.Properties.GetValueOrDefault("sha256"), artifactHash));
bool Reach(string from, Func<MapNode, bool> target)
{
    var pending = new Queue<string>(); pending.Enqueue(from); var seen = new HashSet<string>();
    while (pending.TryDequeue(out var id)) { if (!seen.Add(id)) continue; if (target(nodes[id])) return true; foreach (var edge in result.Edges.Where(e => e.FromId == id)) pending.Enqueue(edge.ToId); }
    return false;
}
Check("artifact reaches verified source revision and commit", Reach(artifact.Id, n => n.Kind == "RepositoryFile") && Reach(artifact.Id, n => n.Kind == "Commit" && Equals(n.Properties.GetValueOrDefault("sha"), head)));
Check("artifact reaches owner and contributor", Reach(artifact.Id, n => n.Kind == "Owner" && n.Name == "@override") && Reach(artifact.Id, n => n.Kind == "Contributor"));
Check("artifact reaches TFM dependencies", Reach(artifact.Id, n => n.Kind == "PackageVersion" && n.Name == "Fixture.B" && Equals(n.Properties.GetValueOrDefault("version"), "2.0.0")));
Check("framework versions remain distinct", result.Nodes.Count(n => n.Kind == "PackageVersion" && n.Name == "Fixture.A") == 2);
Check("TFM-scoped project reference", result.Edges.Any(e => e.Kind == "DEPENDS_ON" && nodes[e.ToId].Kind == "Project" && nodes[e.ToId].Name == "Helper" && Equals(e.Properties?.GetValueOrDefault("targetContext"), "net11.0")));
Check("CycloneDX and SPDX source identifiers retained", result.Nodes.Any(n => Equals(n.Properties.GetValueOrDefault("bomRef"), "artifact")) && result.Nodes.Any(n => Equals(n.Properties.GetValueOrDefault("spdxId"), "SPDXRef-Artifact")));
Check("both SBOM formats link artifact by verified hash", result.Edges.Count(e => e.Kind == "IDENTIFIES" && e.ToId == artifact.Id) == 2);
Check("SPDX generation claim remains declared", result.Edges.Any(e => e.Kind == "DECLARES_GENERATED_FROM"));
Check("explicit build lineage never claims observed execution", result.Edges.Where(e => e.Kind == "GENERATED_FROM").Any() && result.Edges.Where(e => e.Kind == "GENERATED_FROM").All(e => Equals(e.Properties?.GetValueOrDefault("executionVerified"), false) && observations[e.EvidenceId].SourceUri.EndsWith("build.provenance.json", StringComparison.Ordinal)));
Check("bad manifest hashes and escape paths rejected", result.Observations.Any(o => o.ErrorCode == "provenance_import" && o.SourceUri.EndsWith("bad-hash.provenance.json", StringComparison.Ordinal)) && result.Observations.Any(o => o.ErrorCode == "provenance_import" && o.SourceUri.EndsWith("escape.provenance.json", StringComparison.Ordinal)));
Check("DTD and malformed JSON rejected", result.Observations.Any(o => o.ErrorCode == "package_metadata" && o.SourceUri.EndsWith("external-entity.csproj", StringComparison.Ordinal)) && result.Observations.Any(o => o.ErrorCode == "provenance_import" && o.SourceUri.EndsWith("malformed-bom.json", StringComparison.Ordinal)));
Check("worktree clean status stays unknown without filters", result.Observations.Any(o => o.Predicate == "worktree_status" && o.Value.Contains("\"trackedClean\":null", StringComparison.Ordinal)));
var bounded = await new GitCollector(new() { GitExecutable = git, MaxCommits = 1, MaxChangeCommits = 1, MaxBlameFiles = 0, MaxComponents = 2 }).ExtractAsync("bounded", root, CancellationToken.None);
Check("bounded collection exposes truncation", bounded.Observations.Any(o => o.ErrorCode == "history_limit") && bounded.Observations.Any(o => o.ErrorCode is "package_metadata" or "provenance_import"));
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); var observed = false;
    try { await new GitCollector(new() { GitExecutable = git }).ExtractAsync("cancelled", root, cancelled.Token); }
    catch (OperationCanceledException) { observed = true; }
    Check("caller cancellation propagates", observed);
}
var report = new { passed = failures.Count == 0, root, head, artifactHash, actualArtifactCompiler = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(), elapsedMilliseconds = stopwatch.ElapsedMilliseconds, nodes = result.Nodes.Count, edges = result.Edges.Count, observations = result.Observations.Count, checks = results, failures, diagnostics = result.Observations.Where(o => o.ErrorCode is not null).Select(o => new { o.ErrorCode, o.SourceUri, o.Value }), scope = "Controlled real Git history and Roslyn-emitted DLL; synthetic multi-TFM resolution/SBOM records. Collector executes neither repository build logic nor produced artifact. Hash matches prove observed content, not execution or package restore." };
Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
if (args.Length > 1) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllText(args[1], JsonSerializer.Serialize(report, jsonOptions)); }
if (failures.Count > 0) Environment.ExitCode = 1;
