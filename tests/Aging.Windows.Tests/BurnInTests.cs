using Aging.Core;
using Aging.Windows;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class BurnInTests
{
    private const string Good = "BurnInTest V9.2 1006 (64-bit)\r\nTest Duration: 000h 06m 00s\r\nCPU  12  107 Trillion  PASS  0  No errors\r\nGPGPU  12  107 Trillion  PASS  0  No errors\r\nMemory (RAM)  12  107 Trillion  PASS  0  No errors\r\n2D Graphics  2  12000  PASS  0  No errors\r\n3D Graphics  1  6000  PASS  0  No errors\r\nSound  1  6000  PASS  0  No errors\r\nVideo Playback  1  60  PASS  0  No errors\r\nDisk (C:)  3  4.970 Trillion  PASS  0  No errors\r\nTEST RUN PASSED\r\n";
    [Fact]
    public void EveryEnabledComponentRequiresActualOperationsAndZeroErrors()
    {
        foreach (string test in new[] { "GPGPU", "2D Graphics", "3D Graphics", "Sound", "Video Playback" })
            Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace(test, "Unexpected"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(Good.Replace("PASS  0", "PASS  1"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("107 Trillion", "0 Trillion"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good + Good, 0.1, 0).Outcome);
    }
    [Fact]
    public void ValidOriginalReportAndDurationAreRequired()
    {
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(Good, 0.1, 0).Outcome);
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(Good, 0.1, 2).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(Good.Replace("06m", "01m"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(Good.Replace("PASSED", "FAILED"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport("TEST RUN PASSED", 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("Disk (C:)", "No disk"), 0.1, 2).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("Sound  ", "Other  "), 0.1, 2).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("TEST RUN PASSED", ""), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("Memory (RAM)", "No RAM"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("CPU  ", "Other  "), 0.1, 1).Outcome);
    }
    [Fact]
    public void DdrRequiresAnIsolatedMemoryReportWithRealOperations()
    {
        const string memory = "Test Duration: 000h 06m 02s\r\nMemory (RAM)  12  107 Trillion  PASS  0  No errors\r\nTEST RUN PASSED\r\n";
        Assert.Equal("BurnIn|0.1|3", AgingItem.Parse("BurnIn|0.1|3").Serialize());
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(memory, 0.1, 3).Outcome);
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(memory.Replace("TEST RUN PASSED", "Temperature  -  -  PASS  0  No errors\r\nTEST RUN PASSED"), 0.1, 3).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(memory.Replace("107 Trillion", "0 Trillion"), 0.1, 3).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(memory.Replace("PASS  0", "FAIL  1"), 0.1, 3).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good, 0.1, 3).Outcome);
        string script = BurnInAdapter.CreateScript(@"C:\QA\mode.bitcfg", @"C:\QA\result.log", .1, 3);
        Assert.Contains("SETRAM TYPE standard PATTERN cyclic PRETEST no LOG yes", script);
        Assert.Contains("SETDUTYCYCLE MEMORY 100", script);
        Assert.EndsWith("RUN MEMORY\r\nEXIT\r\n", script);
    }
    [Fact]
    public void ScriptUsesMinutesAndOverridesPreAndPostActions()
    {
        string script = BurnInAdapter.CreateScript(@"C:\QA\mode.bitcfg", @"C:\QA\result.log", 0.1);
        Assert.Contains("SETDURATION 6\r\n", script);
        Assert.Contains("SETCYCLES 0\r\n", script);
        Assert.Contains("SETPRETEST ACTION NONE", script);
        Assert.Contains("AUTO_STOP_P NORMAL", script);
        Assert.Contains("AUTO_STOP_F NORMAL", script);
        Assert.EndsWith("RUN CONFIG\r\nEXIT\r\n", script);
        Assert.Throws<IOException>(() => BurnInAdapter.CreateScript("C:\\QA\\input\"\r\nEXIT", @"C:\QA\result.log", 0.1));
    }
    [Fact]
    public void EngineDefaultWritesStayInTheCopy()
    {
        string root = Path.Combine(Path.GetTempPath(), "gw-engine-isolation", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "原工具"), work = Path.Combine(root, "work");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "bit.exe"), "non-executable test data");
            File.WriteAllText(Path.Combine(source, "LastUsed.bitcfg"), "original default");
            File.WriteAllText(Path.Combine(source, "LastUsed_0.bitcfg"), "selected mode");
            string copied = BurnInAdapter.CreateEngineCopy(Path.Combine(source, "bit.exe"), Path.Combine(source, "LastUsed_0.bitcfg"), work, CancellationToken.None);
            string changed = Path.Combine(Path.GetDirectoryName(copied)!, "LastUsed.bitcfg");
            Assert.Equal("selected mode", File.ReadAllText(changed));
            File.WriteAllText(changed, "engine changed its default");
            Assert.Equal("original default", File.ReadAllText(Path.Combine(source, "LastUsed.bitcfg")));
            Assert.Equal("selected mode", File.ReadAllText(Path.Combine(source, "LastUsed_0.bitcfg")));
        }
        finally { Directory.Delete(root, true); }
    }
}
