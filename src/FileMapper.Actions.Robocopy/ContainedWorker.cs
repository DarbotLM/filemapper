using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FileMapper.Actions.Robocopy;

internal sealed record WorkerRequest(string BinaryPath, string BinarySha256, string[] Arguments, string Directory, long OutputBudget);
internal sealed record WorkerResult(int? ExitCode, string? Error, long StdoutBytes, long StderrBytes);
internal sealed record RunResult(int? ExitCode, string? Error, bool Cancelled, bool TimedOut, bool ContainmentConfirmed, string WorkerSha256);

internal static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--worker" || !OperatingSystem.IsWindows()) return 64;
        // The parent starts us inert, assigns the Job Object, then releases this barrier. EOF/crash never releases it.
        var gate = Console.In.ReadLineAsync();
        if (await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(20))) != gate || await gate != "CONTAINED-GO") return 65;
        if (!WindowsJob.IsCurrentProcessInJob()) return 66;
        var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(args[1]), Data.Json)!;
        WorkerResult result;
        try
        {
            if (Data.HashFile(request.BinaryPath) != request.BinarySha256) throw new IOException("Robocopy binary changed before native launch.");
            var psi = new ProcessStartInfo(request.BinaryPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = request.Directory };
            foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);
            // A minimum environment prevents bearer tokens, installer sessions and inherited application secrets leaking to the tool.
            SanitizeEnvironment(psi);
            using var process = Process.Start(psi) ?? throw new IOException("Native process did not start.");
            try
            {
            await File.WriteAllTextAsync(Path.Combine(request.Directory, "native-process.json"), Data.Serialize(new { process.Id, startedAt = process.StartTime.ToUniversalTime(), executable = request.BinaryPath }));
            long bytes = 0;
            async Task<long> Drain(Stream input, string file)
            {
                await using var output = new FileStream(Path.Combine(request.Directory, file), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536]; long local = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer); if (count == 0) break;
                    var total = Interlocked.Add(ref bytes, count);
                    if (total > request.OutputBudget) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("Combined raw output budget exceeded; native process terminated, audit is incomplete."); }
                    await output.WriteAsync(buffer.AsMemory(0, count)); local += count;
                }
                await output.FlushAsync(); output.Flush(true); return local;
            }
            var stdout = Drain(process.StandardOutput.BaseStream, "stdout.raw"); var stderr = Drain(process.StandardError.BaseStream, "stderr.raw");
            async Task EnforceBudget()
            {
                while (!process.HasExited)
                {
                    var logPath = Path.Combine(request.Directory, "native.log"); var logBytes = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                    if (Interlocked.Read(ref bytes) + logBytes > request.OutputBudget) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("Combined native log/console output budget exceeded; audit is incomplete."); }
                    await Task.Delay(50);
                }
            }
            try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(), EnforceBudget());
                var logPath = Path.Combine(request.Directory, "native.log");
                if (Interlocked.Read(ref bytes) + (File.Exists(logPath) ? new FileInfo(logPath).Length : 0) > request.OutputBudget) throw new IOException("Completed native output exceeds the audit budget; outcome is not approvable.");
                result = new(process.ExitCode, null, stdout.Result, stderr.Result); }
            catch (Exception e) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(); result = new(process.ExitCode, e.Message, stdout.IsCompletedSuccessfully ? stdout.Result : 0, stderr.IsCompletedSuccessfully ? stderr.Result : 0); }
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
        catch (Exception e) { result = new(null, e.GetType().Name + ": " + e.Message, 0, 0); }
        await File.WriteAllTextAsync(Path.Combine(request.Directory, "worker-result.json"), Data.Serialize(result));
        return result.Error is null ? 0 : 1;
    }
    internal static void SanitizeEnvironment(ProcessStartInfo psi)
    {
        var retained = new Dictionary<string, string?>();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA" }) retained[key] = Environment.GetEnvironmentVariable(key);
        psi.Environment.Clear(); foreach (var pair in retained) if (pair.Value is not null) psi.Environment[pair.Key] = pair.Value;
    }
}

