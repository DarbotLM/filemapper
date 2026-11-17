using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileMapper.Actions.Robocopy;

public static class PathSafety
{
    public static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32700 || path.Any(char.IsControl) || path.IndexOfAny(['"', '%', '*', '?']) >= 0)
            throw new ArgumentException("Endpoints must be literal absolute directory paths, without expansion, wildcards, quotes or controls.");
        path = path.Replace('/', '\\');
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal) || path.StartsWith("\\??\\", StringComparison.Ordinal)) throw new ArgumentException("Device/extended namespace paths are not accepted.");
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var unc = path.StartsWith("\\\\", StringComparison.Ordinal) && path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        if (!drive && !unc || path[(drive ? 2 : 0)..].Contains(':')) throw new ArgumentException("Endpoints must be drive-absolute or UNC directories; alternate streams and drive-relative paths are rejected.");
        foreach (var segment in path[(drive ? 3 : 2)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')) throw new ArgumentException("Ambiguous dot/trailing-dot/trailing-space path components are rejected.");
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }
    public static bool IsRoot(string path) => string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), StringComparison.OrdinalIgnoreCase);
    public static bool Contains(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);
    internal static void RejectReparseAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse endpoint/ancestor is not allowed: {current}");
        }
    }
    internal static EndpointIdentity Probe(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Robocopy actions require Windows.");
        path = CanonicalDirectory(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("This policy requires an existing endpoint directory: " + path);
        RejectReparseAncestors(path);
        using var h = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(h, final, final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = final.ToString();
        if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) resolved = "\\\\" + resolved[8..];
        else if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal)) resolved = resolved[4..];
        var fs = new StringBuilder(128);
        if (!GetVolumeInformationByHandleW(h, null, 0, out _, out _, out var flags, fs, fs.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(path, CanonicalDirectory(resolved), info.VolumeSerial.ToString("x8"), ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow).ToString("x16"), fs.ToString(), flags);
    }
    internal static FileStream OpenOrdinaryFile(string path, out string identity, out uint links)
    {
        var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero); // OPEN_REPARSE_POINT, SEQUENTIAL_SCAN, share read only
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open ordinary stream: " + path);
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0) throw new IOException("Queued file became a reparse point or directory: " + path);
            var final = new StringBuilder(32768); var count = GetFinalPathNameByHandleW(handle, final, final.Capacity, 0);
            if (count == 0 || count >= final.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            var actual = final.ToString(); if (actual.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) actual = "\\\\" + actual[8..]; else if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
            if (!actual.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Resolved file path differs from its validated name: " + path);
            identity = info.VolumeSerial.ToString("x8") + ":" + ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow).ToString("x16"); links = info.Links;
            return new FileStream(handle, FileAccess.Read, 65536, false);
        }
        catch { handle.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volume, int volumeLength, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, int fsLength);
}
