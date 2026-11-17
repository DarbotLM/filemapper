using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using FileMapper.Actions.Robocopy;

if (args is ["--crash-child", var childRoot, var childHost])
{
    var childBackend = new RobocopyBackend(new(Path.Combine(childRoot, "state")) { DotnetHostPath = childHost });
    var childCaller = new ActionCaller("contained-crash-fixture", new HashSet<string> { "plan" });
    var childPlan = childBackend.CreatePlan(new(Path.Combine(childRoot, "source"), Path.Combine(childRoot, "destination")), childCaller);
    var childJob = childBackend.StartPreview(childPlan.Id, childCaller);
    await File.WriteAllTextAsync(Path.Combine(childRoot, "ready.json"), JsonSerializer.Serialize(new { jobId = childJob.Id, ownerPid = Environment.ProcessId }));
    await childBackend.WaitAsync(childJob.Id); return;
}
if (args.Length != 2) throw new ArgumentException("Usage: acceptance <new-absolute-fixture-directory> <absolute-dotnet-host>");
var root = Path.GetFullPath(args[0]);
if (root.StartsWith("D:", StringComparison.OrdinalIgnoreCase) || Directory.Exists(root)) throw new ArgumentException("Acceptance requires a new isolated workspace directory outside D:.");
Directory.CreateDirectory(root);
var source = Path.Combine(root, "source space Ω"); var destination = Path.Combine(root, "destination space Ω"); Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
Directory.CreateDirectory(Path.Combine(source, "empty")); Directory.CreateDirectory(Path.Combine(source, "nested"));
await File.WriteAllTextAsync(Path.Combine(source, "nested", "snow 雪.txt"), "ordinary stream unicode\n");
await File.WriteAllTextAsync(Path.Combine(source, "replace.txt"), "new source content\n");
await File.WriteAllTextAsync(Path.Combine(destination, "replace.txt"), "old content\n"); File.SetLastWriteTimeUtc(Path.Combine(destination, "replace.txt"), DateTime.UtcNow.AddMinutes(-10));
await File.WriteAllTextAsync(Path.Combine(destination, "extra.txt"), "must survive ordinary copy and preview\n");
var backend = new RobocopyBackend(new(Path.Combine(root, "state")) { DotnetHostPath = Path.GetFullPath(args[1]), RuntimeConfigPath = Path.ChangeExtension(typeof(RobocopyBackend).Assembly.Location, ".runtimeconfig.json"), EnableExecution = true });
var planner = new ActionCaller("fixture-planner", new HashSet<string> { "read", "plan" });
var executor = new ActionCaller("fixture-executor", new HashSet<string> { "read", "plan", "execute" });
var checks = new List<object>();
void Check(string name, bool value, object? details = null) { checks.Add(new { name, passed = value, details }); if (!value) throw new Exception("FAILED: " + name + ": " + JsonSerializer.Serialize(details)); }
void Throws(string name, Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException) { Check(name, true, e.Message); return; } Check(name, false); }
void Denied(string name, Action action) { try { action(); } catch (UnauthorizedAccessException e) { Check(name, true, e.Message); return; } Check(name, false); }
var draft = new RobocopyDraft(source, destination);
Check("catalog exact installed switch set", backend.GetCatalog().GetProperty("options").GetArrayLength() == 97);
Check("default typed plan validates", backend.Validate(draft).Valid);
Check("null option rejected", !backend.Validate(draft with { Options = [null!] }).Valid);
Check("null patterns rejected", !backend.Validate(draft with { Files = null! }).Valid);
Check("null endpoints rejected", !backend.Validate(draft with { Source = null!, Destination = null! }).Valid);
Check("byte quantity above 2GiB supported", backend.Validate(draft with { Options = [.. draft.Options, new("IOMAXSIZE", Number: 5L * 1024 * 1024 * 1024)] }).Valid);
Check("switch-shaped pattern rejected", !backend.Validate(draft with { Files = ["/MIR"] }).Valid);
Check("typed option quote injection rejected", !backend.Validate(draft with { Options = [.. draft.Options, new("XD", Items: ["x\" /MIR"])] }).Valid);
Check("absolute typed exclusions supported", backend.Validate(draft with { Options = [.. draft.Options, new("XD", Items: ["C:\\build space\\obj"])] }).Valid);
Check("unknown option rejected", !backend.Validate(draft with { Options = [.. draft.Options, new("UNKNOWN", true)] }).Valid);
foreach (var pair in new[] { ("MT", "IPG"), ("MT", "EFSRAW"), ("LFSM", "MT"), ("LFSM", "EFSRAW") })
{
    OptionValue O(string n) => n is "MT" or "IPG" or "LFSM" ? new(n, Number: 2) : new(n, true);
    Check("native conflict " + pair, !backend.Validate(draft with { Options = [.. draft.Options, O(pair.Item1), O(pair.Item2)] }).Valid);
}
Check("drive-root boundary", PathSafety.Contains("C:\\", "C:\\child") && !PathSafety.Contains("C:\\foo", "C:\\foobar"));
Check("UNC boundary", PathSafety.Contains("\\\\server\\share\\", "\\\\server\\share\\child") && !PathSafety.Contains("\\\\server\\share", "\\\\server\\share2\\child"));
Check("lexical overlap rejected", !backend.Validate(draft with { Destination = Path.Combine(source, "child") }).Valid);
foreach (var path in new[] { "C:relative", "\\\\?\\C:\\test", "C:\\test:stream", "C:\\trailing.\\x", "%TEMP%\\x" }) Check("unsafe endpoint " + path, !backend.Validate(draft with { Source = path }).Valid);
var rcj = NativeJobFiles.Export(draft); await File.WriteAllTextAsync(Path.Combine(root, "export.rcj"), rcj);
var imported = NativeJobFiles.Import(rcj); Check("RCJ round trip", imported.Draft.Source == source && imported.Draft.Destination == destination && imported.Draft.Options.Length == draft.Options.Length);
Throws("nested RCJ rejected", () => NativeJobFiles.Import(rcj + "\n/JOB:C:\\attacker.rcj\n"));
Throws("registry settings rejected", () => backend.CreatePlan(draft with { Options = [.. draft.Options, new("REG", true)] }, planner));
foreach (var capabilities in new[] { new[] { "read" }, new[] { "read", "scan" }, new[] { "read", "scan", "plan" }, new[] { "read", "plan" } })
    Denied("no implicit execution " + string.Join(',', capabilities), () => backend.StartExecution(new string('0', 32), new("role-probe", capabilities.ToHashSet())));
