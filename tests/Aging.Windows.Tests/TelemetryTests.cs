using Aging.Core;
using Aging.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Aging.Windows.Tests;

public sealed class TelemetryTests(ITestOutputHelper output)
{
    [Fact]
    public void GpuLoadSumsProcessesWithinAnEngineAndKeepsConcurrentEnginesSeparate()
    {
        var samples = new KeyValuePair<string, double>[]
        {
            new("pid_1_luid_0x00000000_0x00000123_phys_0_eng_0_engtype_3D", 40),
            new("pid_2_luid_0x00000000_0x00000123_phys_0_eng_0_engtype_3D", 35),
            new("pid_3_luid_0x00000000_0x00000123_phys_0_eng_1_engtype_Copy", 65),
            new("pid_4_luid_0x00000000_0x00000456_phys_0_eng_0_engtype_3D", 30)
        };
        var result = LoadTelemetryReader.AggregateGpuEngines(samples);
        Assert.Equal(3, result.Count);
        Assert.Equal(75, result.Max(e => e.Percent));
        Assert.Equal(65, result.Single(e => e.Engine.EndsWith("Copy")).Percent);
    }

    [Fact]
    public void GpuLoadRejectsInvalidValuesAndDoesNotRepeatIdenticalInstances()
    {
        string key = "pid_1_luid_0x0_0x123_phys_0_eng_0_engtype_3D";
        var result = LoadTelemetryReader.AggregateGpuEngines([
            new(key, 80), new(key, 80), new("_Total", 10000),
            new("pid_2_luid_0x0_0x123_phys_0_eng_0_engtype_3D", double.NaN),
            new("pid_3_luid_0x0_0x123_phys_0_eng_0_engtype_3D", -1)]);
        Assert.Single(result); Assert.Equal(80, result[0].Percent);
        Assert.Empty(LoadTelemetryReader.AggregateGpuEngines([]));
    }

    [Fact]
    public async Task NativeReaderPreservesUnavailableInitialCpuAndReadsRealMemory()
    {
        using var reader = new LoadTelemetryReader();
        var first = reader.Read();
        Assert.Null(first.CpuPercent);
        Assert.True(first.MemoryTotalBytes > 0);
        Assert.InRange(first.MemoryPercent!.Value, 0, 100);
        await Task.Delay(1100);
        var second = reader.Read();
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(second));
        Assert.NotNull(second.CpuPercent); Assert.InRange(second.CpuPercent.Value, 0, 100);
        Assert.All(second.GpuEngines, engine => Assert.InRange(engine.Percent, 0, 100));
        reader.Dispose(); Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [Fact]
    public async Task StoppingCollectorFlushesBothFormatsAndPreventsFurtherWrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "gw-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var collector = new TelemetryCollector();
            var run = AgingCoordinator.Create([AgingItem.Parse("BurnIn|0.1|0")], new AppOptions(), false, root);
            run.Phase = RunPhase.Running;
            var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            collector.Sampled += _ => sampled.TrySetResult();
            collector.Start(run);
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            collector.Stop();
            string legacy = Path.Combine(run.LogDirectory, "GW_CoreTemp.log"), load = Path.Combine(run.LogDirectory, "load-telemetry.tsv");
            Assert.StartsWith("Item\tTemp\tCPU%\tBAT%\tPower\tCharge\tDate", File.ReadAllText(legacy));
            Assert.Contains("MemoryTotalBytes", File.ReadAllText(load));
            long bytes = new FileInfo(load).Length;
            await Task.Delay(3200);
            Assert.Equal(bytes, new FileInfo(load).Length);
            collector.Dispose(); Assert.Throws<ObjectDisposedException>(() => collector.Start(run));
        }
        finally { Directory.Delete(root, true); }
    }
}
