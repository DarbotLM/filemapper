using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FileMapper.Domain;

namespace FileMapper.Collect.FileSystem;

/// <summary>Bounded native reads of the unnamed stream. No file, journal, snapshot or learner writes.</summary>
public static class GuardedContentReader
{
    public static GuardedContentReadLease Open(FileContentReadRequest request, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); return new(new GuardedContentFile(request, cancellationToken)); }
}

/// <summary>A single-reader lease. Ordinary writers/delete/rename are excluded while handles are held.
/// Journal evidence is a filesystem-version observation, not a fresh content read, atomic snapshot,
/// or protection against pre-existing writable mappings, raw-volume writes or offline changes.</summary>
public sealed class GuardedContentReadLease : IDisposable, IAsyncDisposable
{
    private const int BufferBytes = 131072;
    private readonly GuardedContentFile file;
    private long bytesRead;
    private int busy, disposed;
    internal GuardedContentReadLease(GuardedContentFile file) { this.file = file; Before = file.Initial; }
    public FileContentVersion Before { get; }
    /// <summary>Actual bytes returned by content reads over this lease, including repeated reads across stages; excludes sample framing and metadata reads.</summary>
    public long BytesRead => Interlocked.Read(ref bytesRead);
    public FileVersionValidation Revalidate(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); cancellationToken.ThrowIfCancellationRequested();
        var current = file.Capture(cancellationToken);
        var stable = current.VersionKey == Before.VersionKey && current.ValidatedPathCount == Before.ValidatedPathCount && current.PathsComplete == Before.PathsComplete;
        return new(stable, current, stable ? null : "Held native identity, size/time, allocation/stream/alias metadata or journal-version evidence changed.");
    }

    public async Task<FileReadReceipt> SampleAsync(FileSamplePolicy policy, FileReadRateLimiter? limiter = null,
        IProgress<FileReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy); policy.Validate(); Enter(); var token = cancellationToken;
        var started = DateTimeOffset.UtcNow; var initialBytes = BytesRead; const string stage = "sample";
        try
        {
            var validation = Revalidate(token); if (!validation.IsStable) return Changed(stage, policy.PolicyHash, started, initialBytes, validation.Current, validation.Reason);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Exact portable framing: UTF8(prefix + policyHash + NUL), int64LE source length,
            // int32LE merged range count; for each sorted range int64LE offset/length then raw bytes.
            digest.AppendData(Encoding.UTF8.GetBytes("filemapper.sample/v1\0" + policy.PolicyHash + "\0"));
            var ranges = SampleRanges(Before.Length, policy.WindowBytes); var framing = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(framing, Before.Length); digest.AppendData(framing, 0, 8);
            BinaryPrimitives.WriteInt32LittleEndian(framing, ranges.Count); digest.AppendData(framing, 0, 4);
            var buffer = new byte[BufferBytes];
            foreach (var (offset, length) in ranges)
            {
                token.ThrowIfCancellationRequested(); BinaryPrimitives.WriteInt64LittleEndian(framing, offset); BinaryPrimitives.WriteInt64LittleEndian(framing.AsSpan(8), length); digest.AppendData(framing);
                file.Stream.Position = offset; long remaining = length;
                while (remaining > 0)
                {
                    var count = await ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), limiter, token).ConfigureAwait(false);
                    if (count == 0) throw new FileContentReadException("changed-during-read", "The sampled range ended before its observed extent.");
                    digest.AppendData(buffer, 0, count); remaining -= count; progress?.Report(new(stage, BytesRead - initialBytes, Before.Length));
                }
            }
            var after = Revalidate(token); if (!after.IsStable) return Changed(stage, policy.PolicyHash, started, initialBytes, after.Current, after.Reason);
            progress?.Report(new(stage, BytesRead - initialBytes, Before.Length));
            return new(stage, policy.PolicyHash, Before, after.Current, started, DateTimeOffset.UtcNow, BytesRead - initialBytes,
                Convert.ToHexStringLower(digest.GetHashAndReset()), 0, 0, false);
        }
        catch (FileContentReadException error) { return Failed(stage, policy.PolicyHash, started, initialBytes, error); }
        finally { Exit(); }
    }

    public async Task<FileReadReceipt> FullAsync(FileChunkPolicy? chunks = null,
        Func<IReadOnlyList<FileContentChunk>, CancellationToken, ValueTask>? sink = null,
        FileReadRateLimiter? limiter = null, IProgress<FileReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        chunks?.Validate();
        if (chunks is not null && sink is null) throw new ArgumentException("CDC requires a bounded provisional chunk sink.", nameof(sink));
        if (chunks is null && sink is not null) throw new ArgumentException("A chunk sink requires an explicit CDC policy.", nameof(chunks));
        Enter(); var token = cancellationToken; var started = DateTimeOffset.UtcNow; var initialBytes = BytesRead;
        const string stage = "full";
        var policyHash = chunks?.PolicyHash ?? Identity.For("full-policy", "sha256-unnamed-stream/v1");
        long chunkCount = 0, chunkBytes = 0;
        try
        {
            var validation = Revalidate(token); if (!validation.IsStable) return Changed(stage, policyHash, started, initialBytes, validation.Current, validation.Reason);
            file.Stream.Position = 0; var buffer = new byte[BufferBytes];
            using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var chunkDigest = chunks is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var pending = new List<FileContentChunk>(128); ulong gear = 0; int chunkLength = 0; long offset = 0, consumed = 0;
            var chunksTruncated = false;
            async ValueTask Flush()
            { if (pending.Count == 0) return; await sink!(pending.ToArray(), token).ConfigureAwait(false); pending.Clear(); }
            async ValueTask FinishChunk()
            {
                pending.Add(new(checked((int)chunkCount), offset, chunkLength, Convert.ToHexStringLower(chunkDigest!.GetHashAndReset())));
                chunkCount++; chunkBytes += chunkLength; offset += chunkLength; chunkLength = 0; gear = 0;
                if (pending.Count == 128) await Flush().ConfigureAwait(false);
            }
            while (consumed < Before.Length)
            {
                var count = await ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Before.Length - consumed)), limiter, token).ConfigureAwait(false);
                if (count == 0) throw new FileContentReadException("changed-during-read", "The full stream ended before its observed length.");
                full.AppendData(buffer, 0, count);
                if (chunks is not null && !chunksTruncated)
                {
                    var segment = 0;
                    for (var index = 0; index < count; index++)
                    {
                        if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                        gear = unchecked((gear << 1) + GearTable.Values[buffer[index]]); chunkLength++;
                        if (chunkLength < chunks.MinimumBytes || chunkLength < chunks.MaximumBytes && (gear & (ulong)(chunks.AverageBytes - 1)) != 0) continue;
                        chunkDigest!.AppendData(buffer, segment, index - segment + 1); segment = index + 1;
                        await FinishChunk().ConfigureAwait(false);
                        if (chunkCount >= chunks.MaxChunks && consumed + index + 1 < Before.Length)
                        {
                            chunksTruncated = true; await Flush().ConfigureAwait(false); break;
                        }
                    }
                    if (!chunksTruncated && segment < count) chunkDigest!.AppendData(buffer, segment, count - segment);
                }
                consumed += count; progress?.Report(new(stage, BytesRead - initialBytes, Before.Length));
            }
            if (chunks is not null)
            {
                if (!chunksTruncated && chunkLength > 0) await FinishChunk().ConfigureAwait(false);
                await Flush().ConfigureAwait(false);
            }
            var after = Revalidate(token); if (!after.IsStable) return Changed(stage, policyHash, started, initialBytes, after.Current, after.Reason, chunkCount, chunkBytes);
            progress?.Report(new(stage, BytesRead - initialBytes, Before.Length));
            return new(stage, policyHash, Before, after.Current, started, DateTimeOffset.UtcNow, BytesRead - initialBytes,
                Convert.ToHexStringLower(full.GetHashAndReset()), chunkCount, chunkBytes, chunks is not null && !chunksTruncated,
                Error: chunksTruncated ? "The per-file chunk cap was reached; the full digest is complete but retained chunks cover only an explicit prefix." : null);
        }
        catch (FileContentReadException error) { return Failed(stage, policyHash, started, initialBytes, error, chunkCount, chunkBytes); }
        finally { Exit(); }
    }
    private async ValueTask<int> ReadAsync(Memory<byte> buffer, FileReadRateLimiter? limiter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var reservation = limiter is null ? null : await limiter.ReserveAsync(buffer.Length, token).ConfigureAwait(false);
        int count;
        // Once a bounded read is issued, observe its byte count before honoring cancellation.
        // This avoids dropping completed bytes from the budget. Cancellation cannot preempt a blocked native read.
        try { count = await file.Stream.ReadAsync(buffer[..(reservation?.Count ?? buffer.Length)], CancellationToken.None).ConfigureAwait(false); }
        catch (IOException error) { throw new FileContentReadException("read-error", "Native content read failed: " + error.Message, inner: error); }
        Interlocked.Add(ref bytesRead, count); reservation?.Commit(count); token.ThrowIfCancellationRequested(); return count;
    }
    private FileReadReceipt Changed(string stage, string policy, DateTimeOffset started, long initialBytes, FileContentVersion? after,
        string? reason, long chunks = 0, long chunkBytes = 0) => new(stage, policy, Before, after, started, DateTimeOffset.UtcNow,
            BytesRead - initialBytes, null, chunks, chunkBytes, false, "changed", reason);
    private FileReadReceipt Failed(string stage, string policy, DateTimeOffset started, long initialBytes, FileContentReadException error,
        long chunks = 0, long chunkBytes = 0) => new(stage, policy, Before, null, started, DateTimeOffset.UtcNow,
            BytesRead - initialBytes, null, chunks, chunkBytes, false, error.Code, error.Message);
    private static List<(long Offset, long Length)> SampleRanges(long length, int window)
    {
        if (length == 0) return [];
        var size = Math.Min(length, window); var starts = new[] { 0L, (length - size) / 2, length - size };
        var result = new List<(long Offset, long Length)>();
        foreach (var start in starts)
        {
            if (result.Count > 0 && start <= result[^1].Offset + result[^1].Length)
            { var prior = result[^1]; result[^1] = (prior.Offset, Math.Max(prior.Offset + prior.Length, start + size) - prior.Offset); }
            else result.Add((start, size));
        }
        return result;
    }
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("A guarded lease supports one content operation at a time.");
    }
    private void Exit() => Volatile.Write(ref busy, 0);
    public void Dispose()
    {
        if (Volatile.Read(ref busy) != 0) throw new InvalidOperationException("Await the current content operation before disposing its lease.");
        if (Interlocked.Exchange(ref disposed, 1) == 0) file.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    private static class GearTable
    {
        // Pinned table derivation for gear64-sha256-cdc/v1. Rolling state resets after each cut;
        // cut after minimum when low log2(average) bits are zero, or force at maximum. Final tail is retained.
        internal static readonly ulong[] Values = Enumerable.Range(0, 256).Select(value =>
            BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes("filemapper.gear64/1/" + value.ToString(CultureInfo.InvariantCulture))))).ToArray();
    }
}
