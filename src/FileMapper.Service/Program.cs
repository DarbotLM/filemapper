using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;
using FileMapper.Service;
using FileMapper.Actions.Robocopy;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = SourceHarvestConnectionStore.NormalizePath(builder.Configuration["data"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data"));
var tokenFile = Path.Combine(dataDirectory, "service.token");
var installerSession = bool.TryParse(builder.Configuration["installer-session"], out var installerFlag) && installerFlag;
var installerToken = installerSession ? Environment.GetEnvironmentVariable("FILEMAPPER_INSTALLER_TOKEN") : null;
if (installerSession && string.IsNullOrWhiteSpace(installerToken)) throw new InvalidOperationException("Installer session credentials are missing.");
// Resolve saved source evidence before creating credentials or initializing the map database.
LocalCredentials.EnsurePrivateDirectory(dataDirectory);
var sourceConnections = new SourceHarvestConnectionStore(dataDirectory);
using var sourceSelection = await sourceConnections.ResolveAsync(builder.Configuration["harvest-workspace"], builder.Configuration["harvest-compact-index"]);
var credentials = new LocalCredentials(dataDirectory);
var port = int.TryParse(builder.Configuration["port"], out int configuredPort) ? configuredPort : 4177;
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = 64 * 1024; });
builder.Services.AddSingleton(new MapStore(Path.Combine(dataDirectory, "filemapper.db"), installerToken));
builder.Services.AddSingleton<ScanCoordinator>(); builder.Services.AddSingleton<QueryEngine>();
builder.Services.AddSingleton(_ => new WordLibraryWorkspace(dataDirectory));
builder.Services.AddSingleton<HashingCoordinator>();
builder.Services.AddSingleton<HashingSpatialProjection>();
builder.Services.AddSingleton<SemanticWorkspace>();
builder.Services.AddSingleton<PatternLearningWorkspace>();
builder.Services.AddSingleton<SpatialWorkspace>();
builder.Services.AddSingleton<EditionPracticeWorkspace>();
builder.Services.AddSingleton(_ => new SourceSemanticCatalog(dataDirectory));
// Deferred initialization keeps the installer's health probe free of semantic writer admission.
builder.Services.AddSingleton(services =>
{
    // Initialize the existing job schema before a source operation can acquire the shared writer.
    _ = services.GetRequiredService<ScanCoordinator>();
    return SourceSemanticCoordinator.OpenAsync(services.GetRequiredService<MapStore>(), sourceSelection,
        services.GetRequiredService<SourceSemanticCatalog>(), dataDirectory).GetAwaiter().GetResult();
});
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton(_ =>
    {
        var worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "worker", "FileMapper.Actions.Robocopy.exe"));
        var processPath = Environment.ProcessPath;
        var dotnet = builder.Configuration["robocopy:dotnetHost"] ??
            (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? processPath : null);
        return new RobocopyBackend(new(Path.Combine(dataDirectory, "actions", "robocopy"))
        {
            WorkerExecutablePath = File.Exists(worker) ? worker : null,
            DotnetHostPath = dotnet,
            RuntimeConfigPath = Path.Combine(AppContext.BaseDirectory, "FileMapper.Service.runtimeconfig.json"),
            MaintenanceLockPath = Path.Combine(dataDirectory, "filemapper.db.maintenance.lock"),
            EnableExecution = false
        });
    });
}
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
var store = app.Services.GetRequiredService<MapStore>(); store.Initialize();
app.UseRouting();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    var host = context.Request.Host.Host;
    if (host != "127.0.0.1" && host != "localhost" && host != "[::1]") { context.Response.StatusCode = 400; return; }
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/health")
    {
        context.Response.Headers.CacheControl = "no-store";
        var session = credentials.Authenticate(context.Request.Headers.Authorization.ToString());
        if (session == null)
        { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { title = "Authentication required", detail = "Connect using the local service token." }); return; }
        var endpoint = context.GetEndpoint();
        if (endpoint == null) { context.Response.StatusCode = 404; return; }
        var capability = endpoint.Metadata.GetMetadata<EndpointCapability>()?.Name;
        if (capability == null)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { title = "Operation is not enabled", detail = "This endpoint has no declared FileMapper capability." });
            return;
        }
        if (!session.Capabilities.Contains(capability))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { title = "Role cannot perform this operation", detail = $"The {session.Role} role does not have {capability} permission." }); return; }
        context.Items["access-session"] = session;
        if (context.Request.Headers.Origin.Count > 0 && !Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out _)) { context.Response.StatusCode = 403; return; }
        if (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != $"http://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch (BadHttpRequestException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { title = "Invalid HTTP request", detail = ex.StatusCode == 413
            ? "Request body exceeds this endpoint's configured size limit." : "The HTTP request could not be read." });
    }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex switch { KeyNotFoundException => 404, UnauthorizedAccessException => 403, InvalidOperationException => 409, _ => 400 };
        await context.Response.WriteAsJsonAsync(new { title = "Request could not be completed", detail = ex.Message });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { title = "Resource is in use", detail = "Another action or maintenance operation holds this resource. Retry after it finishes." });
    }
    catch (InvalidDataException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { title = "Evidence could not be verified", detail = ex.Message });
    }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 404;
        await context.Response.WriteAsJsonAsync(new { title = "Record not found", detail = "The requested local record is unavailable." });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed: {TraceId}", context.TraceIdentifier);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { title = "Internal processing error", detail = "The request failed. Consult local diagnostics using the trace ID.", traceId = context.TraceIdentifier });
    }
});
// Release archives deliberately normalize timestamps for reproducibility. The application shell
// must not reuse a length/mtime validator across equal-sized upgrades with different contents.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/health", () => new { status = "ok", product = "FileMapper", version = typeof(EndpointCapability).Assembly.GetName().Version?.ToString(3), release = "development", authentication = "bearer", processId = Environment.ProcessId, platform = Environment.OSVersion.Platform.ToString() });
app.MapGet("/api/snapshots", (MapStore db) => db.ListSnapshots()).RequireCapability("read");
app.MapGet("/api/session", (HttpContext context) => context.Items["access-session"]).RequireCapability("read");
app.MapGet("/api/jobs", (ScanCoordinator scans, SourceSemanticCoordinator sourceJobs, HashingCoordinator hashing) => scans.Jobs
    .Concat(sourceJobs.List(200).Select(j => new JobStatus(j.Id, "", j.Kind, j.Status, j.StartedAt,
        j.FinishedAt, j.Coverage?.Nodes ?? 0, j.Error)))
    .Concat(hashing.List(limit: 200).Select(j => new JobStatus(j.Id, j.SnapshotId, "content-hashing", j.Status,
        j.StartedAt, j.FinishedAt, j.Coverage.FullyHashedIdentities, j.Error)))
    .OrderByDescending(j => j.StartedAt).ToArray()).RequireCapability("read");
