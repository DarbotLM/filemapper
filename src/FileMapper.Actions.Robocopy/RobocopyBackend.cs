using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.AccessControl;

namespace FileMapper.Actions.Robocopy;

public sealed class RobocopyBackend
{
    private readonly RobocopyBackendOptions options;
    private readonly string root;
    private readonly ConcurrentDictionary<string, Task<ActionJob>> active = new();
    private static readonly string[] ActiveStates = ["queued", "previewing", "running", "verifying"];
    public RobocopyBackend(RobocopyBackendOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Robocopy backend requires Windows.");
        root = PathSafety.CanonicalDirectory(options.StateDirectory);
        PathSafety.RejectReparseAncestors(root); Directory.CreateDirectory(root); PathSafety.RejectReparseAncestors(root);
        RestrictStateDirectory(root);
        foreach (var sub in new[] { "plans", "jobs", "approvals", "consumed" }) Directory.CreateDirectory(Path.Combine(root, sub));
        this.options = options with { StateDirectory = root }; RecoverInterrupted();
    }
    public JsonElement GetCatalog()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(RobocopyCatalog.GetCatalog().GetRawText())!;
        node["executionEnabledByHost"] = options.EnableExecution;
        return JsonSerializer.SerializeToElement(node, Data.Json);
    }
    public PlanValidation Validate(RobocopyDraft draft) => RobocopyCompiler.Validate(draft);
    public JobImport ImportJob(string text, ActionCaller caller) { caller.Demand("plan"); return NativeJobFiles.Import(text); }
    public string ExportJob(RobocopyDraft draft, ActionCaller caller) { caller.Demand("plan"); return NativeJobFiles.Export(draft); }
    public RobocopyPlan CreatePlan(RobocopyDraft draft, ActionCaller caller)
    {
        caller.Demand("plan"); var validation = Validate(draft); if (!validation.Valid) throw new ArgumentException(string.Join(" ", validation.Issues.Select(x => x.Message)));
        draft = validation.Normalized!;
        if (PathSafety.Overlaps(root, draft.Source) || PathSafety.Overlaps(root, draft.Destination)) throw new ArgumentException("Managed state/logs must be outside both copy trees, and neither endpoint may contain the state directory.");
        var source = PathSafety.Probe(draft.Source); var destination = PathSafety.Probe(draft.Destination); EnsureSeparate(source, destination);
        var binary = ResolveBinary(); var blocks = validation.ExecutionBlocks.ToList();
        if (binary.Sha256 != RobocopyCatalog.Document.RootElement.GetProperty("installedBinary").GetProperty("sha256").GetString()) blocks.Add("Installed binary differs from the version-specific catalog; execution requires a newly reviewed catalog/capability matrix.");
        if (PathSafety.Overlaps(Path.GetDirectoryName(binary.Path)!, draft.Source) || PathSafety.Overlaps(Path.GetDirectoryName(binary.Path)!, draft.Destination)) throw new ArgumentException("System executable directories cannot overlap copy endpoints.");
        var scopes = new List<string> { "destination files may be created or overwritten; existing content can be lost", "destination directory/file metadata may change" };
        var names = draft.Options.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Overlaps(["MIR", "PURGE"])) scopes.Add("destination entries absent from source may be deleted");
        if (names.Overlaps(["MOVE", "MOV"])) scopes.Add("source files or directories may be deleted by native Robocopy before independent verification");
        if (names.Contains("M")) scopes.Add("source archive attributes may be cleared");
        if (names.Overlaps(["SECFIX", "TIMFIX"])) scopes.Add("broad destination security/time repair");
        var p = new RobocopyPlan(Guid.NewGuid().ToString("N"), 1, DateTimeOffset.UtcNow, caller.Identity, draft, binary, source, destination,
            RobocopyCatalog.Sha256, RobocopyCompiler.PolicyHash, CurrentIdentity(), "", blocks.Distinct().ToArray(), scopes.ToArray());
        p = p with { PlanHash = Data.Hash(Data.Serialize(p)) }; Write(PlanPath(p.Id), p); return p;
    }
    public RobocopyPlan GetPlan(string id)
    {
        var plan = Read<RobocopyPlan>(PlanPath(id));
        if (Data.Hash(Data.Serialize(plan with { PlanHash = "" })) != plan.PlanHash) throw new IOException("Stored plan integrity check failed.");
        return plan;
    }
    public ActionJob GetJob(string id) { RecoverInterrupted(); return Read<ActionJob>(JobPath(id)); }
    public IReadOnlyList<ActionJob> GetJobs(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit)); RecoverInterrupted();
        return Directory.EnumerateDirectories(Path.Combine(root, "jobs")).Select(x => Path.Combine(x, "job.json")).Where(File.Exists).Select(Read<ActionJob>).OrderByDescending(x => x.StartedAt).Take(limit).ToArray();
    }
    public ActionJob StartPreview(string planId, ActionCaller caller, CancellationToken cancellationToken = default)
    {
        caller.Demand("plan"); return Start(GetPlan(planId), "preview", caller, null, cancellationToken);
    }
    public async Task<ActionJob> PreviewAsync(string planId, ActionCaller caller, CancellationToken cancellationToken = default) => await WaitAsync(StartPreview(planId, caller, cancellationToken).Id, cancellationToken);
    public async Task<ActionJob> WaitAsync(string id, CancellationToken cancellationToken = default)
    {
        if (active.TryGetValue(id, out var task)) return await task.WaitAsync(cancellationToken);
        while (true) { cancellationToken.ThrowIfCancellationRequested(); var job = GetJob(id); if (!ActiveStates.Contains(job.State)) return job; await Task.Delay(100, cancellationToken); }
    }
    public ActionApproval Approve(string planId, string previewJobId, ApprovalRequest request, ActionCaller caller)
    {
        caller.Demand("execute"); var plan = GetPlan(planId); var preview = GetJob(previewJobId); CheckExecutable(plan);
        if (preview.PlanId != planId || preview.Kind != "preview" || preview.State != "preview-ready" || !preview.EnumerationComplete || preview.PreviewHash is null || preview.Before?.Sha256 != preview.After?.Sha256)
            throw new InvalidOperationException("A successful, unchanged, complete list-only preview is required.");
        if (preview.ExecutionBlocks.Length != 0) throw new InvalidOperationException(string.Join(" ", preview.ExecutionBlocks));
        if (request.ReviewedPlanHash != plan.PlanHash || request.ReviewedPreviewHash != preview.PreviewHash || request.RollbackStatement != plan.Draft.RollbackStatement)
            throw new InvalidOperationException("Approval must match the exact displayed plan, preview and rollback statement.");
        if (!request.AcceptOverwrites || !request.AcceptIrreversibleChanges || !request.ConfirmTreesQuiescent) throw new InvalidOperationException("Explicit overwrite/irreversibility acceptance and a reviewed quiescent-tree assertion are required. FileMapper cannot isolate external writers.");
        if (request.ValidForSeconds is < 1 or > 900) throw new ArgumentException("Approval lifetime must be 1 through 900 seconds.");
        if (IsDestructive(plan)) caller.Demand("approve-destructive");
        var approval = new ActionApproval(Guid.NewGuid().ToString("N"), plan.Id, preview.Id, plan.PlanHash, preview.PreviewHash, caller.Identity, plan.ExecutionIdentity,
            DateTimeOffset.UtcNow.AddSeconds(request.ValidForSeconds), request.RollbackStatement, plan.MutationScopes);
        Write(Path.Combine(root, "approvals", approval.Id + ".json"), approval); return approval;
    }
    public ActionJob StartExecution(string approvalId, ActionCaller caller, CancellationToken cancellationToken = default)
    {
        caller.Demand("execute"); var approval = Read<ActionApproval>(Path.Combine(root, "approvals", ValidId(approvalId) + ".json"));
        var plan = GetPlan(approval.PlanId); CheckExecutable(plan);
        if (IsDestructive(plan)) caller.Demand("approve-destructive");
        if (approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.PlanHash != plan.PlanHash || approval.ExecutionIdentity != CurrentIdentity()) throw new InvalidOperationException("Approval expired or identity/plan changed.");
        return Start(plan, "execute", caller, approval, cancellationToken);
    }
    public async Task<ActionJob> ExecuteAsync(string approvalId, ActionCaller caller, CancellationToken cancellationToken = default) => await WaitAsync(StartExecution(approvalId, caller, cancellationToken).Id, cancellationToken);
    public bool Cancel(string jobId, ActionCaller caller)
    {
        var job = GetJob(jobId); caller.Demand(job.Kind == "preview" ? "plan" : "execute");
        if (!ActiveStates.Contains(job.State)) return false;
        Write(Path.Combine(JobDirectory(jobId), "cancel.json"), new { requestedAt = DateTimeOffset.UtcNow, requestedBy = caller.Identity }); return true;
    }
    public IReadOnlyList<ActionEvent> GetEvents(string jobId)
    {
        using var gate = AcquireJournal(jobId); return ReadEvents(jobId);
    }
    public string ReadOutput(string jobId, string channel = "native", int maximumCharacters = 65536)
    {
        if (channel is not "stdout" and not "stderr" and not "native" || maximumCharacters is < 1 or > 1_000_000) throw new ArgumentException("Invalid output request.");
        var path = Path.Combine(JobDirectory(jobId), channel == "native" ? "native.log" : channel + ".raw"); if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream, channel == "native" ? Encoding.Unicode : Encoding.UTF8, channel == "native"); var buffer = new char[maximumCharacters]; var count = reader.ReadBlock(buffer, 0, buffer.Length); return new(buffer, 0, count);
    }
    private ActionJob Start(RobocopyPlan plan, string kind, ActionCaller caller, ActionApproval? approval, CancellationToken token)
    {
        var lease = AcquireRun(); FileStream? maintenance = null;
        try
        {
            if (options.MaintenanceLockPath is not null)
            {
                if (!Path.IsPathFullyQualified(options.MaintenanceLockPath)) throw new ArgumentException("Maintenance admission path must be absolute.");
                PathSafety.RejectReparseAncestors(options.MaintenanceLockPath);
                maintenance = new FileStream(options.MaintenanceLockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            }
            Refresh(plan);
            if (approval is not null) File.Move(Path.Combine(root, "approvals", approval.Id + ".json"), Path.Combine(root, "consumed", approval.Id + ".json"), false); // atomic single use, even if later preflight fails
            var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(JobDirectory(id));
            var job = new ActionJob(id, plan.Id, kind, "queued", DateTimeOffset.UtcNow) { RequestedBy = caller.Identity, Command = RobocopyCompiler.Compile(plan, kind == "preview"), PartialChangesPossible = kind == "execute",
                Limitations = ["No atomic tree snapshot or external-writer isolation. A FileMapper action lease serializes this configured state directory only.", "Native output is localized advisory evidence, not a complete machine-readable operation manifest."] };
            job = job with { Command = job.Command with { Arguments = [.. job.Command.Arguments, "/UNILOG:" + Path.Combine(JobDirectory(id), "native.log"), "/TEE"], PresentationChanges = [.. job.Command.PresentationChanges, "Managed /UNILOG and /TEE retain authoritative Unicode text plus original raw console streams. Redirected console /UNICODE can contain mixed encodings on the observed binary."] } };
            Write(JobPath(id), job); Event(id, "queued", "Persisted job and acquired exclusive action admission.");
            var task = Task.Run(() => Run(job, plan, approval, lease, maintenance, token), CancellationToken.None); active[id] = task;
            _ = task.ContinueWith(_ => active.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return job;
        }
        catch { maintenance?.Dispose(); lease.Dispose(); throw; }
    }
    private async Task<ActionJob> Run(ActionJob job, RobocopyPlan plan, ActionApproval? approval, FileStream lease, FileStream? maintenance, CancellationToken callerToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(plan.Draft.TimeoutSeconds)); using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token);
        try
        {
            job = job with { State = job.Kind == "preview" ? "previewing" : "running" }; Save(job); Event(job.Id, "preconditions", "Hashing ordinary streams and recording both trees before native launch.");
            var before = TreeEvidence.Capture(plan, linked.Token, () => IsCancelRequested(job.Id)); job = job with { Before = before.Fingerprint, ExecutionBlocks = TreeEvidence.ExecutionBlocks(before) }; Save(job);
            if (approval is not null)
            {
                if (job.ExecutionBlocks.Length != 0) throw new InvalidOperationException(string.Join(" ", job.ExecutionBlocks));
                var preview = Read<ActionJob>(JobPath(approval.PreviewJobId));
                if (approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.PreviewHash != preview.PreviewHash || preview.After?.Sha256 != before.Fingerprint.Sha256) throw new InvalidOperationException("Preview preconditions changed or approval expired; create and review another preview.");
            }
            Refresh(plan); linked.Token.ThrowIfCancellationRequested(); if (IsCancelRequested(job.Id)) throw new OperationCanceledException("Durable cancellation requested.");
            Event(job.Id, "native-start", "Starting inert bootstrap; Robocopy is released only after Job Object containment.");
            var result = await ContainedRunner.Run(options, plan, job.Command!, JobDirectory(job.Id), () => IsCancelRequested(job.Id), linked.Token);
            job = job with { NativeExit = result.ExitCode is int code ? NativeExit.Decode(code, job.Kind == "preview") : null, WorkerSha256 = result.WorkerSha256, Output = CaptureOutput(job.Id) };
            if (result.Cancelled || result.TimedOut) { job = job with { State = result.TimedOut || timeout.IsCancellationRequested ? "timed-out" : "cancelled", Error = result.Error }; }
            else if (result.Error is not null || job.NativeExit?.NativeSuccess != true) { job = job with { State = "failed", Error = result.Error ?? "Native failure or unrecognized exit status; inspect raw output." }; }
            else
            {
                job = job with { State = "verifying" }; Save(job); Event(job.Id, "verification", "Capturing post-run ordinary streams; native success alone is not verification.");
                var after = TreeEvidence.Capture(plan, linked.Token, () => IsCancelRequested(job.Id)); job = job with { After = after.Fingerprint, EnumerationComplete = true, ExecutionBlocks = TreeEvidence.ExecutionBlocks(after) };
                if (job.Kind == "preview")
                {
                    job = job with { State = before.Fingerprint.Sha256 == after.Fingerprint.Sha256 ? "preview-ready" : "stale-preview", Error = before.Fingerprint.Sha256 == after.Fingerprint.Sha256 ? null : "Trees changed during list-only preview; review a fresh preview." };
                    if (job.State == "preview-ready") job = job with { PreviewHash = Data.Hash(Data.Serialize(new { plan.PlanHash, job.Command, job.Before, job.After, job.Output, job.NativeExit, job.WorkerSha256 })) };
                }
                else
                {
                    var verification = TreeEvidence.Verify(before, after); job = job with { Verification = verification, State = !verification.Complete ? "verification-failed" : job.NativeExit.Extras || job.NativeExit.Mismatches ? "completed-with-differences" : "completed" };
                }
            }
        }
        catch (OperationCanceledException e) { job = job with { State = timeout.IsCancellationRequested ? "timed-out" : "cancelled", Error = e.Message, Output = CaptureOutput(job.Id) }; }
        catch (Exception e) { File.WriteAllText(Path.Combine(JobDirectory(job.Id), "failure.txt"), e.ToString()); job = job with { State = "failed", Error = e.GetType().Name + ": " + e.Message, Output = CaptureOutput(job.Id) }; }
        finally
        {
            try { job = job with { FinishedAt = DateTimeOffset.UtcNow }; Save(job); Event(job.Id, job.State, job.Error ?? "Terminal outcome persisted."); }
            finally { maintenance?.Dispose(); lease.Dispose(); }
        }
        return job;
    }
    private void Refresh(RobocopyPlan plan)
    {
        if (plan.PolicyHash != RobocopyCompiler.PolicyHash || plan.CatalogHash != RobocopyCatalog.Sha256 || plan.ExecutionIdentity != CurrentIdentity() || ResolveBinary() != plan.Binary) throw new InvalidOperationException("Policy, catalog, execution identity or binary changed; create another plan.");
        var source = PathSafety.Probe(plan.Draft.Source); var destination = PathSafety.Probe(plan.Draft.Destination); EnsureSeparate(source, destination);
        if (source != plan.SourceIdentity || destination != plan.DestinationIdentity) throw new InvalidOperationException("Endpoint physical identity/capabilities changed; create another plan.");
        PathSafety.RejectReparseAncestors(root);
    }
    private void CheckExecutable(RobocopyPlan plan) { if (!options.EnableExecution) throw new UnauthorizedAccessException("Execution is disabled by administrator configuration."); if (plan.ExecutionBlocks.Length != 0) throw new InvalidOperationException("Execution policy blocks: " + string.Join(" ", plan.ExecutionBlocks)); Refresh(plan); }
    private static bool IsDestructive(RobocopyPlan plan) => plan.Draft.Options.Any(x => x.Name is "MIR" or "PURGE" or "MOV" or "MOVE" or "M" or "SECFIX" or "TIMFIX");
    private static void EnsureSeparate(EndpointIdentity source, EndpointIdentity destination)
    {
        if (source.VolumeId == destination.VolumeId && source.FileId == destination.FileId || PathSafety.Overlaps(source.FinalPath, destination.FinalPath)) throw new ArgumentException("Endpoint physical aliases or resolved overlap are not allowed.");
    }
    private static BinaryIdentity ResolveBinary()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("A 64-bit Windows process is required; WOW64 redirection is not accepted.");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"); PathSafety.RejectReparseAncestors(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Installed Windows system Robocopy was not found.", path);
        return new(path, FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown", Data.HashFile(path), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
    }
    private static string CurrentIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value + "|" + identity.Name;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictStateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User ?? throw new UnauthorizedAccessException("An OS user SID is required.");
        var allowed = new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        var current = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var rules = current.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (current.AreAccessRulesProtected && rules.Length == allowed.Distinct().Count() && rules.All(r => r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && allowed.Contains(r.IdentityReference)) && allowed.All(sid => rules.Any(r => r.IdentityReference == sid))) return;
        // Changing inheritable ACLs can temporarily contend with atomic file replacement. Only initialize/migrate when no action owns admission.
        using var admission = new FileStream(Path.Combine(directory, "action.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        foreach (var sid in allowed)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    private void RecoverInterrupted()
    {
        FileStream lease; try { lease = AcquireRun(); } catch (IOException) { return; }
        using (lease) foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "jobs")))
        {
            var path = Path.Combine(directory, "job.json"); if (!File.Exists(path)) continue; var job = Read<ActionJob>(path);
            if (!ActiveStates.Contains(job.State)) continue;
            Write(path, job with { State = "interrupted", FinishedAt = DateTimeOffset.UtcNow, Error = "No live owner holds action admission. The Job Object was configured to kill descendants on owner exit; recovery does not independently prove their termination or final write extent. No automatic replay. Revalidate and approve a fresh plan.", PartialChangesPossible = job.Kind == "execute" });
        }
    }
    private FileStream AcquireRun() { PathSafety.RejectReparseAncestors(root); return new FileStream(Path.Combine(root, "action.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private bool IsCancelRequested(string id) => File.Exists(Path.Combine(JobDirectory(id), "cancel.json"));
    private string PlanPath(string id) => Path.Combine(root, "plans", ValidId(id) + ".json");
    private string JobDirectory(string id) => Path.Combine(root, "jobs", ValidId(id));
    private string JobPath(string id) => Path.Combine(JobDirectory(id), "job.json");
    private static string ValidId(string id) => Regex.IsMatch(id ?? "", "\\A[0-9a-f]{32}\\z") ? id! : throw new ArgumentException("Invalid record identifier.");
    private static T Read<T>(string path) { PathSafety.RejectReparseAncestors(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return JsonSerializer.Deserialize<T>(stream, Data.Json) ?? throw new IOException("Empty record: " + path); }
    private static void Write<T>(string path, T value)
    {
        PathSafety.RejectReparseAncestors(path); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(s, value, Data.Json); s.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void Save(ActionJob job) => Write(JobPath(job.Id), job);
    private void Event(string id, string kind, string message)
    {
        using var gate = AcquireJournal(id); var previous = ReadEvents(id); var sequence = previous.Count + 1;
        if (sequence > 10000) throw new IOException("Action event budget exceeded.");
        var record = new ActionEvent(sequence, DateTimeOffset.UtcNow, kind, message, previous.Count == 0 ? new string('0', 64) : previous[^1].EventHash);
        record = record with { EventHash = EventHash(record) };
        var path = Path.Combine(JobDirectory(id), "events.ndjson");
        var json = JsonSerializer.Serialize(record, CompactJson);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) { var bytes = Encoding.UTF8.GetBytes(json + "\n"); stream.Write(bytes); stream.Flush(true); }
        Write(Path.Combine(JobDirectory(id), "events-head.json"), new EventHead(sequence, record.EventHash, new FileInfo(path).Length));
    }
    private sealed record EventHead(long Sequence, string EventHash, long Bytes);
    private static readonly JsonSerializerOptions CompactJson = new(Data.Json) { WriteIndented = false };
    private static string EventHash(ActionEvent record) => Data.Hash(JsonSerializer.Serialize(record with { EventHash = "" }, CompactJson));
    private FileStream AcquireJournal(string id)
    {
        var path = Path.Combine(JobDirectory(id), "events.lock"); PathSafety.RejectReparseAncestors(path);
        for (var attempt = 0; ; attempt++) { try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException) when (attempt < 100) { Thread.Sleep(10); } }
    }
    private IReadOnlyList<ActionEvent> ReadEvents(string id)
    {
        var path = Path.Combine(JobDirectory(id), "events.ndjson"); var headPath = Path.Combine(JobDirectory(id), "events-head.json");
        if (!File.Exists(path) && !File.Exists(headPath)) return [];
        if (!File.Exists(path) || !File.Exists(headPath)) throw new IOException("Unverified legacy, incomplete or missing hash-chain journal/head; no silent partial event list is returned.");
        var head = Read<EventHead>(headPath);
        if (head.Sequence is < 1 or > 10000 || new FileInfo(path).Length != head.Bytes) throw new IOException("Event journal differs from its durable head (corruption or uncommitted crash tail).");
        var events = new List<ActionEvent>(); var previous = new string('0', 64);
        foreach (var line in File.ReadLines(path))
        {
            ActionEvent record;
            try { record = JsonSerializer.Deserialize<ActionEvent>(line, Data.Json) ?? throw new JsonException("Null event."); }
            catch (JsonException e) { throw new IOException("Malformed hash-chain event; journal integrity is incomplete.", e); }
            if (record.Sequence != events.Count + 1 || record.PreviousHash != previous || record.EventHash != EventHash(record)) throw new IOException("Action event hash-chain verification failed.");
            events.Add(record); previous = record.EventHash; if (events.Count > 10000) throw new IOException("Event budget exceeded.");
        }
        if (events.Count != head.Sequence || previous != head.EventHash) throw new IOException("Action journal head does not match the verified event chain.");
        return events;
    }
    private OutputEvidence[] CaptureOutput(string id) => new[] { "stdout.raw", "stderr.raw", "native.log" }.Select(x => Path.Combine(JobDirectory(id), x)).Where(File.Exists).Select(x => new OutputEvidence(Path.GetFileName(x), new FileInfo(x).Length, Data.HashFile(x), x.EndsWith("native.log", StringComparison.Ordinal) ? "UTF-16LE native /UNILOG; authoritative display text" : "Raw console bytes; installed /UNICODE output has mixed OEM and UTF-16 sections. UTF-8 replacement decoding is explicitly lossy; prefer native.log.")).ToArray();
}
