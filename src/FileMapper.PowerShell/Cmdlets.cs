using System.Management.Automation;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FileMapper.PowerShell;

public abstract class FileMapperCmdlet : PSCmdlet
{
    [Parameter] public Uri Server { get; set; } = new("http://127.0.0.1:4177/");
    [Parameter] public string TokenFile { get; set; } = DefaultTokenFile();
    private static string DefaultTokenFile()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper");
        var installed = Path.Combine(root, "data", "service.token");
        return File.Exists(installed) ? installed : Path.Combine(root, "service.token");
    }
    private readonly CancellationTokenSource cancellation = new();
    private HttpClient? client;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override void BeginProcessing()
    {
        try
        {
            if (!Server.IsAbsoluteUri || !Server.IsLoopback || Server.Scheme is not ("http" or "https") || Server.UserInfo.Length != 0)
                throw new ArgumentException("Server must be an absolute loopback HTTP(S) URL without user information.");
            var token = File.ReadAllText(GetUnresolvedProviderPathFromPSPath(TokenFile)).Trim();
            if (token.Length < 32) throw new InvalidOperationException("Service token is missing or invalid.");
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ThrowTerminatingError(new ErrorRecord(ex, "FileMapper.Connection", ErrorCategory.ConnectionError, Server)); }
    }
    protected T Request<T>(string route, HttpMethod? method = null, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, route);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var response = client!.SendAsync(request, cancellation.Token).GetAwaiter().GetResult();
            var content = response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"FileMapper returned HTTP {(int)response.StatusCode}: {content}", null, response.StatusCode);
            return JsonSerializer.Deserialize<T>(content, Json) ?? throw new JsonException("Service returned an empty response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "FileMapper.Request", ex is OperationCanceledException ? ErrorCategory.OperationStopped : ErrorCategory.InvalidResult, route));
            return default!;
        }
    }
    protected static string Escape(string value) => Uri.EscapeDataString(value);
    protected override void StopProcessing() => cancellation.Cancel();
    protected override void EndProcessing() { client?.Dispose(); cancellation.Dispose(); }
}

[Cmdlet(VerbsLifecycle.Start, "FileMapperScan", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperScanReceipt))]
public sealed class StartFileMapperScanCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)] [ValidateNotNullOrEmpty] public string Path { get; set; } = "";
    [Parameter] public SwitchParameter HashFiles { get; set; }
    [Parameter] [ValidateSet("skip", "within", "all", "reference")] public string ReparsePolicy { get; set; } = "skip";
    [Parameter] public string[]? Exclude { get; set; }
    protected override void ProcessRecord()
    {
        var root = GetUnresolvedProviderPathFromPSPath(Path);
        if (ShouldProcess(root, "Create filesystem snapshot")) WriteObject(Request<FileMapperScanReceipt>("/api/scans", HttpMethod.Post, new { root, hashFiles = HashFiles.IsPresent, reparsePolicy = ReparsePolicy, excludes = Exclude }));
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperSnapshot")]
[OutputType(typeof(FileMapperSnapshot))]
public sealed class GetFileMapperSnapshotCommand : FileMapperCmdlet
{
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string? SnapshotId { get; set; }
    protected override void ProcessRecord()
    {
        if (string.IsNullOrWhiteSpace(SnapshotId)) WriteObject(Request<FileMapperSnapshot[]>("/api/snapshots"), enumerateCollection: true);
        else WriteObject(Request<FileMapperSnapshot>("/api/snapshots/" + Escape(SnapshotId)));
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperJob")]
[OutputType(typeof(FileMapperJob))]
public sealed class GetFileMapperJobCommand : FileMapperCmdlet
{
    protected override void ProcessRecord() => WriteObject(Request<FileMapperJob[]>("/api/jobs"), true);
}

[Cmdlet(VerbsLifecycle.Suspend, "FileMapperScan", SupportsShouldProcess = true)]
public sealed class SuspendFileMapperScanCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string JobId { get; set; } = "";
    protected override void ProcessRecord() { if (ShouldProcess(JobId, "Pause scan at its next durable checkpoint")) WriteObject(Request<JsonElement>("/api/jobs/" + Escape(JobId) + "/pause", HttpMethod.Post)); }
}

[Cmdlet(VerbsLifecycle.Resume, "FileMapperScan", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperJob))]
public sealed class ResumeFileMapperScanCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string JobId { get; set; } = "";
    protected override void ProcessRecord() { if (ShouldProcess(JobId, "Resume scan from its durable traversal queue")) WriteObject(Request<FileMapperJob>("/api/jobs/" + Escape(JobId) + "/resume", HttpMethod.Post)); }
}

[Cmdlet(VerbsLifecycle.Stop, "FileMapperScan", SupportsShouldProcess = true)]
public sealed class StopFileMapperScanCommand : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string JobId { get; set; } = "";
    protected override void ProcessRecord() { if (ShouldProcess(JobId, "Cancel scan; retain incomplete evidence")) WriteObject(Request<JsonElement>("/api/jobs/" + Escape(JobId) + "/cancel", HttpMethod.Post)); }
}

public abstract class SnapshotCmdlet : FileMapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Id")] [ValidateNotNullOrEmpty] public string SnapshotId { get; set; } = "";
    protected string SnapshotRoute => "/api/snapshots/" + Escape(SnapshotId);
}

