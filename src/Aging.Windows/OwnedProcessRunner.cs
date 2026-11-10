using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aging.Windows;

public sealed record ProcessResult(int ExitCode, TimeSpan Elapsed, string StdoutPath, string StderrPath);

/// <summary>Only this process's assigned job is terminated. No image-name matching.</summary>
public sealed class OwnedProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workDir, string outputPrefix, TimeSpan timeout, CancellationToken ct, Action<int>? started = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix)!);
        using var job = new ProcessJob();
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workDir, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var watch = Stopwatch.StartNew();
        if (!process.Start()) throw new IOException("进程未能启动：" + executable);
        try { job.Assign(process.Handle); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        started?.Invoke(process.Id);
        await using var output = File.Create(outputPrefix + ".stdout.txt");
        await using var error = File.Create(outputPrefix + ".stderr.txt");
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(error, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            // Kill any descendant left behind by an early-exiting launcher before draining inherited pipes.
            job.Terminate();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new(process.ExitCode, watch.Elapsed, outputPrefix + ".stdout.txt", outputPrefix + ".stderr.txt");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { job.Terminate(); throw new TimeoutException("外部引擎超时"); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited && process.CloseMainWindow())
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { /* force termination follows for unresponsive owned processes */ }
            }
            throw;
        }
        finally
        {
            job.Terminate();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { /* cancellation can close job pipes */ }
        }
    }

    private sealed class ProcessJob : IDisposable
    {
        private IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        public ProcessJob()
        {
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimit>()))
            { Dispose(); throw new Win32Exception(); }
        }
        public void Assign(IntPtr process) { if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception(); }
        public void Terminate() { if (handle != IntPtr.Zero) TerminateJobObject(handle, 1); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit information, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