for (var code = 0; code <= 31; code++) { var value = NativeExit.Decode(code, true); Check("exit bits " + code, value.RawCode == code && value.NativeSuccess == (code < 8) && value.CopiedOrProposed == ((code & 1) != 0) && value.Extras == ((code & 2) != 0) && value.Mismatches == ((code & 4) != 0) && value.CopyFailures == ((code & 8) != 0) && value.SeriousError == ((code & 16) != 0)); }
Check("unknown exit is not success", NativeExit.Decode(32, false).UnknownBits == 32 && !NativeExit.Decode(32, false).NativeSuccess);
var plan = backend.CreatePlan(draft, planner);
var mirror = backend.CreatePlan(draft with { Options = [.. draft.Options, new("MIR", true), new("MON", Number: 1), new("NFL", true)] }, planner);
var compiledMirror = RobocopyCompiler.Compile(mirror, true);
Check("preview retains destructive scope and forces observability", compiledMirror.Arguments.Contains("/MIR") && compiledMirror.Arguments.Contains("/L") && !compiledMirror.Arguments.Contains("/MON:1") && !compiledMirror.Arguments.Contains("/NFL"));
Check("destructive execution blocked", mirror.ExecutionBlocks.Any(x => x.Contains("MIR")));
var mirrorPreview = await backend.PreviewAsync(mirror.Id, planner);
Check("native MIR list-only preview did not prune", mirrorPreview.State == "preview-ready" && mirrorPreview.Before?.Sha256 == mirrorPreview.After?.Sha256 && File.Exists(Path.Combine(destination, "extra.txt")), mirrorPreview);
var preview = backend.StartPreview(plan.Id, planner);
var second = new RobocopyBackend(new(Path.Combine(root, "state")) { DotnetHostPath = args[1], EnableExecution = true });
Check("live writer not marked interrupted by reader", second.GetJob(preview.Id).State != "interrupted");
Throws("concurrent action admission denied", () => second.StartPreview(plan.Id, planner));
preview = await backend.WaitAsync(preview.Id);
await File.WriteAllTextAsync(Path.Combine(root, "preview.json"), JsonSerializer.Serialize(preview, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
Check("native contained preview ready", preview.State == "preview-ready", preview);
Check("preview ordinary content and attributes unchanged", preview.Before?.Sha256 == preview.After?.Sha256 && File.ReadAllText(Path.Combine(destination, "replace.txt")) == "old content\n");
Check("preview raw Unicode output retained", preview.Output.Any(x => x.Bytes > 0) && backend.ReadOutput(preview.Id).Contains("snow 雪.txt"));
Throws("altered approval rejected", () => backend.Approve(plan.Id, preview.Id, new("wrong", preview.PreviewHash!, draft.RollbackStatement, true, true, ConfirmTreesQuiescent: true), executor));
Throws("missing tree isolation assertion rejected", () => backend.Approve(plan.Id, preview.Id, new(plan.PlanHash, preview.PreviewHash!, draft.RollbackStatement, true, true), executor));
var approval = backend.Approve(plan.Id, preview.Id, new(plan.PlanHash, preview.PreviewHash!, draft.RollbackStatement, true, true, ConfirmTreesQuiescent: true), executor);
var execution = await backend.ExecuteAsync(approval.Id, executor);
Check("ordinary copy verified", execution.State is "completed" or "completed-with-differences" && execution.Verification?.Complete == true, execution);
Check("overwrite copied and extras retained", File.ReadAllText(Path.Combine(destination, "replace.txt")) == "new source content\n" && File.Exists(Path.Combine(destination, "extra.txt")) && Directory.Exists(Path.Combine(destination, "empty")));
Throws("single-use approval replay rejected", () => backend.StartExecution(approval.Id, executor));
var stalePreview = await backend.PreviewAsync(plan.Id, planner);
var staleApproval = backend.Approve(plan.Id, stalePreview.Id, new(plan.PlanHash, stalePreview.PreviewHash!, draft.RollbackStatement, true, true, ConfirmTreesQuiescent: true), executor);
await File.WriteAllTextAsync(Path.Combine(source, "replace.txt"), "changed after approval\n");
var staleRun = await backend.ExecuteAsync(staleApproval.Id, executor); Check("changed source blocks execution", staleRun.State == "failed" && staleRun.NativeExit is null && File.ReadAllText(Path.Combine(destination, "replace.txt")) == "new source content\n", staleRun.Error);
var cancelPreview = backend.StartPreview(plan.Id, planner); Check("cancel persisted", backend.Cancel(cancelPreview.Id, planner)); var cancelled = await backend.WaitAsync(cancelPreview.Id); Check("cancel terminal", cancelled.State == "cancelled", cancelled);
var restarted = new RobocopyBackend(new(Path.Combine(root, "state")) { DotnetHostPath = args[1] }); Check("jobs survive coordinator restart", restarted.GetJob(execution.Id).State == execution.State);
var maintenancePath = Path.Combine(root, "maintenance.lock");
var admitted = new RobocopyBackend(new(Path.Combine(root, "admission-state")) { DotnetHostPath = args[1], MaintenanceLockPath = maintenancePath });
var admittedPlan = admitted.CreatePlan(draft, planner);
using (var barrier = new FileStream(maintenancePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Throws("installer maintenance blocks action launch", () => admitted.StartPreview(admittedPlan.Id, planner));
var admittedPreview = admitted.StartPreview(admittedPlan.Id, planner);
Throws("live action excludes maintenance", () => { using var barrier = new FileStream(maintenancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
Check("action succeeds after maintenance release", (await admitted.WaitAsync(admittedPreview.Id)).State == "preview-ready");
using (var barrier = new FileStream(maintenancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check("completed action released maintenance", true);
var linkedSource = Path.Combine(root, "linked-source"); var linkedDestination = Path.Combine(root, "linked-destination"); Directory.CreateDirectory(linkedSource); Directory.CreateDirectory(linkedDestination);
await File.WriteAllTextAsync(Path.Combine(linkedSource, "target.txt"), "new content must not reach alias"); var external = Path.Combine(root, "external-alias.txt"); await File.WriteAllTextAsync(external, "preserve external alias");
if (!Native.CreateHardLinkW(Path.Combine(linkedDestination, "target.txt"), external, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
var linkedPlan = backend.CreatePlan(new(linkedSource, linkedDestination), planner); var linkedPreview = await backend.PreviewAsync(linkedPlan.Id, planner);
Check("hardlink preview records physical scope limitation", linkedPreview.State == "preview-ready" && linkedPreview.ExecutionBlocks.Any(x => x.Contains("multiply linked")));
Throws("hardlinked destination cannot be approved", () => backend.Approve(linkedPlan.Id, linkedPreview.Id, new(linkedPlan.PlanHash, linkedPreview.PreviewHash!, linkedPlan.Draft.RollbackStatement, true, true, ConfirmTreesQuiescent: true), executor));
Check("external hardlink remains unchanged", File.ReadAllText(external) == "preserve external alias");
var outputBudgetPlan = backend.CreatePlan(draft with { MaximumOutputBytes = 4096 }, planner); var budgetRun = await backend.PreviewAsync(outputBudgetPlan.Id, planner);
Check("output budget failure explicit", budgetRun.State == "failed" && budgetRun.Error!.Contains("budget", StringComparison.OrdinalIgnoreCase), budgetRun);
var chain = backend.GetEvents(execution.Id); Check("event chain verifies against durable head", chain.Count > 3 && chain.All(x => x.EventHash.Length == 64 && x.PreviousHash.Length == 64));
var journalPath = Path.Combine(root, "state", "jobs", execution.Id, "events.ndjson"); var journalOriginal = await File.ReadAllTextAsync(journalPath);
try { await File.WriteAllTextAsync(journalPath, journalOriginal.Replace("queued", "edited", StringComparison.Ordinal)); Throws("tampered event journal rejected", () => backend.GetEvents(execution.Id)); await File.WriteAllTextAsync(Path.Combine(root, "tampered-events-evidence.ndjson"), await File.ReadAllTextAsync(journalPath)); }
finally { await File.WriteAllTextAsync(journalPath, journalOriginal); }
Check("retained original head validates restored evidence", backend.GetEvents(execution.Id).Count == chain.Count);
var crashRoot = Path.Combine(root, "crash-fixture"); Directory.CreateDirectory(Path.Combine(crashRoot, "source")); Directory.CreateDirectory(Path.Combine(crashRoot, "destination"));
for (var i = 0; i < 10000; i++) await File.WriteAllTextAsync(Path.Combine(crashRoot, "source", $"item-{i:D5}.txt"), "list only containment evidence\n");
var childStart = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true };
childStart.ArgumentList.Add("exec"); childStart.ArgumentList.Add("--runtimeconfig"); childStart.ArgumentList.Add(Path.ChangeExtension(typeof(Native).Assembly.Location, ".runtimeconfig.json")); childStart.ArgumentList.Add(typeof(Native).Assembly.Location);
childStart.ArgumentList.Add("--crash-child"); childStart.ArgumentList.Add(crashRoot); childStart.ArgumentList.Add(args[1]);
using (var child = Process.Start(childStart)!)
{
    var clock = Stopwatch.StartNew(); string? crashJobId = null; JsonElement? nativeInfo = null; JsonElement? bootstrapInfo = null;
    while (clock.Elapsed < TimeSpan.FromSeconds(60) && !child.HasExited)
    {
        try
        {
            if (crashJobId is null && File.Exists(Path.Combine(crashRoot, "ready.json"))) crashJobId = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(crashRoot, "ready.json"))).RootElement.GetProperty("jobId").GetString();
            if (crashJobId is not null)
            {
                var jobDirectory = Path.Combine(crashRoot, "state", "jobs", crashJobId);
                if (File.Exists(Path.Combine(jobDirectory, "native-process.json")))
                {
                    nativeInfo = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobDirectory, "native-process.json"))).RootElement.Clone();
                    bootstrapInfo = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(jobDirectory, "bootstrap-process.json"))).RootElement.Clone();
                    if (Alive(nativeInfo.Value)) break;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        await Task.Delay(2);
    }
    try
    {
        Check("crash probe observed a live owned native descendant", nativeInfo is not null && bootstrapInfo is not null && Alive(nativeInfo.Value), new { ownerPid = child.Id, nativeInfo, bootstrapInfo });
        child.Kill(false); await child.WaitForExitAsync();
        for (var i = 0; i < 200 && (Alive(nativeInfo!.Value) || Alive(bootstrapInfo!.Value)); i++) await Task.Delay(10);
        Check("owner crash left no live matching worker/native descendants", !Alive(nativeInfo!.Value) && !Alive(bootstrapInfo!.Value));
        var recovery = new RobocopyBackend(new(Path.Combine(crashRoot, "state")) { DotnetHostPath = args[1] }); var recovered = recovery.GetJob(crashJobId!);
        Check("crashed action recovered interrupted without replay", recovered.State == "interrupted" && Directory.GetFileSystemEntries(Path.Combine(crashRoot, "destination")).Length == 0, recovered);
        await File.WriteAllTextAsync(Path.Combine(root, "crash-containment.json"), JsonSerializer.Serialize(new { ownerPid = child.Id, nativeInfo, bootstrapInfo, recovered }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
}
static bool Alive(JsonElement record)
{
    try { using var p = Process.GetProcessById(record.GetProperty("id").GetInt32()); return !p.HasExited && p.StartTime.ToUniversalTime() == record.GetProperty("startedAt").GetDateTime().ToUniversalTime(); }
    catch (ArgumentException) { return false; }
    catch (InvalidOperationException) { return false; }
}
await File.WriteAllTextAsync(Path.Combine(root, "acceptance.json"), JsonSerializer.Serialize(new { observedAt = DateTimeOffset.UtcNow, fixtureRoot = root, tool = plan.Binary, checks, preview, execution, limitations = new[] { "Only isolated workspace fixtures were copied. D: and sealed bakeoff artifacts were untouched.", "No ACL/ADS/EFS/sparse, network-share or destructive execution verification claim; crash containment and output-budget failure were exercised separately." } }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, artifact = Path.Combine(root, "acceptance.json") }));
internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateHardLinkW(string file, string existing, IntPtr reserved);
}
