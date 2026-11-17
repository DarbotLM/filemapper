using System.Management.Automation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FileMapper.PowerShell;

// Independent lazy transport leaves existing cmdlets unchanged and makes export -WhatIf entirely local.
public abstract class SemanticCmdlet : PSCmdlet
{
    [Parameter] public Uri Server { get; set; } = new("http://127.0.0.1:4177/");
    [Parameter] public string TokenFile { get; set; } = DefaultTokenFile();
    [Parameter] [ValidateRange(1, 86400)] public int TimeoutSeconds { get; set; } = 300;
    private readonly CancellationTokenSource cancellation = new();
    protected static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    protected CancellationToken Cancellation => cancellation.Token;
    protected static string Escape(string value) => Uri.EscapeDataString(value);
    private static string DefaultTokenFile()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper");
        var installed = Path.Combine(directory, "data", "service.token");
        return File.Exists(installed) ? installed : Path.Combine(directory, "service.token");
    }
    protected override void BeginProcessing()
    {
        if (!Server.IsAbsoluteUri || !Server.IsLoopback || Server.Scheme is not ("http" or "https") ||
            Server.UserInfo.Length != 0 || Server.AbsolutePath != "/" || Server.Query.Length != 0 || Server.Fragment.Length != 0)
            ThrowTerminatingError(new ErrorRecord(new ArgumentException("Server must be a loopback HTTP(S) origin without user information, path, query, or fragment."),
                "FileMapper.Semantic.Server", ErrorCategory.InvalidArgument, Server));
    }
    protected HttpClient CreateClient()
    {
        Cancellation.ThrowIfCancellationRequested();
        var credential = File.ReadAllText(GetUnresolvedProviderPathFromPSPath(TokenFile)).Trim();
        if (credential.Length != 64 || !credential.All(Uri.IsHexDigit)) throw new InvalidOperationException("A valid local service role credential is required.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
        { BaseAddress = Server, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }
    protected CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        return deadline;
    }
    protected T Protect<T>(Func<T> operation, object? target, string id = "FileMapper.Semantic.Request")
    {
        try { return operation(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or HttpRequestException or JsonException or OperationCanceledException or OverflowException or NotSupportedException or KeyNotFoundException)
        {
            var category = error switch
            {
                UnauthorizedAccessException => ErrorCategory.PermissionDenied,
                FileNotFoundException or DirectoryNotFoundException => ErrorCategory.ObjectNotFound,
                FileMapperExportDestinationExistsException => ErrorCategory.ResourceExists,
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ErrorCategory.PermissionDenied,
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => ErrorCategory.ObjectNotFound,
                HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity } => ErrorCategory.InvalidArgument,
                HttpRequestException { StatusCode: HttpStatusCode.Conflict } => ErrorCategory.InvalidOperation,
                HttpRequestException => ErrorCategory.ConnectionError,
                OperationCanceledException => ErrorCategory.OperationStopped,
                ArgumentException or NotSupportedException => ErrorCategory.InvalidArgument,
                IOException when error is not InvalidDataException => ErrorCategory.WriteError,
                _ => ErrorCategory.InvalidResult
            };
            ThrowTerminatingError(new ErrorRecord(error, id, category, target));
            return default!;
        }
    }
    protected T Read<T>(string route, HttpMethod? method = null, object? body = null) => Protect(() =>
    {
        using var deadline = Deadline();
        using var client = CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, route);
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, WireJson);
            if (bytes.Length > 64 * 1024) throw new ArgumentException("Semantic replay request exceeds the service's 64 KiB body limit; accepted move count is also constrained by encoded request size.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
        EnsureSuccess(response, deadline.Token);
        using var stream = response.Content.ReadAsStream(deadline.Token);
        using var payload = new MemoryStream();
        CopyBounded(stream, payload, 256L * 1024 * 1024, deadline.Token);
        return JsonSerializer.Deserialize<T>(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)), WireJson)
            ?? throw new JsonException("Service returned an empty result.");
    }, route);
    protected static void EnsureSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        // Bound error text and retain status. Never include request headers or credentials.
        using var stream = response.Content.ReadAsStream(token);
        var buffer = new byte[8192]; var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.ReadAsync(buffer.AsMemory(count), token).AsTask().GetAwaiter().GetResult();
            if (read == 0) break;
            count += read;
        }
        throw new HttpRequestException($"FileMapper returned HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(buffer, 0, count)}", null, response.StatusCode);
    }
    protected static long CopyBounded(Stream source, Stream destination, long maximumBytes, CancellationToken token)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = source.ReadAsync(buffer, token).AsTask().GetAwaiter().GetResult();
            if (count == 0) break;
            if (count > maximumBytes - total) throw new InvalidDataException("Response exceeds the configured byte limit.");
            destination.WriteAsync(buffer.AsMemory(0, count), token).AsTask().GetAwaiter().GetResult();
            total += count;
        }
        return total;
    }
    protected override void StopProcessing() => cancellation.Cancel();
    protected override void EndProcessing() => cancellation.Dispose();
}

