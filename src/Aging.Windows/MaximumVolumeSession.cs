using System.Runtime.InteropServices;

namespace Aging.Windows;

/// <summary>Temporarily changes the default multimedia endpoint, restoring its previous level and mute.</summary>
public sealed class MaximumVolumeSession : IDisposable
{
    private IAudioEndpointVolume? endpoint;
    private float original;
    private bool muted, changed;
    private readonly Action<string>? report;
    private MaximumVolumeSession(Action<string>? report) => this.report = report;
    public static MaximumVolumeSession Start(bool enabled, Action<string>? report = null)
    {
        var session = new MaximumVolumeSession(report);
        if (!enabled) return session;
        object? enumerator = null; IMMDevice? device = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!);
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var value));
            session.endpoint = (IAudioEndpointVolume)value;
            Marshal.ThrowExceptionForHR(session.endpoint.GetMasterVolumeLevelScalar(out session.original));
            Marshal.ThrowExceptionForHR(session.endpoint.GetMute(out session.muted));
            session.changed = true;
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(session.endpoint.SetMute(false, ref context));
            Marshal.ThrowExceptionForHR(session.endpoint.SetMasterVolumeLevelScalar(1, ref context));
            report?.Invoke("已按设置启用最大音量，测试结束时恢复原音量");
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            if (session.changed && session.endpoint != null)
            {
                Guid context = Guid.Empty;
                session.endpoint.SetMasterVolumeLevelScalar(session.original, ref context);
                session.endpoint.SetMute(session.muted, ref context);
            }
            session.Dispose(); report?.Invoke("最大音量设置失败：" + e.Message);
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        return session;
    }
    public void Dispose()
    {
        var current = endpoint; endpoint = null;
        if (current == null) return;
        try
        {
            if (changed)
            {
                Guid context = Guid.Empty;
                Marshal.ThrowExceptionForHR(current.GetMasterVolumeLevelScalar(out float now));
                Marshal.ThrowExceptionForHR(current.GetMute(out bool nowMuted));
                // Preserve a manual volume change made while the test was running.
                if (Math.Abs(now - 1) < 0.001F && !nowMuted)
                {
                    Marshal.ThrowExceptionForHR(current.SetMasterVolumeLevelScalar(original, ref context));
                    Marshal.ThrowExceptionForHR(current.SetMute(muted, ref context));
                    report?.Invoke("已恢复测试前音量");
                }
            }
        }
        catch (COMException e) { report?.Invoke("音量恢复失败：" + e.Message); }
        finally { Marshal.ReleaseComObject(current); }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    { [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
