using System.Diagnostics;
using System.Text;

namespace FileMapper.Collect.Git;

internal sealed class GitReader(ProvenanceOptions limits, string analysisRoot)
{
    private string Executable()
    {
        var candidates = limits.GitExecutable is not null ? new[] { limits.GitExecutable } : OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe") }
            : new[] { "/usr/bin/git", "/usr/local/bin/git" };
        foreach (var candidate in candidates)
        {
            if (!Path.IsPathFullyQualified(candidate)) continue;
            var path = Path.GetFullPath(candidate);
            if (ProvenanceGraph.IsWithinRoot(path, analysisRoot) || !File.Exists(path)) continue;
            var safe = true;
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { safe = false; break; }
            if (safe) return path;
        }
        throw new GitReadException("No trusted absolute Git executable was found outside the analysis root. Set ProvenanceOptions.GitExecutable to a trusted host installation.");
    }
    public async Task<string> Text(string root, string[] arguments, CancellationToken ct) => Encoding.UTF8.GetString(await Bytes(root, arguments, ct));
    public async Task<byte[]> Bytes(string root, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(limits.CommandTimeout);
        var start = new ProcessStartInfo(Executable()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "--no-pager", "--no-optional-locks", "--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "-c", "diff.external=", "-c", "log.showSignature=false", "-c", "core.quotePath=false", "-c", "mailmap.file=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "mailmap.blob=", "-C", root }.Concat(arguments)) start.ArgumentList.Add(arg);
        foreach (var variable in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(variable);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new GitReadException("Git process failed to start.");
            var stdout = Read(process.StandardOutput.BaseStream, timeout.Token); var stderr = Read(process.StandardError.BaseStream, timeout.Token);
            foreach (var task in new[] { stdout, stderr }) _ = task.ContinueWith(_ => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new GitReadException(Encoding.UTF8.GetString(await stderr).Trim());
            return await stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitReadException("Git command time budget exceeded."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new GitReadException(ex.Message); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }
    private async Task<byte[]> Read(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream(); var buffer = new byte[16_384]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        { if (result.Length + count > limits.MaxDocumentBytes) throw new GitReadException("Git output exceeded its byte budget."); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
}
internal sealed class GitReadException(string message) : IOException(message);
