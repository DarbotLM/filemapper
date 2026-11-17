using System.Text;
using FileMapper.Harvest;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

/// <summary>Durable, explicitly started retained-source work. Never creates filesystem snapshots or learner records.</summary>
public sealed partial class SourceSemanticCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly MapStore store;
    private readonly SourceHarvestSelection selection;
    private readonly SourceSemanticCatalog catalog;
    private readonly string rootDirectory, jobsDirectory, runsDirectory, ledgerPath;
    private readonly string ownerId = "source-owner-" + Guid.NewGuid().ToString("N");
    private FileStream? coordinatorLease;
    private Task? activeTask;
    private CancellationTokenSource? activeCancellation;
    private string? activeId;
    private bool disposing, disposed, recoveryDeferred;

    private SourceSemanticCoordinator(MapStore store, SourceHarvestSelection selection, SourceSemanticCatalog catalog, string data)
    {
        this.store = store; this.selection = selection; this.catalog = catalog;
        rootDirectory = Ordinary(Path.Combine(data, "source-semantics"));
        jobsDirectory = Ordinary(Path.Combine(rootDirectory, "jobs"));
        runsDirectory = Ordinary(Path.Combine(rootDirectory, "runs"));
        ledgerPath = Ordinary(Path.Combine(jobsDirectory, "history.json"));
    }

    public bool RecoveryDeferred { get { lock (sync) return recoveryDeferred; } }

    public static async Task<SourceSemanticCoordinator> OpenAsync(MapStore store, SourceHarvestSelection activeSource,
        SourceSemanticCatalog catalog, string existingServiceData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(activeSource); ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var data = Ordinary(existingServiceData);
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException("Existing service data is required for semantic jobs.");
        if (store.IsReadOnly) throw new InvalidOperationException("Semantic jobs require a writable service ownership handle.");
        if (!Same(Path.GetDirectoryName(Ordinary(store.DatabasePath))!, data))
            throw new ArgumentException("Semantic job service data must own the MapStore database.");
        var result = new SourceSemanticCoordinator(store, activeSource, catalog, data);
        if (!Same(Ordinary(catalog.RootDirectory), result.rootDirectory)) throw new ArgumentException("Semantic catalog belongs to different service data.");
        try
        {
            Directory.CreateDirectory(result.jobsDirectory); Ordinary(result.jobsDirectory);
            var leasePath = Ordinary(Path.Combine(result.jobsDirectory, "coordinator.lock"));
            try { result.coordinatorLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("Another service owns retained-source job coordination.", error); }
            await result.ReadLedgerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.TryRecover();
            return result;
        }
        catch { result.coordinatorLease?.Dispose(); throw; }
    }

    public IReadOnlyList<SourceSemanticJob> List(int limit = 100)
    {
        if (limit is < 1 or > MaximumHistory) throw new ArgumentOutOfRangeException(nameof(limit), "Job history page is 1–200 records.");
        lock (sync)
        {
            Alive(); TryRecover();
            return history.Values.OrderByDescending(IsActive).ThenByDescending(j => j.CreatedAt).ThenBy(j => j.Id, StringComparer.Ordinal).Take(limit).ToArray();
        }
    }

    public SourceSemanticJob? Get(string jobId)
    {
        RequireJobId(jobId);
        lock (sync) { Alive(); TryRecover(); return history.GetValueOrDefault(jobId); }
    }

    /// <summary>Admission cancellation applies only before acceptance; use Cancel for accepted work.</summary>
    public async Task<SourceSemanticJob> StartAsync(SourceSemanticRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        request = CopyAndValidate(request);
        var reader = selection.CreateReader();
        var corpus = Ordinary(selection.WorkspacePath);
        if (!Directory.Exists(corpus)) throw new DirectoryNotFoundException("The active selection has no retained source corpus. A compact index alone cannot supply source bytes.");
        await using var admission = await PinSourceAsync(corpus, request, cancellationToken);
        var indexed = FindSelectedCapture(reader, request, cancellationToken);
        var indexObservedAt = DateTimeOffset.UtcNow;
        if (indexed.RepositoryId != admission.Capture!.RepositoryId || indexed.Commit != admission.Capture.Commit || indexed.ExtractionPolicyHash != admission.Extraction!.Policy.PolicyHash)
            throw new InvalidDataException("The active query selection differs from the pinned capture/extraction provenance.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            Alive(); if (activeId is not null) throw new InvalidOperationException("A retained-source job is already active.");
            var writer = store.AcquireWriter();
            try
            {
                RecoverWithWriter(); cancellationToken.ThrowIfCancellationRequested();
                var id = NewJobId(); var now = DateTimeOffset.UtcNow;
                var receipt = new SourceSemanticJobAdmission("filemapper.source-semantic-job-admission/1", id, "source-semantics", now,
                    ownerId, selection.Info, indexed, indexObservedAt, selection.Info.Mode == "compact"
                        ? "Active startup selection and exact compact SHA/length; requested capture/extraction manifest handles pinned for job lifetime."
                        : "Active ingestion workspace path; no immutable query-index claim. Requested capture/extraction manifest handles pinned for job lifetime.",
                    request, null, null);
                var job = Admit(receipt, request.CaptureId, request.ExtractionId);
                var held = admission.Transfer();
                return Launch(job, writer, held, async token =>
                {
                    var parent = Ordinary(Path.Combine(runsDirectory, id));
                    Directory.CreateDirectory(parent); Ordinary(parent);
                    var analysis = await SourceSemanticWorkspace.AnalyzeAsync(corpus, Path.Combine(parent, "analysis"), request,
                        new InlineProgress(value => UpdateProgress(id, value)), token);
                    MarkSealed(id, analysis.AnalysisId, analysis.Manifest.Coverage);
                    token.ThrowIfCancellationRequested();
                    await catalog.ImportAsync(Path.GetDirectoryName(analysis.ManifestPath)!, analysis.AnalysisId, token);
                    MarkRegistered(id, analysis.AnalysisId, analysis.Manifest.Coverage);
                });
            }
            catch { writer.Dispose(); throw; }
        }
    }

    public async Task<SourceSemanticJob> StartImportAsync(string existingSealedAnalysisDirectory, string expectedAnalysisId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HashValue(expectedAnalysisId)) throw new ArgumentException("An exact lowercase SHA-256 analysis ID is required.", nameof(expectedAnalysisId));
        var source = Ordinary(existingSealedAnalysisDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("An existing sealed analysis is required.");
        await using var admission = new InputGuards();
        var manifest = await admission.ReadAsync<SourceSemanticManifest>(Ordinary(Path.Combine(source, "manifest.json")), 1024 * 1024, cancellationToken);
        if (CanonicalJson.Sha256(manifest) != expectedAnalysisId || manifest.Schema != SourceSemanticWorkspace.Schema || manifest.State != "sealed-partial-semantic-analysis" ||
            !HashValue(manifest.CaptureId) || !HashValue(manifest.ExtractionId))
            throw new InvalidDataException("Import manifest does not match the requested sealed native analysis identity.");
        lock (sync)
        {
            Alive(); if (activeId is not null) throw new InvalidOperationException("A retained-source job is already active.");
            var writer = store.AcquireWriter();
            try
            {
                RecoverWithWriter(); cancellationToken.ThrowIfCancellationRequested();
                var receipt = new SourceSemanticJobAdmission("filemapper.source-semantic-job-admission/1", NewJobId(), "source-semantic-import",
                    DateTimeOffset.UtcNow, ownerId, null, null, null, "Explicit sealed analysis ID and pinned source manifest; full artifact verification/copy performed by catalog.",
                    null, source, expectedAnalysisId);
                var job = Admit(receipt, manifest.CaptureId, manifest.ExtractionId);
                var held = admission.Transfer();
                return Launch(job, writer, held, async token =>
                {
                    UpdateProgress(job.Id, new { phase = "verifying-and-importing-sealed-analysis", analysisId = expectedAnalysisId });
                    await catalog.ImportAsync(source, expectedAnalysisId, token);
                    MarkRegistered(job.Id, expectedAnalysisId, manifest.Coverage);
                });
            }
            catch { writer.Dispose(); throw; }
        }
    }

    public bool Cancel(string jobId)
    {
        RequireJobId(jobId); CancellationTokenSource? cancel;
        lock (sync)
        {
            Alive(); if (activeId != jobId || activeCancellation is null) return false;
            var job = history[jobId];
            history[jobId] = job with { Status = "cancelling", StopReason = "user-cancel", UpdatedAt = DateTimeOffset.UtcNow };
            Persist(); cancel = activeCancellation;
        }
        try { cancel.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public async Task<SourceSemanticJob> WaitAsync(string jobId, CancellationToken cancellationToken = default)
    {
        RequireJobId(jobId); Task? pending;
        lock (sync) { Alive(); if (!history.ContainsKey(jobId)) throw new KeyNotFoundException("Semantic job not found in retained history."); pending = activeId == jobId ? activeTask : null; }
        if (pending is not null) await pending.WaitAsync(cancellationToken);
        return Get(jobId) ?? throw new KeyNotFoundException("Semantic job was evicted from bounded history.");
    }

    private SourceSemanticJob Admit(SourceSemanticJobAdmission admission, string? capture, string? extraction)
    {
        var input = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(admission));
        if (input.Length > 512 * 1024) throw new ArgumentException("Semantic job request receipt exceeds its bound.");
        var directory = Ordinary(Path.Combine(jobsDirectory, admission.JobId));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Job identity path already exists.");
        Directory.CreateDirectory(directory); Ordinary(directory);
        AtomicWrite(Path.Combine(directory, "request.json"), input, replace: false);
        var job = new SourceSemanticJob(admission.JobId, admission.Kind, "running", "accepted", admission.AcceptedAt,
            admission.AcceptedAt, null, admission.AcceptedAt, ownerId, CanonicalJson.Sha256(admission), PayloadHash(admission), capture, extraction,
            null, "unsealed", null, null, null, 0, null);
        history.Add(job.Id, job);
        try { Persist(); }
        catch { history.Remove(job.Id); throw; }
        return job;
    }

    private SourceSemanticJob Launch(SourceSemanticJob job, IDisposable writer, InputGuards held, Func<CancellationToken, Task> work)
    {
        var cancellation = new CancellationTokenSource(); activeCancellation = cancellation; activeId = job.Id;
        activeTask = Task.Run(async () =>
        {
            Exception? failure = null; bool wasCancelled = false;
            try { await work(cancellation.Token); }
            catch (OperationCanceledException error) when (cancellation.IsCancellationRequested) { failure = error; wasCancelled = true; }
            catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
            finally
            {
                lock (sync)
                {
                    var current = history[job.Id]; var now = DateTimeOffset.UtcNow;
                    // Successful catalog registration wins over a late cancellation request. Never claim a rollback of sealed bytes.
                    var completed = failure is null && current.OutputState == "registered";
                    var status = completed ? "completed" : wasCancelled ? current.StopReason == "service-shutdown" ? "interrupted" : "cancelled" : "failed";
                    history[job.Id] = current with { Status = status, Phase = completed ? "registered" : status,
                        FinishedAt = now, UpdatedAt = now, Error = failure is null ? null : ErrorText(failure),
                        OutputState = !completed && current.Kind == "source-semantic-import" && current.OutputState == "unsealed"
                            ? "unknown-after-interruption" : current.OutputState };
                    try { Persist(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    { history[job.Id] = history[job.Id] with { Status = "failed", Error = Bounded("Final state persistence failed; durable history may require recovery. " + ErrorText(error)) }; }
                    finally
                    {
                        held.Dispose(); cancellation.Dispose(); activeCancellation = null; activeId = null;
                        writer.Dispose();
                    }
                }
            }
        });
        return job;
    }

    private void MarkSealed(string id, string analysisId, SourceSemanticCoverage coverage)
    {
        lock (sync) { history[id] = history[id] with { AnalysisId = analysisId, OutputState = "sealed-unregistered", Coverage = coverage,
            Phase = "registering-sealed-analysis", UpdatedAt = DateTimeOffset.UtcNow }; Persist(); }
    }
    private void MarkRegistered(string id, string analysisId, SourceSemanticCoverage coverage)
    {
        lock (sync) { history[id] = history[id] with { AnalysisId = analysisId, OutputState = "registered", Coverage = coverage,
            Phase = "registered", UpdatedAt = DateTimeOffset.UtcNow }; Persist(); }
    }

    private void TryRecover()
    {
        if (activeId is not null || !history.Values.Any(IsActive)) { recoveryDeferred = false; return; }
        IDisposable writer;
        try { writer = store.AcquireWriter(); }
        catch (InvalidOperationException) { recoveryDeferred = true; return; }
        using (writer) RecoverWithWriter();
    }
    private void RecoverWithWriter()
    {
        if (activeId is not null) return;
        var stale = history.Values.Where(IsActive).ToArray();
        foreach (var job in stale)
        {
            var now = DateTimeOffset.UtcNow;
            history[job.Id] = job with { Status = "interrupted", Phase = "interrupted", FinishedAt = now, UpdatedAt = now,
                StopReason = "previous-owner-ended", OutputState = job.OutputState == "unsealed" ? "unknown-after-interruption" : job.OutputState,
                Error = "Previous owner ended before durable completion. No automatic replay. A sealed or catalog publication may exist; inspect/import by exact analysis ID." };
        }
        if (stale.Length > 0) Persist(); recoveryDeferred = false;
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending; CancellationTokenSource? cancel;
        lock (sync)
        {
            if (disposed) return; disposing = true; pending = activeTask; cancel = activeCancellation;
            if (activeId is not null)
            {
                var job = history[activeId]; history[activeId] = job with { Status = "cancelling", StopReason = "service-shutdown", UpdatedAt = DateTimeOffset.UtcNow };
                try { Persist(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        try { if (pending is not null) await pending; }
        finally { lock (sync) { coordinatorLease?.Dispose(); coordinatorLease = null; disposed = true; } }
    }

    private void Alive() => ObjectDisposedException.ThrowIf(disposing || disposed, this);
    private static string NewJobId() => "source-job-" + Guid.NewGuid().ToString("N");
    private static bool Same(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static SourceSemanticRequest CopyAndValidate(SourceSemanticRequest request)
    {
        if (!HashValue(request.CaptureId) || !HashValue(request.ExtractionId)) throw new ArgumentException("Exact lowercase capture and extraction SHA-256 IDs are required.");
        var entries = request.EntryIds?.ToArray();
        if (entries is { Length: < 1 or > 2000 } || entries is not null && (entries.Any(id => id is null || id.Length != 70 || !id.StartsWith("entry:", StringComparison.Ordinal) || !HashValue(id[6..])) || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length))
            throw new ArgumentException("Select 1–2,000 unique native entry IDs or omit entryIds.");
        return request with { EntryIds = entries, Options = SourceSemanticWorkspace.ValidateOptions(request.Options ?? new()) };
    }

    private static async Task<InputGuards> PinSourceAsync(string corpus, SourceSemanticRequest request, CancellationToken token)
    {
        var guards = new InputGuards();
        try
        {
            var capture = await guards.ReadAsync<SourceCaptureManifest>(Ordinary(Path.Combine(corpus, "captures", request.CaptureId + ".json")), 4 * 1024 * 1024, token);
            var extraction = await guards.ReadAsync<SourceExtractionManifest>(Ordinary(Path.Combine(corpus, "extractions", request.ExtractionId + ".json")), 1024 * 1024, token);
            if (capture.Schema != "filemapper.source-capture/1" || CanonicalJson.Sha256(capture) != request.CaptureId ||
                extraction.Schema != "filemapper.source-extraction/1" || CanonicalJson.Sha256(extraction) != request.ExtractionId || extraction.CaptureId != request.CaptureId)
                throw new InvalidDataException("Active source capture/extraction manifests do not match the requested commitments.");
            guards.Capture = capture; guards.Extraction = extraction;
            return guards;
        }
        catch { guards.Dispose(); throw; }
    }

    private sealed class InputGuards : IDisposable, IAsyncDisposable
    {
        private readonly List<FileStream> files = [];
        public SourceCaptureManifest? Capture { get; set; }
        public SourceExtractionManifest? Extraction { get; set; }
        public async Task<T> ReadAsync<T>(string path, int maximumBytes, CancellationToken token)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true); files.Add(stream);
            return await NativeRequestJson.ReadAsync<T>(stream, maximumBytes, token);
        }
        public InputGuards Transfer() { var held = new InputGuards(); held.files.AddRange(files); files.Clear(); return held; }
        public void Dispose() { foreach (var file in files) file.Dispose(); files.Clear(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static SourceIndexedCapture FindSelectedCapture(ISourceHarvestReader reader, SourceSemanticRequest request, CancellationToken token)
    {
        // Bounded pages preserve the existing reader contract; mutable ingestion membership is an admission observation,
        // while compact membership is additionally protected by the startup selection's lifetime byte guard.
        const int pageSize = 500, maximumCaptures = 100_000;
        for (int offset = 0; offset < maximumCaptures; offset += pageSize)
        {
            token.ThrowIfCancellationRequested();
            var page = reader.ListCaptures(offset, pageSize, token);
            var match = page.Items.SingleOrDefault(c => c.CaptureId == request.CaptureId);
            if (match is not null)
            {
                if (match.ExtractionHash != request.ExtractionId) throw new InvalidOperationException("The requested extraction is not the extraction exposed by the active source selection.");
                return match;
            }
            if (offset + page.Items.Count >= page.Total || page.Items.Count == 0) break;
        }
        throw new InvalidOperationException("Requested capture/extraction is not present in the active source selection (admission lookup is bounded to 100,000 captures).");
    }
}
