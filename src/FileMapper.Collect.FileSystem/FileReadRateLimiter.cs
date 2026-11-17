using System.Diagnostics;

namespace FileMapper.Collect.FileSystem;

/// <summary>A shared job budget and admission-rate limiter. Counts content bytes returned by reads, not device/cache traffic.</summary>
public sealed class FileReadRateLimiter
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long reserved, actual;
    private double nextSeconds;
    public FileReadRateLimiter(long maxBytesRead, long bytesPerSecond = 0)
    {
        if (maxBytesRead < 0 || maxBytesRead > 1L << 60 || bytesPerSecond < 0 || bytesPerSecond > 1L << 40)
            throw new ArgumentOutOfRangeException(nameof(maxBytesRead), "Read budget/rate is outside supported bounds.");
        MaximumBytes = maxBytesRead; BytesPerSecond = bytesPerSecond;
    }
    public long MaximumBytes { get; }
    public long BytesPerSecond { get; }
    /// <summary>Completed bytes plus outstanding read reservations; never exceeds MaximumBytes.</summary>
    public long ReservedBytes { get { lock (gate) return reserved; } }
    public long ActualBytes { get { lock (gate) return actual; } }
    public long OutstandingBytes { get { lock (gate) return reserved - actual; } }

    internal async ValueTask<Reservation> ReserveAsync(int requestedBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (requestedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requestedBytes));
        int granted; double eligible;
        lock (gate)
        {
            var available = MaximumBytes - reserved;
            if (available <= 0) throw new FileReadBudgetExceededException(MaximumBytes, reserved, actual, requestedBytes);
            // A bounded initial burst: at most 250 ms of quota (one byte at very low rates).
            var burst = BytesPerSecond == 0 ? requestedBytes : (int)Math.Min(requestedBytes, Math.Max(1, BytesPerSecond / 4));
            granted = (int)Math.Min(available, burst); reserved += granted;
            eligible = Math.Max(clock.Elapsed.TotalSeconds, nextSeconds);
            if (BytesPerSecond > 0) nextSeconds = eligible + (double)granted / BytesPerSecond;
        }
        var reservation = new Reservation(this, granted);
        try
        {
            if (BytesPerSecond > 0)
                while (eligible > clock.Elapsed.TotalSeconds)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(eligible - clock.Elapsed.TotalSeconds, 0, 1)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return reservation;
        }
        catch { reservation.Dispose(); throw; }
    }
    private void Complete(int count, int returned)
    { lock (gate) { reserved -= count - returned; actual += returned; } }
    internal sealed class Reservation(FileReadRateLimiter owner, int count) : IDisposable
    {
        private int completed;
        internal int Count { get; } = count;
        internal void Commit(int returned)
        {
            if (returned < 0 || returned > Count) throw new ArgumentOutOfRangeException(nameof(returned));
            if (Interlocked.Exchange(ref completed, 1) != 0) throw new InvalidOperationException("Read reservation was already completed.");
            owner.Complete(Count, returned);
        }
        public void Dispose() { if (Interlocked.Exchange(ref completed, 1) == 0) owner.Complete(Count, 0); }
    }
}

public sealed class FileReadBudgetExceededException(long maximumBytes, long reservedBytes, long actualBytes, int requestedBytes)
    : IOException("The shared content-read byte budget is exhausted; no further content read was issued.")
{
    public long MaximumBytes { get; } = maximumBytes;
    public long ReservedBytes { get; } = reservedBytes;
    public long ActualBytes { get; } = actualBytes;
    public int RequestedBytes { get; } = requestedBytes;
}

public sealed class FileContentReadException(string code, string message, int? nativeErrorCode = null, Exception? inner = null)
    : IOException(message, inner)
{
    public string Code { get; } = code;
    public int? NativeErrorCode { get; } = nativeErrorCode;
}
