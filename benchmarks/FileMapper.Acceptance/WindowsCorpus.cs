using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FileMapper.Domain;
using FileMapper.Engine;
using FileMapper.Storage;
using Microsoft.Win32.SafeHandles;

namespace FileMapper.Acceptance;

internal static class WindowsCorpus
{
    public static Task Run(string directory, AcceptanceReport report, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native filesystem corpus requires Windows/NTFS.");
        return RunWindows(directory, report, token);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindows(string directory, AcceptanceReport report, CancellationToken token)
    {
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new PlatformNotSupportedException("Golden corpus requires NTFS.");
        var store = new MapStore(Path.Combine(directory, "filesystem.db")); store.Initialize();
        await using var scans = new ScanCoordinator(store);
        async Task<Snapshot> Scan(string root, string policy = "skip", string[]? excludes = null)
        {
            var job = scans.StartScan(new(root, policy, Excludes: excludes, HashFiles: true));
            using var stop = token.Register(() => scans.Cancel(job.Id));
            job = await scans.WaitAsync(job.Id).WaitAsync(TimeSpan.FromMinutes(3), token);
            if (job.Status != "completed") throw new InvalidOperationException("Fixture scan did not complete: " + job.Error);
            return store.GetSnapshot(job.SnapshotId)!;
        }
        // The direct inside-data path is excluded by name. The selected inside-link path is
        // deliberately included: filters describe observed paths, not a canonical-target denylist.
        var policyRoot = Path.Combine(directory, "policy-root");
        var inside = Path.Combine(policyRoot, "inside-data");
        var outside = Path.Combine(directory, "outside-data");
        Directory.CreateDirectory(inside); Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(policyRoot, "ordinary.bin"), [1, 2, 3], token);
        await File.WriteAllBytesAsync(Path.Combine(inside, "inside.bin"), [1, 2, 3, 4], token);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.bin"), [1, 2, 3, 4, 5, 6, 7], token);
        Junction(Path.Combine(policyRoot, "inside-link"), inside);
        Junction(Path.Combine(policyRoot, "outside-link"), outside);
        Junction(Path.Combine(policyRoot, "back-link"), policyRoot);
        var policyResults = new List<object>();
        foreach (var policy in new[] { "skip", "reference", "within", "all" })
        {
            var snapshot = await Scan(policyRoot, policy, ["inside-data"]);
            var entries = store.StreamEntries(snapshot.Id).ToArray();
            var insideObserved = entries.Any(e => e.Name == "inside.bin");
            var outsideObserved = entries.Any(e => e.Name == "outside.bin");
            var expectedBytes = policy == "all" ? 14 : policy == "within" ? 7 : 3;
            var expectedFiles = policy == "all" ? 3 : policy == "within" ? 2 : 1;
            report.Check("policy-" + policy, insideObserved == (policy is "within" or "all") && outsideObserved == (policy == "all")
                && snapshot.LogicalBytes == expectedBytes && snapshot.FileCount == expectedFiles && snapshot.EntryCount <= 7,
                $"snapshot={snapshot.Id}; inside={insideObserved}; outside={outsideObserved}; logical={snapshot.LogicalBytes}; files={snapshot.FileCount}; entries={snapshot.EntryCount}");
            policyResults.Add(new { policy, snapshot, insideObserved, outsideObserved });
        }
        report.Measurements["policyCases"] = policyResults;

        var links = Path.Combine(directory, "hardlinks");
        Directory.CreateDirectory(links);
        var primary = Path.Combine(links, "primary.bin");
        await File.WriteAllBytesAsync(primary, Enumerable.Range(0, 8193).Select(i => (byte)(i * 29)).ToArray(), token);
        var before = await Scan(links);
        if (!CreateHardLinkW(Path.Combine(links, "alias.bin"), primary, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var after = await Scan(links);
        report.Check("hardlink-identity-and-allocation", before.FileCount == 1 && after.FileCount == 1
            && after.EntryCount == before.EntryCount + 1 && before.LogicalBytes == after.LogicalBytes
            && before.AllocatedBytes.HasValue && before.AllocatedBytes == after.AllocatedBytes,
            $"before={before.Id}; after={after.Id}; entries={before.EntryCount}->{after.EntryCount}; logical={after.LogicalBytes}; allocation={after.AllocatedBytes}");
        await File.WriteAllBytesAsync(primary + ":metadata", Encoding.UTF8.GetBytes("named-stream-evidence"), token);
        var streams = await Scan(links);
        var streamEntries = store.StreamEntries(streams.Id).Where(e => e.Kind == "Stream").ToArray();
        report.Check("ads-alias-deduplication", streams.LogicalBytes == 8193 + Encoding.UTF8.GetByteCount("named-stream-evidence")
            && streamEntries.Length == 2 && streamEntries.Select(e => e.NodeId).Distinct().Count() == 1,
            $"snapshot={streams.Id}; logical={streams.LogicalBytes}; streamPaths={streamEntries.Length}; streamIdentities={streamEntries.Select(e => e.NodeId).Distinct().Count()}");

        var attributes = Path.Combine(directory, "attributes");
        Directory.CreateDirectory(attributes);
        var sparsePath = Path.Combine(attributes, "sparse.bin");
        using (var sparse = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            Control(sparse.SafeFileHandle, 0x000900c4, null); // FSCTL_SET_SPARSE
            sparse.SetLength(16 * 1024 * 1024);
            sparse.Position = sparse.Length - 1; sparse.WriteByte(7); sparse.Flush(flushToDisk: true);
        }
        var compressedPath = Path.Combine(attributes, "compressed.bin");
        using (var compressed = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            Control(compressed.SafeFileHandle, 0x0009c040, [1, 0]); // FSCTL_SET_COMPRESSION / DEFAULT
            compressed.Write(new byte[1024 * 1024]); compressed.Flush(flushToDisk: true);
        }
        var attributeSnapshot = await Scan(attributes);
        var attributeEntries = store.StreamEntries(attributeSnapshot.Id).Where(e => e.Kind == "File").ToArray();
        foreach (var name in new[] { "sparse.bin", "compressed.bin" })
        {
            var entry = attributeEntries.Single(e => e.Name == name);
            report.Check(name, entry.AllocatedBytes.HasValue && entry.AllocatedBytes < entry.LogicalBytes
                && (name != "sparse.bin" || entry.AllocatedBytes > 0)
                && entry.Attributes.Contains(name.StartsWith("sparse", StringComparison.Ordinal) ? "SparseFile" : "Compressed", StringComparison.Ordinal),
                $"snapshot={attributeSnapshot.Id}; logical={entry.LogicalBytes}; allocated={entry.AllocatedBytes}; attributes={entry.Attributes}");
        }

        var longRoot = Path.Combine(directory, "long-path");
        var deepest = longRoot;
        for (var i = 0; i < 8; i++) deepest = Path.Combine(deepest, new string((char)('a' + i), 40));
        Directory.CreateDirectory(deepest);
        var longFile = Path.Combine(deepest, "payload.bin");
        await File.WriteAllBytesAsync(longFile, [8, 9], token);
        var longSnapshot = await Scan(longRoot);
        report.Check("long-path", longFile.Length > 260 && longSnapshot.FileCount == 1 && longSnapshot.LogicalBytes == 2 && longSnapshot.ErrorCount == 0,
            $"snapshot={longSnapshot.Id}; length={longFile.Length}; files={longSnapshot.FileCount}; errors={longSnapshot.ErrorCount}");

        var denialRoot = Path.Combine(directory, "denial");
        var denied = Path.Combine(denialRoot, "denied");
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(denied, "unobserved.txt"), "not a deletion", token);
        var deniedInfo = new DirectoryInfo(denied);
        var original = deniedInfo.GetAccessControl();
        try
        {
            var security = deniedInfo.GetAccessControl();
            using var current = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            deniedInfo.SetAccessControl(security);
            var deniedSnapshot = await Scan(denialRoot);
            report.Check("access-denied-is-incomplete", deniedSnapshot.ErrorCount > 0 && deniedSnapshot.Completeness != "complete",
                $"snapshot={deniedSnapshot.Id}; completeness={deniedSnapshot.Completeness}; errors={deniedSnapshot.ErrorCount}");
        }
        finally { deniedInfo.SetAccessControl(original); }
        report.Limitations.Add("This controlled directory corpus is not a complete adversarial NTFS volume, privilege matrix, or mutation stress test. EFS, mount points and live rename/replace races remain separate acceptance cases.");
        report.Limitations.Add("Allocation checks assert independent metamorphic properties and sparse/compressed inequalities; they do not claim an external extent-by-extent allocation oracle.");
    }

    [SupportedOSPlatform("windows")]
    private static void Junction(string link, string target)
    {
        if (Directory.Exists(link) || File.Exists(link)) throw new IOException("Junction path already exists.");
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        using (var memory = new MemoryStream(buffer))
        using (var writer = new BinaryWriter(memory))
        {
            writer.Write(0xa0000003u); writer.Write(checked((ushort)(buffer.Length - 8))); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2))); writer.Write(checked((ushort)print.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
        }
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Control(handle, 0x000900a4, buffer); // FSCTL_SET_REPARSE_POINT
    }
    private static void Control(SafeFileHandle handle, uint code, byte[]? input)
    {
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
