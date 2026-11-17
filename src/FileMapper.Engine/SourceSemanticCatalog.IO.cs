using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using FileMapper.Semantics;

namespace FileMapper.Engine;

public sealed partial class SourceSemanticCatalog
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumRecordBytes = 16 * 1024 * 1024;
    public const long MaximumSealedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumRecords = 20_000_000;
    public const int MaximumPageBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };
    private static readonly HashSet<string> FixedArtifacts = new(StringComparer.Ordinal)
    { "documents.ndjson", "parsers.ndjson", "parser-documents.ndjson", "nodes.ndjson", "edges.ndjson", "observations.ndjson",
      "findings.ndjson", "objects.ndjson", "object-sources.ndjson", "source-spans.ndjson", "word-links.ndjson", "lexical-replays.ndjson", "document-joins.ndjson" };
    private static void HashId(string value)
    { if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("A lowercase SHA-256 identity is required."); }
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool ArtifactName(string name) => FixedArtifacts.Contains(name) || name.Length == 82 && name.EndsWith(".lexical.ndjson.gz", StringComparison.Ordinal) && name[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static byte[] Canonical<T>(T value) => Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value));
    private static T Parse<T>(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Null JSON record.");
        if (!bytes.AsSpan().SequenceEqual(Canonical(value))) throw new InvalidDataException("JSON is not the exact canonical native record (including its complete schema).");
        return value;
    }
    private static JsonElement ParseRecord(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !bytes.AsSpan().SequenceEqual(Canonical(doc.RootElement)))
            throw new InvalidDataException("Artifact record must be a canonical JSON object.");
        return doc.RootElement.Clone();
    }
    private static string Text(JsonElement value, string name)
    {
        var result = value.GetProperty(name).GetString();
        if (string.IsNullOrEmpty(result) || result.Length > 32768) throw new InvalidDataException("Missing or oversized " + name + ".");
        return result;
    }
    private static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    private static long Number(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static async Task<string> HashStreamAsync(Stream stream, CancellationToken token)
    { stream.Position = 0; var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)); stream.Position = 0; return hash; }
    private static string HashStream(Stream stream, CancellationToken token)
    {
        stream.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        int n; while ((n = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
        stream.Position = 0; token.ThrowIfCancellationRequested(); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static byte[] BoundedRead(Stream stream, int bound)
    {
        if (stream.Length < 0 || stream.Length > bound) throw new InvalidDataException("Catalog JSON admission byte limit exceeded.");
        stream.Position = 0; var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); stream.Position = 0; return bytes;
    }
    // One bounded line buffer, including a mandatory final LF. Offsets refer to the exact retained byte stream.
    private static async Task<(long Records, long Bytes, string Hash)> ScanAsync(Stream input, long maximumBytes,
        long maximumRecords, Action<byte[], long, long> consume, CancellationToken token, Stream? copy = null)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; var line = new ArrayBufferWriter<byte>(); long bytes = 0, count = 0, start = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (read > maximumBytes - bytes) throw new InvalidDataException("Catalog expanded-byte admission limit exceeded.");
            digest.AppendData(buffer, 0, read); if (copy is not null) await copy.WriteAsync(buffer.AsMemory(0, read), token);
            for (int i = 0, segment = 0; i < read; i++)
            {
                if (buffer[i] != 10 && i != read - 1) continue;
                var length = i - segment + (buffer[i] == 10 ? 0 : 1);
                if (length > MaximumRecordBytes - line.WrittenCount) throw new InvalidDataException("Catalog record exceeds 16 MiB admission limit.");
                line.Write(buffer.AsSpan(segment, length)); segment = i + 1;
                if (buffer[i] != 10) continue;
                token.ThrowIfCancellationRequested();
                if (line.WrittenCount == 0 || count >= maximumRecords) throw new InvalidDataException("Empty record or catalog record-count admission limit exceeded.");
                consume(line.WrittenSpan.ToArray(), start, count++); start = bytes + i + 1; line.Clear();
            }
            bytes += read;
        }
        if (line.WrittenCount != 0) throw new InvalidDataException("NDJSON ended without a complete line delimiter.");
        token.ThrowIfCancellationRequested(); return (count, bytes, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }
    private static SqliteConnection Connect(string path, bool writer)
    {
        SourceHarvestConnectionStore.RequireOrdinaryPath(path);
        using var identity = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
        if (identity is not null && OperatingSystem.IsWindows()) ValidateHandle(identity.SafeFileHandle, path, directory: false);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = writer ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { db.Open(); Execute(db, "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;" + (writer ? "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;" : "PRAGMA query_only=ON;")); return db; }
        catch { db.Dispose(); throw; }
    }
    private static void Execute(SqliteConnection db, string sql, params (string, object?)[] parameters)
    { using var command = Command(db, sql, parameters); command.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] parameters)
    { var command = db.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command; }
    private static long Scalar(SqliteConnection db, string sql, params (string, object?)[] parameters)
    { using var command = Command(db, sql, parameters); return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private sealed class CancellationScope : IDisposable
    {
        private readonly SQLitePCL.sqlite3 handle; private readonly CancellationToken token; private readonly CancellationTokenRegistration registration;
        internal CancellationScope(SqliteConnection db, CancellationToken token)
        {
            this.token = token; token.ThrowIfCancellationRequested(); handle = db.Handle!;
            if (token.CanBeCanceled) { SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null); registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle)); }
        }
        internal T Run<T>(Func<T> action)
        { try { token.ThrowIfCancellationRequested(); var result = action(); token.ThrowIfCancellationRequested(); return result; } catch (SqliteException e) when (e.SqliteErrorCode == 9 && token.IsCancellationRequested) { throw new OperationCanceledException("Semantic catalog query canceled.", e, token); } }
        public void Dispose() { registration.Dispose(); if (token.CanBeCanceled) SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
    }
    /// <summary>Windows handle identities prohibit symlink, junction, hard-link and short-name aliases; deny write/delete while held.</summary>
    private sealed class HeldDirectory : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal string Path { get; }
        internal HeldDirectory(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The alias-protected semantic catalog currently requires Windows native file identities.");
            Path = SourceHarvestConnectionStore.NormalizePath(path);
            try
            {
                for (string? current = Path; current is not null; current = System.IO.Path.GetDirectoryName(current))
                {
                    var handle = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot hold semantic catalog directory.", new Win32Exception(Marshal.GetLastWin32Error())); }
                    handles.Add(handle); ValidateHandle(handle, current, directory: true);
                }
            }
            catch { Dispose(); throw; }
        }
        internal FileStream Open(string name)
        {
            if (name.Length == 0 || name != System.IO.Path.GetFileName(name) || name.Contains(':')) throw new ArgumentException("An internal plain filename is required.");
            var path = System.IO.Path.Combine(Path, name); SourceHarvestConnectionStore.RequireOrdinaryPath(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try { ValidateHandle(stream.SafeFileHandle, path, directory: false); return stream; } catch { stream.Dispose(); throw; }
        }
        public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }
    private static void ValidateHandle(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot verify native file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & 0x400) != 0 || !directory && info.Links != 1 || ((info.Attributes & 0x10) != 0) != directory)
            throw new IOException("Catalog inputs must be ordinary files/directories without reparse points or hard-link aliases.");
        var text = new StringBuilder(32768); var size = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        if (size == 0 || size >= text.Capacity) throw new IOException("Cannot verify final native path.");
        var actual = text.ToString(); if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
        if (!System.IO.Path.TrimEndingDirectorySeparator(actual).Equals(System.IO.Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Catalog path is an alias or changed during admission.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, High, Low, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
