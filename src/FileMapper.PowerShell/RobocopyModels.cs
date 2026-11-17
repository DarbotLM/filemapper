namespace FileMapper.PowerShell;

public sealed class FileMapperRobocopyOption
{
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    public long? Number { get; set; }
    public string? Text { get; set; }
    public string[]? Items { get; set; }
}
public sealed class FileMapperRobocopyDraft
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string[] Files { get; set; } = ["*.*"];
    public FileMapperRobocopyOption[] Options { get; set; } = [new() { Name = "E", Enabled = true }, new() { Name = "Z", Enabled = true }, new() { Name = "COPY", Text = "DAT" }, new() { Name = "DCOPY", Text = "DAT" }, new() { Name = "XJ", Enabled = true }, new() { Name = "R", Number = 2 }, new() { Name = "W", Number = 2 }];
    public int TimeoutSeconds { get; set; } = 300;
    public long MaximumOutputBytes { get; set; } = 32 * 1024 * 1024;
    public int MaximumTreeEntries { get; set; } = 100_000;
    public string RollbackStatement { get; set; } = "No backup is managed by FileMapper; overwritten content cannot be restored by this action.";
}
public sealed record FileMapperRobocopyIssue(string Code, string Message, string Severity, string? Option);
public sealed record FileMapperRobocopyValidation(bool Valid, FileMapperRobocopyDraft? Normalized, FileMapperRobocopyIssue[] Issues, string[] ExecutionBlocks);
public sealed record FileMapperRobocopyBinary(string Path, string Version, string Sha256, string Architecture);
public sealed record FileMapperRobocopyEndpoint(string Path, string FinalPath, string VolumeId, string FileId, string FileSystem, uint FileSystemFlags);
public sealed record FileMapperRobocopyPlan(string Id, int Revision, DateTimeOffset CreatedAt, string CreatedBy, FileMapperRobocopyDraft Draft,
    FileMapperRobocopyBinary Binary, FileMapperRobocopyEndpoint SourceIdentity, FileMapperRobocopyEndpoint DestinationIdentity,
    string CatalogHash, string PolicyHash, string ExecutionIdentity, string PlanHash, string[] ExecutionBlocks, string[] MutationScopes);
public sealed record FileMapperRobocopyArguments(string[] Arguments, string[] PresentationChanges, bool ListOnly);
public sealed record FileMapperRobocopyFingerprint(string Sha256, int Entries, long FileBytes, DateTimeOffset ObservedAt, string Method);
public sealed record FileMapperRobocopyExit(int RawCode, bool CopiedOrProposed, bool Extras, bool Mismatches, bool CopyFailures, bool SeriousError, int UnknownBits, bool NativeSuccess, string Meaning);
public sealed record FileMapperRobocopyOutput(string RelativePath, long Bytes, string Sha256, string Encoding);
public sealed record FileMapperRobocopyEvent(long Sequence, DateTimeOffset ObservedAt, string Kind, string Message)
{
    public string? PreviousHash { get; init; }
    public string? EventHash { get; init; }
}
public sealed record FileMapperRobocopyVerification(string Level, bool Complete, int CheckedFiles, long CheckedBytes, string[] Differences, string[] Limitations);
public sealed record FileMapperRobocopyJob(string Id, string PlanId, string Kind, string State, DateTimeOffset StartedAt)
{
    public DateTimeOffset? FinishedAt { get; init; }
    public string? RequestedBy { get; init; }
    public string? Error { get; init; }
    public string? WorkerSha256 { get; init; }
    public FileMapperRobocopyExit? NativeExit { get; init; }
    public FileMapperRobocopyArguments? Command { get; init; }
    public FileMapperRobocopyOutput[] Output { get; init; } = [];
    public FileMapperRobocopyFingerprint? Before { get; init; }
    public FileMapperRobocopyFingerprint? After { get; init; }
    public bool EnumerationComplete { get; init; }
    public string? PreviewHash { get; init; }
    public FileMapperRobocopyVerification? Verification { get; init; }
    public bool PartialChangesPossible { get; init; }
    public string[] Limitations { get; init; } = [];
    public string[] ExecutionBlocks { get; init; } = [];
}
public sealed record FileMapperRobocopyImport(FileMapperRobocopyDraft Draft, string SourceSha256, string[] Notes);
public sealed record FileMapperRobocopyCancellation(string JobId, bool Requested);
public sealed class FileMapperRobocopyCatalog
{
    public int SchemaVersion { get; init; }
    public string CatalogId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? BackendStatus { get; init; }
    public string? BackendPolicyVersion { get; init; }
    public string? BackendCatalogSha256 { get; init; }
    public bool? ExecutionEnabledByHost { get; init; }
    public string InstalledHelpSha256 { get; init; } = "";
    public Dictionary<string, object?> InstalledBinary { get; init; } = [];
    public Dictionary<string, object?> Coverage { get; init; } = [];
    public Dictionary<string, object?>[] Sources { get; init; } = [];
    public object? Positionals { get; init; }
    public object? NativeDefaults { get; init; }
    public object? WrapperDefaultProposals { get; init; }
    public string[] TileCategories { get; init; } = [];
    public FileMapperRobocopyCatalogOption[] Options { get; init; } = [];
    public object? Rules { get; init; }
    public object? ExitCodeContract { get; init; }
    public object? KnownDocumentationDifferences { get; init; }
    public object? Validation { get; init; }
    public object? ProcessInvocation { get; init; }
    public object? ApprovalContract { get; init; }
    public object? CredentialRoles { get; init; }
    public object? ProgressPolicy { get; init; }
}
public sealed record FileMapperRobocopyCatalogOption(string Id, string Switch, Dictionary<string, object?>[] InstalledHelp, string TileCategory,
    string Semantics, Dictionary<string, object?> Parameter, string[] Effects, bool DestructiveApprovalRequired,
    string PreviewHandling, string[] RuleIds, string ImplementationStatus)
{
    public string? BackendHandling { get; init; }
}
