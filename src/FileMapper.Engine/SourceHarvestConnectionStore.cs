using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Semantics;
using FileMapper.Storage;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

public sealed record SourceHarvestConnectionDetails(string WorkspacePath, string Mode, string? CompactIndexPath,
    string? CompactSha256, long? CompactByteLength, DateTimeOffset ConnectedAt);
public sealed record SourceHarvestConnection(string Schema, string ConnectionId, SourceHarvestConnectionDetails Connection);
public sealed record SourceHarvestSelectionInfo(string Origin, string Mode, string WorkspacePath, string IndexPath,
    bool IndexAvailableAtSelection, string? ConnectionId, DateTimeOffset? ConnectedAt, string? CompactSha256,
    long? CompactByteLength, bool ImmutableIndexPinned, string ApplicationPolicy);

/// <summary>A startup selection. Compact selections retain a Windows read-only sharing guard until disposed.</summary>
public sealed class SourceHarvestSelection : IDisposable
{
    private FileStream? guard;
    private int disposed;
    public SourceHarvestSelectionInfo Info { get; }
    public string WorkspacePath => Info.WorkspacePath;
    public string IndexPath => Info.IndexPath;
    public bool IndexExists { get { Alive(); return File.Exists(IndexPath); } }
    internal SourceHarvestSelection(SourceHarvestSelectionInfo info, FileStream? compactGuard) { Info = info; guard = compactGuard; }
    public ISourceHarvestReader CreateReader() { Alive(); return new SelectionReader(this); }
    private void Alive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private ISourceHarvestReader Reader()
    {
        Alive(); SourceHarvestConnectionStore.RequireOrdinaryPath(WorkspacePath);
        SourceHarvestConnectionStore.RequireOrdinaryPath(IndexPath);
        if (Info.Mode == "compact")
        {
            SourceHarvestConnectionStore.RequireNoCompactSidecars(IndexPath);
            return new CompactSourceHarvestIndex(IndexPath);
        }
        return new SourceHarvestIndex(IndexPath, readOnly: true);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref guard, null)?.Dispose();
    }
    private sealed class SelectionReader(SourceHarvestSelection owner) : ISourceHarvestReader
    {
        public string DatabasePath { get { owner.Alive(); return owner.IndexPath; } }
        public bool IsReadOnly => true;
        public SourceIndexPage<SourceIndexedCapture> ListCaptures(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
            => owner.Reader().ListCaptures(offset, limit, cancellationToken);
        public SourceIndexCounts Counts(string captureId, CancellationToken cancellationToken = default)
            => owner.Reader().Counts(captureId, cancellationToken);
        public SourceIndexPage<SourceIndexedDocument> Documents(string captureId, string? search = null, bool supportedOnly = false, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
            => owner.Reader().Documents(captureId, search, supportedOnly, offset, limit, cancellationToken);
        public SourceIndexPage<SourceTermCount> Terms(string captureId, string? search, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
            => owner.Reader().Terms(captureId, search, offset, limit, cancellationToken);
        public SourceIndexPage<SourceIndexedOccurrence> Occurrences(string captureId, string termId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
            => owner.Reader().Occurrences(captureId, termId, offset, limit, cancellationToken);
    }
}

/// <summary>Persists only an explicit source selection. It never initializes a corpus, source index, map database or learner.</summary>
public sealed class SourceHarvestConnectionStore
{
    public const string Schema = "filemapper.source-connection/1";
    public const string ConfigurationFileName = "source-connection.json";
    private const int MaximumConfigurationBytes = 64 * 1024;
    private const string ApplicationPolicy = "startup-only; explicit options replace the entire saved selection; ingestion reads may coordinate SQLite shared memory without SQL writes";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public string ServiceDataDirectory { get; }
    public string ConfigurationPath => Path.Combine(ServiceDataDirectory, ConfigurationFileName);

    /// <summary>Validates and normalizes an ordinary local filesystem path without creating anything.</summary>
    public static string NormalizePath(string path) => Normalize(path);

    public SourceHarvestConnectionStore(string existingServiceDataDirectory)
    {
        ServiceDataDirectory = Normalize(existingServiceDataDirectory);
        RequireDirectory(ServiceDataDirectory, "An existing service data directory is required.");
    }

    /// <summary>Reads saved configuration only, not the active service state. Missing configuration returns null without creating files.</summary>
    public async Task<SourceHarvestConnection?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); RequireDirectory(ServiceDataDirectory, "The service data directory is unavailable.");
        RequireOrdinaryPath(ConfigurationPath);
        if (Directory.Exists(ConfigurationPath)) throw new InvalidDataException("The source connection configuration must be a regular file.");
        FileStream stream;
        try { stream = new(ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        await using (stream)
        {
            if (stream.Length is <= 0 or > MaximumConfigurationBytes) throw new InvalidDataException("Source connection configuration exceeds its bounded format.");
            var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, cancellationToken);
            var value = JsonSerializer.Deserialize<SourceHarvestConnection>(bytes, Json) ?? throw new InvalidDataException("Invalid source connection configuration.");
            ValidateEnvelope(value);
            RejectControlIndex(value.Connection.CompactIndexPath ?? Path.Combine(value.Connection.WorkspacePath, "source-harvest.db"));
            if (!bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value))))
                throw new InvalidDataException("Source connection configuration must be exact canonical JSON without duplicate or unrecognized fields.");
            cancellationToken.ThrowIfCancellationRequested(); return value;
        }
    }

    public async Task<SourceHarvestConnection> ConnectAsync(string existingWorkspace, string? compactIndexPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = Normalize(existingWorkspace); var compact = compactIndexPath is null ? null : Normalize(compactIndexPath);
        RequireDirectory(ServiceDataDirectory, "The service data directory is unavailable.");
        RejectControlIndex(compact ?? Path.Combine(workspace, "source-harvest.db"));
        // Configuration writes cannot be redirected into the selected source corpus.
        if (SameOrChild(ServiceDataDirectory, workspace)) throw new ArgumentException("Service data must be outside the selected source corpus.");
        var lockPath = Path.Combine(ServiceDataDirectory, ".source-connection.writer.lock"); RequireOrdinaryPath(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var selection = await SelectAsync("explicit", workspace, compact, null, allowMissing: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var details = new SourceHarvestConnectionDetails(workspace, selection.Info.Mode, compact, selection.Info.CompactSha256,
            selection.Info.CompactByteLength, DateTimeOffset.UtcNow);
        var connection = new SourceHarvestConnection(Schema, Identity(details), details);
        var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(connection));
        if (bytes.Length > MaximumConfigurationBytes) throw new ArgumentException("Source connection configuration exceeds 64 KiB.");
        var temporary = Path.Combine(ServiceDataDirectory, ".source-connection." + Guid.NewGuid().ToString("N") + ".tmp");
        var published = false;
        try
        {
            RequireOrdinaryPath(temporary);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await file.WriteAsync(bytes, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(ServiceDataDirectory, "The service data directory is unavailable."); RequireOrdinaryPath(ConfigurationPath);
            if (Directory.Exists(ConfigurationPath)) throw new InvalidDataException("Source connection configuration cannot replace a directory.");
            cancellationToken.ThrowIfCancellationRequested();
            // The same-volume rename/replace is the commit point. Cancellation observed before it leaves the previous bytes intact.
            if (File.Exists(ConfigurationPath)) File.Replace(temporary, ConfigurationPath, destinationBackupFileName: null);
            else File.Move(temporary, ConfigurationPath, overwrite: false);
            published = true;
            return connection;
        }
        finally
        {
            // Only the exact generated staging file is eligible for cleanup; never enumerate or remove directories.
            if (!published)
            {
                RequireOrdinaryPath(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public async Task<SourceHarvestSelection> ResolveAsync(string? explicitWorkspace = null, string? explicitCompactIndex = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDirectory(ServiceDataDirectory, "The service data directory is unavailable.");
        if (explicitWorkspace is not null || explicitCompactIndex is not null)
        {
            var workspace = Normalize(explicitWorkspace ?? Path.Combine(ServiceDataDirectory, "sources"));
            var compact = explicitCompactIndex is null ? null : Normalize(explicitCompactIndex);
            RejectControlIndex(compact ?? Path.Combine(workspace, "source-harvest.db"));
            return await SelectAsync("explicit", workspace, compact, null, allowMissing: false, cancellationToken,
                compactWithoutWorkspace: explicitWorkspace is null && explicitCompactIndex is not null);
        }
        var saved = await LoadAsync(cancellationToken);
        if (saved is not null)
            return await SelectAsync("saved", saved.Connection.WorkspacePath, saved.Connection.CompactIndexPath, saved, allowMissing: false, cancellationToken);
        return await SelectAsync("default", Normalize(Path.Combine(ServiceDataDirectory, "sources")), null, null, allowMissing: true, cancellationToken);
    }

    private static async Task<SourceHarvestSelection> SelectAsync(string origin, string workspace, string? compact,
        SourceHarvestConnection? saved, bool allowMissing, CancellationToken ct, bool compactWithoutWorkspace = false)
    {
        ct.ThrowIfCancellationRequested(); RequireOrdinaryPath(workspace);
        var index = compact ?? Path.Combine(workspace, "source-harvest.db"); RequireOrdinaryPath(index);
        if (!Directory.Exists(workspace))
        {
            if (File.Exists(workspace)) throw new DirectoryNotFoundException("The selected source workspace is unavailable.");
            // The legacy explicit compact-only startup reads the index without requiring a raw corpus.
            // It must still reach all compact validation below; saved connections never take this branch.
            if (!(compactWithoutWorkspace && compact is not null))
            {
                if (!allowMissing) throw new DirectoryNotFoundException("The selected source workspace is unavailable.");
                return NewSelection(false, null, null, null);
            }
        }
        if (!File.Exists(index))
        {
            if (!allowMissing || Directory.Exists(index)) throw new FileNotFoundException("The selected source index is unavailable.", index);
            return NewSelection(false, null, null, null);
        }
        if (compact is null)
        {
            ValidateIngestion(index, ct);
            ct.ThrowIfCancellationRequested(); return NewSelection(true, null, null, null);
        }
        RequireNoCompactSidecars(index);
        FileStream? guard = new(index, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var header = new byte[100];
            if (guard.Length < header.Length) throw new InvalidDataException("A complete SQLite compact projection is required.");
            await guard.ReadExactlyAsync(header, ct);
            if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) || header[18] != 1 || header[19] != 1 ||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4)) != 0x53484332)
                throw new InvalidDataException("The compact projection must be a checkpointed, non-WAL FileMapper compact database.");
            guard.Position = 0; var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(guard, ct)); var length = guard.Length;
            if (saved is not null && (saved.Connection.CompactSha256 != hash || saved.Connection.CompactByteLength != length))
                throw new InvalidDataException("The saved compact projection differs from its pinned SHA-256 or byte length.");
            _ = new CompactSourceHarvestIndex(index); // Native format/version and completed publication state validation.
            RequireNoCompactSidecars(index); ct.ThrowIfCancellationRequested();
            var result = NewSelection(true, hash, length, guard); guard = null; return result;
        }
        finally { guard?.Dispose(); }

        SourceHarvestSelection NewSelection(bool available, string? hash, long? length, FileStream? held)
            => new(new(origin, compact is null ? "ingestion" : "compact", workspace, index, available, saved?.ConnectionId,
                saved?.Connection.ConnectedAt, hash, length, compact is not null, ApplicationPolicy), held);
    }

    private static void ValidateIngestion(string index, CancellationToken ct)
    {
        // A normal live WAL ingestion index remains supported. SQLite may coordinate its shared-memory files;
        // there are no migrations, checkpoints, SQL mutations or source-record writes here.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); var handle = db.Handle;
        using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => ct.IsCancellationRequested ? 1 : 0, null);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA query_only=ON; PRAGMA application_id;";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0x53484958)
                throw new InvalidDataException("The selected ingestion database is not a FileMapper source-harvest index.");
            command.CommandText = "SELECT version FROM sh_schema LIMIT 2";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != 1 || reader.Read()) throw new InvalidDataException("The source-harvest schema version is unsupported.");
            ct.ThrowIfCancellationRequested();
        }
        catch (SqliteException error) when (ct.IsCancellationRequested && error.SqliteErrorCode == 9)
        { throw new OperationCanceledException("Source connection validation was canceled.", error, ct); }
        finally { registration.Dispose(); SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
        _ = new SourceHarvestIndex(index, readOnly: true).ListCaptures(0, 1, ct);
    }

    private static string Identity(SourceHarvestConnectionDetails details) => CanonicalJson.Sha256(new { Schema, Connection = details });
    private void RejectControlIndex(string index)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetDirectoryName(index), ServiceDataDirectory, comparison) &&
            (Path.GetFileName(index).Equals(ConfigurationFileName, comparison) || Path.GetFileName(index).StartsWith(".source-connection.", comparison)))
            throw new ArgumentException("A source index cannot be a connection configuration, writer lock or staging file.");
    }
    private static void ValidateEnvelope(SourceHarvestConnection value)
    {
        if (value.Schema != Schema || value.Connection is null || value.ConnectionId != Identity(value.Connection))
            throw new InvalidDataException("Source connection schema or identity hash is invalid.");
        var c = value.Connection;
        if (c.WorkspacePath != Normalize(c.WorkspacePath) || c.ConnectedAt == default || c.ConnectedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Source connection paths or connection time are invalid.");
        if (c.Mode == "ingestion")
        {
            if (c.CompactIndexPath is not null || c.CompactSha256 is not null || c.CompactByteLength is not null)
                throw new InvalidDataException("An ingestion selection cannot contain a compact commitment.");
        }
        else if (c.Mode == "compact")
        {
            if (c.CompactIndexPath is null || c.CompactIndexPath != Normalize(c.CompactIndexPath) || c.CompactByteLength is < 100 or null ||
                c.CompactSha256 is null || c.CompactSha256.Length != 64 || c.CompactSha256.Any(x => !char.IsAsciiHexDigitLower(x)))
                throw new InvalidDataException("A compact selection requires normalized path, exact SHA-256 and byte length.");
        }
        else throw new InvalidDataException("Unsupported source connection mode.");
    }

    internal static void RequireNoCompactSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        { RequireOrdinaryPath(path + suffix); if (File.Exists(path + suffix) || Directory.Exists(path + suffix)) throw new InvalidDataException("A sealed compact projection cannot have SQLite sidecars."); }
    }
    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows() && (path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', path.Length >= 2 && path[1] == ':' ? 2 : 0) >= 0))
            throw new ArgumentException("Source connections require ordinary local filesystem paths without device prefixes or alternate streams.");
        // Win32/GetFullPath can erase trailing dots/spaces, so reject those aliases before normalization.
        if (OperatingSystem.IsWindows() && path.Split(['\\', '/']).Any(segment => segment is not ("." or "..") &&
            (segment.EndsWith(' ') || segment.EndsWith('.'))))
            throw new ArgumentException("Trailing-dot or trailing-space Windows path aliases are not supported for source connections.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); RequireOrdinaryPath(full); return full;
    }
    private static bool SameOrChild(string candidate, string parent)
        => candidate.Equals(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RequireDirectory(string path, string message)
    { RequireOrdinaryPath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException(message); }
    internal static void RequireOrdinaryPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            var name = Path.GetFileName(current);
            if (OperatingSystem.IsWindows() && (name.EndsWith(' ') || name.EndsWith('.') || name.Contains(':')))
                throw new ArgumentException("Ambiguous Windows path aliases are not supported for source connections.");
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source connections do not follow reparse points."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
