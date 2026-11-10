using Aging.Core;
using Aging.Windows;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class FanTests
{
    [Fact]
    public void HexPairsAndDecimalAveragesNeedAllFourActualReadings()
    {
        Assert.Equal(Outcome.Pass, FanTestAdapter.Evaluate("IP3", ["value = 0x10", "value = 0x00", "value = 0x17", "value = 0x70"], false, 0).Outcome);
        Assert.Equal(Outcome.Pass, FanTestAdapter.Evaluate("IP3", ["value = 0x00000010", "value = 0x00000000", "value = 0x00000017", "value = 0x00000070"], false, 0).Outcome);
        Assert.Equal(Outcome.Pass, FanTestAdapter.Evaluate("WB", ["5100", "5300", "2800", "3000"], false, 0).Outcome);
        Assert.Contains("5200", FanTestAdapter.Evaluate("WB", ["5100", "5300", "2800", "3000"], false, 0).Message);
        Assert.Equal(Outcome.Fail, FanTestAdapter.Evaluate("Ed", ["5100", "5300", "2800", "3000"], true, 0).Outcome);
        Assert.Equal(Outcome.Fail, FanTestAdapter.Evaluate("WB", ["5100", "5300", "2800", "3000"], false, 1).Outcome);
        Assert.Equal(Outcome.Fail, FanTestAdapter.Evaluate("WB", [], false, 0).Outcome);
        Assert.Equal(Outcome.Fail, FanTestAdapter.Evaluate("Ed", ["5100", "5300", "0", "0"], false, 0).Outcome);
        Assert.Equal(Outcome.Fail, FanTestAdapter.Evaluate("IP3", ["garbage", "garbage", "garbage", "garbage"], false, 0).Outcome);
    }

    [Fact]
    public void PrivateRuntimeScriptFixesOnlyKnownWaitAndConditionalLines()
    {
        string original = "if %fanr2% LSS 5000 set errmsg=\"Fan speed (100 PWM) < 5000 RPM\" & goto fail";
        Assert.Equal("if %fanr2% LSS 5000 (set errmsg=\"Fan speed (100 PWM) < 5000 RPM\" & goto fail)", FanTestAdapter.TransformKnownLine(original, "WB"));
        Assert.Equal("if %fanr4:~15,4% GTR 4000 (set errmsg=\"high\" & goto fail)", FanTestAdapter.TransformKnownLine("if %fanr4:~15,4% GTR 4000 errmsg=\"high\" & goto fail", "Ed"));
        Assert.Equal(original, FanTestAdapter.TransformKnownLine(original, "IP3"));
        Assert.Contains("Start-Sleep -Seconds 3", FanTestAdapter.TransformKnownLine("timeout /t 3", "WB"));
        Assert.Equal("del *.txt /f /q", FanTestAdapter.TransformKnownLine("del *.txt /f /q", "WB"));
        Assert.Throws<IOException>(() => FanTestAdapter.CreateRuntimeScript("WB", [0, 1, 2]));
    }

    [Fact]
    public void EachVendorHasAnExplicitAutomaticModeRestorePlan()
    {
        Assert.Equal(new[] { "/set", "0" }, FanTestAdapter.RestoreCommands("WB").Single().Arguments);
        Assert.Equal(new[] { "-c" }, FanTestAdapter.RestoreCommands("Ed").Single().Arguments);
        var ip3 = FanTestAdapter.RestoreCommands("IP3");
        Assert.Equal(12, ip3.Count);
        Assert.Equal("/command=oid 0x4e 0x4f 0x2f 0x00", ip3[5].Arguments[0]);
        Assert.Equal("/command=oid 0x4e 0x4f 0x2f 0x19", ip3[7].Arguments[0]);
        Assert.Equal(2, ip3[5].DelayAfterSeconds);
        Assert.Equal("/command=oid 0x4e 0x4f 0x2f 0x00", ip3[11].Arguments[0]);
    }
}
