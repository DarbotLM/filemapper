using System.Management.Automation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMapper.PowerShell;

// A separate lazy HTTP base keeps -WhatIf entirely local, including no token-file read or HTTP call.
public abstract class RobocopyCmdlet : PSCmdlet
{
    [Parameter] public Uri Server { get; set; } = new("http://127.0.0.1:4177/");
    [Parameter] public string TokenFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "planner.token");
    private readonly CancellationTokenSource cancellation = new();
    private HttpClient? client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64, Converters = { new NativeObjectConverter() } };
    protected static string Escape(string id) => Uri.EscapeDataString(id);
    protected override void BeginProcessing()
    {
        if (!Server.IsAbsoluteUri || !Server.IsLoopback || Server.Scheme is not ("http" or "https") || Server.UserInfo.Length != 0 || Server.AbsolutePath != "/" || Server.Query.Length != 0 || Server.Fragment.Length != 0)
            ThrowTerminatingError(new ErrorRecord(new ArgumentException("Server must be a loopback HTTP(S) origin without user information, path, query, or fragment."), "FileMapper.Robocopy.Server", ErrorCategory.InvalidArgument, Server));
    }
    protected T Read<T>(string route, HttpMethod? method = null, object? body = null) => Protect(() => JsonSerializer.Deserialize<T>(Send(route, method, body), Json) ?? throw new JsonException("Service returned an empty response."), route);
    protected string Text(string route, HttpMethod? method = null, object? body = null) => Protect(() => Send(route, method, body), route);
    protected void Empty(string route) => Protect(() => Send(route, HttpMethod.Post, null), route);
    protected T Protect<T>(Func<T> action, object? target)
    {
        try { return action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or HttpRequestException or JsonException or OperationCanceledException)
        {
            var category = e switch { UnauthorizedAccessException => ErrorCategory.PermissionDenied, HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ErrorCategory.PermissionDenied,
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => ErrorCategory.ObjectNotFound, OperationCanceledException => ErrorCategory.OperationStopped,
                ArgumentException => ErrorCategory.InvalidArgument, _ => ErrorCategory.InvalidResult };
            ThrowTerminatingError(new ErrorRecord(e, "FileMapper.Robocopy.Request", category, target)); return default!;
        }
    }
    private string Send(string route, HttpMethod? method, object? body)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if (client is null)
        {
            var token = File.ReadAllText(GetUnresolvedProviderPathFromPSPath(TokenFile)).Trim();
            if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new InvalidOperationException("A valid local service role credential is required.");
            client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, route);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) throw new ArgumentException("Request exceeds the service's 64 KiB limit.");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
        using var stream = response.Content.ReadAsStream(deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = reader.ReadAsync(buffer.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult(); if (count == 0) break;
            if (text.Length + count > 8 * 1024 * 1024) throw new IOException("Service response exceeds the bounded 8 Mi-character response limit.");
            text.Append(buffer, 0, count);
        }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"FileMapper returned HTTP {(int)response.StatusCode}: {text.ToString(0, Math.Min(text.Length, 8192))}", null, response.StatusCode);
        return text.ToString();
    }
    protected string ReadLocalText(string literalPath) => Protect(() =>
    {
        using var stream = File.OpenRead(GetUnresolvedProviderPathFromPSPath(literalPath));
        if (stream.Length > 1024 * 1024) throw new ArgumentException("RCJ input exceeds 1 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var buffer = new char[1024 * 1024 + 1]; var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 1024 * 1024) throw new ArgumentException("RCJ input exceeds 1 MiB.");
        return new string(buffer, 0, count);
    }, literalPath);
    protected override void StopProcessing() => cancellation.Cancel();
    protected override void EndProcessing() { client?.Dispose(); cancellation.Dispose(); }

    // JsonElement values lose their contents through PowerShell ConvertTo-Json. Materialize dynamic catalog fields as CLR values.
    private sealed class NativeObjectConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return Convert(document.RootElement);
        }
        private static object? Convert(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToArray(), JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var i) ? i : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
            JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => throw new JsonException("Unsupported catalog value.")
        };
        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}

[Cmdlet(VerbsCommon.Get, "FileMapperRobocopyCatalog")]
[OutputType(typeof(FileMapperRobocopyCatalog))]
public sealed class GetFileMapperRobocopyCatalogCommand : RobocopyCmdlet
{ protected override void ProcessRecord() => WriteObject(Read<FileMapperRobocopyCatalog>("/api/robocopy/catalog")); }

public abstract class RobocopyDraftCmdlet : RobocopyCmdlet
{ [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)] [ValidateNotNull] public FileMapperRobocopyDraft Draft { get; set; } = new(); }

[Cmdlet(VerbsDiagnostic.Test, "FileMapperRobocopyDraft")]
[OutputType(typeof(FileMapperRobocopyValidation))]
public sealed class TestFileMapperRobocopyDraftCommand : RobocopyDraftCmdlet
{ protected override void ProcessRecord() => WriteObject(Read<FileMapperRobocopyValidation>("/api/robocopy/validate", HttpMethod.Post, Draft)); }

