using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Aging.Windows;

public sealed record GpuEngineLoad(string Engine, double Percent);
public sealed record LoadTelemetryReading(double? CpuPercent, double? GpuPercent, double? MemoryPercent,
    ulong? MemoryUsedBytes, ulong? MemoryTotalBytes, double? DiskActivePercent,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond,
    IReadOnlyList<GpuEngineLoad> GpuEngines, string Status);

/// <summary>System-wide measurements, independent of a test engine's claimed results.</summary>
public sealed class LoadTelemetryReader : IDisposable
{
    private readonly object gate = new();
    private readonly List<string> setupErrors = [];
    private IntPtr query, gpu, diskIdle, diskRead, diskWrite;
    private ulong previousIdle, previousTotal;
    private bool disposed, haveCpuBaseline;

    public LoadTelemetryReader()
    {
        uint status = PdhOpenQueryW(null, UIntPtr.Zero, out query);
        if (status != 0) { setupErrors.Add("PDH.Query=" + Code(status)); return; }
        gpu = Add(@"\GPU Engine(*)\Utilization Percentage", "GPU");
        diskIdle = Add(@"\PhysicalDisk(_Total)\% Idle Time", "DiskIdle");
        diskRead = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", "DiskRead");
        diskWrite = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", "DiskWrite");
        PdhCollectQueryData(query); // Rate counters need a previous collection.
    }

    private IntPtr Add(string path, string name)
    {
        uint status = PdhAddEnglishCounterW(query, path, UIntPtr.Zero, out var counter);
        if (status == 0) return counter;
        setupErrors.Add(name + "=" + Code(status)); return IntPtr.Zero;
    }

    public LoadTelemetryReading Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var errors = new List<string>(setupErrors);
            double? cpu = null, memoryPercent = null;
            ulong? memoryUsed = null, memoryTotal = null;
            if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                ulong total = kernel + user;
                if (haveCpuBaseline && total > previousTotal && idle >= previousIdle)
                    cpu = Math.Clamp(100.0 * (1 - (double)(idle - previousIdle) / (total - previousTotal)), 0, 100);
                else errors.Add("CPU=Baseline");
                previousTotal = total; previousIdle = idle; haveCpuBaseline = true;
            }
            else errors.Add("CPU=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 && memory.AvailablePhysical <= memory.TotalPhysical)
            {
                memoryTotal = memory.TotalPhysical; memoryUsed = memory.TotalPhysical - memory.AvailablePhysical;
                memoryPercent = 100.0 * memoryUsed.Value / memory.TotalPhysical;
            }
            else errors.Add("Memory=Unavailable");
            uint collected = query != IntPtr.Zero ? PdhCollectQueryData(query) : 1;
            IReadOnlyList<GpuEngineLoad> engines = [];
            double? diskActive = null, read = null, write = null;
            if (collected == 0)
            {
                engines = ReadGpu(errors);
                var idlePercent = Counter(diskIdle, "DiskIdle", errors);
                diskActive = idlePercent.HasValue ? Math.Clamp(100 - idlePercent.Value, 0, 100) : null;
                read = Counter(diskRead, "DiskRead", errors); write = Counter(diskWrite, "DiskWrite", errors);
            }
            else errors.Add("PDH.Collect=" + Code(collected));
            return new(cpu, engines.Count > 0 ? engines.Max(e => e.Percent) : null, memoryPercent,
                memoryUsed, memoryTotal, diskActive, read, write, engines, errors.Count == 0 ? "OK" : string.Join(';', errors));
        }
    }

    private static double? Counter(IntPtr counter, string name, List<string> errors)
    {
        if (counter == IntPtr.Zero) return null;
        uint status = PdhGetFormattedCounterValue(counter, 0x200 | 0x8000, out _, out var value);
        if (status == 0 && value.Status <= 1 && double.IsFinite(value.Value) && value.Value >= 0) return value.Value;
        errors.Add(name + "=" + Code(status != 0 ? status : value.Status)); return null;
    }

    private IReadOnlyList<GpuEngineLoad> ReadGpu(List<string> errors)
    {
        if (gpu == IntPtr.Zero) return [];
        // Repeat the sizing call if the instance list grows between the two calls.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            uint status = PdhGetFormattedCounterArrayW(gpu, 0x200 | 0x8000, ref bytes, out _, IntPtr.Zero);
            if (status != 0x800007D2 || bytes == 0 || bytes > 32 * 1024 * 1024)
            { errors.Add("GPU=" + Code(status)); return []; }
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArrayW(gpu, 0x200 | 0x8000, ref bytes, out uint count, buffer);
                if (status == 0x800007D2) continue;
                if (status != 0) { errors.Add("GPU=" + Code(status)); return []; }
                int size = Marshal.SizeOf<CounterItem>();
                if (count > bytes / size) { errors.Add("GPU=InvalidArray"); return []; }
                var instances = new List<KeyValuePair<string, double>>();
                for (int i = 0; i < count; i++)
                {
                    var value = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * size));
                    string? name = Marshal.PtrToStringUni(value.Name);
                    if (value.Value.Status <= 1 && name != null && double.IsFinite(value.Value.Value) && value.Value.Value >= 0)
                        instances.Add(new(name, value.Value.Value));
                }
                var result = AggregateGpuEngines(instances);
                if (result.Count == 0) errors.Add("GPU=NoValidEngineSamples");
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        errors.Add("GPU=InstanceListChanged"); return [];
    }

    /// <summary>Sum process contributions to the same physical engine, then use the busiest engine.
    /// Engines may run concurrently, so their percentages must not be summed into overall GPU load.</summary>
    public static IReadOnlyList<GpuEngineLoad> AggregateGpuEngines(IEnumerable<KeyValuePair<string, double>> instances)
    {
        var groups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (!double.IsFinite(instance.Value) || instance.Value < 0 || !seen.Add(instance.Key)) continue;
            var match = Regex.Match(instance.Key, @"^pid_\d+_(luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+_eng_\d+_engtype_.+?)(?:#\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            string key = match.Groups[1].Value;
            groups[key] = groups.GetValueOrDefault(key) + instance.Value;
        }
        return groups.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new GpuEngineLoad(kv.Key, Math.Clamp(kv.Value, 0, 100))).ToArray();
    }

    private static string Code(uint status) => "0x" + status.ToString("X8", CultureInfo.InvariantCulture);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
