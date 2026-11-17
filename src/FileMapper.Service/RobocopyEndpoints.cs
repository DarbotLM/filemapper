using FileMapper.Actions.Robocopy;

namespace FileMapper.Service;

public static class RobocopyEndpoints
{
    private static ActionCaller Caller(HttpContext context)
    {
        var session = context.Items["access-session"] as AccessSession ?? throw new UnauthorizedAccessException("Authenticated action session required.");
        return new($"{session.Role}@{Environment.UserDomainName}\\{Environment.UserName}", session.Capabilities.ToHashSet(StringComparer.Ordinal));
    }
    public static void MapRobocopyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/robocopy/catalog", (RobocopyBackend backend) => backend.GetCatalog()).RequireCapability("read");
        app.MapPost("/api/robocopy/validate", (RobocopyDraft draft, RobocopyBackend backend) => backend.Validate(draft)).RequireCapability("plan");
        app.MapPost("/api/robocopy/plans", (RobocopyDraft draft, RobocopyBackend backend, HttpContext context) =>
        {
            var plan = backend.CreatePlan(draft, Caller(context));
            return Results.Created($"/api/robocopy/plans/{plan.Id}", plan);
        }).RequireCapability("plan");
        app.MapGet("/api/robocopy/plans/{id}", (string id, RobocopyBackend backend) => backend.GetPlan(id)).RequireCapability("read");
        app.MapPost("/api/robocopy/plans/{id}/preview", (string id, RobocopyBackend backend, HttpContext context) =>
        {
            // A persisted background job outlives its HTTP start request. Cancel through the job endpoint.
            var job = backend.StartPreview(id, Caller(context));
            return Results.Accepted($"/api/robocopy/jobs/{job.Id}", job);
        }).RequireCapability("plan");
        app.MapGet("/api/robocopy/jobs", (int? limit, RobocopyBackend backend) => backend.GetJobs(Math.Clamp(limit ?? 100, 1, 1000))).RequireCapability("read");
        app.MapGet("/api/robocopy/jobs/{id}", (string id, RobocopyBackend backend) => backend.GetJob(id)).RequireCapability("read");
        app.MapGet("/api/robocopy/jobs/{id}/events", (string id, RobocopyBackend backend) => backend.GetEvents(id)).RequireCapability("read");
        app.MapGet("/api/robocopy/jobs/{id}/output", (string id, string? channel, int? maximumCharacters, RobocopyBackend backend) =>
            Results.Text(backend.ReadOutput(id, channel ?? "native", Math.Clamp(maximumCharacters ?? 65536, 1, 1_000_000)), "text/plain")).RequireCapability("read");
        app.MapPost("/api/robocopy/jobs/{id}/cancel", (string id, RobocopyBackend backend, HttpContext context) =>
            backend.Cancel(id, Caller(context)) ? Results.Accepted() : Results.Conflict(new { detail = "The job is already terminal." })).RequireCapability("plan");
        app.MapPost("/api/robocopy/import-job", (NativeJobText request, RobocopyBackend backend, HttpContext context) => backend.ImportJob(request.Text, Caller(context))).RequireCapability("plan");
        app.MapPost("/api/robocopy/export-job", (RobocopyDraft draft, RobocopyBackend backend, HttpContext context) => Results.Text(backend.ExportJob(draft, Caller(context)), "text/plain")).RequireCapability("plan");
        // Execution and approval are not exposed through the default reader/scanner/planner service.
        // The contained backend requires a separate explicitly configured executor host and credential.
    }
}
public sealed record NativeJobText(string Text);
