using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Domain;

namespace FileMapper.Collect.Git;

internal sealed class ProvenanceGraph(string snapshot, string root, ProvenanceOptions limits)
{
    private readonly Dictionary<string, MapNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MapEdge> edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Evidence> evidence = new(StringComparer.Ordinal);
    private long readBytes;
    public string Root { get; } = Path.TrimEndingDirectorySeparator(root);
    public ProvenanceOptions Limits { get; } = limits;
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public void Node(string id, string kind, string name, string? path, Dictionary<string, object?>? properties = null)
    {
        if (!nodes.TryGetValue(id, out var existing)) nodes[id] = new(id, kind, name, path, properties ?? []);
        else
        {
            var merged = new Dictionary<string, object?>(existing.Properties);
            foreach (var pair in properties ?? []) if (pair.Value is not null) merged[pair.Key] = pair.Value;
            nodes[id] = existing with { Properties = merged };
        }
    }
    public string Document(string path, byte[]? bytes = null)
    {
        var id = Identity.For("source", path);
        var hash = bytes is null ? null : Hash(bytes);
        Node(id, "SourceDocument", Path.GetFileName(path), path, new() { ["sha256"] = hash, ["trust"] = "untrusted-data" });
        Observe(id, "source_document", path, path, hash: hash);
        return id;
    }
    public string Observe(string subject, string predicate, object value, string source, double confidence = 1,
        string completeness = "complete", string? hash = null, IEnumerable<string>? contributes = null, string? error = null)
    {
        var text = value is string s ? s : JsonSerializer.Serialize(value);
        var id = Identity.For("observation", snapshot + ":provenance:" + subject + ":" + predicate + ":" + source + ":" + text + ":" + hash);
        evidence[id] = new(id, snapshot, subject, predicate, text, "repository-provenance", source, DateTimeOffset.UtcNow,
            confidence, completeness, error, error is null ? null : text, EvidenceHash: hash)
        { Scope = Root, ContributingObservationIds = contributes?.Distinct(StringComparer.Ordinal).ToArray() ?? [] };
        return id;
    }
    public void Edge(string from, string to, string kind, string observation, double confidence = 1, Dictionary<string, object?>? properties = null)
    {
        var id = Identity.For("edge", from + ":" + kind + ":" + to + ":" + observation);
        edges[id] = new(id, from, to, kind, observation, confidence, properties);
    }
    public void Diagnostic(string path, string code, string message)
    { Observe(Document(path), "diagnostic", message, path, completeness: "partial", error: code); }
    public AnalysisResult Result() => new(nodes.Values.ToArray(), edges.Values.ToArray(), evidence.Values.ToArray(), []);
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static string Text(JsonElement value, string property, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString()! : fallback;
    public static IEnumerable<JsonElement> Items(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.Array ? found.EnumerateArray() : [];
    public bool Contains(string path) => IsWithinRoot(path, Root);
    internal static bool IsWithinRoot(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        // Volume roots retain their separator; adding another rejects every descendant.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, PathComparison) || path.StartsWith(prefix, PathComparison);
    }
    public string Resolve(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0 || relative.Contains('$') || relative.Contains('*')) throw new InvalidDataException("Unresolved or invalid provenance path.");
        var path = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!Contains(path)) throw new InvalidDataException("Provenance path is outside the analysis root.");
        if (OperatingSystem.IsWindows() && Path.GetRelativePath(Root, path).Contains(':')) throw new InvalidDataException("Alternate-stream provenance paths are not accepted.");
        return path;
    }
    public void CheckPath(string path)
    {
        if (!Contains(path)) throw new InvalidDataException("Read outside the analysis root refused.");
        for (var current = path; ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point provenance read refused.");
            if (string.Equals(current, Root, PathComparison)) break;
        }
    }
    public byte[] Read(string path, CancellationToken ct)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > Limits.MaxDocumentBytes) throw new IOException("Provenance document exceeds parser byte budget.");
        using var output = new MemoryStream(); var buffer = new byte[16_384]; int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested(); readBytes += count;
            if (output.Length + count > Limits.MaxDocumentBytes || readBytes > 64L * 1024 * 1024) throw new IOException("Provenance parser byte budget reached.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public (string Hash, long Length) HashFile(string path, CancellationToken ct)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > Limits.MaxArtifactBytes) throw new IOException("Artifact exceeds bounded hashing limit.");
        var initialLength = stream.Length; var initialWrite = File.GetLastWriteTimeUtc(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long bytes = 0; int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested(); bytes += count;
            if (bytes > Limits.MaxArtifactBytes) throw new IOException("Artifact grew past hashing limit.");
            hash.AppendData(buffer, 0, count);
        }
        if (bytes != initialLength || stream.Length != initialLength || File.GetLastWriteTimeUtc(path) != initialWrite) throw new IOException("Artifact changed during hashing; provenance is unknown.");
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), bytes);
    }
    public IEnumerable<string> Discover(CancellationToken ct)
    {
        var pending = new Stack<string>(); pending.Push(Root); int directories = 0, files = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (++directories > 20_000) { Diagnostic(Root, "directory_limit", "Provenance traversal limited to 20,000 directories."); yield break; }
            string[] entries;
            try { CheckPath(directory); entries = Directory.EnumerateFileSystemEntries(directory).Take(50_001).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostic(directory, "directory_read", ex.Message); continue; }
            if (entries.Length > 50_000) { Diagnostic(directory, "directory_entry_limit", "Provenance enumeration stopped at 50,000 entries in one directory."); entries = entries[..50_000]; }
            foreach (var path in entries.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostic(path, "entry_read", ex.Message); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(path) is not (".git" or "node_modules" or ".vs" or "bin"))
                    {
                        if (pending.Count >= 20_000) { Diagnostic(Root, "pending_directory_limit", "Provenance pending-directory limit reached."); yield break; }
                        pending.Push(path);
                    }
                    continue;
                }
                var extension = Path.GetExtension(path); var name = Path.GetFileName(path);
                if (extension is not (".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx") &&
                    !(extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && (name.Contains("bom", StringComparison.OrdinalIgnoreCase) || name.Contains("spdx", StringComparison.OrdinalIgnoreCase) || name.EndsWith("provenance.json", StringComparison.OrdinalIgnoreCase) || name is "project.assets.json" or "packages.lock.json"))) continue;
                if (++files > Limits.MaxDocuments) { Diagnostic(Root, "document_limit", "Provenance document count limit reached."); yield break; }
                yield return path;
            }
        }
    }
}
