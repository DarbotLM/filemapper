using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

/// <summary>One cancellation scope for an exclusively owned, open SQLite connection.
/// Dispose this scope before its connection; do not nest scopes on the same connection.</summary>
internal sealed class SourceSqliteCancellation : IDisposable
{
    private readonly SQLitePCL.sqlite3 handle;
    private readonly CancellationToken token;
    private CancellationTokenRegistration registration;
    private bool progressInstalled;
    private int disposed;

    public SourceSqliteCancellation(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        token = cancellationToken;
        token.ThrowIfCancellationRequested();
        handle = connection.Handle ?? throw new InvalidOperationException("SQLite cancellation requires an open connection.");
        if (!token.CanBeCanceled) return;
        try
        {
            // sqlite3_interrupt covers a statement already executing; the progress handler also
            // covers cancellation between statements, when an interrupt alone would have no effect.
            SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
            progressInstalled = true;
            registration = token.Register(static state => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)state!), handle);
            token.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }

    public T Run<T>(Func<T> action)
    {
        token.ThrowIfCancellationRequested();
        try { var value = action(); token.ThrowIfCancellationRequested(); return value; }
        catch (Exception error)
        {
            var translated = Translate(error, token);
            if (!ReferenceEquals(error, translated)) throw translated;
            throw;
        }
    }

    public void Run(Action action) => Run(() => { action(); return true; });

    public async Task RunAsync(Func<Task> action)
    {
        token.ThrowIfCancellationRequested();
        try { await action().ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            var translated = Translate(error, token);
            if (!ReferenceEquals(error, translated)) throw translated;
            throw;
        }
    }

    public static Exception Translate(Exception error, CancellationToken cancellationToken) =>
        error is SqliteException { SqliteErrorCode: 9 } && cancellationToken.IsCancellationRequested
            ? new OperationCanceledException("The source index operation was canceled.", error, cancellationToken)
            : error;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Dispose waits for a running interrupt callback. Only then clear the native callback;
        // the caller can subsequently close or pool the connection without a stale token.
        registration.Dispose();
        if (progressInstalled)
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null);
            progressInstalled = false;
        }
    }
}
