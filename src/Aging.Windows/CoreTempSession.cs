using Aging.Core;
using System.Diagnostics;

namespace Aging.Windows;

/// <summary>Owns only the Core Temp process launched for this run. Existing user instances are left alone.</summary>
public sealed class CoreTempSession : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? execution;
    private string? work;
    private CoreTempSession() { }

    public static CoreTempSession Start(RunSnapshot snapshot, Action<string>? report = null)
    {
        var session = new CoreTempSession();
        if (snapshot.Simulation || CoreTempReader.Read() != null) return session;
        try
        {
            if (HasExistingProvider())
            {
                report?.Invoke("已有 Core Temp 实例但共享内存不可用；保留该实例，温度显示未获取");
                return session;
            }
            string? executable = FindExecutable(snapshot.Options.ReferenceRoot);
            if (executable == null)
            {
                report?.Invoke("未找到 Core Temp 温度组件，温度显示未获取");
                return session;
            }
            string directory = Path.Combine(Path.GetTempPath(), "GW.AgingTool", "CoreTemp", Guid.NewGuid().ToString("N"));
            session.work = directory;
            Directory.CreateDirectory(directory);
            string copy = Path.Combine(directory, Path.GetFileName(executable));
            File.Copy(executable, copy);
            // A private INI prevents writes in the reference/install directory. Disable update checks,
            // logging, plugins and overheat actions; Core Temp's own sensor support remains authoritative.
            File.WriteAllText(Path.Combine(directory, "CoreTemp.ini"), "[General]\r\nReadInt=1000;\r\nLogInt=10;\r\nPlugins=0;\r\nEnLog=0;\r\nSingleInstance=1;\r\nAutoUpdateCheck=0;\r\n[Display]\r\nFahr=0;\r\nMinimized=1;\r\nCloseToSystray=0;\r\nHideTaskbarButton=1;\r\n[Advanced]\r\nShowDTJ=0;\r\nSnmpSharedMemory=0;\r\n[Overheat protection settings]\r\nEnableOHP=0;\r\nEnableShutDown=0;\r\nExecute=;\r\n");
            session.execution = session.RunAsync(copy, snapshot.LogDirectory, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { report?.Invoke("Core Temp 温度组件未能启动：" + e.Message); }
        return session;
    }

    public static string? FindExecutable(string referenceRoot)
    {
        string relative = Path.Combine("Other", "Tools", "CoreTemp", Environment.Is64BitOperatingSystem ? "CoreTemp_x64.exe" : "CoreTemp_x86.exe");
        foreach (string root in new[] { referenceRoot, Path.Combine(AppContext.BaseDirectory, "Tools") })
            if (!string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, relative))) return Path.Combine(root, relative);
        return null;
    }

    private static bool HasExistingProvider()
    {
        foreach (string name in new[] { "Core Temp", "CoreTemp", "CoreTemp_x64", "CoreTemp_x86" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) { if (!process.HasExited) return true; }
            }
        }
        return false;
    }

    private async Task RunAsync(string executable, string logDirectory, Action<string>? report)
    {
        try
        {
            var result = await new OwnedProcessRunner().RunAsync(executable, [], Path.GetDirectoryName(executable)!, Path.Combine(logDirectory, "CoreTemp", "provider"), Timeout.InfiniteTimeSpan, stop.Token,
                pid => report?.Invoke("已启动本轮 Core Temp 温度组件，PID=" + pid));
            if (!stop.IsCancellationRequested) report?.Invoke("Core Temp 已退出（" + result.ExitCode + "）；没有有效传感器数据时温度显示未获取");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { report?.Invoke("Core Temp 温度组件不可用：" + e.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (execution != null) await execution;
        stop.Dispose();
        if (work != null)
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Windows may retain a provider driver file; never broaden cleanup. */ }
        }
    }
}