[Cmdlet(VerbsCommon.Get, "FileMapperItem")]
[OutputType(typeof(FileMapperItem))]
public sealed class GetFileMapperItemCommand : SnapshotCmdlet
{
    [Parameter] public string? Search { get; set; }
    [Parameter] public string? Parent { get; set; }
    [Parameter] [ValidateRange(1, 1000)] public int Limit { get; set; } = 100;
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    protected override void ProcessRecord()
    {
        var route = SnapshotRoute + $"/entries?offset={Offset}&limit={Limit}";
        if (Search is not null) route += "&search=" + Escape(Search);
        if (Parent is not null) route += "&parent=" + Escape(Parent);
        var page = Request<FileMapperPage<FileMapperItem>>(route);
        WriteVerbose($"Showing {page.Items.Length} of {page.Total} entries at offset {page.Offset}.");
        WriteObject(page.Items, true);
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperTree")]
[OutputType(typeof(FileMapperHierarchyItem))]
public sealed class GetFileMapperTreeCommand : SnapshotCmdlet
{
    [Parameter] public string? Parent { get; set; }
    [Parameter] [ValidateRange(1, 1000)] public int Limit { get; set; } = 100;
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    protected override void ProcessRecord()
    {
        var parent = Parent ?? Request<FileMapperSnapshot>(SnapshotRoute).Root;
        var page = Request<FileMapperHierarchy>(SnapshotRoute + "/hierarchy?parent=" + Escape(parent) + "&limit=" + Limit + "&offset=" + Offset).Page;
        if (page.Total > page.Items.Length + Offset) WriteWarning($"Showing {page.Items.Length} of {page.Total} children. Use -Offset for further pages.");
        WriteObject(page.Items, true);
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperRanking")]
[OutputType(typeof(FileMapperRankingResult))]
public sealed class GetFileMapperRankingCommand : SnapshotCmdlet
{
    [Parameter] [ValidateSet("logical", "allocated", "age")] public string By { get; set; } = "logical";
    [Parameter] [ValidateRange(1, 500)] public int Top { get; set; } = 50;
    protected override void ProcessRecord() => WriteObject(Request<FileMapperRankingResult>(SnapshotRoute + "/rank?by=" + By + "&limit=" + Top));
}

[Cmdlet(VerbsCommon.Find, "FileMapperDuplicate")]
[OutputType(typeof(FileMapperDuplicateResult))]
public sealed class FindFileMapperDuplicateCommand : SnapshotCmdlet
{
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 500)] public int Limit { get; set; } = 100;
    protected override void ProcessRecord() => WriteObject(Request<FileMapperDuplicateResult>(SnapshotRoute + $"/duplicates?offset={Offset}&limit={Limit}"));
}

[Cmdlet(VerbsCommon.Get, "FileMapperBreakdown")]
[OutputType(typeof(FileMapperBreakdown))]
public sealed class GetFileMapperBreakdownCommand : SnapshotCmdlet
{
    [Parameter] [ValidateSet("extension", "owner", "age")] public string By { get; set; } = "extension";
    [Parameter] public string? Path { get; set; }
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 500)] public int Limit { get; set; } = 100;
    protected override void ProcessRecord() => WriteObject(Request<FileMapperBreakdown>(SnapshotRoute + $"/breakdown?by={By}&offset={Offset}&limit={Limit}" + (Path is null ? "" : "&path=" + Escape(Path))));
}

[Cmdlet(VerbsCommon.Get, "FileMapperDuplicateMember")]
[OutputType(typeof(FileMapperDuplicateMembers))]
public sealed class GetFileMapperDuplicateMemberCommand : SnapshotCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] [ValidateNotNullOrEmpty] public string Hash { get; set; } = "";
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] [ValidateRange(0, long.MaxValue)] public long LogicalBytes { get; set; }
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 1000)] public int Limit { get; set; } = 1000;
    protected override void ProcessRecord() => WriteObject(Request<FileMapperDuplicateMembers>(SnapshotRoute + $"/duplicates/members?hash={Escape(Hash)}&logicalBytes={LogicalBytes}&offset={Offset}&limit={Limit}"));
}

[Cmdlet(VerbsCommon.Find, "FileMapperRoute")]
[OutputType(typeof(FileMapperRoute))]
public sealed class FindFileMapperRouteCommand : SnapshotCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string From { get; set; } = "";
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string To { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Request<FileMapperRoute>(SnapshotRoute + "/route?from=" + Escape(From) + "&to=" + Escape(To)));
}

[Cmdlet(VerbsLifecycle.Invoke, "FileMapperAudit")]
[OutputType(typeof(FileMapperFinding), typeof(FileMapperFindingExplanation))]
public sealed class InvokeFileMapperAuditCommand : SnapshotCmdlet
{
    [Parameter] [ValidateNotNullOrEmpty] public string? FindingId { get; set; }
    protected override void ProcessRecord()
    {
        if (FindingId is not null) WriteObject(Request<FileMapperFindingExplanation>(SnapshotRoute + "/findings/" + Escape(FindingId)));
        else WriteObject(Request<FileMapperFinding[]>(SnapshotRoute + "/findings"), true);
    }
}

[Cmdlet(VerbsLifecycle.Start, "FileMapperAnalysis", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperJob))]
public sealed class StartFileMapperAnalysisCommand : SnapshotCmdlet
{
    [Parameter] [ValidateCount(0, 16)] public string[]? AnalyzerReportPath { get; set; }
    protected override void ProcessRecord()
    {
        var reports = AnalyzerReportPath?.Select(GetUnresolvedProviderPathFromPSPath).ToArray() ?? [];
        var suffix = reports.Length == 0 ? "" : "?" + string.Join('&', reports.Select(p => "analyzerReportPaths=" + Escape(p)));
        if (ShouldProcess(SnapshotId, "Save immutable static analysis and explicitly selected analyzer reports"))
            WriteObject(Request<FileMapperJob>(SnapshotRoute + "/analyze" + suffix, HttpMethod.Post));
    }
}
