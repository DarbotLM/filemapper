using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Engine;

internal sealed class McpTools(MapStore store)
{
    private sealed record Definition(string Name, string Description, JsonElement InputSchema);
    private static object Text(int maximum = 256, bool empty = false) => new { type = "string", minLength = empty ? 0 : 1,
        maxLength = maximum, pattern = empty ? @"^[^\u0000-\u001f\u007f]*$" : @"^[^\u0000-\u001f\u007f]+$" };
    private static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static object Hash() => new { type = "string", pattern = "^[a-f0-9]{64}$", minLength = 64, maxLength = 64 };
    private static object SpatialMoves() => new { type = "array", maxItems = SpatialGame.MaximumDeckLength,
        items = Schema(new { orientationId = Text(512), x = Integer(0, 11), z = Integer(0, 11), layerId = Text(512), expectedPriorStateHash = Hash() },
            "orientationId", "x", "z", "layerId", "expectedPriorStateHash") };
    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static Definition Tool(string name, string description, object properties, params string[] required) =>
        new(name, description, JsonSerializer.SerializeToElement(Schema(properties, required), McpWire.Json));
    private static readonly Definition[] Definitions = [
        Tool("filemapper.search", "Find observed paths and semantic entities. At most 100 results.", new { snapshotId = Text(), search = Text(512, true) }, "snapshotId", "search"),
        Tool("filemapper.context", "Select literal task matches and bounded recorded graph connections with score components and evidence. Budget covers the complete UTF-8 context payload, not MCP framing or model tokens.", new { snapshotId = Text(), search = Text(512, true), budget = Integer(256, 100000) }, "snapshotId", "search"),
        Tool("filemapper.rank", "Rank at most 50 observed files by bytes, known allocation, or age.", new { snapshotId = Text(), by = new { type = "string", @enum = new[] { "logical", "allocated", "age" } } }, "snapshotId"),
        Tool("filemapper.diff", "Compare a bounded page. Missing paths in partial scans are not deletions.", new { before = Text(), after = Text(), offset = Integer(0, int.MaxValue), limit = Integer(1, 500) }, "before", "after"),
        Tool("filemapper.route", "Find a directed evidence path within explicit traversal bounds.", new { snapshotId = Text(), from = Text(), to = Text(), maxDepth = Integer(0, 64) }, "snapshotId", "from", "to"),
        Tool("filemapper.explain", "Return snapshot metadata and up to 100 supporting observations for a subject.", new { snapshotId = Text(), subjectId = Text() }, "snapshotId", "subjectId"),
        Tool("filemapper.finding", "Explain a saved finding with its ordered witness, assumptions and evidence.", new { snapshotId = Text(), findingId = Text() }, "snapshotId", "findingId"),
        Tool("filemapper.semantic.objects", "Read a pinned page of native SOM objects with provenance and explicit unknowns; maximum 500.", new { snapshotId = Text(), kind = Text(128), search = Text(512, true), offset = Integer(0, int.MaxValue), limit = Integer(1, 500) }, "snapshotId"),
        Tool("filemapper.semantic.patterns", "Compare identities or lexical path patterns from the first 500 files per snapshot. Does not infer movement.", new { before = Text(), after = Text(), limit = Integer(1, 50) }, "before", "after"),
        Tool("filemapper.semantic.challenge", "Generate an observed native challenge for round 1–3. Requires eligible filenames and filesystem evidence.", new { snapshotId = Text(), round = Integer(1, 3) }, "snapshotId"),
        Tool("filemapper.semantic.replay", "Regenerate the observed challenge, require its expected hash, then replay bounded moves. No client evidence, scores or weights.", new { snapshotId = Text(), round = Integer(1, 3), expectedChallengeHash = Hash(), moves = new { type = "array", maxItems = SemanticGame.MaximumDeckLength,
            items = Schema(new { rotation = Integer(0, 3), column = Integer(0, SemanticGame.BoardWidth - 1), expectedPriorStateHash = Hash() }, "rotation", "column", "expectedPriorStateHash") } }, "snapshotId", "round", "expectedChallengeHash", "moves"),
        Tool("filemapper.learning.model", "Read a stored immutable learned checkpoint and its fitted parameters. Zero training updates means untrained; no weights are modified.", new { learnerId = Text(), checkpointHash = Hash() }, "learnerId"),
        Tool("filemapper.learning.vocabulary", "Read retained lexical patterns, encounter counts and distinct source-observation counts. Counts alone are not predictive learning.", new { learnerId = Text(), search = Text(512, true), offset = Integer(0, int.MaxValue), limit = Integer(1, 100) }, "learnerId"),
        Tool("filemapper.learning.occurrences", "Read bounded source-pattern encounters with original observation times and separately recorded context/capture time. Unknown contexts remain unknown.", new { learnerId = Text(), snapshotId = Text(), sessionId = Text(512), offset = Integer(0, int.MaxValue), limit = Integer(1, 50) }, "learnerId"),
        Tool("filemapper.spatial.arenas", "List saved immutable 3D arenas without creating a match or changing learning state.", new { offset = Integer(0, int.MaxValue), limit = Integer(1, 50) }),
        Tool("filemapper.spatial.geometry", "Read true XYZ cells and all proper orientations of a declared spatial encoding. Alternate geometry does not change native SOM identity.", new { baseShape = Text(64), variant = Text(64) }, "baseShape", "variant"),
        Tool("filemapper.spatial.replay", "Replay moves against a locally saved frozen arena. Returns verified state, legal commands and next-piece orientations; never accepts client scores or rewards.", new { arenaHash = Hash(), moves = SpatialMoves() }, "arenaHash", "moves"),
        Tool("filemapper.spatial.computer", "Choose one command with FileMapper's deterministic bounded native opponent. This is not an Astra or SOL model run.", new { arenaHash = Hash(), moves = SpatialMoves() }, "arenaHash", "moves")
    ];
    internal static object[] Catalog => Definitions.Select(d => (object)new { name = d.Name, description = d.Description, inputSchema = d.InputSchema,
        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false } }).ToArray();

    internal object Call(string name, JsonElement arguments, CancellationToken token)
    {
        var definition = Definitions.FirstOrDefault(d => d.Name == name) ?? throw new McpProtocolException(-32602, "Unknown tool.");
        try
        {
            Validate(arguments, definition.InputSchema, "arguments");
            token.ThrowIfCancellationRequested();
            var query = new QueryEngine(store); var semantic = new SemanticWorkspace(store);
            string Get(string key) => arguments.GetProperty(key).GetString()!;
            string? Optional(string key) => arguments.TryGetProperty(key, out var value) ? value.GetString() : null;
            int Number(string key, int fallback) => arguments.TryGetProperty(key, out var value) ? McpWire.Integer(value) : fallback;
            SpatialReplayRequest SpatialRequest() => new(Get("arenaHash"), arguments.GetProperty("moves").EnumerateArray().Select(move =>
                new SpatialMove(move.GetProperty("orientationId").GetString()!, McpWire.Integer(move.GetProperty("x")), McpWire.Integer(move.GetProperty("z")),
                    move.GetProperty("layerId").GetString()!, move.GetProperty("expectedPriorStateHash").GetString()!)).ToArray());
            object value = name switch
            {
                "filemapper.search" => new { snapshot = query.Require(Get("snapshotId")), results = store.GetNodes(Get("snapshotId"), search: Get("search"), limit: 100) },
                "filemapper.context" => query.Context(Get("snapshotId"), Get("search"), Number("budget", 16000), token),
                "filemapper.rank" => query.Rank(Get("snapshotId"), Optional("by") ?? "logical", 50),
                "filemapper.diff" => query.Diff(Get("before"), Get("after"), Number("offset", 0), Number("limit", 100)),
                "filemapper.route" => query.Route(Get("snapshotId"), Get("from"), Get("to"), Number("maxDepth", 12), token),
                "filemapper.explain" => new { snapshot = query.Require(Get("snapshotId")), observations = store.GetObservations(Get("snapshotId"), Get("subjectId"), 100) },
                "filemapper.finding" => query.ExplainFinding(Get("snapshotId"), Get("findingId")),
                "filemapper.semantic.objects" => semantic.Objects(Get("snapshotId"), Optional("kind"), Optional("search"), Number("offset", 0), Number("limit", 50), token),
                "filemapper.semantic.patterns" => semantic.Patterns(Get("before"), Get("after"), Number("limit", 50), token),
                "filemapper.semantic.challenge" => semantic.Challenge(Get("snapshotId"), Number("round", 1), token),
                "filemapper.semantic.replay" => Replay(),
                "filemapper.learning.model" => new PatternLearningWorkspace(store).Load(Get("learnerId"), Optional("checkpointHash")),
                "filemapper.learning.vocabulary" => store.ReadPatternLearningVocabulary(Get("learnerId"), Optional("search"), offset: Number("offset", 0), limit: Number("limit", 50)),
                "filemapper.learning.occurrences" => store.ReadPatternLearningOccurrences(Get("learnerId"), new(SnapshotId: Optional("snapshotId"), SessionId: Optional("sessionId"), Offset: Number("offset", 0), Limit: Number("limit", 50))),
                "filemapper.spatial.arenas" => store.ListSemanticArtifacts("spatial-arena/1", Number("offset", 0), Number("limit", 50)),
                "filemapper.spatial.geometry" => new { geometry = SpatialGameGeometry.Get(Get("baseShape"), Get("variant")), orientations = SpatialGameGeometry.Orientations(Get("baseShape"), Get("variant")) },
                "filemapper.spatial.replay" => new SpatialWorkspace(store).Replay(SpatialRequest(), token),
                "filemapper.spatial.computer" => new SpatialWorkspace(store).Choose(SpatialRequest(), cancellationToken: token),
                _ => throw new McpProtocolException(-32602, "Unknown tool.")
            };
            token.ThrowIfCancellationRequested();
            return Result(Encoding.UTF8.GetString(McpWire.Serialize(value, 524_288)), false);
            GameState Replay()
            {
                var challenge = semantic.Challenge(Get("snapshotId"), Number("round", 1), token);
                if (!string.Equals(challenge.ChallengeHash, Get("expectedChallengeHash"), StringComparison.Ordinal))
                    throw new ArgumentException("Expected challenge hash does not match the current server-generated observed challenge.");
                var moves = arguments.GetProperty("moves").EnumerateArray().Select(m => new GameMove(McpWire.Integer(m.GetProperty("rotation")),
                    McpWire.Integer(m.GetProperty("column")), m.GetProperty("expectedPriorStateHash").GetString()!)).ToArray();
                return SemanticGame.Replay(challenge, moves);
            }
        }
        catch (McpOutputLimitException) { return Result("Result exceeds the 512 KiB tool payload limit. Narrow the query/page; use CLI or HTTP for a full export.", true); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        { return Result(ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message, true); }
    }
    internal static object Result(string message, bool error) => new { content = new[] { new { type = "text", text = message } }, isError = error };

    // Validate the exact small JSON Schema 2020-12 vocabulary emitted above. No external schemas/$refs are accepted.
    private static void Validate(JsonElement value, JsonElement schema, string path)
    {
        void Invalid(string message) => throw new ArgumentException(path + ": " + message);
        var type = schema.GetProperty("type").GetString();
        switch (type)
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object) { Invalid("must be an object"); return; }
                var properties = schema.GetProperty("properties");
                foreach (var required in schema.GetProperty("required").EnumerateArray())
                    if (!value.TryGetProperty(required.GetString()!, out _)) Invalid("missing " + required.GetString());
                foreach (var property in value.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var child)) Invalid("unknown property " + property.Name);
                    Validate(property.Value, child, path + "." + property.Name);
                }
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array) { Invalid("must be an array"); return; }
                if (value.GetArrayLength() > schema.GetProperty("maxItems").GetInt32()) Invalid("too many items");
                foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"), path + "[]");
                break;
            case "integer":
                if (!McpWire.TryInteger(value, out var n)
                    || n < schema.GetProperty("minimum").GetInt32() || n > schema.GetProperty("maximum").GetInt32()) Invalid("integer outside advertised bounds");
                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String) { Invalid("must be a string"); return; }
                var text = value.GetString()!; var length = text.EnumerateRunes().Count();
                if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32()
                    || schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32()) Invalid("string outside advertised length bounds");
                if (schema.TryGetProperty("pattern", out var pattern))
                {
                    var match = Regex.Match(text, pattern.GetString()!, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                    if (!match.Success || match.Length != text.Length) Invalid("invalid string format");
                }
                if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => v.GetString() == text)) Invalid("unknown enum value");
                break;
            default: throw new InvalidOperationException("Unsupported internal MCP schema vocabulary.");
        }
    }
}