[Cmdlet(VerbsCommon.New, "FileMapperRobocopyPlan", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperRobocopyPlan))]
public sealed class NewFileMapperRobocopyPlanCommand : RobocopyDraftCmdlet
{ protected override void ProcessRecord() { if (ShouldProcess(Draft.Source + " -> " + Draft.Destination, "Persist immutable Robocopy plan; no copy")) WriteObject(Read<FileMapperRobocopyPlan>("/api/robocopy/plans", HttpMethod.Post, Draft)); } }

public abstract class RobocopyPlanCmdlet : RobocopyCmdlet
{ [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] [ValidateNotNullOrEmpty] public string PlanId { get; set; } = ""; }

[Cmdlet(VerbsCommon.Get, "FileMapperRobocopyPlan")]
[OutputType(typeof(FileMapperRobocopyPlan))]
public sealed class GetFileMapperRobocopyPlanCommand : RobocopyPlanCmdlet
{ protected override void ProcessRecord() => WriteObject(Read<FileMapperRobocopyPlan>("/api/robocopy/plans/" + Escape(PlanId))); }

[Cmdlet(VerbsLifecycle.Start, "FileMapperRobocopyPreview", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperRobocopyJob))]
public sealed class StartFileMapperRobocopyPreviewCommand : RobocopyPlanCmdlet
{ protected override void ProcessRecord() { if (ShouldProcess(PlanId, "Start contained Robocopy /L job; read/hash trees and write managed evidence")) WriteObject(Read<FileMapperRobocopyJob>("/api/robocopy/plans/" + Escape(PlanId) + "/preview", HttpMethod.Post)); } }

[Cmdlet(VerbsCommon.Get, "FileMapperRobocopyJob")]
[OutputType(typeof(FileMapperRobocopyJob))]
public sealed class GetFileMapperRobocopyJobCommand : RobocopyCmdlet
{
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] public string? JobId { get; set; }
    [Parameter] [ValidateRange(1, 1000)] public int Limit { get; set; } = 100;
    protected override void ProcessRecord()
    { if (JobId is null) WriteObject(Read<FileMapperRobocopyJob[]>("/api/robocopy/jobs?limit=" + Limit), true); else WriteObject(Read<FileMapperRobocopyJob>("/api/robocopy/jobs/" + Escape(JobId))); }
}
public abstract class RobocopyJobCmdlet : RobocopyCmdlet
{ [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] [Alias("Id")] [ValidateNotNullOrEmpty] public string JobId { get; set; } = ""; }

[Cmdlet(VerbsCommon.Get, "FileMapperRobocopyEvent")]
[OutputType(typeof(FileMapperRobocopyEvent))]
public sealed class GetFileMapperRobocopyEventCommand : RobocopyJobCmdlet
{ protected override void ProcessRecord() => WriteObject(Read<FileMapperRobocopyEvent[]>("/api/robocopy/jobs/" + Escape(JobId) + "/events"), true); }

[Cmdlet(VerbsCommon.Get, "FileMapperRobocopyOutput")]
[OutputType(typeof(string))]
public sealed class GetFileMapperRobocopyOutputCommand : RobocopyJobCmdlet
{
    [Parameter] [ValidateSet("native", "stdout", "stderr")] public string Channel { get; set; } = "native";
    [Parameter] [ValidateRange(1, 1_000_000)] public int MaximumCharacters { get; set; } = 65536;
    protected override void ProcessRecord() => WriteObject(Text("/api/robocopy/jobs/" + Escape(JobId) + $"/output?channel={Channel}&maximumCharacters={MaximumCharacters}"));
}

[Cmdlet(VerbsLifecycle.Stop, "FileMapperRobocopyJob", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperRobocopyCancellation))]
public sealed class StopFileMapperRobocopyJobCommand : RobocopyJobCmdlet
{ protected override void ProcessRecord() { if (ShouldProcess(JobId, "Request durable cancellation; partial changes can remain for execution jobs")) { Empty("/api/robocopy/jobs/" + Escape(JobId) + "/cancel"); WriteObject(new FileMapperRobocopyCancellation(JobId, true)); } } }

[Cmdlet(VerbsData.Import, "FileMapperRobocopyJob", SupportsShouldProcess = true, DefaultParameterSetName = "Path")]
[OutputType(typeof(FileMapperRobocopyImport))]
public sealed class ImportFileMapperRobocopyJobCommand : RobocopyCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Path")] [ValidateNotNullOrEmpty] public string LiteralPath { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Text")] [ValidateNotNullOrEmpty] public string InputText { get; set; } = "";
    protected override void ProcessRecord()
    { if (ShouldProcess(ParameterSetName == "Path" ? LiteralPath : "supplied RCJ text", "Parse native job as untrusted data; never execute")) WriteObject(Read<FileMapperRobocopyImport>("/api/robocopy/import-job", HttpMethod.Post, new { text = ParameterSetName == "Path" ? ReadLocalText(LiteralPath) : InputText })); }
}

[Cmdlet(VerbsData.Export, "FileMapperRobocopyJob", SupportsShouldProcess = true)]
[OutputType(typeof(string))]
public sealed class ExportFileMapperRobocopyJobCommand : RobocopyDraftCmdlet
{ protected override void ProcessRecord() { if (ShouldProcess(Draft.Source + " -> " + Draft.Destination, "Render reviewed RCJ data to pipeline; no native /SAVE and no copy")) WriteObject(Text("/api/robocopy/export-job", HttpMethod.Post, Draft)); } }
