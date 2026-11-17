using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FileMapper.Actions.Robocopy;

public static class RobocopyCatalog
{
    private static readonly byte[] Bytes = Read();
    private static byte[] Read() { using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileMapper.Actions.Robocopy.option-catalog.json")!; using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
    public static string Sha256 { get; } = Data.Hash(Bytes);
    internal static readonly JsonDocument Document = JsonDocument.Parse(Bytes);
    internal static readonly Dictionary<string, JsonElement> Options = Document.RootElement.GetProperty("options").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
    public static JsonElement GetCatalog()
    {
        var root = JsonNode.Parse(Bytes)!;
        root["backendPolicyVersion"] = RobocopyCompiler.PolicyVersion;
        root["backendCatalogSha256"] = Sha256;
        root["backendStatus"] = "Typed compiler and contained list-only preview; execution is administrator opt-in and restricted by per-plan policy.";
        foreach (var option in root["options"]!.AsArray())
        {
            var id = option!["id"]!.GetValue<string>();
            option["backendHandling"] = RobocopyCompiler.TemplateOnly.Contains(id) ? "Separate template/settings workflow; never passed to copy worker."
                : RobocopyCompiler.ManagedLog.Contains(id) ? "Managed raw capture replaces client-selected native log paths."
                : "Typed validation; single-pass preview canonicalization; execution eligibility checked per plan.";
        }
        return JsonSerializer.SerializeToElement(root, Data.Json);
    }
}

public static class RobocopyCompiler
{
    public const string PolicyVersion = "robocopy-native-policy-v1";
    public static string PolicyHash { get; } = Data.Hash(PolicyVersion + "|existing-local-endpoints|exclude-reparse|single-pass|reviewed-one-use|ordinary-copy-verify-v1|" + Data.HashFile(typeof(RobocopyCompiler).Assembly.Location));
    internal static readonly HashSet<string> TemplateOnly = new(["JOB", "SAVE", "QUIT", "NOSD", "NODD", "REG", "?"], StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> ManagedLog = new(["LOG", "LOG+", "UNILOG", "UNILOG+"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PreviewOmissions = new(["MON", "MOT", "RH", "PF", "NS", "NC", "NFL", "NDL", "NJH", "NJS", "L", "UNICODE", "TEE"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExecutionAllowed = new(["E", "S", "Z", "COPY", "DCOPY", "XJ", "R", "W", "NP", "FP", "TS", "BYTES", "V", "UNICODE", "TEE", "J", "NOOFFLOAD", "NOCLONE"], StringComparer.OrdinalIgnoreCase);
    public static PlanValidation Validate(RobocopyDraft draft)
    {
        var issues = new List<ValidationIssue>(); var blocks = new List<string>(); var normalized = new List<OptionValue>();
        if (draft is null) return new(false, null, [new("draft", "A typed draft is required.")], []);
        string source = draft.Source ?? "", destination = draft.Destination ?? "";
        try { source = PathSafety.CanonicalDirectory(source); destination = PathSafety.CanonicalDirectory(destination); if (PathSafety.Overlaps(source, destination)) issues.Add(new("overlap", "Source and destination must be separate, non-overlapping trees.")); }
        catch (ArgumentException e) { issues.Add(new("path", e.Message)); }
        if (draft.Files is null || draft.Files.Length is < 1 or > 1024) issues.Add(new("patterns", "One to 1024 file patterns are required."));
        else foreach (var value in draft.Files) if (!SafeItem(value, true)) issues.Add(new("pattern", "File patterns must be individual names without separators, quotes, control characters, colon, or leading slash/hyphen."));
        if (draft.TimeoutSeconds is < 1 or > 3600) issues.Add(new("timeout", "Timeout must be between 1 and 3600 seconds."));
        if (draft.MaximumOutputBytes is < 4096 or > 256 * 1024 * 1024) issues.Add(new("output-budget", "Output budget must be 4 KiB through 256 MiB."));
        if (draft.MaximumTreeEntries is < 1 or > 1_000_000) issues.Add(new("tree-budget", "Tree budget must be 1 through 1,000,000 entries."));
        if (string.IsNullOrWhiteSpace(draft.RollbackStatement) || draft.RollbackStatement.Length > 4096) issues.Add(new("rollback", "A concrete rollback or irreversibility statement is required (maximum 4096 characters)."));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (draft.Options is null || draft.Options.Length > 128) issues.Add(new("options", "At most 128 typed options are accepted."));
        foreach (var input in draft.Options ?? [])
        {
            if (input is null) { issues.Add(new("null-option", "Null option entries are not accepted.")); continue; }
            var name = input.Name?.ToUpperInvariant() ?? "";
            if (!RobocopyCatalog.Options.TryGetValue(name, out var entry)) { issues.Add(new("unknown-option", $"Unknown option '{name}'.", Option: name)); continue; }
            if (!seen.Add(name)) { issues.Add(new("duplicate-option", $"Option '{name}' was declared more than once.", Option: name)); continue; }
            var p = entry.GetProperty("parameter"); var type = p.GetProperty("type").GetString();
            var count = (input.Enabled.HasValue ? 1 : 0) + (input.Number.HasValue ? 1 : 0) + (input.Text is not null ? 1 : 0) + (input.Items is not null ? 1 : 0);
            if (count > 1) { issues.Add(new("typed-value", "Only one typed value may be specified.", Option: name)); continue; }
            bool valid; var value = input with { Name = name };
            switch (type)
            {
                case "booleanSwitch": valid = count == 1 && input.Enabled.HasValue; break;
                case "flagSet":
                    var alphabet = p.GetProperty("allowedCharacters").GetString()!;
                    var flags = input.Text?.ToUpperInvariant(); valid = count == 1 && !string.IsNullOrEmpty(flags) && flags.All(alphabet.Contains) && flags.Distinct().Count() == flags.Length;
                    value = value with { Text = flags }; break;
                case "enum":
                    var choice = input.Text?.ToUpperInvariant(); valid = count == 0 && Optional(p) || count == 1 && choice is not null && p.GetProperty("values").EnumerateArray().Any(x => x.GetString() == choice);
                    value = value with { Text = choice }; break;
                case "argumentList": valid = count == 1 && input.Items is { Length: > 0 and <= 1024 } && input.Items.All(x => SafeItem(x, false)); break;
                case "ageOrDate":
                    valid = count == 1 && (input.Number is >= 0 and < 1900 || input.Text is { Length: 8 } && DateOnly.TryParseExact(input.Text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)); break;
                case "localTimeWindow":
                    valid = count == 1 && input.Text is not null && Regex.IsMatch(input.Text, "^[0-9]{4}-[0-9]{4}$") && TimeOnly.TryParseExact(input.Text[..4], "HHmm", out _) && TimeOnly.TryParseExact(input.Text[5..], "HHmm", out _); break;
                case "managedOutputPath": case "importedJobReference":
                    valid = count == 1 && !string.IsNullOrWhiteSpace(input.Text) && input.Text.Length <= 32767 && !input.Text.Any(char.IsControl);
                    issues.Add(new("separate-artifact-workflow", $"/{name} cannot be supplied to a copy plan. Import/export native jobs separately; logs are managed private artifacts.", Option: name)); break;
                default:
                    var min = p.TryGetProperty("minimum", out var minNode) ? minNode.GetInt64() : 0;
                    var max = p.TryGetProperty("maximum", out var maxNode) ? maxNode.GetInt64() : type is "byteQuantity" or "integerBytes" ? long.MaxValue : int.MaxValue;
                    valid = count == 0 && Optional(p) || count == 1 && input.Number.HasValue && input.Number.Value >= min && input.Number.Value <= max;
                    if (name == "R" && input.Number > 10 || name == "W" && input.Number > 30) issues.Add(new("bounded-retry", "Wrapper policy limits retry count to 10 and wait to 30 seconds.", Option: name));
                    break;
            }
            if (!valid) issues.Add(new("typed-value", $"Invalid typed value for /{name} ({type}).", Option: name));
            if (TemplateOnly.Contains(name)) issues.Add(new("separate-workflow", $"/{name} belongs to the separate job/settings/help workflow and is not a copy-plan option.", Option: name));
            if (valid && input.Enabled != false) normalized.Add(value);
        }
        var options = normalized.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        bool Has(string n) => options.ContainsKey(n);
        void Conflict(string a, string b) { if (Has(a) && Has(b)) issues.Add(new("incompatible", $"/{a} cannot be combined with /{b}.")); }
        foreach (var pair in new[] { ("MT", "IPG"), ("MT", "EFSRAW"), ("LFSM", "MT"), ("LFSM", "EFSRAW"), ("Z", "B"), ("Z", "ZB"), ("B", "ZB"), ("S", "E"), ("MOV", "MOVE"), ("DCOPY", "NODCOPY"), ("COPY", "NOCOPY"), ("SEC", "NOCOPY"), ("COPYALL", "NOCOPY"), ("COPY", "SEC"), ("COPY", "COPYALL"), ("SEC", "COPYALL") }) Conflict(pair.Item1, pair.Item2);
        var copy = Has("COPYALL") ? "DATSOU" : Has("SEC") ? "DATS" : options.GetValueOrDefault("COPY")?.Text ?? "DAT";
        if ((Has("B") || Has("ZB")) && copy.Contains('X')) issues.Add(new("ignored-stream-exclusion", "/COPY:X is ignored by backup modes and cannot be promised."));
        if (Has("SECFIX") && !copy.Any("SOU".Contains)) issues.Add(new("security-components", "/SECFIX requires explicit security components (S, O, or U)."));
        if (options.GetValueOrDefault("MIN")?.Number is long minSize && options.GetValueOrDefault("MAX")?.Number is long maxSize && minSize > maxSize) issues.Add(new("size-range", "/MIN exceeds /MAX."));
        foreach (var option in normalized) if (!ExecutionAllowed.Contains(option.Name)) blocks.Add($"/{option.Name}: cataloged and previewable, but execution is not enabled by this verified ordinary-copy policy.");
        if (copy != "DAT") blocks.Add("Execution currently requires /COPY:DAT; security, ADS exclusion and metadata-only semantics need separate verification.");
        if ((options.GetValueOrDefault("DCOPY")?.Text ?? "DA") != "DAT") blocks.Add("Execution currently requires explicit /DCOPY:DAT.");
        if (!Has("XJ")) blocks.Add("Execution requires /XJ to exclude reparse targets.");
        if (draft.Files is not ["*.*"]) blocks.Add("Execution currently requires the complete ordinary-file pattern *.* for independent verification.");
        if (!Has("E")) blocks.Add("Execution currently requires /E for complete ordinary-tree verification.");
        if (!Has("R") || !Has("W")) issues.Add(new("explicit-retry", "Explicit bounded /R and /W values are required; native million-retry defaults are not accepted."));
        if (source.StartsWith("\\\\", StringComparison.Ordinal) || destination.StartsWith("\\\\", StringComparison.Ordinal)) blocks.Add("UNC execution requires an alias/isolation policy not provided by this version; preview only.");
        if (PathSafety.IsRoot(source) || PathSafety.IsRoot(destination)) blocks.Add("Volume/share-root execution is not enabled; native root attribute changes require separate approval policy.");
        var effective = draft with { Source = source, Destination = destination, Options = normalized.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray() };
        return new(!issues.Any(x => x.Severity == "error"), effective, issues, blocks.Distinct().ToArray());
    }
    private static bool Optional(JsonElement p) => p.TryGetProperty("optional", out var value) && value.GetBoolean();
    internal static bool SafeItem(string? value, bool filePattern)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.Any(char.IsControl) || value.Contains('"') || value[0] is '/' or '-') return false;
        if (filePattern) return !value.Contains(':') && !value.Contains('\\') && !value.Contains('/');
        if (value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
        var driveAbsolute = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
        return !value[(driveAbsolute ? 2 : 0)..].Contains(':');
    }
    public static CompiledArguments Compile(RobocopyPlan plan, bool preview)
    {
        var validation = Validate(plan.Draft); if (!validation.Valid) throw new ArgumentException(string.Join(" ", validation.Issues.Select(x => x.Message)));
        var args = new List<string> { plan.Draft.Source, plan.Draft.Destination }; args.AddRange(plan.Draft.Files);
        var changes = new List<string>();
        foreach (var o in validation.Normalized!.Options)
        {
            if (TemplateOnly.Contains(o.Name) || ManagedLog.Contains(o.Name)) throw new InvalidOperationException("Artifact/settings options never reach the native worker.");
            if (o.Name is "UNICODE" or "TEE" || preview && PreviewOmissions.Contains(o.Name)) { changes.Add($"/{o.Name} omitted or replaced by managed single-pass preview/encoding policy."); continue; }
            args.Add("/" + o.Name + (o.Number.HasValue ? ":" + o.Number.Value.ToString(CultureInfo.InvariantCulture) : o.Text is not null ? ":" + o.Text : ""));
            if (o.Items is not null) args.AddRange(o.Items);
        }
        if (preview) { args.Add("/L"); foreach (var n in new[] { "FP", "BYTES", "TS", "V" }) if (!args.Contains("/" + n)) args.Add("/" + n); changes.Add("Forced /L /FP /BYTES /TS /V and removed suppression/scheduling for a bounded, observable preview."); }
        args.Add("/UNICODE"); changes.Add("Raw stdout/stderr are captured privately; /UNICODE is requested but the observed redirected stream mixes encodings. The runner adds managed /UNILOG for authoritative Unicode display. No user-selected log path is passed.");
        if (args.Sum(x => x.Length + 3) > 30000) throw new ArgumentException("Argument vector exceeds the bounded Windows command-line budget.");
        return new(args.ToArray(), changes.ToArray(), preview);
    }
}
