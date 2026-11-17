using System.Security.Cryptography;

namespace FileMapper.Harvest;

/// <summary>Raw-byte SHA-256 CAS. Paths are computed from validated hashes; existing objects are never overwritten.</summary>
public sealed class SourceBlobStore
{
    public string RootDirectory { get; }
    public long MaxDocumentBytes { get; }

    public SourceBlobStore(string rootDirectory, long maxDocumentBytes, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (maxDocumentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxDocumentBytes));
        if (readOnly && !Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException("An existing raw source object store is required.");
        RootDirectory = HarvestPaths.Directory(rootDirectory, create: !readOnly);
        MaxDocumentBytes = maxDocumentBytes;
        IsReadOnly = readOnly;
    }
    public bool IsReadOnly { get; }

    public async Task<SourceBlobPutResult> PutAsync(Stream source, string? expectedSha256 = null,
        long? expectedLength = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsReadOnly) throw new InvalidOperationException("Raw source object store is read-only.");
        if (!source.CanRead) throw new ArgumentException("Source must be readable.", nameof(source));
        if (expectedSha256 is not null) ValidateHash(expectedSha256);
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedLength > MaxDocumentBytes) return new("too-large", null, 0, "Declared bytes exceed the store's explicit document bound.");
        var staging = HarvestPaths.Directory(Path.Combine(RootDirectory, ".staging"), create: true);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".part");
        long count = 0;
        try
        {
            string hash;
            await using (var output = HarvestPaths.CreateNew(temporary))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    count = checked(count + read);
                    if (count > MaxDocumentBytes) return new("too-large", null, count, "Read stopped at the configured raw-byte bound; no blob was published.");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (expectedLength.HasValue && expectedLength != count) return new("length-mismatch", null, count, "Actual raw-byte length differs from the expected length.");
                hash = Convert.ToHexStringLower(digest.GetHashAndReset());
                if (expectedSha256 is not null && hash != expectedSha256) return new("hash-mismatch", null, count, "Actual raw-byte SHA-256 differs from the expected commitment.");
                await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(hash); var destination = Path.Combine(RootDirectory, relative);
            HarvestPaths.Directory(Path.GetDirectoryName(destination)!, create: true);
            if (File.Exists(destination)) return await Existing(hash, count, cancellationToken);
            try { HarvestPaths.Publish(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { return await Existing(hash, count, cancellationToken); }
            return new("stored", new(hash, count, relative.Replace('\\', '/')), count);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<SourceBlobPutResult> Existing(string hash, long length, CancellationToken token)
    {
        var verified = await VerifyAsync(hash, token);
        return verified.Status == "verified" && verified.Blob?.ByteLength == length
            ? new("existing", verified.Blob, length)
            : new("existing-object-conflict", null, length, "The existing content-addressed object failed verification; it was not overwritten.");
    }

    public FileStream OpenRead(string sha256)
    {
        ValidateHash(sha256);
        return HarvestPaths.OpenRead(Path.Combine(RootDirectory, Relative(sha256)));
    }

    public async Task<SourceBlobVerification> VerifyAsync(string sha256, CancellationToken cancellationToken = default)
    {
        ValidateHash(sha256); cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = OpenRead(sha256);
            if (stream.Length > MaxDocumentBytes) return new("too-large", null, null, 0, "Stored object exceeds the configured store verification bound.");
            var length = stream.Length;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            return hash == sha256
                ? new("verified", new(sha256, length, Relative(sha256).Replace('\\', '/')), hash, stream.Position)
                : new("hash-mismatch", null, hash, stream.Position, "Stored raw bytes differ from their content-addressed name.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("unavailable", null, null, 0, error.Message); }
    }

    internal static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase 64-character SHA-256 is required.", nameof(hash));
    }
    private static string Relative(string hash) => Path.Combine("objects", "sha256", hash[..2], hash);
}

internal static class HarvestPaths
{
    // Check every existing ancestor, not just the final file. No user-derived name is appended to a CAS path.
    internal static string Directory(string path, bool create)
    {
        var full = Full(path);
        var chain = new Stack<string>();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
        while (chain.TryPop(out var current))
        {
            if (System.IO.Directory.Exists(current)) RejectReparse(current);
            else
            {
                if (File.Exists(current)) throw new IOException("A directory ancestor is a file.");
                if (!create) throw new DirectoryNotFoundException(current);
                System.IO.Directory.CreateDirectory(current); RejectReparse(current);
            }
        }
        return full;
    }
    internal static string Full(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Split(['/', '\\']).Any(part => part == "..")) throw new ArgumentException("Parent traversal is not accepted in harvest storage paths.", nameof(path));
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new ArgumentException("Alternate-stream paths are not accepted.", nameof(path));
        return full;
    }
    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point harvest paths are not accepted.");
    }
    internal static FileStream CreateNew(string path)
    {
        var full = Full(path); Directory(Path.GetDirectoryName(full)!, create: false);
        if (File.Exists(full) || System.IO.Directory.Exists(full)) throw new IOException("Immutable output already exists.");
        var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { Directory(Path.GetDirectoryName(full)!, create: false); RejectReparse(full); return stream; }
        catch { stream.Dispose(); throw; }
    }
    internal static FileStream OpenRead(string path)
    {
        var full = Full(path); Directory(Path.GetDirectoryName(full)!, create: false); RejectReparse(full);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { Directory(Path.GetDirectoryName(full)!, create: false); RejectReparse(full); return stream; }
        catch { stream.Dispose(); throw; }
    }
    internal static void Publish(string temporary, string destination)
    {
        temporary = Full(temporary); destination = Full(destination);
        Directory(Path.GetDirectoryName(temporary)!, create: false); RejectReparse(temporary);
        Directory(Path.GetDirectoryName(destination)!, create: false);
        if (File.Exists(destination) || System.IO.Directory.Exists(destination)) throw new IOException("Immutable output already exists.");
        File.Move(temporary, destination, overwrite: false);
    }
}
