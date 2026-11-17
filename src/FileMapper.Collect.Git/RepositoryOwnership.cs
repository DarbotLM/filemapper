using System.Text;
using System.Text.RegularExpressions;
using FileMapper.Domain;

namespace FileMapper.Collect.Git;

internal sealed partial class RepositoryHistory
{
    private sealed record OwnerRule(int Line, string Pattern, string[] Owners, Regex Match, string Evidence, string Source);
    public async Task CollectOwnershipAsync(CancellationToken ct)
    {
        if (Repository is null || RepositoryId is null || Head is null) return;
        var rules = ReadCodeOwners(ct);
        foreach (var entry in tracked.Values)
        {
            ct.ThrowIfCancellationRequested(); OwnerRule[] matches;
            try { matches = rules.Where(r => { ct.ThrowIfCancellationRequested(); return r.Match.IsMatch(entry.Relative); }).ToArray(); }
            catch (RegexMatchTimeoutException) { graph.Diagnostic(entry.FullPath, "ownership_match_budget", "Ownership pattern matching exceeded its budget; attribution is unknown."); continue; }
            if (matches.Length == 0) continue;
            var winner = matches[^1]; var source = graph.Document(entry.FullPath);
            var ev = graph.Observe(source, "ownership_policy_resolution", new
            {
                policy = "CODEOWNERS", precedence = "first-file-in-.github-root-docs; last-matching-rule", selectedLine = winner.Line,
                selectedPattern = winner.Pattern, owners = winner.Owners, shadowedLines = matches[..^1].Select(r => r.Line).ToArray(),
                conflictingOwners = matches[..^1].Any(r => !r.Owners.SequenceEqual(winner.Owners)), authorizationVerified = false
            }, winner.Source, completeness: "partial", contributes: matches.Select(r => r.Evidence));
            foreach (var ownerName in winner.Owners)
            {
                var owner = Identity.For("owner", RepositoryId + ":codeowners:" + ownerName);
                graph.Node(owner, "Owner", ownerName, null, new() { ["source"] = "CODEOWNERS", ["membershipVerified"] = false });
                graph.Edge(owner, source, "OWNS", ev, properties: new() { ["advisory"] = true, ["priority"] = 100, ["basis"] = "declared-review-policy" });
                graph.Edge(source, owner, "ATTRIBUTED_TO", ev, properties: new() { ["advisory"] = true, ["priority"] = 100 });
            }
        }
        var eligible = tracked.Values.Where(e => Path.GetExtension(e.Relative).ToLowerInvariant() is ".cs" or ".ps1" or ".psm1" or ".csproj" or ".fs" or ".vb" or ".md" or ".txt")
            .OrderBy(e => e.Relative, StringComparer.Ordinal).ToArray();
        foreach (var entry in eligible.Take(graph.Limits.MaxBlameFiles))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = graph.Read(entry.FullPath, ct); if (bytes.Contains((byte)0)) continue;
                var source = graph.Document(entry.FullPath, bytes);
                await LinkVerifiedSourceAsync(entry.FullPath, ProvenanceGraph.Hash(bytes), ct);
                var headBytes = await git.Bytes(Repository, ["cat-file", "blob", entry.Blob], ct);
                var headLines = headBytes.Count(b => b == (byte)'\n') + (headBytes.Length > 0 && headBytes[^1] != (byte)'\n' ? 1 : 0);
                if (headLines == 0) continue;
                var raw = await git.Bytes(Repository, ["blame", "--line-porcelain", "--no-textconv", "-L", "1," + Math.Min(headLines, graph.Limits.MaxBlameLinesPerFile), Head, "--", entry.Relative], ct);
                var rows = Encoding.UTF8.GetString(raw).Split('\n');
                var counts = new Dictionary<string, int>(StringComparer.Ordinal); var commits = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                string sha = "", author = "", email = ""; var lines = 0;
                foreach (var row in rows)
                {
                    var parts = row.Split(' ', 4);
                    if (parts.Length >= 3 && ObjectId(parts[0])) sha = parts[0];
                    else if (row.StartsWith("author ", StringComparison.Ordinal)) author = row[7..];
                    else if (row.StartsWith("author-mail ", StringComparison.Ordinal)) email = row[12..].Trim('<', '>');
                    else if (row.StartsWith('\t') && ObjectId(sha))
                    {
                        var owner = Contributor(author + " <" + email + ">"); counts[owner] = counts.GetValueOrDefault(owner) + 1;
                        if (!commits.TryGetValue(owner, out var set)) commits[owner] = set = new(StringComparer.Ordinal); set.Add(sha); lines++;
                    }
                }
                var ev = graph.Observe(source, "blame_attribution", new { revision = Head, observedLines = lines, requestedLineLimit = graph.Limits.MaxBlameLinesPerFile, workingContentMayDiffer = true, ownership = "advisory-line-attribution; never authority" }, Repository + "#blame=" + Head + ":" + entry.Relative, completeness: "partial", hash: ProvenanceGraph.Hash(raw));
                foreach (var (owner, count) in counts)
                {
                    graph.Edge(source, owner, "ATTRIBUTED_TO", ev, 0.7, new() { ["lineCount"] = count, ["sampledLineFraction"] = lines == 0 ? 0d : (double)count / lines, ["advisory"] = true, ["priority"] = 10, ["policyOverride"] = rules.Any(r => r.Match.IsMatch(entry.Relative)) });
                    foreach (var commit in commits[owner])
                    {
                        graph.Node(CommitId(commit), "Commit", commit[..12], Repository, new() { ["sha"] = commit });
                        graph.Edge(Revision(Head, entry.Relative, entry.Blob, entry.Mode), CommitId(commit), "LINES_ATTRIBUTED_TO", ev, 0.9);
                        graph.Edge(CommitId(commit), owner, "AUTHORED_BY", ev, 0.9);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException) { graph.Diagnostic(entry.FullPath, "blame_unavailable", ex.Message); }
        }
        if (eligible.Length > graph.Limits.MaxBlameFiles) graph.Diagnostic(Repository, "blame_file_limit", "Blame attribution covers a deterministic bounded subset of text source files.");
    }
    private List<OwnerRule> ReadCodeOwners(CancellationToken ct)
    {
        var rules = new List<OwnerRule>();
        var candidates = new[] { ".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS" }.Select(p => Path.Combine(Repository!, p.Replace('/', Path.DirectorySeparatorChar))).Where(File.Exists).ToArray();
        if (candidates.Length == 0) return rules;
        var selected = candidates[0];
        try
        {
            var bytes = graph.Read(selected, ct); var document = graph.Document(selected, bytes);
            graph.Observe(document, "codeowners_file_precedence", new { selected, ignoredLowerPriorityFiles = candidates[1..], source = "working-tree-policy", evaluation = "supported glob subset; provider permissions unverified" }, selected, hash: ProvenanceGraph.Hash(bytes));
            var lineNumber = 0;
            foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                ct.ThrowIfCancellationRequested(); lineNumber++;
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); var pattern = tokens[0];
                if (pattern.Length > 1024 || pattern.Contains('!') || pattern.Contains('[') || pattern.Contains(']') || pattern.Contains('\\') || pattern.Split('/').Any(p => p.Contains("**", StringComparison.Ordinal) && p != "**"))
                { graph.Diagnostic(selected + "#L" + lineNumber, "unsupported_codeowners_pattern", "Unsupported CODEOWNERS syntax was not interpreted: " + pattern); continue; }
                var owners = tokens.Skip(1).TakeWhile(t => !t.StartsWith('#')).ToArray();
                if (owners.Any(o => o.Length > 256 || !o.StartsWith('@') && !o.Contains('@')))
                { graph.Diagnostic(selected + "#L" + lineNumber, "invalid_codeowner", "Malformed owner declaration was not interpreted."); continue; }
                var observation = graph.Observe(document, "codeowners_rule", new { pattern, owners, line = lineNumber }, selected + "#L" + lineNumber, hash: ProvenanceGraph.Hash(bytes));
                rules.Add(new(lineNumber, pattern, owners, Glob(pattern), observation, selected + "#L" + lineNumber));
                if (rules.Count >= 2_000) { graph.Diagnostic(selected, "codeowners_rule_limit", "CODEOWNERS rule limit reached."); break; }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { graph.Diagnostic(selected, "codeowners_read", ex.Message); }
        return rules;
    }
    private static Regex Glob(string pattern)
    {
        var anchored = pattern.StartsWith('/'); var directory = pattern.EndsWith('/');
        pattern = pattern.Trim('/'); var output = new StringBuilder(anchored || pattern.Contains('/') ? "^" : "(?:^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*') { i++; if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; output.Append("(?:.*/)?"); } else output.Append(".*"); }
                else output.Append("[^/]*");
            }
            else if (pattern[i] == '?') output.Append("[^/]");
            else output.Append(Regex.Escape(pattern[i].ToString()));
        }
        output.Append(directory ? "/.*$" : "(?:/.*)?$");
        return new(output.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    }
}
