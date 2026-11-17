using System.Management.Automation;
using System.Text;
using System.Text.Json;

namespace FileMapper.PowerShell;

public abstract class LearningCmdlet : SemanticCmdlet
{
    protected JsonElement Request(string json, string[] allowed, string[] required) => Protect(() =>
    {
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new ArgumentException("Learning/arena request exceeds the service's 64 KiB encoded body limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("RequestJson must be a JSON object.");
        CheckDuplicateProperties(root);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("RequestJson contains an unknown or incorrectly cased top-level property.");
        foreach (var name in required)
            if (!root.TryGetProperty(name, out _)) throw new ArgumentException($"RequestJson is missing '{name}'.");
        // Sending JsonElement preserves original nested numeric lexemes, including exponent notation.
        var result = root.Clone();
        if (JsonSerializer.SerializeToUtf8Bytes(result, WireJson).Length > 64 * 1024)
            throw new ArgumentException("Learning/arena request exceeds the service's 64 KiB encoded body limit.");
        return result;
    }, "RequestJson", "FileMapper.Learning.RequestJson");

    private void CheckDuplicateProperties(JsonElement value)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("RequestJson contains a duplicate property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }
    protected void EmitPage<T>(FileMapperLearningPage<T> page, bool asPage, int offset, int limit)
    {
        Protect(() =>
        {
            if (page.Offset != offset || page.Limit != limit || page.Total < 0 || page.Items.Length > limit)
                throw new InvalidDataException("Response paging metadata does not match the request.");
            return true;
        }, "page");
        WriteVerbose($"{page.Items.Length} of {page.Total} items at offset {page.Offset}. Separate pages are separate reads, not an atomic export.");
        if (asPage) WriteObject(page); else WriteObject(page.Items, true);
    }
}
public abstract class LearningIdCmdlet : LearningCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrEmpty] public string LearnerId { get; set; } = "";
    protected string LearnerRoute => "/api/learning/" + Escape(LearnerId);
}

