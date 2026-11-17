using System.Collections.Concurrent;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Storage;
using FileMapper.Collect.FileSystem;
using FileMapper.Extract.CSharp;
using FileMapper.Extract.PowerShell;
using FileMapper.Collect.Git;
namespace FileMapper.Engine;
public sealed record JobStatus(string Id,string SnapshotId,string Kind,string Status,DateTimeOffset StartedAt,DateTimeOffset? FinishedAt,long Entries,string? Error);
public sealed class ScanCoordinator : IAsyncDisposable
{
    private readonly MapStore store;
    private readonly ConcurrentDictionary<string,CancellationTokenSource> cancellations=new();
    private readonly ConcurrentDictionary<string,Task> tasks=new();
    private readonly SemaphoreSlim gate=new(1);
    public ScanCoordinator(MapStore store)
    {
        this.store=store;store.InitializeJobs();
        TryRecover();
    }
    private static JobStatus Status(DurableJob j)=>new(j.Id,j.SnapshotId,j.Kind,j.Status,j.StartedAt,j.FinishedAt,j.Entries,j.Error);
    private void TryRecover()
    {
        if(!cancellations.IsEmpty)return;
        try {using var ownership=store.AcquireWriter();store.RecoverAbandonedJobs();}
        catch(InvalidOperationException) { }
    }
    public IReadOnlyList<JobStatus> Jobs {get {TryRecover();return store.ListJobs().Select(Status).ToArray();}}
    private IDisposable EnterWriter()
    {
        if(!gate.Wait(0))throw new InvalidOperationException("Another scan or analysis is running.");
        IDisposable? ownership=null;
        try {ownership=store.AcquireWriter();store.RecoverAbandonedJobs();return ownership;}
        catch {ownership?.Dispose();gate.Release();throw;}
    }
    public JobStatus StartScan(ScanOptions options)
    {
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        if(!Directory.Exists(root))throw new ArgumentException("Scan root must be an existing directory.");
        if(options.ReparsePolicy is not("skip" or "within" or "all" or "reference"))throw new ArgumentException("Reparse policy must be skip, within, all, or reference.");
        if(options.BatchSize is <1 or >10000)throw new ArgumentException("Batch size must be 1–10000.");
        var ownership=EnterWriter();
        try
        {
            options=options with {Root=root};var snapshot=store.CreateSnapshot(root);
            store.Ingest(snapshot.Id,new([],[],[
                new Evidence(Identity.For("scan-policy",snapshot.Id),snapshot.Id,snapshot.Id,"scan.policy",JsonSerializer.Serialize(options,Exporter.Json),"engine",root,DateTimeOffset.UtcNow),
                new Evidence(Identity.For("scan-scope",snapshot.Id),snapshot.Id,snapshot.Id,"scan.options",JsonSerializer.Serialize(options,Exporter.Json),"engine",root,DateTimeOffset.UtcNow)],[]));
            var job=store.CreateJob(snapshot.Id,"scan",options);
            return Launch(job,ownership,token=>RunScan(job,token));
        }
        catch {ownership.Dispose();gate.Release();throw;}
    }
    private async Task RunScan(DurableJob job,CancellationToken token)
    {
        var options=job.Options??throw new InvalidOperationException("Scan options are missing.");
        await foreach(var batch in new FileSystemCollector().CollectCheckpointedAsync(job.SnapshotId,options,store.WorkQueue(job.Id),token))
        {token.ThrowIfCancellationRequested();store.CommitCheckpoint(job.Id,batch);}
    }
    public JobStatus Resume(string jobId)
    {
        var ownership=EnterWriter();
        try
        {
            var job=store.GetJob(jobId);
            if(job.Kind!="scan"||job.Options is null)throw new InvalidOperationException("Only scans have a resumable frontier.");
            if(job.Status is not("paused" or "interrupted" or "cancelled" or "failed"))throw new InvalidOperationException("The job is not resumable in its current state.");
            if(!Directory.Exists(job.Options.Root))throw new InvalidOperationException("The scan root is unavailable.");
            store.SetJobStatus(jobId,"running");
            store.Ingest(job.SnapshotId,new([],[],[new Evidence(Identity.For("resume",job.Id+DateTimeOffset.UtcNow.ToString("O")),job.SnapshotId,job.SnapshotId,"scan.resumed","Continuing the persisted frontier over the original observation interval; this is not an atomic filesystem snapshot.","engine",job.Options.Root,DateTimeOffset.UtcNow)],[]));
            return Launch(store.GetJob(jobId),ownership,token=>RunScan(job,token));
        }
        catch {ownership.Dispose();gate.Release();throw;}
    }
    public JobStatus StartAnalysis(string snapshotId, IReadOnlyList<string>? analyzerReportPaths = null)
    {
        if (analyzerReportPaths?.Count > 16) throw new ArgumentException("At most 16 analyzer reports can be imported.");
        var reports = analyzerReportPaths?.Select(Path.GetFullPath).ToArray();
        var ownership=EnterWriter();
        try
        {
            var snapshot=store.GetSnapshot(snapshotId)??throw new KeyNotFoundException("Snapshot not found.");
            if(snapshot.Status!="completed")throw new InvalidOperationException("Analyze a finished snapshot.");
            if(store.GetObservations(snapshotId,snapshotId).Any(o=>o.Predicate=="analysis.committed"))throw new InvalidOperationException("This snapshot already has an immutable analysis. Create a fresh hashed scan to analyze again.");
            return Launch(store.CreateJob(snapshotId,"analysis"),ownership,token=>RunAnalysis(snapshot,reports,token));
        }
        catch {ownership.Dispose();gate.Release();throw;}
    }
    private async Task RunAnalysis(Snapshot snapshot,IReadOnlyList<string>? analyzerReportPaths,CancellationToken token)
    {
        var snapshotId=snapshot.Id;
        ICodeExtractor[] extractors=[new CSharpExtractor(),new PowerShellExtractor(new(analyzerReportPaths)),new GitCollector()];var results=new List<AnalysisResult>();
        foreach(var extractor in extractors)
        {
            token.ThrowIfCancellationRequested();var result=await extractor.ExtractAsync(snapshotId,snapshot.Root,token);
            if(extractor.Language is "C#" or "PowerShell")
            {
                foreach(var source in result.Nodes.Where(n=>n.Kind=="SourceDocument"&&n.Path!=null))
                {
                    var entry=store.GetEntries(snapshotId,search:source.Path,limit:100).Items.FirstOrDefault(e=>e.Path==source.Path);
                    var hash=source.Properties.GetValueOrDefault("sha256")?.ToString();
                    if(entry?.Hash==null||hash==null||entry.Hash!=hash)throw new InvalidOperationException("Source content does not match this snapshot. Create a fresh scan with hashing enabled, then analyze: "+source.Path);
                }
            }
            results.Add(result);
        }
        token.ThrowIfCancellationRequested();
        var marker=new Evidence(Identity.For("analysis",snapshotId),snapshotId,snapshotId,"analysis.committed","Immutable compiler/parser analysis; Git observations are live, partial provenance","engine",snapshot.Root,DateTimeOffset.UtcNow,Completeness:"partial");
        store.SaveAnalysis(snapshotId,new(results.SelectMany(r=>r.Nodes).ToArray(),results.SelectMany(r=>r.Edges).ToArray(),[..results.SelectMany(r=>r.Observations),marker],results.SelectMany(r=>r.Findings).ToArray()));
    }
    private JobStatus Launch(DurableJob job,IDisposable ownership,Func<CancellationToken,Task> work)
    {
        var cts=new CancellationTokenSource();cancellations[job.Id]=cts;
        tasks[job.Id]=Task.Run(async()=>
        {
            string state="completed";string? error=null;
            using var monitoring=new CancellationTokenSource();
            var monitor=Task.Run(async()=>
            {
                try
                {
                    using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                    while(await timer.WaitForNextTickAsync(monitoring.Token))if(store.GetJobStop(job.Id) is not null){cts.Cancel();break;}
                }
                catch(OperationCanceledException) { }
            });
            try {await work(cts.Token);}
            catch(OperationCanceledException) {state=store.GetJobStop(job.Id)??"paused";}
            catch(Exception ex) {state="failed";error=ex.Message;}
            finally
            {
                monitoring.Cancel();
                try {await monitor;}catch(Exception ex){state="failed";error="Job control monitor failed: "+ex.Message;}
                try
                {
                    if(state!="completed")
                    {
                        var observation=new Evidence(Identity.For("job-event",job.Id+DateTimeOffset.UtcNow.ToString("O")),job.SnapshotId,job.SnapshotId,"job."+state,error??("Job reached "+state+"; committed checkpoints remain available."),"engine",job.Options?.Root??job.SnapshotId,DateTimeOffset.UtcNow,Completeness:error is null?"complete":"incomplete",ErrorCode:error is null?null:"JOB_FAILED",ErrorDetail:error);
                        store.Ingest(job.SnapshotId,new([],[],[observation],[]));
                    }
                    if(job.Kind=="scan")store.FinishScanJob(job.Id,state,error);
                    else store.SetJobStatus(job.Id,state,error);
                }
                finally {cancellations.TryRemove(job.Id,out _);cts.Dispose();ownership.Dispose();gate.Release();}
            }
        });
        return Status(job);
    }
    public bool Pause(string id)=>RequestStop(id,"paused");
    public bool Cancel(string id)=>RequestStop(id,"cancelled");
    private bool RequestStop(string id,string state)
    {
        if(!store.RequestJobStop(id,state))return false;
        if(cancellations.TryGetValue(id,out var cts))try{cts.Cancel();}catch(ObjectDisposedException){}
        return true;
    }
    public async Task<JobStatus> WaitAsync(string id,CancellationToken token=default)
    {
        if(tasks.TryGetValue(id,out var task))await task.WaitAsync(token);
        else {while(store.GetJob(id).Status=="running"){TryRecover();await Task.Delay(200,token);}}
        return Status(store.GetJob(id));
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var id in cancellations.Keys) {if(!Pause(id))Cancel(id);}
        await Task.WhenAll(tasks.Values);gate.Dispose();
    }
}
