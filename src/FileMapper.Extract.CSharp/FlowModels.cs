using Microsoft.CodeAnalysis;

namespace FileMapper.Extract.CSharp;

/// <summary>Models are trusted host configuration, never discovered or executed from the analyzed repository.</summary>
public sealed record CSharpMemberPattern(string Type, string Member, int? ParameterCount = null)
{
    public bool Matches(ISymbol symbol)
    {
        if (symbol.Name != Member && !(Member == ".ctor" && symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })) return false;
        if (ParameterCount is { } count && symbol is IMethodSymbol method && method.Parameters.Length != count) return false;
        for (var type = symbol.ContainingType; type is not null; type = type.BaseType)
            if (type.OriginalDefinition.ToDisplayString() == Type || type.ToDisplayString() == Type ||
                type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.None)) == Type) return true;
        return false;
    }
}
public sealed record CSharpSourceModel(CSharpMemberPattern Member, string Kind = "untrusted");
public sealed record CSharpSinkModel(CSharpMemberPattern Member, string Rule, int[] Arguments, bool IncludeReceiver = false);
public sealed record CSharpSanitizerModel(CSharpMemberPattern Member, string[] Rules, int Argument = 0);
public sealed record CSharpBarrierModel(CSharpMemberPattern Member, string[] Rules, int? Argument = null, bool Authorizes = false);
public sealed record CSharpPropagationModel(CSharpMemberPattern Member, int[] Arguments, bool IncludeReceiver = false);
public sealed record CSharpRule(string Id, string Family, string Title, string Cwe, string Severity, string SourceKind, string FalsePositiveNotes);
public sealed record CSharpAnalysisOptions(int MaxMethods = 2000, int MaxBlocksPerMethod = 2048,
    int MaxBlockVisits = 10000, int MaxOperations = 200000, int MaxRouteLength = 64,
    int MaxOriginsPerValue = 64, int MaxDefinitionsPerValue = 128, int MaxCallDepth = 2,
    bool PublicParametersAreUntrusted = true, int MaxSyntaxEntities = 100000);

public sealed class CSharpFlowModels
{
    public string Version { get; init; } = "csharp-flow-v1";
    public List<CSharpRule> Rules { get; } =
    [
        new("FM-CS-COMMAND", "command", "Untrusted input reaches process execution", "CWE-78", "error", "untrusted", "A fixed executable with separately validated arguments may be intentional; no shell or deployment configuration is inferred."),
        new("FM-CS-SQL", "sql", "Untrusted input reaches SQL text", "CWE-89", "error", "untrusted", "Parameterized values are safe only when kept separate from SQL text. Provider-specific APIs require models."),
        new("FM-CS-PATH", "path", "Untrusted input reaches a filesystem path", "CWE-22", "warning", "untrusted", "A path source is a trust assumption. Access controls, canonical-root checks and filesystem races are not inferred."),
        new("FM-CS-DESERIALIZE", "deserialization", "Untrusted input reaches unsafe deserialization", "CWE-502", "error", "untrusted", "Only explicitly modeled unsafe deserializers are sinks; ordinary JSON parsing is not treated as unsafe."),
        new("FM-CS-SSRF", "ssrf", "Untrusted input reaches a network destination", "CWE-918", "warning", "untrusted", "Network policy and DNS rebinding protection are outside the local analysis."),
        new("FM-CS-SECRET", "secrets", "Secret material reaches an output sink", "CWE-532", "warning", "secret", "Environment variables are secret only when selected by the configured secret-name policy; no secret text is retained."),
        new("FM-CS-CRYPTO", "cryptography", "Legacy cryptographic digest", "CWE-328", "warning", "policy", "MD5/SHA1 may be intentional non-security checksums. This is an intrinsic policy pattern, not proven exploitable taint."),
        new("FM-CS-AUTH", "authorization", "Privileged operation lacks a modeled authorization barrier", "CWE-862", "error", "untrusted", "Privileged operations and throwing authorization checks must be explicitly modeled by the host; authentication alone is not authorization.")
    ];
    public List<CSharpSourceModel> Sources { get; } = [];
    public List<CSharpSinkModel> Sinks { get; } = [];
    public List<CSharpSanitizerModel> Sanitizers { get; } = [];
    public List<CSharpBarrierModel> Barriers { get; } = [];
    public List<CSharpPropagationModel> Propagators { get; } = [];
    public Func<string, bool> IsSecretEnvironmentName { get; init; } = name =>
        new[] { "SECRET", "PASSWORD", "TOKEN", "API_KEY", "PRIVATE_KEY", "CONNECTION_STRING" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

    public static CSharpFlowModels Default()
    {
        var models = new CSharpFlowModels();
        models.Sources.Add(new(new("System.Console", "ReadLine")));
        models.Sources.Add(new(new("System.Environment", "GetEnvironmentVariable")));
        void Sink(string type, string member, string rule, params int[] args) => models.Sinks.Add(new(new(type, member), rule, args));
        Sink("System.Diagnostics.Process", "Start", "FM-CS-COMMAND", 0, 1);
        Sink("System.Data.Common.DbCommand", "CommandText", "FM-CS-SQL", 0);
        foreach (var type in new[] { "Microsoft.Data.SqlClient.SqlCommand", "System.Data.SqlClient.SqlCommand" }) Sink(type, ".ctor", "FM-CS-SQL", 0);
        foreach (var member in new[] { "ReadAllText", "ReadAllBytes", "ReadLines", "WriteAllText", "WriteAllBytes", "Open", "OpenRead", "OpenWrite", "Delete", "Create" }) Sink("System.IO.File", member, "FM-CS-PATH", 0);
        Sink("System.IO.FileStream", ".ctor", "FM-CS-PATH", 0);
        Sink("System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", "Deserialize", "FM-CS-DESERIALIZE", 0);
        Sink("System.Runtime.Serialization.NetDataContractSerializer", "Deserialize", "FM-CS-DESERIALIZE", 0);
        foreach (var member in new[] { "GetAsync", "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync", "PostAsync", "PutAsync", "DeleteAsync", "SendAsync" }) Sink("System.Net.Http.HttpClient", member, "FM-CS-SSRF", 0);
        foreach (var member in new[] { "Write", "WriteLine" }) { Sink("System.Console", member, "FM-CS-SECRET", 0, 1); Sink("System.Diagnostics.Debug", member, "FM-CS-SECRET", 0, 1); }
        foreach (var member in new[] { "Concat", "Format", "Join" }) models.Propagators.Add(new(new("string", member), [-1], true));
        foreach (var member in new[] { "Trim", "Substring", "Replace", "ToLower", "ToUpper", "ToString" }) models.Propagators.Add(new(new("string", member), [-1], true));
        models.Propagators.Add(new(new("System.IO.Path", "Combine"), [-1]));
        models.Propagators.Add(new(new("System.IO.Path", "GetFullPath"), [0])); // Canonicalization alone is not a sanitizer.
        models.Propagators.Add(new(new("System.Text.Encoding", "GetBytes"), [0]));
        models.Propagators.Add(new(new("System.Text.Encoding", "GetString"), [0]));
        return models;
    }
}
