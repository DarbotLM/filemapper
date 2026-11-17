using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Semantics;

namespace FileMapper.Harvest;

public sealed record BoundedMaterializationPolicy(string Mode, long MaximumResponseBodyBytes);
public sealed record GitHarvestPolicy(long MaximumBlobBytes = 16 * 1024 * 1024,
    long MaximumCapturedBytes = 4L * 1024 * 1024 * 1024, int MaximumTreeEntries = 2_000_000,
    bool MaterializeBlobs = false, int FetchTimeoutSeconds = 1800,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoundedMaterializationPolicy? BoundedMaterialization = null);
public sealed record GitHarvestRequest(string ProviderStableId, string CanonicalUri, string DisplayName,
    string BareRepository, string Commit, string? Ref = null, string? InventorySha256 = null,
    GitHarvestPolicy? Policy = null);
public sealed record SourceTreeEntry(string EntryId, string RepositoryPath, string RepositoryPathBytes,
    string PathEncoding, string Mode, string ObjectType, string GitObjectId);
public sealed record CapturedSourceEntry(SourceTreeEntry Entry, string Status, string? DocumentId,
    string SourceKind, SourceBlobDescriptor? Blob, long? ReportedBlobBytes, string? Detail);
public sealed record SourceCaptureCoverage(long TreeEntries, long CapturedEntries, long MissingEntries,
    long PolicyExcludedEntries, long SubmoduleReferences, long ErrorEntries, long AssociatedSourceBytes,
    long UniqueRetainedBytes, long UniqueRetainedBlobs, bool ProviderEnumerationComplete);
public sealed record SourceCaptureManifest(string Schema, string CaptureRequestId, string RepositoryId,
    string ProviderStableId, string IdentityStatus, string CanonicalUri, string DisplayName,
    string Commit, string ObjectFormat, string TreeObjectId, string? Ref, string? InventorySha256,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, GitHarvestPolicy Policy, string PolicyHash,
    SourceBlobDescriptor RawTree, SourceBlobDescriptor Entries, SourceCaptureCoverage Coverage,
    string State, IReadOnlyList<GitCommandReceipt> Commands, string CollectorVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceBlobDescriptor? MaterializationEvidence = null);
public sealed record SourceCaptureReceipt(string CaptureId, string ManifestPath, SourceCaptureManifest Manifest);

