using Aging.Core;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Aging.Windows;

public sealed class TelemetryCollector : IDisposable
{
    private readonly System.Threading.Timer timer;
    private ulong previousIdle, previousTotal;
    private int sampling;
    private RunSnapshot? run;
    public event Action<TelemetrySample>? Sampled;
    public event Action<string>? Error;
    public TelemetryCollector() => timer = new System.Threading.Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    public void Start(RunSnapshot snapshot) { run = snapshot; previousTotal = 0; timer.Change(0, 10000); }
    public void Stop() { timer.Change(Timeout.Infinite, Timeout.Infinite); run = null; }
    private void Sample()
    {
        if (Interlocked.Exchange(ref sampling, 1) != 0) return;
        try
        {
            var snapshot = run;
            if (snapshot == null || snapshot.IsTerminal || snapshot.Phase == RunPhase.AwaitingReboot) return;
            double? cpu = null;
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                ulong total = (ulong)(kernel + user), id = (ulong)idle;
                if (previousTotal > 0 && total > previousTotal) cpu = Math.Clamp(100.0 * (1 - (double)(id - previousIdle) / (total - previousTotal)), 0, 100);
                previousTotal = total; previousIdle = id;
            }
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
            File.AppendAllText(path, $"{sample.Item}\t{Number(sample.Temperature)}\t{Number(cpu)}\t{battery?.ToString() ?? "Unavailable"}\t{power}\t{charge}\t{sample.Time:yyyy-MM-dd HH:mm:ss}\r\n", new UTF8Encoding(false));
            Sampled?.Invoke(sample);
        }
        catch (Exception e) { Error?.Invoke(e.Message); }
        finally { Volatile.Write(ref sampling, 0); }
    }
    public void Dispose() { Stop(); using var done = new ManualResetEvent(false); if (timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(5)); }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
