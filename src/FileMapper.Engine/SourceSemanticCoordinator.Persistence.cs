using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FileMapper.Semantics;

namespace FileMapper.Engine;

public sealed partial class SourceSemanticCoordinator
{
    private const int MaximumHistory = 200;
    private const int MaximumLedgerBytes = 4 * 1024 * 1024;
    private const string LedgerSchema = "filemapper.source-semantic-jobs/1";
    private readonly Dictionary<string, SourceSemanticJob> history = new(StringComparer.Ordinal);
    private long revision;

    private async Task ReadLedgerAsync(CancellationToken token)
    {
        Ordinary(ledgerPath);
        if (!File.Exists(ledgerPath)) return;
        await using var file = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var ledger = await NativeRequestJson.ReadAsync<SourceSemanticJobLedger>(file, MaximumLedgerBytes, token);
        if (ledger.Schema != LedgerSchema || ledger.Revision < 1 || ledger.Jobs.Count > MaximumHistory)
            throw new InvalidDataException("Unsupported or oversized retained-source job ledger.");
        foreach (var job in ledger.Jobs)
        {
            RequireJobId(job.Id);
            if (job.Kind is not ("source-semantics" or "source-semantic-import") ||
                job.Status is not ("running" or "cancelling" or "completed" or "failed" or "cancelled" or "interrupted") ||
                job.OutputState is not ("unsealed" or "sealed-unregistered" or "registered" or "unknown-after-interruption") ||
                job.CreatedAt == default || job.StartedAt == default || job.UpdatedAt == default ||
                job.ProgressSequence < 0 || job.Error?.Length > 4096 || job.Phase.Length > 128 ||
                job.OwnerId.Length > 100 || !HashValue(job.AdmissionSha256) || !HashValue(job.RequestSha256) ||
                job.CaptureId is not null && !HashValue(job.CaptureId) ||
                job.ExtractionId is not null && !HashValue(job.ExtractionId) ||
                job.AnalysisId is not null && !HashValue(job.AnalysisId) ||
                IsActive(job) && job.FinishedAt is not null || !IsActive(job) && job.FinishedAt is null ||
                !history.TryAdd(job.Id, job))
                throw new InvalidDataException("Invalid retained-source job record; automatic recovery is refused.");
            var inputPath = Ordinary(Path.Combine(jobsDirectory, job.Id, "request.json"));
            await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            var admission = await NativeRequestJson.ReadAsync<SourceSemanticJobAdmission>(input, 512 * 1024, token);
            input.Position = 0;
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != job.AdmissionSha256 ||
                CanonicalJson.Sha256(admission) != job.AdmissionSha256 || PayloadHash(admission) != job.RequestSha256 || admission.Schema != "filemapper.source-semantic-job-admission/1" ||
                admission.JobId != job.Id || admission.Kind != job.Kind || admission.OwnerId != job.OwnerId || admission.AcceptedAt != job.CreatedAt ||
                job.Kind == "source-semantics" && (admission.Request is null || admission.SourceSelection is null || admission.IndexedCapture is null || admission.IndexObservedAt is null || admission.ImportDirectory is not null ||
                    admission.IndexedCapture.CaptureId != job.CaptureId || admission.IndexedCapture.ExtractionHash != job.ExtractionId ||
                    admission.Request.CaptureId != job.CaptureId || admission.Request.ExtractionId != job.ExtractionId) ||
                job.Kind == "source-semantic-import" && (admission.Request is not null || admission.ImportDirectory is null ||
                    !HashValue(admission.ExpectedAnalysisId) || job.AnalysisId is not null && job.AnalysisId != admission.ExpectedAnalysisId))
                throw new InvalidDataException("Retained-source job admission receipt differs from its durable commitment.");
        }
        if (history.Values.Count(IsActive) > 1) throw new InvalidDataException("Multiple active retained-source job records.");
        revision = ledger.Revision;
    }

    private void Persist()
    {
        // Callers hold sync. Keep terminal job history bounded; immutable results and request receipts are not deleted.
        var ordered = history.Values.OrderByDescending(IsActive).ThenByDescending(j => j.CreatedAt).ThenBy(j => j.Id, StringComparer.Ordinal).ToArray();
        var evicted = ordered.Skip(MaximumHistory).ToArray();
        if (evicted.Any(IsActive)) throw new InvalidOperationException("An active retained-source job cannot be evicted.");
        var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new SourceSemanticJobLedger(LedgerSchema,
            checked(revision + 1), ordered.Take(MaximumHistory).ToArray())));
        if (bytes.Length > MaximumLedgerBytes) throw new IOException("Retained-source job history exceeds its explicit byte bound.");
        AtomicWrite(ledgerPath, bytes, replace: true);
        foreach (var old in evicted) history.Remove(old.Id);
        revision++;
    }

    private static void AtomicWrite(string path, byte[] bytes, bool replace)
    {
        Ordinary(path);
        var temporary = Ordinary(path + ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            Ordinary(path); Ordinary(temporary);
            File.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            if (File.Exists(temporary)) { Ordinary(temporary); File.Delete(temporary); }
        }
    }

    private void UpdateProgress(string id, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value));
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("Native semantic progress exceeds its bounded envelope.");
        using var document = JsonDocument.Parse(bytes);
        var progress = document.RootElement.Clone();
        var phase = progress.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "working";
        if (phase.Length > 128) throw new InvalidDataException("Native progress phase is too long.");
        lock (sync)
        {
            var job = history[id];
            history[id] = job with { Phase = phase, UpdatedAt = DateTimeOffset.UtcNow,
                ProgressSequence = checked(job.ProgressSequence + 1), Progress = progress };
            Persist();
        }
    }

    private static string Ordinary(string path) => SourceHarvestConnectionStore.NormalizePath(path);
    private static bool HashValue(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static void RequireJobId(string id)
    {
        if (id is null || !id.StartsWith("source-job-", StringComparison.Ordinal) || id.Length != 43 ||
            !id.AsSpan(11).ToString().All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("A native retained-source job ID is required.", nameof(id));
    }
    private static bool IsActive(SourceSemanticJob job) => job.Status is "running" or "cancelling";
    private static string Bounded(string text) => text.Length > 4096 ? text[..4096] : text;
    private static string ErrorText(Exception error) => Bounded(error.GetType().Name + ": " + error.Message);
    private static string PayloadHash(SourceSemanticJobAdmission value) => value.Request is not null ? CanonicalJson.Sha256(value.Request)
        : CanonicalJson.Sha256(new { Directory = value.ImportDirectory, AnalysisId = value.ExpectedAnalysisId });
    private sealed class InlineProgress(Action<object> callback) : IProgress<object>
    { public void Report(object value) => callback(value); }
}
