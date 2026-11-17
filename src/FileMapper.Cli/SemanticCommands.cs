using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Engine;
using FileMapper.Semantics;
using FileMapper.Storage;

namespace FileMapper.Cli;

internal static class SemanticCommands
{
    private static readonly JsonSerializerOptions InputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 64
    };

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 0) { Console.WriteLine(Help); return 0; }
        if (args[0] is "help" or "--help" or "-h")
        {
            if (args.Length != 1) throw new ArgumentException("Semantic help accepts no additional arguments.");
            Console.WriteLine(Help); return 0;
        }
        var command = args[0];
        var allowed = new HashSet<string>(command switch
        {
            "objects" => ["--database", "--kind", "--search", "--offset", "--limit"],
            "patterns" => ["--database", "--limit"],
            "challenge" => ["--database", "--round"],
            "export" => ["--database", "--destination"],
            "replay" => ["--challenge", "--moves"],
            _ => throw new ArgumentException("Unknown semantic command. Use filemapper semantic --help.")
        }, StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (!allowed.Contains(argument) || options.ContainsKey(argument))
                    throw new ArgumentException("Unknown or repeated option: " + argument);
                if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("Missing value for " + argument);
                options.Add(argument, args[index]);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(argument) || argument.Length > 256 || argument.Any(char.IsControl))
                    throw new ArgumentException("Snapshot ids must contain 1 to 256 non-control characters.");
                positionals.Add(argument);
            }
        }
        var expectedPositionals = command == "replay" ? 0 : command == "patterns" ? 2 : 1;
        if (positionals.Count != expectedPositionals)
            throw new ArgumentException($"semantic {command} requires exactly {expectedPositionals} positional argument(s). Use filemapper semantic --help.");
        string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException(key + " is required.");
        int Number(string key, int fallback, int minimum, int maximum)
        {
            if (!options.TryGetValue(key, out var value)) return fallback;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                && result >= minimum && result <= maximum ? result
                : throw new ArgumentException($"{key} requires an integer from {minimum} through {maximum}.");
        }

        // Offline replay has no database dependency and never constructs a store or starts a coordinator.
        if (command == "replay")
        {
            var challenge = await ReadJsonAsync<GameChallenge>(Required("--challenge"), 8 * 1024 * 1024, cancellationToken);
            var moves = await ReadJsonAsync<GameMove[]>(Required("--moves"), 1024 * 1024, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var replay = SemanticGame.Replay(challenge, moves);
            Console.Error.WriteLine(CanonicalJson.Serialize(new
            {
                provenance = "caller-supplied-challenge-and-moves",
                note = "Replay validates game rules and hashes; it does not authenticate dataset evidence or model authorship."
            }));
            Print(replay);
            return 0;
        }

        var database = Path.GetFullPath(options.GetValueOrDefault("--database")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMapper", "data", "filemapper.db"));
        if (!File.Exists(database)) throw new FileNotFoundException("An existing FileMapper database is required. Semantic commands do not initialize a database.", database);
        var workspace = new SemanticWorkspace(new MapStore(database));
        switch (command)
        {
            case "objects":
                Print(workspace.Objects(positionals[0], options.GetValueOrDefault("--kind"), options.GetValueOrDefault("--search"),
                    Number("--offset", 0, 0, int.MaxValue), Number("--limit", 50, 1, 500), cancellationToken));
                break;
            case "patterns":
                Print(workspace.Patterns(positionals[0], positionals[1], Number("--limit", 50, 1, 50), cancellationToken));
                break;
            case "challenge":
                Print(workspace.Challenge(positionals[0], Number("--round", 1, 1, 3), cancellationToken));
                break;
            case "export":
                Print(await ExportAsync(workspace, positionals[0], Required("--destination"), cancellationToken));
                break;
        }
        return 0;
    }

    private static async Task<object> ExportAsync(SemanticWorkspace workspace, string snapshotId, string requestedDestination,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(requestedDestination);
        var compressed = destination.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase);
        if (!compressed && !destination.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--destination must end in .ndjson or .ndjson.gz.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The export destination's parent directory must already exist: " + parent);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Export destination already exists; overwrite is not allowed: " + destination);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var committed = false;
        var temporaryCreated = false;
        try
        {
            SemanticExportResult export;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryCreated = true;
                if (compressed)
                {
                    await using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
                    export = await workspace.ExportAsync(snapshotId, gzip, cancellationToken);
                }
                else export = await workspace.ExportAsync(snapshotId, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            string digest;
            long bytes;
            await using (var input = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bytes = input.Length;
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            committed = true;
            return new { destination, compressed, bytes, fileSha256 = digest, export };
        }
        finally
        {
            if (temporaryCreated && !committed && File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(CanonicalJson.Serialize(new { warning = "incomplete-export-temporary-file", path = temporary, detail = error.Message }));
                }
            }
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string requestedPath, int maximumBytes, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(requestedPath);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes) throw new ArgumentException($"JSON input exceeds {maximumBytes} bytes: {path}");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        if (input.ReadByte() != -1) throw new IOException("JSON input changed while reading: " + path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = InputJson.MaxDepth });
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(InputJson) ?? throw new JsonException("JSON input must not be null.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name)) throw new JsonException("Repeated JSON property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void Print(object value) => Console.WriteLine(CanonicalJson.Serialize(value));

    private const string Help = """
    filemapper semantic objects <snapshot> [--kind <kind>] [--search <text>] [--offset 0] [--limit 50]
    filemapper semantic patterns <before-snapshot> <after-snapshot> [--limit 50]
    filemapper semantic challenge <snapshot> [--round 1|2|3]
    filemapper semantic export <snapshot> --destination <new-file.ndjson|new-file.ndjson.gz>
    filemapper semantic replay --challenge <challenge.json> --moves <moves.json>

    Observed-data commands: --database <existing-file>. They do not initialize or scan a database.
    Export streams a new file and reports its SHA-256, size, and dataset counts. Existing files are never overwritten.
    Replay is offline and outputs GameState JSON. Challenge/move provenance is caller-supplied;
    replay validates game rules and hashes, not dataset authenticity or model authorship.
    Rotation is 0–3 counterclockwise XY quarter turns; every move needs expectedPriorStateHash.
    Options are exact, unique, and command-specific. Unknown options and extra positionals are rejected.
    """;
}
