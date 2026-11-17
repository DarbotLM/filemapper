using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FileMapper.Domain;

namespace FileMapper.Collect.Git;

internal sealed partial class RepositoryHistory(ProvenanceGraph graph)
{
    private readonly GitReader git = new(graph.Limits, graph.Root);
    private readonly Dictionary<string, HeadEntry> tracked = new(StringComparer.Ordinal);
    private readonly HashSet<string> proofs = new(StringComparer.Ordinal);
    private int changeCount;
    public string? Repository { get; private set; }
    public string? RepositoryId { get; private set; }
    public string? Head { get; private set; }
    private sealed record HeadEntry(string Relative, string FullPath, string Blob, string Mode);
    private string CommitId(string sha) => Identity.For("commit", RepositoryId + ":" + sha);
    private static bool ObjectId(string sha) => sha.Length is 40 or 64 && sha.All(Uri.IsHexDigit);
    private string Revision(string sha, string relative, string blob, string mode)
    {
        var id = Identity.For("repository-file", RepositoryId + ":" + sha + ":" + relative);
        graph.Node(id, mode == "160000" ? "Submodule" : "RepositoryFile", relative, null,
            new() { ["repositoryId"] = RepositoryId, ["revision"] = sha, ["repositoryPath"] = relative, ["blobObjectId"] = blob, ["gitMode"] = mode, ["immutableRevisionIdentity"] = true });
        return id;
    }
    public async Task CollectAsync(CancellationToken ct)
    {
        try
        {
            Repository = Path.GetFullPath((await git.Text(graph.Root, ["rev-parse", "--show-toplevel"], ct)).TrimEnd('\r', '\n'));
            RepositoryId = Identity.For("repository", Repository);
            graph.Node(RepositoryId, "Repository", Path.GetFileName(Repository), Repository,
                new() { ["vcs"] = "git", ["trust"] = "read-only-no-hooks-no-fetch", ["historyLimit"] = graph.Limits.MaxCommits });
            var headBytes = await git.Bytes(Repository, ["rev-parse", "--verify", "HEAD"], ct);
            Head = Encoding.UTF8.GetString(headBytes).Trim();
            if (!ObjectId(Head)) throw new GitReadException("Unexpected HEAD object identifier.");
            var repoEvidence = graph.Observe(RepositoryId, "repository_head", Head, Repository, hash: ProvenanceGraph.Hash(headBytes));
            var refs = await git.Text(Repository, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(*objecttype)%00", "refs/heads", "refs/tags"], ct);
            var columns = refs.Split('\0');
            for (var index = 0; index + 4 < columns.Length; index += 5)
            {
                var name = columns[index].TrimStart('\r', '\n'); var target = columns[index + 3].Length > 0 ? columns[index + 3] : columns[index + 1];
                var targetType = columns[index + 3].Length > 0 ? columns[index + 4] : columns[index + 2];
                if (!ObjectId(target)) continue;
                var kind = name.StartsWith("refs/tags/", StringComparison.Ordinal) ? "Tag" : "Branch";
                var id = Identity.For(kind.ToLowerInvariant(), RepositoryId + ":" + name);
                graph.Node(id, kind, name, Repository, new() { ["objectId"] = columns[index + 1], ["peeledObjectId"] = target });
                graph.Edge(RepositoryId, id, "CONTAINS", repoEvidence);
                var targetId = targetType == "commit" ? CommitId(target) : Identity.For("git-object", RepositoryId + ":" + target);
                graph.Node(targetId, targetType == "commit" ? "Commit" : "GitObject", target[..12], Repository, new() { ["sha"] = target, ["objectType"] = targetType });
                graph.Edge(id, targetId, "POINTS_TO", repoEvidence);
            }
            var revisions = (await git.Text(Repository, ["rev-list", "--parents", "--max-count=" + (graph.Limits.MaxCommits + 1), "HEAD"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (revisions.Length > graph.Limits.MaxCommits) graph.Diagnostic(Repository, "history_limit", "Commit history was truncated at the configured limit.");
            var indexCommit = 0;
            foreach (var row in revisions.Take(graph.Limits.MaxCommits))
            {
                ct.ThrowIfCancellationRequested(); var hashes = row.Trim().Split(' ');
                if (hashes.Any(h => !ObjectId(h))) throw new GitReadException("Malformed revision list.");
                var sha = hashes[0]; var raw = await git.Bytes(Repository, ["cat-file", "commit", sha], ct);
                CollectCommit(sha, hashes[1..], raw);
                if (indexCommit++ < graph.Limits.MaxChangeCommits && changeCount < graph.Limits.MaxChanges)
                    await CollectChanges(sha, hashes.Length > 1 ? hashes[1] : null, hashes.Length > 2, ct);
            }
            if (revisions.Length > graph.Limits.MaxChangeCommits) graph.Diagnostic(Repository, "change_history_limit", "Diff collection covers only the configured recent-commit window; merge diffs compare first parent.");
            var tree = await git.Text(Repository, ["ls-tree", "-r", "-z", "HEAD"], ct);
            var entries = tree.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var row in entries.Take(graph.Limits.MaxTrackedFiles))
            {
                var split = row.IndexOf('\t'); if (split < 0) continue;
                var metadata = row[..split].Split(' '); var relative = row[(split + 1)..];
                if (metadata.Length != 3 || !ObjectId(metadata[2])) continue;
                var path = ScopedPath(relative); if (path is null) continue;
                var revisionId = Revision(Head, relative, metadata[2], metadata[0]);
                graph.Edge(revisionId, CommitId(Head), "OBSERVED_IN", repoEvidence);
                if (metadata[1] != "blob" || metadata[0] == "120000") continue;
                tracked[relative] = new(relative, path, metadata[2], metadata[0]);
                var source = graph.Document(path);
                graph.Edge(source, revisionId, "TRACKED_PATH", repoEvidence, properties: new() { ["workingContentVerified"] = false });
                graph.Edge(RepositoryId, source, "CONTAINS", repoEvidence);
            }
            if (entries.Length > graph.Limits.MaxTrackedFiles) graph.Diagnostic(Repository, "tracked_file_limit", "Tracked path linkage was truncated.");
            // status/diff-files can invoke configured clean/process filters while refreshing content. Read the index only.
            var indexBytes = await git.Bytes(Repository, ["ls-files", "--stage", "-z"], ct);
            var indexChanges = 0; var unmerged = 0;
            foreach (var row in Encoding.UTF8.GetString(indexBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = row.IndexOf('\t'); if (tab < 0) continue; var metadata = row[..tab].Split(' '); var relative = row[(tab + 1)..];
                if (metadata.Length != 3 || ScopedPath(relative) is null) continue;
                if (metadata[2] != "0") unmerged++;
                if (!tracked.TryGetValue(relative, out var entry) || metadata[1] != entry.Blob || metadata[0] != entry.Mode) indexChanges++;
            }
            graph.Observe(RepositoryId, "worktree_status", new { indexedChangesObserved = indexChanges, unmergedEntries = unmerged, trackedClean = (bool?)null, workingTreeComparison = "bounded raw-byte HEAD comparisons only; clean filters and line-ending normalization never run", deletedIndexEntriesChecked = false, untrackedIncluded = false }, Repository, completeness: "partial", hash: ProvenanceGraph.Hash(indexBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { graph.Diagnostic(graph.Root, "git_read", ex.Message); }
    }
    private string? ScopedPath(string relative)
    {
        if (Repository is null || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or ".git") || relative.Contains('\0') || OperatingSystem.IsWindows() && (relative.Contains('\\') || relative.Contains(':'))) return null;
        var path = Path.GetFullPath(Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar)));
        return graph.Contains(path) ? path : null;
    }
    private void CollectCommit(string sha, string[] parents, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes); var separator = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = separator >= 0 ? text[..separator] : text; var body = separator >= 0 ? text[(separator + 2)..] : "";
        var lines = headers.Split('\n');
        var author = lines.FirstOrDefault(l => l.StartsWith("author ", StringComparison.Ordinal))?[7..] ?? "unknown";
        var committer = lines.FirstOrDefault(l => l.StartsWith("committer ", StringComparison.Ordinal))?[10..] ?? "unknown";
        var signature = lines.Any(l => l.StartsWith("gpgsig ", StringComparison.Ordinal) || l.StartsWith("gpgsig-sha256 ", StringComparison.Ordinal));
        var id = CommitId(sha);
        var ev = graph.Observe(id, "commit_object", new { sha, author, committer, parents, signaturePresent = signature, signatureVerification = "not-performed" }, Repository! + "#commit=" + sha, hash: ProvenanceGraph.Hash(bytes));
        graph.Node(id, "Commit", body.Split('\n')[0][..Math.Min(body.Split('\n')[0].Length, 1024)], Repository,
            new() { ["sha"] = sha, ["author"] = author, ["committer"] = committer, ["metadataComplete"] = true, ["signaturePresent"] = signature, ["signatureVerification"] = "not-performed", ["signatureTrust"] = "unknown; no GPG/SSH helper executed" });
        graph.Edge(RepositoryId!, id, "CONTAINS", ev);
        graph.Edge(id, Contributor(author), "AUTHORED_BY", ev);
        graph.Edge(id, Contributor(committer), "COMMITTED_BY", ev);
        foreach (var parent in parents)
        {
            graph.Node(CommitId(parent), "Commit", parent[..12], Repository, new() { ["sha"] = parent });
            graph.Edge(id, CommitId(parent), "PARENT", ev);
        }
    }
    private string Contributor(string identity)
    {
        var match = Regex.Match(identity, @"^(.*) <([^>]*)>(?: -?\d+ [+-]\d{4})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var name = match.Success ? match.Groups[1].Value : identity; var email = match.Success ? match.Groups[2].Value : "";
        var id = Identity.For("contributor", RepositoryId + ":" + name + ":" + email);
        graph.Node(id, "Contributor", name, null, new() { ["email"] = email, ["identityVerification"] = "unverified Git attribution", ["ownership"] = "advisory" });
        return id;
    }
    private async Task CollectChanges(string sha, string? parent, bool merge, CancellationToken ct)
    {
        var arguments = new List<string> { "diff-tree", "--root", "--no-commit-id", "-r", "--raw", "-z", "--no-ext-diff", "--no-textconv", "-M" + graph.Limits.SimilarityPercent + "%", "-C" + graph.Limits.SimilarityPercent + "%", "--find-copies-harder", "-l2000" };
        if (parent is not null) arguments.Add(parent); arguments.Add(sha); arguments.Add("--");
        var raw = await git.Bytes(Repository!, arguments.ToArray(), ct); var tokens = Encoding.UTF8.GetString(raw).Split('\0');
        for (var index = 0; index < tokens.Length;)
        {
            var header = tokens[index++].TrimStart('\r', '\n'); if (header.Length == 0) continue;
            var fields = header.Split(' ');
            if (fields.Length != 5 || !fields[0].StartsWith(':') || index >= tokens.Length) throw new GitReadException("Malformed raw diff record.");
            var status = fields[4]; var oldPath = tokens[index++]; var newPath = oldPath;
            if (status[0] is 'R' or 'C') { if (index >= tokens.Length) throw new GitReadException("Incomplete rename/copy record."); newPath = tokens[index++]; }
            if (ScopedPath(oldPath) is null && ScopedPath(newPath) is null) continue;
            if (++changeCount > graph.Limits.MaxChanges) { graph.Diagnostic(Repository!, "change_limit", "Change record limit reached."); break; }
            var change = Identity.For("change", RepositoryId + ":" + sha + ":" + parent + ":" + oldPath + ":" + newPath + ":" + status);
            int? similarity = int.TryParse(status.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var score) ? score : null;
            var ev = graph.Observe(change, "git_change", new { sha, parent, status, oldPath, newPath, similarityPercent = similarity, detectionThresholdPercent = graph.Limits.SimilarityPercent, comparison = merge ? "first-parent-only" : "parent-or-empty-tree", copyCandidateLimit = 2000 }, Repository! + "#commit=" + sha, hash: ProvenanceGraph.Hash(raw));
            graph.Node(change, "Change", status + " " + newPath, null, new() { ["status"] = status, ["oldPath"] = oldPath, ["newPath"] = newPath, ["similarityPercent"] = similarity, ["mergeComparison"] = merge ? "first-parent-only" : "single-parent" });
            graph.Edge(change, CommitId(sha), "IN_COMMIT", ev);
            string? before = null, after = null;
            if (parent is not null && fields[2].Any(c => c != '0')) before = Revision(parent, oldPath, fields[2], fields[0][1..]);
            if (fields[3].Any(c => c != '0')) after = Revision(sha, newPath, fields[3], fields[1]);
            foreach (var file in new[] { before, after }.OfType<string>()) { graph.Edge(file, change, "CHANGED_BY", ev); graph.Edge(file, CommitId(sha), "MODIFIED_BY", ev); }
            if (before is not null && after is not null && status[0] is 'R' or 'C')
                graph.Edge(after, before, status[0] == 'R' ? "RENAMED_FROM" : "COPIED_FROM", ev, (similarity ?? graph.Limits.SimilarityPercent) / 100d,
                    new() { ["detection"] = "git-similarity-heuristic", ["thresholdPercent"] = graph.Limits.SimilarityPercent, ["similarityPercent"] = similarity, ["identityProof"] = false });
        }
    }
    public async Task LinkVerifiedSourceAsync(string path, string observedSha256, CancellationToken ct)
    {
        if (Repository is null || Head is null || proofs.Count >= graph.Limits.MaxBlameFiles + 128) return;
        var relative = Path.GetRelativePath(Repository, path).Replace('\\', '/');
        if (!tracked.TryGetValue(relative, out var entry) || !proofs.Add(relative)) return;
        try
        {
            var bytes = await git.Bytes(Repository, ["cat-file", "blob", entry.Blob], ct); var hash = ProvenanceGraph.Hash(bytes);
            var source = graph.Document(path);
            var ev = graph.Observe(source, "head_content_comparison", new { head = Head, entry.Blob, observedSha256, headSha256 = hash, equal = hash.Equals(observedSha256, StringComparison.OrdinalIgnoreCase) }, Repository + "#blob=" + entry.Blob, hash: hash);
            if (hash.Equals(observedSha256, StringComparison.OrdinalIgnoreCase)) graph.Edge(source, Revision(Head, relative, entry.Blob, entry.Mode), "CONTENT_EQUALS_REVISION", ev);
        }
        catch (GitReadException ex) { graph.Diagnostic(path, "head_content_unavailable", ex.Message); }
    }
}