public abstract class SemanticSnapshotCmdlet : SemanticCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Id")] [ValidateNotNullOrEmpty] public string SnapshotId { get; set; } = "";
    protected string SnapshotRoute => "/api/snapshots/" + Escape(SnapshotId);
}

[Cmdlet(VerbsCommon.Get, "FileMapperSemanticObject", DefaultParameterSetName = "Items")]
[OutputType(typeof(FileMapperSemanticObject), typeof(FileMapperSemanticObjectPage))]
public sealed class GetFileMapperSemanticObjectCommand : SemanticSnapshotCmdlet
{
    [Parameter] [ValidateLength(0, 512)] public string? Search { get; set; }
    [Parameter] [ValidateLength(0, 128)] public string? Kind { get; set; }
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 500)] public int Limit { get; set; } = 50;
    [Parameter(ParameterSetName = "Items")] public SwitchParameter All { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Page")] public SwitchParameter AsPage { get; set; }
    protected override void ProcessRecord()
    {
        var offset = Offset;
        do
        {
            var route = SnapshotRoute + $"/semantic-objects?offset={offset}&limit={Limit}";
            if (Search is not null) route += "&search=" + Escape(Search);
            if (Kind is not null) route += "&kind=" + Escape(Kind);
            var page = Read<FileMapperSemanticObjectPage>(route);
            Protect(() =>
            {
                if (page.Snapshot.Id != SnapshotId || page.Offset != offset || page.Limit != Limit || page.Total < 0 ||
                    page.Items.Length > Limit || page.Items.Any(item => item.SourceReference.SnapshotId != SnapshotId || item.SnapshotWindow.SnapshotId != SnapshotId))
                    throw new InvalidDataException("Semantic page does not match the requested snapshot or paging bounds.");
                return true;
            }, route);
            WriteVerbose($"Snapshot {page.Snapshot.Id}: {page.Items.Length} of {page.Total} matching objects at offset {page.Offset}; completeness {page.Snapshot.Completeness}. Paging requests are separate reads; use semantic export for one pinned complete dataset.");
            if (AsPage) WriteObject(page); else WriteObject(page.Items, true);
            if (!All || (long)offset + page.Items.Length >= page.Total) break;
            offset = Protect(() => page.Items.Length == 0 ? throw new InvalidDataException("Semantic paging made no progress.") : checked(offset + page.Items.Length), route);
        } while (true);
    }
}

[Cmdlet(VerbsData.Compare, "FileMapperSemanticSnapshot")]
[OutputType(typeof(FileMapperSemanticPatternPage))]
public sealed class CompareFileMapperSemanticSnapshotCommand : SemanticCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] [ValidateNotNullOrEmpty] public string Before { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1)] [ValidateNotNullOrEmpty] public string After { get; set; } = "";
    [Parameter] [ValidateRange(1, 50)] public int Limit { get; set; } = 50;
    protected override void ProcessRecord() => WriteObject(Read<FileMapperSemanticPatternPage>($"/api/semantic-patterns?before={Escape(Before)}&after={Escape(After)}&limit={Limit}"));
}

[Cmdlet(VerbsCommon.Get, "FileMapperSemanticChallenge")]
[OutputType(typeof(FileMapperSemanticChallenge))]
public sealed class GetFileMapperSemanticChallengeCommand : SemanticSnapshotCmdlet
{
    [Parameter] [ValidateRange(1, 3)] public int Round { get; set; } = 1;
    protected override void ProcessRecord() => WriteObject(Read<FileMapperSemanticChallenge>(SnapshotRoute + "/semantic-game/challenge?round=" + Round));
}

[Cmdlet(VerbsLifecycle.Invoke, "FileMapperSemanticReplay")]
[OutputType(typeof(FileMapperSemanticReplay))]
public sealed class InvokeFileMapperSemanticReplayCommand : SemanticSnapshotCmdlet
{
    [Parameter] [ValidateRange(1, 3)] public int Round { get; set; } = 1;
    [Parameter(Mandatory = true)] [ValidatePattern("^[0-9a-f]{64}$")] public string ExpectedChallengeHash { get; set; } = "";
    [Parameter] [ValidateNotNull] [ValidateCount(0, 512)] public FileMapperSemanticMove[] Moves { get; set; } = [];
    protected override void ProcessRecord()
    {
        Protect(() =>
        {
            if (Moves.Any(move => move is null || move.Rotation is < 0 or > 3 || move.Column is < 0 or > 11 ||
                move.ExpectedPriorStateHash is null || move.ExpectedPriorStateHash.Length != 64 || move.ExpectedPriorStateHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
                throw new ArgumentException("Every move needs rotation 0–3, column 0–11 and the exact lowercase SHA256 prior-state hash returned by replay.");
            return true;
        }, SnapshotId);
        WriteObject(Read<FileMapperSemanticReplay>("/api/semantic-game/replay", HttpMethod.Post,
            new { SnapshotId, Round, ExpectedChallengeHash, Moves }));
    }
}
