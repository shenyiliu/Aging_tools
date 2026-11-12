using Aging.Core;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Aging.Windows;

public sealed class TelemetryCollector : IDisposable
{
    private readonly System.Threading.Timer timer;
    private readonly object gate = new();
    private LoadTelemetryReader? loads;
    private bool disposed;
    private RunSnapshot? run;
    public event Action<TelemetrySample>? Sampled;
    public event Action<string>? Error;
    public TelemetryCollector() => timer = new System.Threading.Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    public void Start(RunSnapshot snapshot)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            loads?.Dispose(); loads = new LoadTelemetryReader(); run = snapshot;
            timer.Change(0, 3000);
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            timer.Change(Timeout.Infinite, Timeout.Infinite); run = null;
            loads?.Dispose(); loads = null;
        }
    }
    private void Sample()
    {
        if (!Monitor.TryEnter(gate)) return;
        TelemetrySample? published = null;
        string? error = null;
        try
        {
            var snapshot = run;
            if (disposed || snapshot == null || snapshot.IsTerminal || snapshot.Phase == RunPhase.AwaitingReboot) return;
            var load = loads!.Read();
            double? cpu = load.CpuPercent;
            int? battery = null; string power = "Unavailable", charge = "Unavailable";
            if (GetSystemPowerStatus(out var status))
            {
                battery = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
                power = status.ACLineStatus switch { 0 => "DC", 1 => "AC", _ => "Unavailable" };
                charge = status.BatteryFlag == 255 ? "Unavailable" : (status.BatteryFlag & 128) != 0 ? "NoBattery" : (status.BatteryFlag & 8) != 0 ? "Charging" : "NotCharging";
            }
            double? temperature = snapshot.Simulation ? null : CoreTempReader.Read()?.TemperatureCelsius;
            var sample = new TelemetrySample(DateTimeOffset.Now, snapshot.CurrentIndex + 1, temperature, cpu, battery, power, charge);
            string path = Path.Combine(snapshot.LogDirectory, "GW_CoreTemp.log");
            Directory.CreateDirectory(snapshot.LogDirectory);
            if (!File.Exists(path)) File.WriteAllText(path, "Item\tTemp\tCPU%\tBAT%\tPower\tCharge\tDate\r\n", new UTF8Encoding(false));
            string Number(double? number) => number?.ToString("0.0", CultureInfo.InvariantCulture) ?? "Unavailable";
            string RawNumber(double? number) => number?.ToString("R", CultureInfo.InvariantCulture) ?? "Unavailable";
            File.AppendAllText(path, $"{sample.Item}\t{Number(sample.Temperature)}\t{Number(cpu)}\t{battery?.ToString() ?? "Unavailable"}\t{power}\t{charge}\t{sample.Time:yyyy-MM-dd HH:mm:ss}\r\n", new UTF8Encoding(false));
            string loadPath = Path.Combine(snapshot.LogDirectory, "load-telemetry.tsv");
            if (!File.Exists(loadPath))
            {
                File.WriteAllText(loadPath, "Date\tItem\tCPU%\tGPU%\tMemory%\tMemoryUsedBytes\tMemoryTotalBytes\tDiskActive%\tDiskReadBytesPerSecond\tDiskWriteBytesPerSecond\tGPUEngines\tStatus\r\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(snapshot.LogDirectory, "load-telemetry-source.txt"),
                    "IntervalSeconds=3\r\nCPU=GetSystemTimes busy delta / total delta\r\nMemory=GlobalMemoryStatusEx physical used / total\r\nDiskActive=100 - PDH PhysicalDisk(_Total) % Idle Time\r\nDiskReadWrite=PDH PhysicalDisk(_Total) bytes/sec\r\nGPU=PDH GPU Engine Utilization Percentage; sum processes per physical engine, then maximum engine across adapters\r\nTemperature=Core Temp shared memory in GW_CoreTemp.log; unavailable sensors remain Unavailable; GPU temperature is not collected\r\nStatus=Native error/status codes or CPU=Baseline for the first CPU sample; no unavailable value is replaced with zero\r\n", new UTF8Encoding(false));
            }
            string engines = string.Join(';', load.GpuEngines.Select(e => e.Engine + "=" + RawNumber(e.Percent)));
            File.AppendAllText(loadPath, $"{sample.Time:O}\t{sample.Item}\t{RawNumber(cpu)}\t{RawNumber(load.GpuPercent)}\t{RawNumber(load.MemoryPercent)}\t{load.MemoryUsedBytes?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}\t{load.MemoryTotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}\t{RawNumber(load.DiskActivePercent)}\t{RawNumber(load.DiskReadBytesPerSecond)}\t{RawNumber(load.DiskWriteBytesPerSecond)}\t{engines}\t{load.Status}\r\n", new UTF8Encoding(false));
            published = sample;
        }
        catch (Exception e) { error = e.Message; }
        finally { Monitor.Exit(gate); }
        try { if (published != null) Sampled?.Invoke(published); }
        catch (Exception e) { error = e.Message; }
        try { if (error != null) Error?.Invoke(error); }
        catch (Exception) { /* A closing UI must not crash a thread-pool sampling callback. */ }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            timer.Change(Timeout.Infinite, Timeout.Infinite); run = null; disposed = true;
            loads?.Dispose(); loads = null; timer.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