internal static class ContainedRunner
{
    internal static async Task<RunResult> Run(RobocopyBackendOptions options, RobocopyPlan plan, CompiledArguments command, string directory, Func<bool> cancelRequested, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var requestPath = Path.Combine(directory, "worker-request.json");
        await File.WriteAllTextAsync(requestPath, Data.Serialize(new WorkerRequest(plan.Binary.Path, plan.Binary.Sha256, command.Arguments, directory, plan.Draft.MaximumOutputBytes)), token);
        var worker = options.WorkerExecutablePath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "worker", "FileMapper.Actions.Robocopy.exe"));
        ProcessStartInfo psi;
        string workerHash;
        if (File.Exists(worker))
        {
            if (!Path.IsPathFullyQualified(worker)) throw new ArgumentException("Worker executable must be absolute.");
            PathSafety.RejectReparseAncestors(worker); workerHash = Data.HashFile(worker);
            psi = new(worker);
        }
        else
        {
            if (options.WorkerExecutablePath is not null || options.DotnetHostPath is null) throw new FileNotFoundException("Publish the standalone ../worker/FileMapper.Actions.Robocopy.exe, or explicitly configure a trusted development DotnetHostPath.", worker);
            if (!Path.IsPathFullyQualified(options.DotnetHostPath) || !File.Exists(options.DotnetHostPath)) throw new ArgumentException("Development dotnet host must be an existing absolute path.");
            PathSafety.RejectReparseAncestors(options.DotnetHostPath);
            var assembly = typeof(ContainedRunner).Assembly.Location;
            var config = options.RuntimeConfigPath ?? Path.ChangeExtension(assembly, ".runtimeconfig.json");
            if (!Path.IsPathFullyQualified(config) || !File.Exists(config)) throw new FileNotFoundException("A trusted runtimeconfig is required for the development worker.", config);
            psi = new(options.DotnetHostPath); psi.ArgumentList.Add("exec"); psi.ArgumentList.Add("--runtimeconfig"); psi.ArgumentList.Add(config); psi.ArgumentList.Add(assembly);
            workerHash = Data.HashFile(assembly);
        }
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.WorkingDirectory = directory;
        psi.ArgumentList.Add("--worker"); psi.ArgumentList.Add(requestPath); WorkerProgram.SanitizeEnvironment(psi);
        using var job = new WindowsJob();
        using var process = Process.Start(psi) ?? throw new IOException("Worker bootstrap failed to start.");
        bool assigned = false, cancelled = false, timedOut = false;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "bootstrap-process.json"), Data.Serialize(new { process.Id, startedAt = process.StartTime.ToUniversalTime(), executable = psi.FileName }));
            job.Assign(process); assigned = true;
            await process.StandardInput.WriteLineAsync("CONTAINED-GO"); process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            var start = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                cancelled = token.IsCancellationRequested || cancelRequested(); timedOut = start.Elapsed.TotalSeconds > plan.Draft.TimeoutSeconds;
                if (cancelled || timedOut) { job.Terminate(); break; }
                await Task.Delay(100, CancellationToken.None);
            }
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            if (stdout.Result.Length != 0 || stderr.Result.Length != 0) await File.WriteAllTextAsync(Path.Combine(directory, "bootstrap-output.txt"), stdout.Result + stderr.Result);
            if (!await job.WaitEmpty()) return new(null, "Could not confirm all contained descendants exited.", cancelled, timedOut, false, workerHash);
            if (cancelled || timedOut) return new(null, timedOut ? "Native job exceeded its time budget." : "Cancellation terminated the contained process tree.", cancelled, timedOut, true, workerHash);
            var resultPath = Path.Combine(directory, "worker-result.json");
            if (!File.Exists(resultPath)) return new(null, "Worker ended without a native outcome. Bootstrap exit: " + process.ExitCode, false, false, true, workerHash);
            var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(resultPath), Data.Json)!;
            return new(result.ExitCode, result.Error, false, false, true, workerHash);
        }
        finally
        {
            if (!process.HasExited) { if (assigned) job.Terminate(); else process.Kill(true); await process.WaitForExitAsync(); }
        }
    }
}

internal sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal WindowsJob()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null); if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE, no BREAKAWAY_OK.
        if (!SetInformationJobObject(handle, 9, ref info, Marshal.SizeOf<ExtendedLimit>())) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
    }
    internal void Assign(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Containment assignment failed; bootstrap was not released."); }
    internal void Terminate() { if (!TerminateJobObject(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal async Task<bool> WaitEmpty()
    {
        for (var i = 0; i < 100; i++) { if (!QueryInformationJobObject(handle, 1, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); if (info.ActiveProcesses == 0) return true; await Task.Delay(100); } return false;
    }
    internal static bool IsCurrentProcessInJob() => IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var result) && result;
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessUserTime, JobUserTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting { public long TotalUser, TotalKernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr unused, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimit info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
