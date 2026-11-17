using System.Collections.Concurrent;
using FileMapper.Collect.FileSystem;
using FileMapper.Domain;
using FileMapper.Storage;

namespace FileMapper.Engine;

/// <summary>Durable content-analysis jobs sharing the map writer and installer admission.</summary>
public sealed class HashingCoordinator(MapStore store) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<string, Task> tasks = new();
    private readonly ConcurrentDictionary<string, (string RunId, long Bytes)> inFlight = new();
    public bool RecoveryDeferred { get; private set; }

    private void Recover()
    {
        if (!cancellations.IsEmpty || !store.HasHashingStore()) return;
        if (!store.ListHashingRuns(limit: 200).Any(r => r.Status == "running")) { RecoveryDeferred = false; return; }
        try
        {
            using var ownership = store.AcquireWriter();
            store.RecoverAbandonedHashingRuns();
            RecoveryDeferred = false;
        }
        catch (InvalidOperationException) { RecoveryDeferred = true; }
    }

    public IReadOnlyList<HashingRun> List(string? snapshotId = null, int limit = 100)
    {
        Recover();
        return store.ListHashingRuns(snapshotId, limit).Select(WithLiveProgress).ToArray();
    }
    public HashingRun Get(string id)
    {
        Recover();
        return WithLiveProgress(store.GetHashingRun(id) ?? throw new KeyNotFoundException("Content analysis job not found."));
    }
    private HashingRun WithLiveProgress(HashingRun run) => run with
    {
        Coverage = run.Coverage with { InFlightBytesRead = inFlight.Values.Where(v => v.RunId == run.Id).Sum(v => v.Bytes) }
    };

    private IDisposable EnterWriter()
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("A content analysis is already running.");
        try { return store.AcquireWriter(); }
        catch { gate.Release(); throw; }
    }
    public HashingRun Start(HashingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (request.Options ?? new HashingOptions()).Validate();
        store.InitializeHashing();
        var ownership = EnterWriter();
        try
        {
            store.RecoverAbandonedHashingRuns();
            var run = store.CreateHashingRun(request);
            return Launch(run, ownership);
        }
        catch { ownership.Dispose(); gate.Release(); throw; }
    }
    public HashingRun Resume(string id)
    {
        var ownership = EnterWriter();
        try
        {
            store.RecoverAbandonedHashingRuns();
            var run = store.GetHashingRun(id) ?? throw new KeyNotFoundException("Content analysis job not found.");
            if (run.Status is not ("paused" or "cancelled" or "interrupted" or "failed")) throw new InvalidOperationException("This content analysis is not resumable.");
            store.PrepareHashingResume(id);
            store.SetHashingStatus(id, "running");
            return Launch(store.GetHashingRun(id)!, ownership);
        }
        catch { ownership.Dispose(); gate.Release(); throw; }
    }

    private HashingRun Launch(HashingRun run, IDisposable ownership)
    {
        var cancellation = new CancellationTokenSource();
        cancellations[run.Id] = cancellation;
        tasks[run.Id] = Task.Run(async () =>
        {
            string status = "completed";
            string? error = null;
            using var monitorStop = new CancellationTokenSource();
            var monitor = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                    while (await timer.WaitForNextTickAsync(monitorStop.Token))
                        if (store.GetHashingStop(run.Id) is not null) { cancellation.Cancel(); break; }
                }
                catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
                catch { cancellation.Cancel(); throw; }
            });
            try { await RunAsync(run.Id, cancellation.Token); }
            catch (OperationCanceledException) { status = store.GetHashingStop(run.Id) ?? "paused"; }
            catch (Exception ex) { status = "failed"; error = ex.Message; }
            finally
            {
                monitorStop.Cancel();
                try { await monitor; }
                catch (Exception ex) { status = "failed"; error = "Content job monitor failed: " + ex.Message; }
                try { store.SetHashingStatus(run.Id, status, error); }
                finally
                {
                    cancellations.TryRemove(run.Id, out _);
                    cancellation.Dispose();
                    ownership.Dispose();
                    gate.Release();
                }
            }
        });
        return run;
    }

    private async Task RunAsync(string id, CancellationToken cancellationToken)
    {
        store.PrepareHashingCandidates(id, cancellationToken);
        var run = store.GetHashingRun(id)!;
        var options = run.Options;
        var snapshot = store.GetSnapshot(run.SnapshotId) ?? throw new KeyNotFoundException("Baseline snapshot not found.");
        var remainingBytes = Math.Max(0, options.MaxBytesRead - run.Coverage.BudgetChargedBytes);
        var limiter = new FileReadRateLimiter(remainingBytes, options.BytesPerSecond);
        using var budgetStop = new CancellationTokenSource();
        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetStop.Token);
        string? budgetKind = null;
        void BudgetReached(string kind)
        {
            Interlocked.CompareExchange(ref budgetKind, kind, null);
            budgetStop.Cancel();
        }

        async Task Stage(string stage)
        {
            string? cursor = null;
            store.SetHashingPhase(id, stage == "sample-pending" ? "sampling" : "full-hashing");
            while (true)
            {
                workerStop.Token.ThrowIfCancellationRequested();
                var batch = store.GetHashingWork(id, stage, cursor, 128);
                if (batch.Count == 0) return;
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = workerStop.Token },
                    async (work, token) => await ReadWorkAsync(run, snapshot, work, stage, limiter, BudgetReached, token));
                cursor = batch[^1].NodeId;
            }
        }
        try
        {
            if (options.Mode != "partial") await Stage("sample-pending");
            store.PromoteHashingSamples(id, cancellationToken);
            if (options.Mode != "probable") await Stage("full-pending");
        }
        catch (OperationCanceledException) when (budgetKind is not null && !cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        if (budgetKind is not null) store.MarkHashingBudgetReached(id, budgetKind);
        store.SetHashingPhase(id, "sealing-results");
        store.SealHashingRun(id, cancellationToken);
    }

    private async Task ReadWorkAsync(HashingRun run, Snapshot snapshot, HashingWorkItem work, string stage,
        FileReadRateLimiter limiter, Action<string> budgetReached, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = store.BeginHashingAttempt(run.Id, work.NodeId, stage);
        var start = DateTimeOffset.UtcNow;
        var samplePolicy = new FileSamplePolicy(run.Options.SampleWindowBytes);
        var chunks = stage == "full-pending" && run.Options.Mode is "partial" or "all"
            ? new FileChunkPolicy(run.Options.ChunkMinimumBytes, run.Options.ChunkAverageBytes, run.Options.ChunkMaximumBytes, run.Options.MaxChunksPerFile) : null;
        var policy = stage == "sample-pending" ? samplePolicy.PolicyHash
            : chunks?.PolicyHash ?? Identity.For("full-policy", "sha256-unnamed-stream/v1");
        var receiptStage = stage == "sample-pending" ? "sample" : "full";
        GuardedContentReadLease? lease = null;
        var committed = false;
        inFlight[attempt] = (run.Id, 0);
        void Complete(FileReadReceipt receipt, string nextStage, bool cacheHit = false, string? sourceId = null, string? error = null)
        {
            inFlight.TryRemove(attempt, out _);
            store.CompleteHashingAttempt(run.Id, work.NodeId, attempt,
                new(attempt, run.Id, work.NodeId, receipt, cacheHit, sourceId), nextStage, error);
            committed = true;
        }
        FileReadReceipt Failure(string status, string error) => new(receiptStage, policy, lease?.Before, null,
            start, DateTimeOffset.UtcNow, lease?.BytesRead ?? 0, null, 0, 0, false, status, error);
        try
        {
            var paths = store.GetHashingPaths(run.Id, work.NodeId, 1024);
            var sample = work.SampleEvidenceId is null ? null : store.GetHashingEvidence(work.SampleEvidenceId);
            var expectedVersion = stage == "full-pending" ? sample?.Receipt.After?.VersionKey : null;
            lease = GuardedContentReader.Open(new(work.Path, snapshot.Root, work.NodeId, work.LogicalBytes,
                work.ModifiedAt, expectedVersion, paths.Items, paths.Total == paths.Items.Count), cancellationToken);
            if (run.Options.ReuseCache && lease.Before.CacheEligible)
            {
                var cached = store.FindHashingCache(work.NodeId, lease.Before.VersionKey, policy);
                if (cached is not null)
                {
                    var validated = lease.Revalidate(cancellationToken);
                    if (!validated.IsStable) throw new IOException("Content version changed during cache revalidation: " + validated.Reason);
                    var prior = cached.Evidence.Receipt;
                    var receipt = prior with { Before = lease.Before, After = validated.Current, StartedAt = start,
                        FinishedAt = DateTimeOffset.UtcNow, BytesRead = 0, Status = "complete", Error = null };
                    Complete(receipt, stage == "sample-pending" ? "sampled" : "done", true, cached.Evidence.Id);
                    return;
                }
            }
            var reservation = stage == "sample-pending"
                ? Math.Min(work.LogicalBytes, checked(3L * samplePolicy.WindowBytes)) : work.LogicalBytes;
            store.ReserveHashingAttemptBytes(run.Id, work.NodeId, attempt, reservation);
            var progress = new InlineProgress<FileReadProgress>(value => inFlight[attempt] = (run.Id, value.BytesRead));
            FileReadReceipt result;
            if (stage == "sample-pending") result = await lease.SampleAsync(samplePolicy, limiter, progress, cancellationToken);
            else result = await lease.FullAsync(chunks,
                chunks is null ? null : (batch, token) =>
                {
                    store.AppendHashingChunks(run.Id, work.NodeId, attempt, batch, token);
                    return ValueTask.CompletedTask;
                }, limiter, progress, cancellationToken);
            Complete(result, result.Status == "complete" ? (stage == "sample-pending" ? "sampled" : "done")
                : result.Status == "chunk-limit" ? "limited" : "invalidated",
                error: result.Error);
        }
        catch (FileReadBudgetExceededException ex)
        {
            if (!committed) Complete(Failure("limited", ex.Message), "limited", error: ex.Message);
            budgetReached("bytes");
        }
        catch (HashingStorageBudgetExceededException ex)
        {
            if (!committed) Complete(Failure("limited", ex.Message), "limited", error: ex.Message);
            budgetReached(ex.Kind);
        }
        catch (OperationCanceledException)
        {
            if (!committed) Complete(Failure("cancelled", "Read cancelled; only completed stages can be reused."), stage);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException)
        {
            if (!committed) Complete(Failure("error", ex.Message), "error", error: ex.Message);
        }
        finally
        {
            inFlight.TryRemove(attempt, out _);
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    public bool Pause(string id) => Stop(id, "paused");
    public bool Cancel(string id) => Stop(id, "cancelled");
    private bool Stop(string id, string state)
    {
        if (!store.RequestHashingStop(id, state)) return false;
        if (cancellations.TryGetValue(id, out var cancellation))
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
    public async Task<HashingRun> WaitAsync(string id, CancellationToken cancellationToken = default)
    {
        if (tasks.TryGetValue(id, out var task)) await task.WaitAsync(cancellationToken);
        else while (Get(id).Status == "running") await Task.Delay(250, cancellationToken);
        return Get(id);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in cancellations.Keys) Pause(id);
        await Task.WhenAll(tasks.Values);
        gate.Dispose();
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
