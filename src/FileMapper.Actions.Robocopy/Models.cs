using System.Text.Json;

namespace FileMapper.Actions.Robocopy;

// Capability sets must come from an authenticated host, never from an HTTP request body.
public sealed record ActionCaller(string Identity, IReadOnlySet<string> Capabilities)
{
    public void Demand(string capability)
    {
        if (string.IsNullOrWhiteSpace(Identity) || !Capabilities.Contains(capability))
            throw new UnauthorizedAccessException($"The explicit '{capability}' capability is required.");
    }
}
public sealed record OptionValue(string Name, bool? Enabled = null, long? Number = null, string? Text = null, string[]? Items = null);
public sealed record RobocopyDraft(string Source, string Destination)
{
    public string[] Files { get; init; } = ["*.*"];
    public OptionValue[] Options { get; init; } = [new("E", true), new("Z", true), new("COPY", Text: "DAT"), new("DCOPY", Text: "DAT"), new("XJ", true), new("R", Number: 2), new("W", Number: 2)];
    public int TimeoutSeconds { get; init; } = 300;
    public long MaximumOutputBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumTreeEntries { get; init; } = 100_000;
    public string RollbackStatement { get; init; } = "No backup is managed by FileMapper; overwritten content cannot be restored by this action.";
}
public sealed record ValidationIssue(string Code, string Message, string Severity = "error", string? Option = null);
public sealed record PlanValidation(bool Valid, RobocopyDraft? Normalized, IReadOnlyList<ValidationIssue> Issues, IReadOnlyList<string> ExecutionBlocks);
public sealed record BinaryIdentity(string Path, string Version, string Sha256, string Architecture);
public sealed record EndpointIdentity(string Path, string FinalPath, string VolumeId, string FileId, string FileSystem, uint FileSystemFlags);
public sealed record RobocopyPlan(string Id, int Revision, DateTimeOffset CreatedAt, string CreatedBy, RobocopyDraft Draft,
    BinaryIdentity Binary, EndpointIdentity SourceIdentity, EndpointIdentity DestinationIdentity, string CatalogHash, string PolicyHash,
    string ExecutionIdentity, string PlanHash, string[] ExecutionBlocks, string[] MutationScopes);
public sealed record CompiledArguments(string[] Arguments, string[] PresentationChanges, bool ListOnly);
public sealed record TreeFingerprint(string Sha256, int Entries, long FileBytes, DateTimeOffset ObservedAt, string Method);
public sealed record NativeExit(int RawCode, bool CopiedOrProposed, bool Extras, bool Mismatches, bool CopyFailures, bool SeriousError, int UnknownBits, bool NativeSuccess, string Meaning)
{
    public static NativeExit Decode(int code, bool preview) => new(code, (code & 1) != 0, (code & 2) != 0, (code & 4) != 0,
        (code & 8) != 0, (code & 16) != 0, code & ~31, code >= 0 && code < 8,
        preview ? "List-only native result: bits describe proposed work, never actual transfers." : "Native exit bits; content verification is a separate result.");
}
public sealed record OutputEvidence(string RelativePath, long Bytes, string Sha256, string Encoding);
public sealed record ActionEvent(long Sequence, DateTimeOffset ObservedAt, string Kind, string Message, string PreviousHash = "", string EventHash = "");
public sealed record VerificationResult(string Level, bool Complete, int CheckedFiles, long CheckedBytes, string[] Differences, string[] Limitations);
public sealed record ActionJob(string Id, string PlanId, string Kind, string State, DateTimeOffset StartedAt)
{
    public DateTimeOffset? FinishedAt { get; init; }
    public string? RequestedBy { get; init; }
    public string? Error { get; init; }
    public string? WorkerSha256 { get; init; }
    public NativeExit? NativeExit { get; init; }
    public CompiledArguments? Command { get; init; }
    public OutputEvidence[] Output { get; init; } = [];
    public TreeFingerprint? Before { get; init; }
    public TreeFingerprint? After { get; init; }
    public bool EnumerationComplete { get; init; }
    public string? PreviewHash { get; init; }
    public VerificationResult? Verification { get; init; }
    public bool PartialChangesPossible { get; init; }
    public string[] Limitations { get; init; } = [];
    public string[] ExecutionBlocks { get; init; } = [];
}
public sealed record ApprovalRequest(string ReviewedPlanHash, string ReviewedPreviewHash, string RollbackStatement, bool AcceptOverwrites, bool AcceptIrreversibleChanges, int ValidForSeconds = 300, bool ConfirmTreesQuiescent = false);
public sealed record ActionApproval(string Id, string PlanId, string PreviewJobId, string PlanHash, string PreviewHash, string ApprovedBy,
    string ExecutionIdentity, DateTimeOffset ExpiresAt, string RollbackStatement, string[] MutationScopes);
public sealed record JobImport(RobocopyDraft Draft, string SourceSha256, string[] Notes);
public sealed record RobocopyBackendOptions(string StateDirectory)
{
    public string? MaintenanceLockPath { get; init; }
    public string? WorkerExecutablePath { get; init; }
    public string? DotnetHostPath { get; init; }
    public string? RuntimeConfigPath { get; init; }
    public bool EnableExecution { get; init; } // Explicit administrator configuration; default is preview-only.
}
internal static class Data
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    internal static string Hash(string text) => Hash(System.Text.Encoding.UTF8.GetBytes(text));
    internal static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)); }
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