if (OperatingSystem.IsWindows()) app.MapRobocopyEndpoints();
app.MapSemanticEndpoints();
app.MapLearningEndpoints();
app.MapHarvestEndpoints(sourceSelection, sourceConnections);
app.MapWordLibraryEndpoints();
app.MapSourceSemanticEndpoints();
app.MapHashingEndpoints();
app.MapPost("/api/scans", (ScanRequest request, ScanCoordinator scans) =>
{
    var job = scans.StartScan(new(request.Root, request.ReparsePolicy, Excludes: request.Excludes, HashFiles: request.HashFiles));
    return Results.Accepted("/api/jobs", new { jobId = job.Id, job.SnapshotId });
}).RequireCapability("scan");
app.MapPost("/api/jobs/{id}/cancel", (string id, ScanCoordinator scans, SourceSemanticCoordinator sourceJobs, HashingCoordinator hashing) =>
    (id.StartsWith("source-job-", StringComparison.Ordinal) ? sourceJobs.Cancel(id) : id.StartsWith("hash-job-", StringComparison.Ordinal) ? hashing.Cancel(id) : scans.Cancel(id))
        ? Results.Ok(new { cancellationRequested = true }) : Results.NotFound()).RequireCapability("scan");
app.MapPost("/api/jobs/{id}/pause", (string id, ScanCoordinator scans, HashingCoordinator hashing) =>
    (id.StartsWith("hash-job-", StringComparison.Ordinal) ? hashing.Pause(id) : scans.Pause(id))
        ? Results.Ok(new { paused = true }) : Results.Conflict(new { detail = "Only active resumable scan or content work can be paused." })).RequireCapability("scan");
