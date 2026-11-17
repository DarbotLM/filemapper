using System.Globalization;
using FileMapper.Engine;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Cli;

internal static class LearningCommands
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length < 2 || args[1] is "--help" or "help" or "-h") { Console.WriteLine(Help); return 0; }
        var family = args[0]; var command = args[1];
        var modes = family == "learning" ? new[] { "create", "model", "subjects", "append", "observe", "predict", "rank", "vocabulary", "events", "list" }
            : new[] { "preview", "freeze", "load", "replay", "computer", "baseline", "save", "list", "export" };
        if (!modes.Contains(command, StringComparer.Ordinal)) throw new ArgumentException("Unknown command. Use filemapper " + family + " --help.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--database" };
        if (command is "create" or "append" or "observe" or "predict" or "rank" or "preview" or "freeze" or "replay" or "computer" or "save") allowed.Add("--request");
        if (command is "subjects" or "vocabulary" or "events" or "list") { allowed.Add("--offset"); allowed.Add("--limit"); }
        if (command == "model") allowed.Add("--checkpoint");
        if (command == "vocabulary") { allowed.Add("--search"); allowed.Add("--category"); }
        if (family == "arena" && command == "list") allowed.Add("--kind");
        if (command == "save") allowed.Add("--competitor");
        var options = new Dictionary<string, string>(StringComparer.Ordinal); var positionals = new List<string>();
        for (var index = 2; index < args.Length; index++)
        {
            var value = args[index];
            if (value.StartsWith("-", StringComparison.Ordinal))
            {
                if (!allowed.Contains(value) || options.ContainsKey(value)) throw new ArgumentException("Unknown or repeated option: " + value);
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index])) throw new ArgumentException("Missing value for " + value);
                options.Add(value, args[index]);
            }
            else if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Invalid positional identifier.");
            else positionals.Add(value);
        }
        var needsId = family == "learning" ? command is not ("create" or "list") : command is "load" or "baseline" or "export";
        if (positionals.Count != (needsId ? 1 : 0)) throw new ArgumentException("Unexpected number of positional identifiers. Use --help.");
        var id = positionals.FirstOrDefault() ?? "";
        string Required(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException(name + " is required.");
        int Number(string name, int fallback, int maximum)
        {
            if (!options.TryGetValue(name, out var value)) return fallback;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= (name == "--offset" ? 0 : 1) && number <= maximum
                ? number : throw new ArgumentException(name + " is outside its supported range.");
        }
        async Task<T> Request<T>()
        {
            await using var file = new FileStream(Path.GetFullPath(Required("--request")), FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            return await NativeRequestJson.ReadAsync<T>(file, 64 * 1024, cancellationToken);
        }
        var database = Path.GetFullPath(options.GetValueOrDefault("--database") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "filemapper.db"));
        if (!File.Exists(database)) throw new FileNotFoundException("An existing FileMapper database is required.", database);
        var write = command is "create" or "append" or "observe" or "freeze" or "save";
        var store = new MapStore(database, readOnly: !write); var learner = new PatternLearningWorkspace(store); var arena = new SpatialWorkspace(store);
        object result;
        if (family == "learning")
            result = command switch
            {
                "create" => learner.Create(await Request<LearningCreateRequest>()),
                "model" => learner.Load(id, options.GetValueOrDefault("--checkpoint")),
                "list" => store.ListPatternLearners(Number("--offset", 0, int.MaxValue), Number("--limit", 50, 100)),
                "subjects" => learner.Subjects(id, Number("--offset", 0, int.MaxValue), Number("--limit", 50, 500), cancellationToken),
                "append" => learner.Append(id, await Request<LearningEventRequest>(), cancellationToken),
                "observe" => learner.ObservePage(id, await Request<LearningObservePageRequest>(), cancellationToken),
                "predict" => learner.Predict(id, await Request<LearningPredictionRequest>(), cancellationToken),
                "rank" => learner.Rank(id, await Request<LearningRankingRequest>(), cancellationToken),
                "vocabulary" => store.ReadPatternLearningVocabulary(id, options.GetValueOrDefault("--search"), options.GetValueOrDefault("--category"), Number("--offset", 0, int.MaxValue), Number("--limit", 50, 500)),
                "events" => store.ReadPatternLearningEvents(id, Number("--offset", 0, int.MaxValue), Number("--limit", 50, 500)),
                _ => throw new InvalidOperationException("Unknown learning command.")
            };
        else
            result = command switch
            {
                "preview" => arena.Preview(await Request<SpatialArenaRequest>(), cancellationToken),
                "freeze" => arena.Freeze(await Request<SpatialArenaRequest>(), cancellationToken),
                "load" => arena.Load(id),
                "replay" => arena.Replay(await Request<SpatialReplayRequest>(), cancellationToken),
                "computer" => arena.Choose(await Request<SpatialReplayRequest>(), cancellationToken: cancellationToken),
                "baseline" => SpatialOpponent.Play(arena.Load(id).Arena.Challenge, cancellationToken: cancellationToken),
                "save" => arena.SaveReplay(await Request<SpatialReplayRequest>(), Required("--competitor"), cancellationToken),
                "list" => store.ListSemanticArtifacts(options.GetValueOrDefault("--kind"), Number("--offset", 0, int.MaxValue), Number("--limit", 50, 100)),
                "export" => store.GetSemanticArtifact(id) ?? throw new KeyNotFoundException("Artifact was not found."),
                _ => throw new InvalidOperationException("Unknown arena command.")
            };
        if (family == "arena" && command == "export") Console.Write(((FileMapper.Domain.SemanticArtifact)result).Payload);
        else Console.WriteLine(CanonicalJson.Serialize(result));
        return result is LearningObservationBatch { PageComplete: false } or SpatialRun { Completed: false } or SpatialDecision { Completed: false } ? 2 : 0;
    }
    private const string Help = """
        Persistent learning (PowerShell/C# native; JSON request files):
          filemapper learning create --request create.json
          filemapper learning list [--offset 0] [--limit 50]
          filemapper learning model <learner> [--checkpoint <sha256>]
          filemapper learning subjects <snapshot> [--offset 0] [--limit 50]
          filemapper learning append <learner> --request event.json
          filemapper learning observe <learner> --request page.json
          filemapper learning predict <learner> --request pair.json
          filemapper learning rank <learner> --request candidates.json
          filemapper learning vocabulary <learner> [--search text] [--category name]
          filemapper learning events <learner> [--offset 0] [--limit 50]

        3D whole-shape arena:
          filemapper arena preview --request arena.json
          filemapper arena freeze --request arena.json
          filemapper arena load <artifact-hash>
          filemapper arena replay --request moves.json
          filemapper arena computer --request moves.json
          filemapper arena baseline <artifact-hash>
          filemapper arena save --request moves.json --competitor <declared-label>
          filemapper arena list [--kind spatial-arena/1|spatial-replay/1]
          filemapper arena export <artifact-hash>

        All commands accept --database <existing-file>. Reads never create a learning schema.
        observe processes one explicitly bounded page (1–100); continue using its next checkpoint and offset.
        Labels must cite human, reviewed-dataset or controlled-fixture provenance. Held-out labels cannot train.
        Observations grow vocabulary; only accepted training labels update weights. A frozen arena never changes.
        The computer is a deterministic native baseline. A supplied competitor label does not verify model authorship.
        """;
}
