using System.Globalization;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Actions.Robocopy;

namespace FileMapper.Cli;

internal static class RobocopyCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Robocopy commands require Windows.");
        var command = args[0];
        var commandOptions = command switch
        {
            "catalog" => Array.Empty<string>(),
            "validate" or "plan" or "export-job" => ["--draft"],
            "get-plan" or "preview" => ["--plan"],
            "jobs" => ["--limit"],
            "job" or "events" or "cancel" => ["--job"],
            "output" => ["--job", "--channel", "--maximum-characters"],
            "import-job" => ["--path"],
            "approve" => ["--plan", "--preview", "--plan-hash", "--preview-hash", "--rollback-statement", "--valid-for-seconds", "--accept-overwrites", "--accept-irreversible-changes", "--confirm-trees-quiescent"],
            "execute" => ["--approval"],
            _ => throw new ArgumentException("Unknown Robocopy command. Use filemapper robocopy --help.")
        };
        var switches = new HashSet<string>(["--enable-execution", "--allow-destructive", "--accept-overwrites", "--accept-irreversible-changes", "--confirm-trees-quiescent"], StringComparer.Ordinal);
        var allowed = new HashSet<string>(commandOptions, StringComparer.Ordinal);
        allowed.UnionWith(["--database", "--state-directory", "--worker-executable", "--dotnet-host", "--runtime-config", "--enable-execution", "--allow-destructive"]);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!allowed.Contains(key) || options.ContainsKey(key)) throw new ArgumentException("Unknown or repeated option: " + key);
            if (switches.Contains(key)) options.Add(key, null);
            else if (++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) options.Add(key, args[i]);
            else throw new ArgumentException("Missing value for " + key);
        }
        string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException(key + " is required.");
        int Number(string key, int fallback, int min, int max)
        {
            if (!options.TryGetValue(key, out var value)) return fallback;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max ? result : throw new ArgumentException($"{key} requires {min} through {max}.");
        }
        string? AbsoluteOption(string key) => options.TryGetValue(key, out var value) ? Path.GetFullPath(value!) : null;
        var database = AbsoluteOption("--database") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "filemapper.db");
        var state = AbsoluteOption("--state-directory") ?? Path.Combine(Path.GetDirectoryName(database)!, "actions", "robocopy");
        var enabled = options.ContainsKey("--enable-execution");
        if (command is "approve" or "execute" && !enabled) throw new UnauthorizedAccessException("Local approval and execution require explicit --enable-execution. No HTTP role or request body can enable it.");
        if (options.ContainsKey("--allow-destructive") && !enabled) throw new ArgumentException("--allow-destructive requires --enable-execution; backend policy blocks still apply.");
        using var identity = WindowsIdentity.GetCurrent();
        EnsurePrivateDataDirectory(Path.GetDirectoryName(database)!, identity.User ?? throw new UnauthorizedAccessException("Local Windows SID unavailable."));
        var capabilities = new HashSet<string>(["read", "plan"], StringComparer.Ordinal);
        if (enabled) capabilities.Add("execute");
        if (enabled && options.ContainsKey("--allow-destructive")) capabilities.Add("approve-destructive");
        var caller = new ActionCaller($"{identity.User?.Value ?? throw new UnauthorizedAccessException("Local Windows identity unavailable.")} ({identity.Name})", capabilities);
        var backend = new RobocopyBackend(new(state)
        {
            MaintenanceLockPath = database + ".maintenance.lock",
            WorkerExecutablePath = AbsoluteOption("--worker-executable"),
            DotnetHostPath = AbsoluteOption("--dotnet-host"),
            RuntimeConfigPath = AbsoluteOption("--runtime-config"),
            EnableExecution = enabled
        });
        RobocopyDraft Draft() => JsonSerializer.Deserialize<RobocopyDraft>(ReadBounded(Required("--draft")), Json) ?? throw new JsonException("Draft must be a JSON object.");
        void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
        switch (command)
        {
            case "catalog": Print(backend.GetCatalog()); break;
            case "validate": var validation = backend.Validate(Draft()); Print(validation); return validation.Valid ? 0 : 2;
            case "plan": Print(backend.CreatePlan(Draft(), caller)); break;
            case "get-plan": Print(backend.GetPlan(Required("--plan"))); break;
            case "jobs": Print(backend.GetJobs(Number("--limit", 100, 1, 1000))); break;
            case "job": Print(backend.GetJob(Required("--job"))); break;
            case "events": Print(backend.GetEvents(Required("--job"))); break;
            case "output": Console.Write(backend.ReadOutput(Required("--job"), options.GetValueOrDefault("--channel") ?? "native", Number("--maximum-characters", 65536, 1, 1_000_000))); break;
            case "cancel": var requested = backend.Cancel(Required("--job"), caller); Print(new { requested }); return requested ? 0 : 2;
            case "import-job": Print(backend.ImportJob(ReadBounded(Required("--path")), caller)); break;
            case "export-job": Console.Write(backend.ExportJob(Draft(), caller)); break;
            case "approve":
                Print(backend.Approve(Required("--plan"), Required("--preview"), new(Required("--plan-hash"), Required("--preview-hash"), Required("--rollback-statement"), options.ContainsKey("--accept-overwrites"), options.ContainsKey("--accept-irreversible-changes"), Number("--valid-for-seconds", 300, 1, 900), options.ContainsKey("--confirm-trees-quiescent")), caller)); break;
            case "preview": case "execute":
                var job = command == "preview" ? backend.StartPreview(Required("--plan"), caller, cancellationToken) : backend.StartExecution(Required("--approval"), caller, cancellationToken);
                // Await the worker's terminal journal even after Ctrl+C; exiting early could orphan its cleanup/status work.
                Console.Error.WriteLine(JsonSerializer.Serialize(new { jobId = job.Id, kind = job.Kind, state = job.State }, Json));
                job = await backend.WaitAsync(job.Id, CancellationToken.None); Print(job);
                return job.State is "preview-ready" or "completed" or "completed-with-differences" ? 0 : 2;
        }
        return 0;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsurePrivateDataDirectory(string path, SecurityIdentifier user)
    {
        var info = new DirectoryInfo(PathSafety.CanonicalDirectory(path));
        for (var ancestor = info; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Action data ancestors must not be reparse points.");
        if (!info.Exists)
        {
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.Create(security);
        }
        foreach (FileSystemAccessRule rule in info.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && sid != user && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                throw new InvalidOperationException("Existing action data directory grants another identity access. Choose a new private database directory or use the per-user installer; existing ACLs are not rewritten.");
        }
    }
    private static string ReadBounded(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        if (stream.Length > 1024 * 1024) throw new ArgumentException("Input exceeds 1 MiB.");
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), true);
        var buffer = new char[1024 * 1024 + 1]; var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 1024 * 1024) throw new ArgumentException("Input exceeds 1 MiB.");
        var text = new string(buffer, 0, count);
        if (text.Contains('\0')) throw new ArgumentException("Invalid input: NUL is not allowed.");
        return text;
    }
    private const string Help = """
    filemapper robocopy catalog
    filemapper robocopy validate --draft <typed-draft.json>
    filemapper robocopy plan --draft <typed-draft.json>
    filemapper robocopy get-plan --plan <id>
    filemapper robocopy preview --plan <id>       (list-only; waits for terminal job)
    filemapper robocopy jobs [--limit 100]
    filemapper robocopy job|events|cancel --job <id>
    filemapper robocopy output --job <id> [--channel native|stdout|stderr] [--maximum-characters 65536]
    filemapper robocopy import-job --path <native-job.rcj>   (parse as data only)
    filemapper robocopy export-job --draft <typed-draft.json> (RCJ text to stdout only)

    Explicit local executor (never enabled by service roles; runs as this Windows identity):
    filemapper robocopy approve --enable-execution --plan <id> --preview <job-id>
      --plan-hash <reviewed-sha256> --preview-hash <reviewed-sha256>
      --rollback-statement <exact-reviewed-text> --accept-overwrites
      --accept-irreversible-changes --confirm-trees-quiescent [--valid-for-seconds 300]
    filemapper robocopy execute --enable-execution --approval <id>

    Global: --database <path> --state-directory <path>. State defaults to actions/robocopy
    beside the database. Published worker defaults to ../worker/FileMapper.Actions.Robocopy.exe.
    Development only: --dotnet-host <absolute-host> --runtime-config <worker-runtimeconfig>.
    An explicit --worker-executable selects a separately published worker.
    Ctrl+C requests cancellation; the command waits for containment cleanup and terminal state.
    --allow-destructive grants an additional local capability, never bypasses backend policy.
    All 97 catalog options are described, but unsupported execution semantics remain blocked.
    Native success is not content verification. No automatic approval, backup, or rollback.
    """;
}