app.MapPost("/api/jobs/{id}/resume", (string id, ScanCoordinator scans, HashingCoordinator hashing) =>
    id.StartsWith("hash-job-", StringComparison.Ordinal) ? Results.Accepted("/api/hashing/jobs/" + id, (object)hashing.Resume(id)) : Results.Accepted("/api/jobs", (object)scans.Resume(id))).RequireCapability("scan");
app.MapPost("/api/snapshots/{id}/analyze", (string id, [Microsoft.AspNetCore.Mvc.FromQuery] string[]? analyzerReportPaths, ScanCoordinator scans) => Results.Accepted("/api/jobs", scans.StartAnalysis(id, analyzerReportPaths))).RequireCapability("scan");
app.MapGet("/api/snapshots/{id}", (string id, QueryEngine query) => query.Require(id)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/entries", (string id, string? parent, string? search, int? offset, int? limit, MapStore db, QueryEngine query, CancellationToken cancellationToken) =>
{ query.Require(id); return db.GetEntries(id, parent, search, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 1000), cancellationToken); }).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/nodes", (string id, string? kind, string? search, int? offset, int? limit, MapStore db, QueryEngine query) =>
{ query.Require(id); return db.GetNodes(id, kind, search, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 1000)); }).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/edges", (string id, string? nodeId, MapStore db, QueryEngine query) => { query.Require(id); return db.GetEdges(id, nodeId, 500); }).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/observations", (string id, string? subjectId, MapStore db, QueryEngine query) => { query.Require(id); return db.GetObservations(id, subjectId, 200); }).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/findings", (string id, MapStore db, QueryEngine query) => { query.Require(id); return db.GetFindings(id); }).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/findings/{findingId}", (string id, string findingId, QueryEngine query) => query.ExplainFinding(id, findingId)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/rank", (string id, string? by, int? limit, QueryEngine query, CancellationToken cancellationToken) => query.Rank(id, by ?? "logical", limit ?? 50, cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/hierarchy", (string id, string? parent, int? offset, int? limit, QueryEngine query, CancellationToken cancellationToken) => query.Hierarchy(id, parent, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 1000), cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/breakdown", (string id, string? by, string? path, int? offset, int? limit, QueryEngine query, CancellationToken cancellationToken) => query.Breakdown(id, by ?? "extension", path, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 500), cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/duplicates", (string id, int? offset, int? limit, QueryEngine query, CancellationToken cancellationToken) => query.Duplicates(id, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 500), cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/duplicates/members", (string id, string hash, long logicalBytes, int? offset, int? limit, QueryEngine query, CancellationToken cancellationToken) => query.DuplicateMembers(id, hash, logicalBytes, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 1000, 1, 1000), cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/route", (string id, string from, string to, int? maxDepth, QueryEngine query, CancellationToken cancellationToken) => query.Route(id, from, to, maxDepth ?? 12, cancellationToken)).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/context", (string id, string search, int? budget, QueryEngine query, CancellationToken cancellationToken) => query.Context(id, search, budget ?? 16000, cancellationToken)).RequireCapability("read");
app.MapGet("/api/diff", (string before, string after, int? offset, int? limit, QueryEngine query) => query.Diff(before, after, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 1000, 1, 1000))).RequireCapability("read");
app.MapGet("/api/snapshots/{id}/export", async (string id, string? format, MapStore db, QueryEngine query, HttpContext context) =>
{
    query.Require(id); format ??= "json";
    if (format is not ("json" or "csv" or "ndjson" or "sarif")) throw new ArgumentException("Unsupported export format.");
    context.Response.ContentType = format == "csv" ? "text/csv; charset=utf-8" : format == "ndjson" ? "application/x-ndjson" : "application/json";
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"filemapper-{id}.{format}\"";
    await Exporter.WriteAsync(db, id, format, context.Response.Body, context.RequestAborted);
}).RequireCapability("read");
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("FileMapper at http://127.0.0.1:{Port}. Token file: {TokenFile}", port, tokenFile));
await app.RunAsync();
public sealed record ScanRequest(string Root, bool HashFiles = false, string ReparsePolicy = "skip", string[]? Excludes = null);