[Cmdlet(VerbsCommon.Get, "FileMapperLearner", DefaultParameterSetName = "List")]
[OutputType(typeof(FileMapperLearnerHead), typeof(FileMapperLearningPage<FileMapperLearnerHead>), typeof(FileMapperLearningModel))]
public sealed class GetFileMapperLearnerCommand : LearningCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true, ParameterSetName = "Model")]
    [ValidateNotNullOrEmpty] public string LearnerId { get; set; } = "";
    [Parameter(ParameterSetName = "Model")] [ValidatePattern("^[0-9a-f]{64}$")] public string? CheckpointHash { get; set; }
    [Parameter(ParameterSetName = "List")] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter(ParameterSetName = "List")] [ValidateRange(1, 500)] public int Limit { get; set; } = 50;
    [Parameter(ParameterSetName = "List")] public SwitchParameter AsPage { get; set; }
    protected override void ProcessRecord()
    {
        if (ParameterSetName == "Model")
            WriteObject(Read<FileMapperLearningModel>("/api/learning/" + Escape(LearnerId) + (CheckpointHash is null ? "" : "?checkpoint=" + CheckpointHash)));
        else EmitPage(Read<FileMapperLearningPage<FileMapperLearnerHead>>($"/api/learning?offset={Offset}&limit={Limit}"), AsPage, Offset, Limit);
    }
}
[Cmdlet(VerbsCommon.New, "FileMapperLearner", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperLearnerHead))]
public sealed class NewFileMapperLearnerCommand : LearningCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Server.ToString(), "Create a persisted learner and initial checkpoint")) return;
        WriteObject(Read<FileMapperLearnerHead>("/api/learning", HttpMethod.Post,
            Request(RequestJson, ["learnerId", "policy", "epochId", "config"], ["learnerId", "policy", "epochId"])));
    }
}
[Cmdlet(VerbsCommon.Get, "FileMapperLearningVocabulary")]
[OutputType(typeof(FileMapperLearningVocabulary), typeof(FileMapperLearningPage<FileMapperLearningVocabulary>))]
public sealed class GetFileMapperLearningVocabularyCommand : LearningIdCmdlet
{
    [Parameter] [ValidateLength(0, 512)] public string? Search { get; set; }
    [Parameter] [ValidateLength(0, 128)] public string? Category { get; set; }
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 500)] public int Limit { get; set; } = 50;
    [Parameter] public SwitchParameter AsPage { get; set; }
    protected override void ProcessRecord()
    {
        var route = LearnerRoute + $"/vocabulary?offset={Offset}&limit={Limit}";
        if (Search is not null) route += "&search=" + Escape(Search);
        if (Category is not null) route += "&category=" + Escape(Category);
        EmitPage(Read<FileMapperLearningPage<FileMapperLearningVocabulary>>(route), AsPage, Offset, Limit);
    }
}
[Cmdlet(VerbsCommon.Get, "FileMapperLearningSubject")]
[OutputType(typeof(FileMapperLearningSubject), typeof(FileMapperLearningPage<FileMapperLearningSubject>))]
public sealed class GetFileMapperLearningSubjectCommand : LearningCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Id")] [ValidateNotNullOrEmpty] public string SnapshotId { get; set; } = "";
    [Parameter] [ValidateRange(0, int.MaxValue)] public int Offset { get; set; }
    [Parameter] [ValidateRange(1, 500)] public int Limit { get; set; } = 50;
    [Parameter] public SwitchParameter AsPage { get; set; }
    protected override void ProcessRecord() => EmitPage(Read<FileMapperLearningPage<FileMapperLearningSubject>>(
        $"/api/snapshots/{Escape(SnapshotId)}/learning-subjects?offset={Offset}&limit={Limit}"), AsPage, Offset, Limit);
}
[Cmdlet(VerbsCommon.Get, "FileMapperLearningPartition")]
[OutputType(typeof(FileMapperLearningPartition))]
public sealed class GetFileMapperLearningPartitionCommand : LearningIdCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string GroupKey { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperLearningPartition>(LearnerRoute + "/partition?groupKey=" + Escape(GroupKey)));
}
[Cmdlet(VerbsLifecycle.Invoke, "FileMapperLearningObservation", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperLearningObservation))]
public sealed class InvokeFileMapperLearningObservationCommand : LearningIdCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord()
    {
        if (!ShouldProcess(LearnerId, "Append one native source observation page")) return;
        WriteObject(Read<FileMapperLearningObservation>(LearnerRoute + "/observe", HttpMethod.Post,
            Request(RequestJson, ["snapshotId", "expectedCheckpointHash", "groupKey", "context", "capturedAt", "batchId", "offset", "limit"],
                ["snapshotId", "expectedCheckpointHash", "groupKey", "context", "capturedAt", "batchId"])));
    }
}
[Cmdlet(VerbsLifecycle.Invoke, "FileMapperLearningEvent", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperLearningAppend))]
public sealed class InvokeFileMapperLearningEventCommand : LearningIdCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord()
    {
        if (!ShouldProcess(LearnerId, "Append an explicit observation or labeled training event")) return;
        WriteObject(Read<FileMapperLearningAppend>(LearnerRoute + "/events", HttpMethod.Post,
            Request(RequestJson, ["eventId", "expectedCheckpointHash", "groupKey", "left", "right", "context", "label", "capturedAt", "epoch"],
                ["eventId", "expectedCheckpointHash", "groupKey", "left", "right", "context", "label", "capturedAt"])));
    }
}
[Cmdlet(VerbsLifecycle.Invoke, "FileMapperLearningPrediction")]
[OutputType(typeof(FileMapperLearningPrediction))]
public sealed class InvokeFileMapperLearningPredictionCommand : LearningIdCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperLearningPrediction>(LearnerRoute + "/predict", HttpMethod.Post,
        Request(RequestJson, ["checkpointHash", "head", "left", "right", "context"], ["checkpointHash", "head", "left", "right", "context"])));
}
[Cmdlet(VerbsCommon.Get, "FileMapperLearningOccurrence")]
[OutputType(typeof(FileMapperLearningOccurrencePage))]
public sealed class GetFileMapperLearningOccurrenceCommand : LearningIdCmdlet
{
    [Parameter] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "{}";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperLearningOccurrencePage>(LearnerRoute + "/occurrences", HttpMethod.Post,
        Request(RequestJson, ["patternHash", "somId", "sourceObservationId", "snapshotId", "sessionId", "workspaceId", "repositoryId", "taskId",
            "terminalId", "computerId", "networkId", "unknownDimension", "from", "to", "timeBasis", "offset", "limit"], [])));
}
[Cmdlet(VerbsCommon.Get, "FileMapperArena")]
[OutputType(typeof(FileMapperSavedArena))]
public sealed class GetFileMapperArenaCommand : LearningCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ArtifactHash")] [ValidatePattern("^[0-9a-f]{64}$")] public string ArenaHash { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperSavedArena>("/api/spatial/arenas/" + ArenaHash));
}
[Cmdlet(VerbsCommon.New, "FileMapperArena", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperSavedArena))]
public sealed class NewFileMapperArenaCommand : LearningCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Server.ToString(), "Persist a frozen evidence and model arena")) return;
        WriteObject(Read<FileMapperSavedArena>("/api/spatial/arenas", HttpMethod.Post,
            Request(RequestJson, ["dataset", "learnerId", "checkpointHash", "context", "board"], ["dataset", "learnerId", "checkpointHash"])));
    }
}
[Cmdlet(VerbsLifecycle.Invoke, "FileMapperArenaReplay")]
[OutputType(typeof(FileMapperArenaReplay))]
public sealed class InvokeFileMapperArenaReplayCommand : LearningCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperArenaReplay>("/api/spatial/replay", HttpMethod.Post,
        Request(RequestJson, ["arenaHash", "moves"], ["arenaHash", "moves"])));
}
[Cmdlet(VerbsLifecycle.Invoke, "FileMapperArenaComputer")]
[OutputType(typeof(FileMapperArenaDecision))]
public sealed class InvokeFileMapperArenaComputerCommand : LearningCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord() => WriteObject(Read<FileMapperArenaDecision>("/api/spatial/computer", HttpMethod.Post,
        Request(RequestJson, ["arenaHash", "moves", "budget"], ["arenaHash", "moves"])));
}
[Cmdlet(VerbsData.Save, "FileMapperArenaReplay", SupportsShouldProcess = true)]
[OutputType(typeof(FileMapperSavedArenaReplay))]
public sealed class SaveFileMapperArenaReplayCommand : LearningCmdlet
{
    [Parameter(Mandatory = true)] [ValidateNotNullOrEmpty] public string RequestJson { get; set; } = "";
    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Server.ToString(), "Persist a verified arena replay and declared competitor attribution")) return;
        WriteObject(Read<FileMapperSavedArenaReplay>("/api/spatial/replays", HttpMethod.Post,
            Request(RequestJson, ["arenaHash", "moves", "competitor"], ["arenaHash", "moves", "competitor"])));
    }
}
