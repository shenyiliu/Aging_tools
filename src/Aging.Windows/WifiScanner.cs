using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aging.Windows;

public static class WifiScanner
{
    public static async Task<int> ScanAsync(CancellationToken ct)
    {
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr client);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            error = WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr list);
            if (error != 0) throw new Win32Exception((int)error);
            var interfaces = new List<Guid>();
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++) interfaces.Add(Marshal.PtrToStructure<WlanInterface>(IntPtr.Add(list, 8 + i * Marshal.SizeOf<WlanInterface>())).Id);
            }
            finally { WlanFreeMemory(list); }
            foreach (var guid in interfaces)
            { var id = guid; error = WlanScan(client, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); if (error != 0) throw new Win32Exception((int)error); }
            if (interfaces.Count == 0) return 0;
            await Task.Delay(4000, ct);
            int total = 0;
            foreach (var guid in interfaces)
            {
                var id = guid;
                error = WlanGetAvailableNetworkList(client, ref id, 0, IntPtr.Zero, out IntPtr networks);
                if (error != 0) throw new Win32Exception((int)error);
                try { total += Marshal.ReadInt32(networks); }
                finally { WlanFreeMemory(networks); }
            }
            return total;
        }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WlanInterface
    { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public int State; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr client, ref Guid id, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr client, ref Guid id, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
