using System.Management.Automation;

namespace FileMapper.PowerShell;

[Cmdlet(VerbsLifecycle.Start, "FileMapperContentAnalysis", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperHashingRun))]
public sealed class StartFileMapperContentAnalysisCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string SnapshotId { get; set; } = "";
    [Parameter] [ValidateSet("exact", "probable", "partial", "all")] public string Mode { get; set; } = "exact";
    [Parameter] public string? ScopePath { get; set; }
    [Parameter] [ValidateRange(0L, long.MaxValue)] public long MinimumBytes { get; set; }
    [Parameter] [ValidateRange(0L, 1_152_921_504_606_846_976L)] public long MaxBytesRead { get; set; } = 1_099_511_627_776;
    [Parameter] [ValidateRange(1, 16)] public int Workers { get; set; } = 2;
    [Parameter] [ValidateRange(0L, 1_099_511_627_776L)] public long BytesPerSecond { get; set; }
    [Parameter] [ValidateRange(1, 10_000_000)] public int MaxIdentities { get; set; } = 1_000_000;
    [Parameter] [ValidateRange(1, 20_000_000)] public int MaxPaths { get; set; } = 2_000_000;
    [Parameter] [ValidateRange(4096, 1_048_576)] public int SampleWindowBytes { get; set; } = 65_536;
    [Parameter] [ValidateRange(4096, 16_777_216)] public int ChunkMinimumBytes { get; set; } = 65_536;
    [Parameter] [ValidateRange(4096, 16_777_216)] public int ChunkAverageBytes { get; set; } = 262_144;
    [Parameter] [ValidateRange(4096, 16_777_216)] public int ChunkMaximumBytes { get; set; } = 1_048_576;
    [Parameter] [ValidateRange(1, 65_536)] public int MaxChunksPerFile { get; set; } = 65_536;
    [Parameter] [ValidateRange(1L, 20_000_000L)] public long MaxChunkRecords { get; set; } = 2_000_000;
    [Parameter] [ValidateRange(1, 1_000_000)] public int MaxOverlapPairs { get; set; } = 100_000;
    [Parameter] [ValidateRange(2, 1024)] public int MaxChunkFanout { get; set; } = 256;
    [Parameter] [ValidateRange(1L, 100_000_000L)] public long MaxPairComparisons { get; set; } = 2_000_000;
    [Parameter] public SwitchParameter NoCache { get; set; }
    protected override void ProcessRecord()
    {
        var options = new FileMapperHashingOptions(Mode, ScopePath is null ? null : GetUnresolvedProviderPathFromPSPath(ScopePath), MinimumBytes,
            MaxIdentities, MaxPaths, MaxBytesRead, Workers, BytesPerSecond, SampleWindowBytes, ChunkMinimumBytes,
            ChunkAverageBytes, ChunkMaximumBytes, MaxChunksPerFile, MaxChunkRecords, MaxOverlapPairs, MaxChunkFanout, MaxPairComparisons, !NoCache.IsPresent);
        if (ShouldProcess(SnapshotId, "Read current file content into a bounded " + Mode + " analysis"))
            WriteObject(Request<FileMapperHashingRun>("/api/hashing/jobs", HttpMethod.Post, new { snapshotId = SnapshotId, options }));
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperContentAnalysis")]
[OutputType(typeof(FileMapperHashingRun), typeof(FileMapperHashingRuns))]
public sealed class GetFileMapperContentAnalysisCommand : FileMapperCmdlet
{
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string? RunId { get; set; }
    [Parameter] public string? SnapshotId { get; set; }
    [Parameter] [ValidateRange(1, 200)] public int Limit { get; set; } = 100;
    protected override void ProcessRecord()
    {
        if (RunId is not null) WriteObject(Request<FileMapperHashingRun>("/api/hashing/jobs/" + Escape(RunId)));
        else WriteObject(Request<FileMapperHashingRuns>("/api/hashing/jobs?limit=" + Limit + (SnapshotId is null ? "" : "&snapshotId=" + Escape(SnapshotId))));
    }
}
public abstract class ContentRunCmdlet : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string RunId { get; set; } = "";
    protected string ContentRoute => "/api/hashing/jobs/" + Escape(RunId);
}
public abstract class ContentPageCmdlet : ContentRunCmdlet
{
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 200)] public int Limit { get; set; } = 50;
    protected string PageQuery => "offset=" + Offset + "&limit=" + Limit;
}
[Cmdlet(VerbsLifecycle.Suspend, "FileMapperContentAnalysis", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperContentControl))]
public sealed class SuspendFileMapperContentAnalysisCommand : ContentRunCmdlet
{
    protected override void ProcessRecord() { if (ShouldProcess(RunId, "Pause content analysis at a durable stage boundary")) WriteObject(Request<FileMapperContentControl>(ContentRoute + "/pause", HttpMethod.Post)); }
}
[Cmdlet(VerbsLifecycle.Stop, "FileMapperContentAnalysis", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperContentControl))]
public sealed class StopFileMapperContentAnalysisCommand : ContentRunCmdlet
{
    protected override void ProcessRecord() { if (ShouldProcess(RunId, "Cancel content analysis and retain committed evidence")) WriteObject(Request<FileMapperContentControl>(ContentRoute + "/cancel", HttpMethod.Post)); }
}
[Cmdlet(VerbsLifecycle.Resume, "FileMapperContentAnalysis", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperHashingRun))]
public sealed class ResumeFileMapperContentAnalysisCommand : ContentRunCmdlet
{
    protected override void ProcessRecord() { if (ShouldProcess(RunId, "Resume the original content policy with identity revalidation")) WriteObject(Request<FileMapperHashingRun>(ContentRoute + "/resume", HttpMethod.Post)); }
}
[Cmdlet(VerbsCommon.Get, "FileMapperContentMatch")]
[OutputType(typeof(FileMapperHashingPage<FileMapperHashingGroup>))]
public sealed class GetFileMapperContentMatchCommand : ContentPageCmdlet
{
    [Parameter] [ValidateSet("exact", "probable")] public string Mode { get; set; } = "exact";
    protected override void ProcessRecord() => WriteObject(Request<FileMapperHashingPage<FileMapperHashingGroup>>(ContentRoute + "/groups?mode=" + Mode + "&" + PageQuery));
}
[Cmdlet(VerbsCommon.Get, "FileMapperContentMember")]
[OutputType(typeof(FileMapperHashingPage<FileMapperHashingMember>))]
public sealed class GetFileMapperContentMemberCommand : ContentPageCmdlet
{
    [Parameter(Mandatory = true, Position = 1)] public string GroupId { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Request<FileMapperHashingPage<FileMapperHashingMember>>(ContentRoute + "/groups/" + Escape(GroupId) + "/members?" + PageQuery));
}
[Cmdlet(VerbsCommon.Get, "FileMapperContentOverlap")]
[OutputType(typeof(FileMapperHashingPage<FileMapperHashingOverlap>))]
public sealed class GetFileMapperContentOverlapCommand : ContentPageCmdlet
{
    [Parameter] public string? NodeId { get; set; }
    protected override void ProcessRecord() => WriteObject(Request<FileMapperHashingPage<FileMapperHashingOverlap>>(ContentRoute + "/overlaps?" + PageQuery + (NodeId is null ? "" : "&nodeId=" + Escape(NodeId))));
}
[Cmdlet(VerbsCommon.Get, "FileMapperContentEvidence")]
[OutputType(typeof(FileMapperHashingPage<FileMapperHashingEvidence>))]
public sealed class GetFileMapperContentEvidenceCommand : ContentPageCmdlet
{
    [Parameter] public string? NodeId { get; set; }
    protected override void ProcessRecord() => WriteObject(Request<FileMapperHashingPage<FileMapperHashingEvidence>>(ContentRoute + "/evidence?" + PageQuery + (NodeId is null ? "" : "&nodeId=" + Escape(NodeId))));
}
[Cmdlet(VerbsCommon.Get, "FileMapperContentChunk")]
[OutputType(typeof(FileMapperContentChunks))]
public sealed class GetFileMapperContentChunkCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] public string EvidenceId { get; set; } = "";
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 200)] public int Limit { get; set; } = 100;
    protected override void ProcessRecord() => WriteObject(Request<FileMapperContentChunks>("/api/hashing/evidence/" + Escape(EvidenceId) + "/chunks?offset=" + Offset + "&limit=" + Limit));
}

[Cmdlet(VerbsCommon.Get, "FileMapperContentPath")]
[OutputType(typeof(FileMapperHashingPage<string>))]
public sealed class GetFileMapperContentPathCommand : ContentPageCmdlet
{
    [Parameter(Mandatory = true, Position = 1)] public string NodeId { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Request<FileMapperHashingPage<string>>(
        ContentRoute + "/paths?nodeId=" + Escape(NodeId) + "&" + PageQuery));
}
