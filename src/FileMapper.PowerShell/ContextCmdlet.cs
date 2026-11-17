using System.Management.Automation;
using System.Text;
using System.Text.Json.Serialization;

namespace FileMapper.PowerShell;

public sealed record FileMapperContextNode(string Id, string Kind, string Name, string? Path);
public sealed record FileMapperContextEvidence(string Id, string SubjectId, string Predicate, string SourceKind,
    string SourceUri, string ObservedAt, double Confidence, string Completeness, string CollectorVersion,
    string? EvidenceHash, string ValuePreview, bool ValueTruncated);
public sealed record FileMapperContextConnection(string Id, string FromId, string ToId, string Kind, double Confidence,
    FileMapperContextEvidence Evidence);
public sealed record FileMapperContextStep(FileMapperContextNode Node, FileMapperContextConnection? Via);
public sealed record FileMapperContextItem(FileMapperContextNode Node, int Score, Dictionary<string, int> Components,
    string[] Reasons, FileMapperContextStep[] ConnectionPath, FileMapperContextEvidence[] Observations);
public sealed record FileMapperContext(string SnapshotId, string Completeness, string ContentTrust, string ModelVersion,
    int ByteBudget, bool Truncated, string[] TruncationReasons, FileMapperContextItem[] Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? QueryTerms = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CandidatesConsidered = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Scope = null)
{
    public string ToJson() => FileMapperSemanticJson.Serialize(this);
}

[Cmdlet(VerbsCommon.Get, "FileMapperContext")]
[OutputType(typeof(FileMapperContext))]
public sealed class GetFileMapperContextCommand : SemanticSnapshotCmdlet
{
    [Parameter(Mandatory = true, Position = 1)] [AllowEmptyString] public string Search { get; set; } = "";
    [Parameter] [ValidateRange(256, 100000)] public int ByteBudget { get; set; } = 16000;
    protected override void ProcessRecord()
    {
        if (Search.EnumerateRunes().Count() > 512 || Search.Any(char.IsControl))
            throw new ArgumentException("Context search must be at most 512 Unicode code points with no control characters.", nameof(Search));
        WriteObject(Read<FileMapperContext>(SnapshotRoute + "/context?search=" + Escape(Search) + "&budget=" + ByteBudget));
    }
}