/// <summary>Captures pinned Git blobs, preserving raw path bytes and all unavailable entries.</summary>
public sealed class GitSourceCapture
{
    public const string CollectorVersion = "filemapper.git-source-capture/3";
    private const long ManifestByteLimit = 512L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string RootDirectory { get; }
    public SourceBlobStore Blobs { get; }
    private readonly Func<HttpMessageHandler>? materializationHandler;
    internal GitSourceCapture(string rootDirectory, Func<HttpMessageHandler> materializationHandler) : this(rootDirectory)
        => this.materializationHandler = materializationHandler;
    public GitSourceCapture(string rootDirectory, bool readOnly = false)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        HarvestFiles.RequireOrdinaryPath(RootDirectory);
        if (readOnly && !Directory.Exists(RootDirectory)) throw new DirectoryNotFoundException("An existing source capture directory is required.");
        if (!readOnly) HarvestFiles.CreateDirectory(RootDirectory);
        Blobs = new(Path.Combine(RootDirectory, "objects"), ManifestByteLimit, readOnly);
    }

    public async Task<SourceCaptureReceipt> CaptureAsync(GitHarvestRequest request,
        IProgress<object>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(request); var policy = request.Policy ?? new();
        if (Blobs.IsReadOnly) throw new InvalidOperationException("Source capture store is read-only.");
        var started = DateTimeOffset.UtcNow; var requestId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(RootDirectory, "attempts", requestId); HarvestFiles.CreateDirectory(staging);
        await WriteNewJsonAsync(Path.Combine(staging, "request.json"), request, cancellationToken);
        var commands = new List<GitCommandReceipt>(); var git = new GitSourceProcess(request.BareRepository);
        BoundedGitHubBlobs? transport = null;
        var repositoryId = "repo:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-repository/1", Provider = "github", request.ProviderStableId });
        try
        {
            if (await git.TextAsync(["rev-parse", "--is-bare-repository"], staging, cancellationToken) != "true")
                throw new ArgumentException("Source capture requires an explicitly acquired bare repository.");
            var objectFormat = await git.TextAsync(["rev-parse", "--show-object-format"], staging, cancellationToken);
            if (objectFormat is not ("sha1" or "sha256")) throw new IOException("Unsupported Git object format.");
            if (await git.TextAsync(["rev-parse", "--verify", request.Commit + "^{commit}"], staging, cancellationToken) != request.Commit)
                throw new IOException("The requested exact commit could not be verified locally.");
            var treeId = await git.TextAsync(["rev-parse", "--verify", request.Commit + "^{tree}"], staging, cancellationToken);
            if (policy.MaterializeBlobs)
            {
                // Only freshly acquired bare-cache configuration is accepted for network materialization.
                // Key names are sufficient: never read or log header/token/helper values from rejected config.
                var names = await git.TextAsync(["config", "--local", "--no-includes", "--name-only", "--list"], staging, cancellationToken);
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates", "core.symlinks", "core.ignorecase",
                    "extensions.objectformat", "extensions.partialclone", "remote.origin.url", "remote.origin.promisor",
                    "remote.origin.partialclonefilter", "remote.origin.fetch", "remote.origin.tagopt"
                };
                if (names.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(name => !allowed.Contains(name)))
                    throw new IOException("Network materialization requires an owned bare acquisition with only the documented cache configuration keys. Additional local configuration was rejected; no values were logged.");
                var origin = await git.TextAsync(["remote", "get-url", "origin"], staging, cancellationToken);
                if (origin != request.CanonicalUri + ".git") throw new IOException("Owned acquisition origin does not match the pinned repository URI.");
                if (policy.BoundedMaterialization is { } bounded)
                {
                    transport = new BoundedGitHubBlobs(request.CanonicalUri, policy.MaximumBlobBytes,
                        bounded.MaximumResponseBodyBytes, policy.FetchTimeoutSeconds, materializationHandler?.Invoke());
                    progress?.Report(new { phase = "materializing", request.DisplayName, request.Commit,
                        maximumResponseBodyBytes = bounded.MaximumResponseBodyBytes, deadlineSeconds = policy.FetchTimeoutSeconds });
                }
                else
                {
                    progress?.Report(new { phase = "materializing", request.DisplayName, request.Commit, maximumObjectBytes = policy.MaximumBlobBytes, aggregateTransferLimit = (long?)null });
                    var fetch = await git.ToFileAsync(["fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules",
                        "--no-auto-maintenance", "--depth=1", "--refetch", "--refmap=", "--filter=blob:limit=" + (policy.MaximumBlobBytes + 1).ToString(CultureInfo.InvariantCulture),
                        "origin", request.Commit], Path.Combine(staging, "fetch.stdout"), policy.FetchTimeoutSeconds, 1024 * 1024, cancellationToken);
                    commands.Add(fetch);
                }
            }
            var treePath = Path.Combine(staging, "tree.raw");
            var treeCommand = await git.ToFileAsync(["ls-tree", "-r", "-z", "--full-tree", request.Commit], treePath, 300, ManifestByteLimit, cancellationToken);
            commands.Add(treeCommand);
            if (treeCommand.ExitCode != 0) throw new IOException("Pinned tree enumeration failed: " + treeCommand.StandardError);
            var rawTree = await RetainFileAsync(treePath, cancellationToken);
            var sizesPath = Path.Combine(staging, "local-objects.txt");
            var sizesCommand = await git.ToFileAsync(["cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype) %(objectsize)"],
                sizesPath, 600, ManifestByteLimit, cancellationToken);
            commands.Add(sizesCommand);
            if (sizesCommand.ExitCode != 0) throw new IOException("Local object inventory failed: " + sizesCommand.StandardError);
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            await foreach (var line in File.ReadLinesAsync(sizesPath, cancellationToken))
            {
                var parts = line.Split(' ');
                if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                    throw new IOException("Invalid local Git object inventory.");
                if (parts[1] == "blob") sizes.Add(parts[0], length);
            }
            long entriesCount = 0, captured = 0, missing = 0, excluded = 0, submodules = 0, errors = 0, associatedBytes = 0, uniqueBytes = 0;
            var retained = new Dictionary<string, SourceBlobDescriptor>(StringComparer.Ordinal);
            var remoteFailures = new Dictionary<string, string>(StringComparer.Ordinal);
            var remoteBudgetExclusions = new Dictionary<string, long>(StringComparer.Ordinal);
            var lfsPointers = new Dictionary<string, string>(StringComparer.Ordinal);
            var entriesPath = Path.Combine(staging, "entries.ndjson");
            using var batch = git.Start(["cat-file", "--batch"], input: true);
            var batchErrors = GitSourceProcess.DrainErrorAsync(batch.StandardError, cancellationToken);
            try
            {
                await using (var writer = new FileStream(entriesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true))
                {
                    await foreach (var entry in ReadTreeAsync(treePath, repositoryId, request.Commit, policy.MaximumTreeEntries, cancellationToken))
                    {
                        entriesCount++; CapturedSourceEntry record;
                        var kind = entry.Mode == "120000" ? "symlink-target-text" : entry.Mode == "160000" ? "submodule-reference" : "git-blob";
                        if (entry.ObjectType == "commit" && entry.Mode == "160000")
                        { submodules++; record = new(entry, "submodule-reference", null, kind, null, null, "Referenced commit retained; submodule repository is not traversed."); }
                        else if (entry.ObjectType != "blob")
                        { errors++; record = new(entry, "unsupported-object-type", null, kind, null, null, "Tree entry is not a source blob."); }
                        else
                        {
                        sizes.TryGetValue(entry.GitObjectId, out var length);
                        byte[]? downloaded = null;
                        string? unavailable = null;
                        if (!sizes.ContainsKey(entry.GitObjectId) && !retained.ContainsKey(entry.GitObjectId))
                        {
                            if (remoteBudgetExclusions.TryGetValue(entry.GitObjectId, out var excludedLength)) length = excludedLength;
                            else if (remoteFailures.TryGetValue(entry.GitObjectId, out var previous)) unavailable = previous;
                            else if (transport is not null)
                            {
                                var response = await transport.ReadAsync(entry.GitObjectId, objectFormat, cancellationToken);
                                downloaded = response.Bytes; unavailable = response.Detail;
                                if (downloaded is not null)
                                {
                                    length = downloaded.LongLength;
                                    if (length <= policy.MaximumCapturedBytes - uniqueBytes) sizes[entry.GitObjectId] = length;
                                    else remoteBudgetExclusions[entry.GitObjectId] = length;
                                }
                                else remoteFailures[entry.GitObjectId] = unavailable!;
                            }
                            else unavailable = "Blob is not materialized locally; its size and exclusion reason are unknown. Lazy fetch is disabled.";
                        }
                        if (unavailable is not null)
                        { missing++; record = new(entry, "unavailable", null, kind, null, null, unavailable); }
                        else if (length > policy.MaximumBlobBytes)
                        { excluded++; record = new(entry, "excluded-size-policy", null, kind, null, length, "Source blob exceeds the per-document byte ceiling."); }
                        else if (!retained.ContainsKey(entry.GitObjectId) && length > policy.MaximumCapturedBytes - uniqueBytes)
                        { excluded++; record = new(entry, "excluded-capture-budget", null, kind, null, length, "Unique captured-byte budget reached."); }
                        else
                        {
                            if (!retained.TryGetValue(entry.GitObjectId, out var blob))
                            {
                                // One bounded blob at a time. The advertised Git object hash is independently checked.
                                var bytes = downloaded ?? await ReadBlobAsync(batch, entry.GitObjectId, length, objectFormat, cancellationToken);
                                var result = await Blobs.PutAsync(new MemoryStream(bytes, writable: false), expectedLength: length, cancellationToken: cancellationToken);
                                blob = result.Blob ?? throw new IOException("Source bytes were not retained: " + result.Detail);
                                if (LfsPointer(bytes) is { } lfs) lfsPointers.Add(entry.GitObjectId, lfs);
                                retained.Add(entry.GitObjectId, blob); uniqueBytes += length;
                            }
                            captured++; associatedBytes += length;
                            var documentId = "document:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-document-occurrence/1", RepositoryId = repositoryId,
                                Revision = request.Commit, entry.RepositoryPathBytes, entry.Mode, BlobSha256 = blob.Sha256 });
                            var pointer = entry.Mode != "120000" && lfsPointers.TryGetValue(entry.GitObjectId, out var lfsDetail) ? lfsDetail : null;
                            record = new(entry, "captured", documentId, pointer is null ? kind : "lfs-pointer", blob, length, pointer);
                        }
                        }
                        await writer.WriteAsync(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(record) + "\n"), cancellationToken);
                        if (entriesCount % 1000 == 0) progress?.Report(new { phase = "capturing", request.DisplayName, entriesCount, captured, missing, excluded, uniqueBytes });
                    }
                    await writer.FlushAsync(cancellationToken); writer.Flush(true);
                }
                batch.StandardInput.Close();
                await batch.WaitForExitAsync(cancellationToken);
                var detail = await batchErrors;
                if (batch.ExitCode != 0) throw new IOException("Git blob stream failed: " + detail);
            }
            finally { await GitSourceProcess.StopAndWaitAsync(batch); try { await batchErrors; } catch (OperationCanceledException) { } }
            var entriesBlob = await RetainFileAsync(entriesPath, cancellationToken);
            SourceBlobDescriptor? materializationEvidence = null;
            if (transport is not null)
            {
                var evidencePath = Path.Combine(staging, "materialization.json");
                await WriteNewJsonAsync(evidencePath, transport.Evidence(), CancellationToken.None);
                materializationEvidence = await RetainFileAsync(evidencePath, cancellationToken);
            }
            var coverage = new SourceCaptureCoverage(entriesCount, captured, missing, excluded, submodules, errors, associatedBytes, uniqueBytes, retained.Count, true);
            var complete = missing == 0 && errors == 0 && commands.All(c => c.ExitCode == 0);
            var manifest = new SourceCaptureManifest("filemapper.source-capture/1", requestId, repositoryId, request.ProviderStableId,
                "provider-identity-asserted-by-request", request.CanonicalUri, request.DisplayName, request.Commit, objectFormat, treeId, request.Ref,
                request.InventorySha256, started, DateTimeOffset.UtcNow, policy, CanonicalJson.Sha256(policy), rawTree, entriesBlob, coverage,
                complete ? "sealed-complete-for-policy" : "sealed-partial", commands, CollectorVersion, materializationEvidence);
            var captureId = CanonicalJson.Sha256(manifest); var directory = Path.Combine(RootDirectory, "captures"); HarvestFiles.CreateDirectory(directory);
            var manifestPath = Path.Combine(directory, captureId + ".json");
            await WriteNewJsonAsync(manifestPath, manifest, cancellationToken);
            var receipt = new SourceCaptureReceipt(captureId, manifestPath, manifest);
            await WriteNewJsonAsync(Path.Combine(staging, "sealed.json"), receipt, cancellationToken);
            progress?.Report(new { phase = "sealed", captureId, manifest.State, coverage });
            return receipt;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (transport is not null && !File.Exists(Path.Combine(staging, "materialization.json")))
                await WriteNewJsonAsync(Path.Combine(staging, "materialization.json"), transport.Evidence(), CancellationToken.None);
            await WriteNewJsonAsync(Path.Combine(staging, "failed.json"), new { state = cancellationToken.IsCancellationRequested ? "canceled" : "failed",
                requestId, started, finishedAt = DateTimeOffset.UtcNow, error = error.GetType().Name, detail = error.Message, commands }, CancellationToken.None);
            throw;
        }
        finally { transport?.Dispose(); }
    }

    public SourceCaptureReceipt Read(string captureId)
    {
        if (!HarvestFiles.IsHash(captureId)) throw new ArgumentException("A lowercase capture SHA-256 is required.");
        var path = Path.Combine(RootDirectory, "captures", captureId + ".json"); HarvestFiles.RequireOrdinaryPath(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Capture manifest exceeds its bound.");
        var manifest = JsonSerializer.Deserialize<SourceCaptureManifest>(File.ReadAllBytes(path), Json) ?? throw new IOException("Invalid capture manifest.");
        if (CanonicalJson.Sha256(manifest) != captureId) throw new IOException("Capture manifest hash mismatch.");
        return new(captureId, path, manifest);
    }

    public async IAsyncEnumerable<CapturedSourceEntry> EntriesAsync(string captureId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var manifest = Read(captureId).Manifest;
        var verification = await Blobs.VerifyAsync(manifest.Entries.Sha256, cancellationToken);
        if (verification.ActualSha256 != manifest.Entries.Sha256 || verification.BytesRead != manifest.Entries.ByteLength)
            throw new IOException("Capture entries manifest is missing or corrupt.");
        using var reader = new StreamReader(Blobs.OpenRead(manifest.Entries.Sha256), new UTF8Encoding(false, true));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            yield return JsonSerializer.Deserialize<CapturedSourceEntry>(line, Json) ?? throw new IOException("Invalid capture entry.");
    }

    internal async Task<SourceBlobDescriptor> RetainFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var result = await Blobs.PutAsync(source, expectedLength: source.Length, cancellationToken: cancellationToken);
        return result.Blob ?? throw new IOException("Manifest was not retained: " + result.Detail);
    }
    internal static async Task WriteNewJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        HarvestFiles.RequireOrdinaryPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        { await file.WriteAsync(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value)), cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(true); }
        HarvestFiles.RequireOrdinaryPath(path); File.Move(temporary, path, overwrite: false);
    }

    private static void Validate(GitHarvestRequest request)
    {
        if (!Uri.TryCreate(request.CanonicalUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not { Length: 2 } parts ||
            parts.Any(p => !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || request.CanonicalUri.EndsWith(".git", StringComparison.Ordinal) ||
            request.CanonicalUri != "https://github.com/" + string.Join('/', parts))
            throw new ArgumentException("Use the canonical HTTPS GitHub owner/repository URI without .git, credentials or query parameters.");
        if (string.IsNullOrEmpty(request.ProviderStableId) || !request.ProviderStableId.All(char.IsAsciiDigit)) throw new ArgumentException("GitHub repository numeric ID is required.");
        if (!HarvestFiles.IsHash(request.Commit, 40) && !HarvestFiles.IsHash(request.Commit)) throw new ArgumentException("A full lowercase Git commit object ID is required.");
        if (request.InventorySha256 is not null && !HarvestFiles.IsHash(request.InventorySha256)) throw new ArgumentException("Invalid inventory hash.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 512) throw new ArgumentException("Repository display name is required and bounded to 512 characters.");
        var policy = request.Policy ?? new();
        if (policy.MaximumBlobBytes is < 1 or > 64 * 1024 * 1024 || policy.MaximumCapturedBytes < policy.MaximumBlobBytes ||
            policy.MaximumTreeEntries is < 1 or > 2_000_000 || policy.FetchTimeoutSeconds is < 1 or > 7200 ||
            (policy.BoundedMaterialization is { } bounded && (!policy.MaterializeBlobs || bounded.Mode != "github-response-body" || bounded.MaximumResponseBodyBytes < 1))) throw new ArgumentException("Invalid capture resource policy.");
    }

    private static async IAsyncEnumerable<SourceTreeEntry> ReadTreeAsync(string path, string repositoryId, string revision, int maxEntries,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var entry = new MemoryStream(); var buffer = new byte[65536]; int count; long entries = 0;
        var utf8 = new UTF8Encoding(false, true);
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != 0)
                { if (entry.Length >= 1024 * 1024) throw new IOException("Git tree entry exceeds the path-byte bound."); entry.WriteByte(buffer[index]); continue; }
                if (++entries > maxEntries) throw new IOException("Git tree exceeds the declared entry limit; enumeration was not sealed complete.");
                var bytes = entry.ToArray(); entry.SetLength(0); var tab = Array.IndexOf(bytes, (byte)'\t');
                if (tab < 0) throw new IOException("Invalid NUL-delimited Git tree entry.");
                var header = Encoding.ASCII.GetString(bytes, 0, tab).Split(' ');
                if (header.Length != 3 || (!HarvestFiles.IsHash(header[2], 40) && !HarvestFiles.IsHash(header[2]))) throw new IOException("Invalid Git tree object identity.");
                var pathBytes = bytes[(tab + 1)..]; var encoding = "utf-8"; string display;
                try { display = utf8.GetString(pathBytes); }
                catch (DecoderFallbackException) { display = Encoding.UTF8.GetString(pathBytes); encoding = "opaque-bytes"; }
                var raw = Convert.ToBase64String(pathBytes);
                var entryId = "entry:" + CanonicalJson.Sha256(new { Schema = "filemapper.source-tree-entry/1", RepositoryId = repositoryId,
                    Revision = revision, PathBytes = raw, Mode = header[0], ObjectId = header[2] });
                yield return new(entryId, display, raw, encoding, header[0], header[1], header[2]);
            }
        }
        if (entry.Length != 0) throw new IOException("Git tree ended in a partial entry.");
    }

    private static async Task<byte[]> ReadBlobAsync(Process process, string id, long length, string format, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var cancellation = timeout.Token.Register(() => GitSourceProcess.Stop(process));
        await process.StandardInput.WriteLineAsync(id.AsMemory(), timeout.Token); await process.StandardInput.FlushAsync(timeout.Token);
        var stream = process.StandardOutput.BaseStream; var header = new List<byte>(); var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, timeout.Token); if (one[0] == '\n') break;
            if (header.Count == 255) throw new IOException("Invalid Git batch header."); header.Add(one[0]);
        }
        if (Encoding.ASCII.GetString(header.ToArray()) != id + " blob " + length.ToString(CultureInfo.InvariantCulture)) throw new IOException("Git blob identity or length changed during capture.");
        var bytes = new byte[checked((int)length)]; await stream.ReadExactlyAsync(bytes, timeout.Token);
        await stream.ReadExactlyAsync(one, timeout.Token); if (one[0] != '\n') throw new IOException("Git blob stream is not framed correctly.");
        using var hash = IncrementalHash.CreateHash(format == "sha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + length.ToString(CultureInfo.InvariantCulture) + "\0")); hash.AppendData(bytes);
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != id) throw new IOException("Git source blob does not match its pinned object ID.");
        return bytes;
    }

    private static string? LfsPointer(byte[] bytes)
    {
        if (bytes.Length > 1024) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { return null; }
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length < 3 || lines[0] != "version https://git-lfs.github.com/spec/v1") return null;
        var oid = lines.FirstOrDefault(line => line.StartsWith("oid sha256:", StringComparison.Ordinal))?[11..];
        var size = lines.FirstOrDefault(line => line.StartsWith("size ", StringComparison.Ordinal))?[5..];
        if (!HarvestFiles.IsHash(oid) || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0) return null;
        return "Git LFS pointer text retained. Declared external object sha256:" + oid + "; declared size " + length.ToString(CultureInfo.InvariantCulture)
            + " bytes. External object was not acquired or executed; source bytes and words describe only this pointer.";
    }
}
