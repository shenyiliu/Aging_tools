using Aging.Core;
using Aging.Windows;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class BurnInTests
{
    private const string Good = "BurnInTest V9.2 1006 (64-bit)\r\nTest Duration: 000h 06m 00s\r\nCPU  12  107 Trillion  PASS  0  No errors\r\nMemory (RAM)  12  107 Trillion  PASS  0  No errors\r\nDisk (C:)  3  4.970 Trillion  PASS  0  No errors\r\nTEST RUN PASSED\r\n";
    [Fact]
    public void ValidOriginalReportAndDurationAreRequired()
    {
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(Good, 0.1, 0).Outcome);
        Assert.Equal(Outcome.Pass, BurnInAdapter.EvaluateReport(Good, 0.1, 2).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(Good.Replace("06m", "01m"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Fail, BurnInAdapter.EvaluateReport(Good.Replace("PASSED", "FAILED"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport("TEST RUN PASSED", 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("Disk (C:)", "No disk"), 0.1, 2).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("TEST RUN PASSED", ""), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("Memory (RAM)", "No RAM"), 0.1, 0).Outcome);
        Assert.Equal(Outcome.Uncertain, BurnInAdapter.EvaluateReport(Good.Replace("CPU  ", "Other  "), 0.1, 1).Outcome);
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
