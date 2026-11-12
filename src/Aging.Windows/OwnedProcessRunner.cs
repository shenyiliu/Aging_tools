using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aging.Windows;

public sealed record ProcessResult(int ExitCode, TimeSpan Elapsed, string StdoutPath, string StderrPath);

/// <summary>Only this process's assigned job is terminated. No image-name matching.</summary>
public sealed class OwnedProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workDir, string outputPrefix, TimeSpan timeout, CancellationToken ct, Action<int>? started = null, bool waitForDescendants = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix)!);
        using var job = new ProcessJob();
        string[] argumentList = arguments.ToArray();
        await File.WriteAllTextAsync(outputPrefix + ".command.json", System.Text.Json.JsonSerializer.Serialize(new { Executable = executable, Arguments = argumentList, WorkingDirectory = workDir, TimeoutSeconds = timeout.TotalSeconds, WaitForDescendants = waitForDescendants }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
        await using var output = File.Create(outputPrefix + ".stdout.txt");
        await using var error = File.Create(outputPrefix + ".stderr.txt");
        var watch = Stopwatch.StartNew();
        using var launch = StartOwned(executable, argumentList, workDir, job, ct);
        var process = launch.Process;
        // Keep draining after cancellation; terminating our job closes the inherited write handles.
        Task stdout = launch.StandardOutput.CopyToAsync(output);
        Task stderr = launch.StandardError.CopyToAsync(error);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            started?.Invoke(process.Id);
            await process.WaitForExitAsync(limit.Token);
            if (waitForDescendants && process.ExitCode == 0)
                while (job.ActiveProcesses > 0) await Task.Delay(100, limit.Token);
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

    private sealed class OwnedLaunch(Process process, Stream standardOutput, Stream standardError) : IDisposable
    {
        public Process Process { get; } = process;
        public Stream StandardOutput { get; } = standardOutput;
        public Stream StandardError { get; } = standardError;
        public void Dispose() { Process.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }
    }

    private static OwnedLaunch StartOwned(string executable, string[] arguments, string workDir, ProcessJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SafeFileHandle? stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        Stream? stdout = null, stderr = null;
        Process? process = null;
        ProcessInformation created = default;
        IntPtr attributes = IntPtr.Zero, inheritedHandles = IntPtr.Zero;
        bool initialized = false, transferred = false;
        try
        {
            (stdoutRead, stdoutWrite) = CreateOutputPipe();
            (stderrRead, stderrWrite) = CreateOutputPipe();
            stdout = new FileStream(stdoutRead, FileAccess.Read, 4096, false);
            stderr = new FileStream(stderrRead, FileAccess.Read, 4096, false);
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            using var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
            if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            UIntPtr bytes = UIntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == UIntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((int)bytes.ToUInt64()));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inheritedHandles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            // Restrict inheritance to this launch's three standard handles, even during concurrent starts.
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, inheritedHandles, (UIntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = stdoutWrite.DangerousGetHandle(), Error = stderrWrite.DangerousGetHandle() },
                Attributes = attributes
            };
            var commandLine = new StringBuilder(string.Join(' ', new[] { QuoteArgument(executable) }.Concat(arguments.Select(QuoteArgument))));
            // No child code can execute or create descendants before job assignment succeeds.
            const uint creationFlags = 0x4 | 0x80000 | 0x08000000; // CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, creationFlags, IntPtr.Zero, workDir, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "进程未能启动：" + executable);
            job.Assign(created.Process);
            process = Process.GetProcessById(checked((int)created.ProcessId));
            _ = process.Handle; // Acquire the managed handle before a fast launcher can exit.
            ct.ThrowIfCancellationRequested();
            if (ResumeThread(created.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new OwnedLaunch(process, stdout, stderr);
            transferred = true;
            return result;
        }
        catch
        {
            // Also covers assignment failure: the still-suspended process cannot escape our ownership.
            if (created.Process != IntPtr.Zero) TerminateProcess(created.Process, 1);
            throw;
        }
        finally
        {
            stdoutWrite?.Dispose(); stderrWrite?.Dispose();
            if (!transferred)
            {
                process?.Dispose(); stdout?.Dispose(); stderr?.Dispose();
                stdoutRead?.Dispose(); stderrRead?.Dispose();
            }
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreateOutputPipe()
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        if (!CreatePipe(out var read, out var write, ref attributes, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (SetHandleInformation(read, 1, 0)) return (read, write);
        int error = Marshal.GetLastWin32Error(); read.Dispose(); write.Dispose(); throw new Win32Exception(error);
    }

    private static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("进程参数不能包含空字符", nameof(value));
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character); backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private sealed class ProcessJob : IDisposable
    {
        private IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        public ProcessJob()
        {
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimit>()))
            { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }
        public void Assign(IntPtr process) { if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception(); }
        public uint ActiveProcesses
        {
            get
            {
                if (!QueryInformationJobObject(handle, 1, out BasicAccounting info, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw new Win32Exception();
                return info.ActiveProcesses;
            }
        }
        public void Terminate() { if (handle != IntPtr.Zero) TerminateJobObject(handle, 1); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    { public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime; public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCharacters, YCharacters, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit information, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, int length, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
