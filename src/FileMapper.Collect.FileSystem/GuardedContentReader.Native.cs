using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FileMapper.Domain;
using Microsoft.Win32.SafeHandles;

namespace FileMapper.Collect.FileSystem;

internal sealed class GuardedContentFile : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SafeFileHandle> aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path, root;
    private readonly bool declaredPathsComplete;
    private FileStream? stream;
    internal FileStream Stream => stream ?? throw new ObjectDisposedException(nameof(GuardedContentFile));
    internal FileContentVersion Initial { get; }
    private sealed record Core(string Identity, string VolumeId, string FileSystem, string? NativeId,
        long Length, long? Allocation, long Created, long Modified, long Changed, uint Attributes, long Links, string? VolumeDevice);

    internal GuardedContentFile(FileContentReadRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new FileContentReadException("unsupported-platform", "Guarded native content reads currently require Windows file identities.");
        path = Normalize(request.Path); root = Normalize(request.BoundaryRoot);
        if (!Within(path, root)) throw new FileContentReadException("outside-boundary", "Content path is outside the declared boundary.");
        if (request.ExpectedLength < 0 || string.IsNullOrWhiteSpace(request.ExpectedIdentity)) throw new ArgumentException("Expected native identity and nonnegative length are required.");
        try
        {
            HoldAncestors(root, includeSelf: true, token); HoldAncestors(path, includeSelf: false, token);
            var handle = OpenFile(path, readData: true); stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            var primary = ReadCore(handle); CheckFile(handle, path);
            if (primary.Identity != request.ExpectedIdentity || primary.Length != request.ExpectedLength || Date(primary.Modified) != request.ExpectedModifiedAt)
                throw new FileContentReadException("baseline-mismatch", "Current native identity, length or modification time differs from the requested baseline.");
            var requested = request.ExpectedPaths ?? [path];
            declaredPathsComplete = request.PathsComplete && requested.Count is > 0 and <= 1024;
            if (declaredPathsComplete)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var supplied in requested)
                {
                    token.ThrowIfCancellationRequested(); var alias = Normalize(supplied);
                    if (!Within(alias, root) || !names.Add(alias)) throw new FileContentReadException("alias-list-invalid", "The complete alias list contains an out-of-boundary or duplicate location.");
                    if (alias.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                    HoldAncestors(alias, includeSelf: false, token); var aliasHandle = OpenFile(alias, readData: false);
                    try
                    {
                        CheckFile(aliasHandle, alias);
                        if (ReadCore(aliasHandle) != primary) throw new FileContentReadException("alias-version-mismatch", "A supplied alias differs in physical volume identity or current native file version.");
                        aliases.Add(alias, aliasHandle);
                    }
                    catch { aliasHandle.Dispose(); throw; }
                }
                if (!names.Contains(path)) throw new FileContentReadException("alias-list-invalid", "The declared complete alias list omits the primary location.");
            }
            Initial = Capture(token);
            if (Initial.Identity != request.ExpectedIdentity || Initial.Length != request.ExpectedLength || Initial.ModifiedAt != request.ExpectedModifiedAt)
                throw new FileContentReadException("baseline-mismatch", "Held native metadata changed during alias admission and no longer matches the requested baseline.");
            if (request.ExpectedVersionKey is not null && Initial.VersionKey != request.ExpectedVersionKey)
                throw new FileContentReadException("version-mismatch", "Current held-handle version differs from the expected read/cache version.");
        }
        catch { Dispose(); throw; }
    }

    internal FileContentVersion Capture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var handle = Stream.SafeFileHandle;
        foreach (var pair in directories) { token.ThrowIfCancellationRequested(); CheckDirectory(pair.Value, pair.Key); }
        CheckFile(handle, path); var core = ReadCore(handle); long verified = 1;
        foreach (var pair in aliases)
        {
            token.ThrowIfCancellationRequested(); CheckFile(pair.Value, pair.Key);
            if (ReadCore(pair.Value) != core) throw new FileContentReadException("alias-version-mismatch", "A held alias no longer has the primary physical identity and native version.");
            verified++;
        }
        var streams = AlternateStreams(handle);
        string? journal = null; long? usn = null;
        var reason = "unsupported-filesystem-or-volume-context";
        if (core.FileSystem == "NTFS" && core.VolumeDevice is not null && core.NativeId is not null)
        {
            var journalBefore = TryJournal(handle, out var before, out var beforeError);
            var fileRead = TryFileUsn(handle, core, out var fileUsn, out var fileError);
            // Retain independently available file USN as a mutation signal even when generation is unavailable.
            if (fileRead && fileUsn > 0) usn = fileUsn;
            var journalAfter = TryJournal(handle, out var after, out var afterError);
            if (journalBefore && fileRead && journalAfter && before.Id == after.Id && fileUsn > 0 && fileUsn >= after.LowestValid && fileUsn < after.Next)
            { journal = before.Id.ToString("x16", CultureInfo.InvariantCulture); reason = "ntfs-journal-and-file-usn-validated; reusable only under ordinary NTFS change tracking; not a fresh byte read or protection against mapped/raw/offline writes"; }
            else
            { var error = beforeError ?? fileError ?? afterError; reason = error is null ? "journal-version-unavailable-or-not-current" : "journal-version-unavailable:" + error; }
        }
        var eligible = journal is not null && usn.HasValue;
        // The version includes actual volume context and native 128-bit identity, not only the legacy NodeId.
        // Access time is deliberately excluded because this reader itself may change it.
        var key = Identity.For("content-version", FormattableString.Invariant($"native-content-version/v1|{core.Identity}|{core.VolumeId}|{core.FileSystem}|{core.NativeId}|{core.Length}|{core.Allocation}|{core.Created}|{core.Modified}|{core.Changed}|{core.Attributes}|{core.Links}|{journal}|{usn}|{streams}"));
        token.ThrowIfCancellationRequested();
        return new(core.Identity, core.VolumeId, core.FileSystem, core.Length, core.Allocation, Date(core.Created), Date(core.Modified),
            Date(core.Changed), ((FileAttributes)core.Attributes).ToString(), core.Links, journal, usn, key, eligible, reason,
            streams, verified, declaredPathsComplete && verified == core.Links);
    }

    private static DateTimeOffset Date(long fileTime)
    { try { return new(DateTime.FromFileTimeUtc(fileTime)); } catch (ArgumentOutOfRangeException e) { throw new FileContentReadException("invalid-native-time", "Native file time is outside its valid range.", inner: e); } }
    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith("\\\\", StringComparison.Ordinal) || value.IndexOf(':', value.Length >= 2 && value[1] == ':' ? 2 : 0) >= 0 ||
            value.Split(['\\', '/']).Any(part => part is not ("." or "..") && (part.EndsWith('.') || part.EndsWith(' '))))
            throw new FileContentReadException("unsupported-path", "Content reads require ordinary local paths without device/UNC prefixes, alternate streams or trailing-dot/space aliases.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (full.Length > 32700) throw new FileContentReadException("path-limit", "Native content path exceeds the bounded path length.");
        return full;
    }
    private static bool Within(string value, string boundary) => value.Equals(boundary, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string NativePath(string value) => "\\\\?\\" + value;
    private void HoldAncestors(string value, bool includeSelf, CancellationToken token)
    {
        var pending = new Stack<string>();
        for (var current = includeSelf ? value : Path.GetDirectoryName(value); current is not null; current = Path.GetDirectoryName(current))
        { if (pending.Count >= 256) throw new FileContentReadException("ancestor-limit", "Path exceeds the guarded ancestor depth."); pending.Push(current); }
        while (pending.TryPop(out var parent))
        {
            token.ThrowIfCancellationRequested(); if (directories.ContainsKey(parent)) continue;
            if (directories.Count >= 4096) throw new FileContentReadException("ancestor-limit", "Alias set exceeds the guarded ancestor handle bound.");
            var handle = CreateFileW(NativePath(parent), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = NativeError("ancestor-unavailable", "Cannot hold a content-read ancestor."); handle.Dispose(); throw error; }
            try { CheckDirectory(handle, parent); directories.Add(parent, handle); } catch { handle.Dispose(); throw; }
        }
    }
    private static SafeFileHandle OpenFile(string value, bool readData)
    {
        var handle = CreateFileW(NativePath(value), readData ? 0x80000000u : 0x80u, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = NativeError("file-unavailable", "Cannot acquire a read handle that excludes ordinary write/delete access."); handle.Dispose(); throw error; }
        return handle;
    }
    private static void CheckDirectory(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError("ancestor-unavailable", "Cannot revalidate held ancestor.");
        if ((info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0) throw new FileContentReadException("reparse-or-directory-change", "A content-read ancestor is not an ordinary directory.");
        CheckFinalPath(handle, path);
    }
    private static void CheckFile(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError("metadata-unavailable", "Cannot inspect held content identity.");
        if ((info.Attributes & (0x10 | 0x400)) != 0) throw new FileContentReadException("not-ordinary-file", "Directories and reparse points cannot be content-hashed by this reader.");
        if (!GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) throw NativeError("metadata-unavailable", "Cannot inspect held file status.");
        if (standard.DeletePending || standard.Directory) throw new FileContentReadException("file-changed", "The held file is delete-pending or no longer an ordinary file.");
        CheckFinalPath(handle, path);
    }
    private static void CheckFinalPath(SafeFileHandle handle, string expected)
    {
        var actual = FinalPath(handle, 0) ?? throw NativeError("path-unavailable", "Cannot resolve the held file's final native path.");
        if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
        if (!Path.TrimEndingDirectorySeparator(actual).Equals(Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new FileContentReadException("path-substitution", "The held file resolved through an alias or changed path.");
    }
    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(32768); var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
        return count == 0 || count >= buffer.Capacity ? null : buffer.ToString();
    }
    private static Core ReadCore(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || !GetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()) ||
            !GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) throw NativeError("metadata-unavailable", "Cannot obtain same-handle identity, size and native times.");
        var identity = $"ntfs:{info.VolumeSerial:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
        var native = new byte[24]; string? nativeId = null; ulong serial = info.VolumeSerial;
        if (GetBytes(handle, 18, native, (uint)native.Length)) { serial = BinaryPrimitives.ReadUInt64LittleEndian(native); nativeId = Convert.ToHexStringLower(native.AsSpan(8, 16)); }
        var guidPath = FinalPath(handle, 1); string? device = null;
        if (guidPath?.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) == true && guidPath.IndexOf('}') is var end && end > 10) device = guidPath[..(end + 1)];
        var volume = (device ?? FinalPath(handle, 2)?.Split('\\').Take(4).Aggregate((a, b) => a + "\\" + b) ?? "unknown-volume") + ":" + serial.ToString("x16", CultureInfo.InvariantCulture);
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) fs.Clear();
        long? allocation = standard.AllocationSize;
        if ((info.Attributes & (0x200u | 0x800u)) != 0)
        { if (GetCompression(handle, 8, out var compressed, (uint)Marshal.SizeOf<CompressionInfo>())) allocation = compressed.Size; else allocation = null; }
        return new(identity, volume.ToLowerInvariant(), fs.Length == 0 ? "unknown" : fs.ToString().ToUpperInvariant(), nativeId,
            standard.EndOfFile, allocation, basic.Created, basic.Written, basic.Changed, info.Attributes, standard.Links, device);
    }
    private static bool? AlternateStreams(SafeFileHandle handle)
    {
        for (var size = 4096; size <= 1024 * 1024; size *= 2)
        {
            var bytes = new byte[size];
            if (!GetBytes(handle, 7, bytes, (uint)bytes.Length))
            { if (Marshal.GetLastWin32Error() is 122 or 234) continue; return null; }
            int offset = 0;
            while (true)
            {
                if (offset < 0 || offset > bytes.Length - 24) return null;
                var next = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)); var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
                if ((nameBytes & 1) != 0 || nameBytes == 0 || nameBytes > bytes.Length - offset - 24) return null;
                var name = Encoding.Unicode.GetString(bytes, offset + 24, (int)nameBytes);
                if (name != "::$DATA") return true;
                if (next == 0) return false;
                if (next < 24 + nameBytes || next > bytes.Length - offset) return null; offset += (int)next;
            }
        }
        return null;
    }
    private sealed record Journal(ulong Id, long Next, long LowestValid);
    private static bool TryJournal(SafeFileHandle handle, out Journal value, out string? error)
    {
        value = new(0, 0, 0); error = null;
        // Canonical Microsoft examples use a volume handle. The held local NTFS filesystem handle
        // supports this query on tested Windows; probe that capability without reopening a device,
        // raising privileges or assuming support elsewhere. Failure keeps cache eligibility false.
        var bytes = new byte[80];
        if (!DeviceIoControl(handle, 0x000900f4, null, 0, bytes, (uint)bytes.Length, out var returned, IntPtr.Zero))
        { error = "journal-query:" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return false; }
        if (returned < 56 || returned > bytes.Length) { error = "journal-record-length"; return false; }
        value = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)));
        if (value.Id == 0 || value.LowestValid < 0 || value.Next < value.LowestValid) { error = "journal-record-bounds"; return false; }
        return true;
    }
    private static bool TryFileUsn(SafeFileHandle handle, Core core, out long usn, out string? error)
    {
        usn = 0; error = null; byte[] versions = [2, 0, 3, 0]; var bytes = new byte[4096];
        if (!DeviceIoControl(handle, 0x000900eb, versions, 4, bytes, (uint)bytes.Length, out var returned, IntPtr.Zero) || returned < 8)
        { error = "file-usn:" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return false; }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes); var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (length > returned) { error = "file-usn-record-length"; return false; }
        if (major == 2 && length >= 60)
        {
            var expected = core.Identity[(core.Identity.LastIndexOf(':') + 1)..];
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)).ToString("x16", CultureInfo.InvariantCulture) != expected) { error = "file-usn-identity"; return false; }
            usn = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)); return true;
        }
        if (major == 3 && length >= 76 && Convert.ToHexStringLower(bytes.AsSpan(8, 16)) == core.NativeId)
        { usn = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)); return true; }
        error = "file-usn-version-or-identity"; return false;
    }
    private static FileContentReadException NativeError(string code, string message)
    { var native = Marshal.GetLastWin32Error(); return new(code, message + " " + new Win32Exception(native).Message, native); }
    public void Dispose()
    { foreach (var alias in aliases.Values) alias.Dispose(); aliases.Clear(); stream?.Dispose(); stream = null; foreach (var directory in directories.Values.Reverse()) directory.Dispose(); directories.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint VolumeSerial, High, Low, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long Created, Accessed, Written, Changed; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { public long AllocationSize, EndOfFile; public uint Links; [MarshalAs(UnmanagedType.U1)] public bool DeletePending; [MarshalAs(UnmanagedType.U1)] public bool Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct CompressionInfo { public long Size; public ushort Format; public byte UnitShift, ChunkShift, ClusterShift, Reserved0, Reserved1, Reserved2; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCompression(SafeFileHandle handle, int kind, out CompressionInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBytes(SafeFileHandle handle, int kind, [Out] byte[] bytes, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? name, uint nameSize, out uint serial, out uint component, out uint flags, StringBuilder fs, uint fsSize);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
