using Aging.Core;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aging.Windows;

/// <summary>Scoped requests for real loads, without changing the user's power scheme.</summary>
public sealed class PowerRequestSession : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly List<int> active = [];
    private bool disposed;

    private PowerRequestSession(string reason)
    {
        IntPtr text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Flags = 1, Reason = new ReasonUnion { ModuleOrSimpleString = text } };
            handle = PowerCreateRequest(ref context);
        }
        finally { Marshal.FreeHGlobal(text); }
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法创建本轮负载电源请求"); }
        try
        {
            // Display + system prevent idle Modern Standby; execution also protects
            // the worker from process lifetime suspension. Explicit sleep is respected.
            foreach (int type in new[] { 0, 1, 3 })
            {
                if (!PowerSetRequest(handle, type)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保持本轮负载运行");
                active.Add(type);
            }
        }
        catch { Dispose(); throw; }
    }
    public static PowerRequestSession? Start(AgingItem item, string? reason = null) => item.IsStress || item.Kind == TaskKind.Video
        ? new(reason ?? "GMK 老化测试正在执行 " + (item.Kind == TaskKind.BurnIn && item.BurnInMode == 3 ? "DDR Test" : item.Name) + "，请勿空闲睡眠") : null;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (int type in active) PowerClearRequest(handle, type);
        active.Clear(); handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonUnion { public IntPtr ModuleOrSimpleString; public uint ReasonId, StringCount; public IntPtr ReasonStrings; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { public uint Version, Flags; public ReasonUnion Reason; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle request, int type);
}
