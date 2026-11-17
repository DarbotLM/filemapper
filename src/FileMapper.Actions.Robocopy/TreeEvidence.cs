using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Actions.Robocopy;

internal sealed record TreeItem(string RelativePath, bool Directory, bool Reparse, long Length, long WriteTicks, long CreationTicks, int Attributes, string? Sha256, string? FileIdentity, uint LinkCount);
internal sealed record TreeView(TreeFingerprint Fingerprint, IReadOnlyList<TreeItem> Source, IReadOnlyList<TreeItem> Destination);
internal static class TreeEvidence
{
    internal static TreeView Capture(RobocopyPlan plan, CancellationToken token, Func<bool>? cancelled = null)
    {
        void CheckCancellation() { token.ThrowIfCancellationRequested(); if (cancelled?.Invoke() == true) throw new OperationCanceledException("Durable cancellation requested during evidence capture."); }
        var count = 0; long bytes = 0;
        List<TreeItem> Walk(string root)
        {
            var rootInfo = new DirectoryInfo(root); if (++count > plan.Draft.MaximumTreeEntries) throw new IOException("Tree entry budget exhausted.");
            var list = new List<TreeItem> { new(".", true, false, 0, rootInfo.LastWriteTimeUtc.Ticks, rootInfo.CreationTimeUtc.Ticks, (int)rootInfo.Attributes, null, null, 0) };
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
            while (pending.TryPop(out var next))
            {
                CheckCancellation(); if (next.Depth > 512) throw new IOException("Tree depth exceeds the evidence budget.");
                PathSafety.RejectReparseAncestors(next.Path);
                foreach (var path in Directory.EnumerateFileSystemEntries(next.Path))
                {
                    CheckCancellation(); if (++count > plan.Draft.MaximumTreeEntries) throw new IOException("Tree enumeration exceeds the evidence entry budget; preview cannot be approved.");
                    var info = new FileInfo(path); var attrs = info.Attributes; var directory = (attrs & FileAttributes.Directory) != 0; var reparse = (attrs & FileAttributes.ReparsePoint) != 0;
                    string? hash = null, fileIdentity = null; uint links = 0; long size = 0;
                    if (!directory && !reparse)
                    {
                        size = info.Length; var write = info.LastWriteTimeUtc;
                        // Deny concurrent writes while hashing each ordinary stream. This is not a whole-tree snapshot.
                        using var stream = PathSafety.OpenOrdinaryFile(path, out fileIdentity, out links);
                        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int n;
                        while ((n = stream.Read(buffer)) != 0) { CheckCancellation(); digest.AppendData(buffer, 0, n); }
                        hash = Convert.ToHexStringLower(digest.GetHashAndReset()); info.Refresh();
                        if (info.Length != size || info.LastWriteTimeUtc != write || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("File changed during evidence hashing: " + path);
                        bytes = checked(bytes + size);
                    }
                    list.Add(new(Path.GetRelativePath(root, path), directory, reparse, size, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, (int)attrs, hash, fileIdentity, links));
                    if (directory && !reparse) pending.Push((path, next.Depth + 1));
                }
            }
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath)); return list;
        }
        var source = Walk(plan.Draft.Source); var destination = Walk(plan.Draft.Destination);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string label, IEnumerable<TreeItem> tree) { digest.AppendData(Encoding.UTF8.GetBytes(label)); foreach (var item in tree) digest.AppendData(Encoding.UTF8.GetBytes(Data.Serialize(item) + "\n")); }
        Append("source\n", source); Append("destination\n", destination);
        return new(new(Convert.ToHexStringLower(digest.GetHashAndReset()), count, bytes, DateTimeOffset.UtcNow,
            "Sorted relative paths, kind, length, creation/write timestamps, attributes, physical file identity/link count, and SHA-256 of every ordinary file stream in both trees. Reparse entries recorded but never followed; access time, ADS, ACLs and external concurrent changes are not proven."), source, destination);
    }
    internal static string[] ExecutionBlocks(TreeView view)
    {
        var blocks = new List<string>();
        if (view.Destination.Any(x => !x.Directory && !x.Reparse && x.LinkCount > 1)) blocks.Add("Destination contains multiply linked files; writing one name could mutate an alias outside the approved tree.");
        var sourceIds = view.Source.Where(x => x.FileIdentity is not null).Select(x => x.FileIdentity!).ToHashSet(StringComparer.Ordinal);
        if (view.Destination.Any(x => x.FileIdentity is not null && sourceIds.Contains(x.FileIdentity))) blocks.Add("Source and destination share physical file identities.");
        if (view.Destination.Any(x => x.Reparse)) blocks.Add("Destination reparse entries are not permitted for execution; /XJ alone is not a destination write boundary.");
        return blocks.ToArray();
    }
    internal static VerificationResult Verify(TreeView before, TreeView after)
    {
        var differences = new List<string>(); var destination = after.Destination.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var sourceAfter = after.Source.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase); var checkedFiles = 0; long checkedBytes = 0;
        foreach (var source in before.Source.Where(x => !x.Reparse))
        {
            if (!sourceAfter.TryGetValue(source.RelativePath, out var current) || current.Directory != source.Directory || current.Sha256 != source.Sha256 || current.WriteTicks != source.WriteTicks) differences.Add("Source changed during execution: " + source.RelativePath);
            if (!destination.TryGetValue(source.RelativePath, out var target) || target.Reparse || target.Directory != source.Directory) { differences.Add("Missing or wrong-kind destination: " + source.RelativePath); continue; }
            if (!source.Directory) { checkedFiles++; checkedBytes += source.Length; if (target.Length != source.Length || target.Sha256 != source.Sha256) differences.Add("Content differs: " + source.RelativePath); }
        }
        if (after.Source.Count != before.Source.Count) differences.Add("Source entry count changed during execution.");
        return new("complete hashes of intended ordinary file streams", differences.Count == 0, checkedFiles, checkedBytes, differences.Take(1000).ToArray(),
            ["ACLs, ADS, sparse/compressed allocation, EFS, directory metadata and native link-copy semantics are not independently verified.", "Destination extras are retained by this ordinary /E policy; native exit bits expose extras separately.", "External writers can race preconditions; this is not an atomic filesystem transaction."]);
    }
}
