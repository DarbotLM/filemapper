using System.Diagnostics;
using System.Text;

namespace FileMapper.Harvest;

/// <summary>Git plumbing only. ArgumentList; no source execution, lazy fetch, hooks or credential helpers.</summary>
internal sealed class GitSourceProcess
{
    private readonly string executable;
    private readonly string repository;
    public GitSourceProcess(string repository, string? executable = null)
    {
        this.repository = Path.GetFullPath(repository);
        HarvestFiles.RequireOrdinaryPath(this.repository);
        this.executable = executable ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe") : "/usr/bin/git");
        if (!Path.IsPathFullyQualified(this.executable) || !File.Exists(this.executable))
            throw new ArgumentException("A trusted absolute Git executable is required.");
        HarvestFiles.RequireOrdinaryPath(this.executable);
    }

    public Process Start(IEnumerable<string> arguments, bool input = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input
        };
        string[] configuration = ["--no-pager", "--no-optional-locks", "--no-replace-objects",
            "-c", "credential.helper=", "-c", "core.askPass=", "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
            "-c", "http.followRedirects=false", "-c", "fetch.fsckObjects=true", "-c", "transfer.fsckObjects=true",
            "-c", "fetch.recurseSubmodules=false", "-c", "maintenance.auto=false", "-c", "gc.auto=0",
            "-c", "safe.directory=" + repository, "--git-dir=" + repository];
        foreach (var argument in configuration.Concat(arguments)) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        var process = new Process { StartInfo = start };
        if (!process.Start()) { process.Dispose(); throw new IOException("Git could not start."); }
        return process;
    }

    public async Task<GitCommandReceipt> ToFileAsync(string[] arguments, string output, int timeoutSeconds,
        long outputLimit, CancellationToken cancellationToken)
    {
        HarvestFiles.RequireOrdinaryPath(output);
        var started = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = Start(arguments);
        var error = DrainErrorAsync(process.StandardError, timeout.Token);
        try
        {
            await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var copy = CopyBoundedAsync(process.StandardOutput.BaseStream, destination, outputLimit, timeout.Token);
            try { await copy; await process.WaitForExitAsync(timeout.Token); }
            catch { Stop(process); throw; }
            await destination.FlushAsync(timeout.Token); destination.Flush(true);
            var detail = await error;
            return new(executable, arguments, started, DateTimeOffset.UtcNow, process.ExitCode, output,
                new FileInfo(output).Length, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Git plumbing command exceeded its declared time budget."); }
        finally { await StopAndWaitAsync(process); try { await error; } catch (OperationCanceledException) { } }
    }

    public async Task<string> TextAsync(string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workingDirectory, "git-" + Guid.NewGuid().ToString("N") + ".txt");
        var receipt = await ToFileAsync(arguments, path, 120, 65536, cancellationToken);
        if (receipt.ExitCode != 0) throw new IOException("Git plumbing failed: " + receipt.StandardError);
        return (await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)).Trim();
    }

    internal static async Task<string> DrainErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }

    internal static async Task CopyBoundedAsync(Stream source, Stream target, long bound, CancellationToken ct)
    {
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total = checked(total + count);
            if (total > bound) throw new IOException("Git output exceeded its declared byte bound.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { /* The awaited teardown below reports a surviving child. */ }
    }
    internal static async Task StopAndWaitAsync(Process process)
    {
        Stop(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        { throw new IOException("Owned Git process " + process.Id + " did not exit within the teardown budget; inspect it before retrying this workspace."); }
    }
}

public sealed record GitCommandReceipt(string Executable, string[] Arguments, DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt, int ExitCode, string StandardOutputFile, long StandardOutputBytes, string StandardError);

internal static class HarvestFiles
{
    internal static void RequireOrdinaryPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Harvest storage and Git paths cannot traverse a reparse point: " + current);
            current = Path.GetDirectoryName(current);
        }
    }
    internal static void CreateDirectory(string path)
    { RequireOrdinaryPath(path); Directory.CreateDirectory(path); RequireOrdinaryPath(path); }
    internal static bool IsHash(string? value, int length = 64) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
