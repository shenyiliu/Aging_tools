using Aging.Core;
using Xunit;

namespace Aging.Tests;

public sealed class LegacyTimingTests
{
    [Fact]
    public void RebootAndSleepUseDifferentComparisonMeans()
    {
        var reboot = AgingItem.Parse("Reboot|3|30|N|Y|N|N");
        var sleep = AgingItem.Parse("S3|3|30|30|N|Y|N");
        Assert.Empty(LegacyTiming.Check(reboot, [30], 100)); // 100 / ((30 + 100) / 2) < 2
        Assert.Single(LegacyTiming.Check(sleep, [30], 100)); // 100 / 30 >= 2
        Assert.Single(LegacyTiming.Check(reboot, [30, 30], 120)); // 120 / 60 == 2
        Assert.Single(LegacyTiming.Check(reboot, [], 0));
    }

    [Fact]
    public void SleepChecksConfiguredDurationFromTheSecondCycleOnly()
    {
        var item = AgingItem.Parse("S4|3|30|60|N|Y|N");
        Assert.Empty(LegacyTiming.Check(item, [], 20));
        Assert.Single(LegacyTiming.Check(item, [60], 20));
        Assert.Empty(LegacyTiming.Check(item with { TimeCheck = false }, [60], 20));
    }

    [Fact]
    public void LegacyIniDatesAndTimestampsMatchTheRecoveredReference()
    {
        string root = Path.Combine(Path.GetTempPath(), "gw-log-timestamp", Guid.NewGuid().ToString("N"));
        try
        {
            var run = AgingCoordinator.Create([AgingItem.Parse("Video")], new AppOptions(), false, root);
            var started = new DateTimeOffset(2026, 11, 5, 18, 1, 4, TimeSpan.FromHours(8));
            run.Items[0].StartedAt = started;
            run.Items[0].EndedAt = started.AddSeconds(3);
            new LegacyLogWriter().Write(run);
            var doc = IniDocument.Load(LegacyLogWriter.LogPath(run));
            Assert.Equal("2026/11/05 18:01:04", doc.Get("1_Video_Info", "FirstTime"));
            Assert.Equal("135626464", doc.Get("1_Video_Info", "FirstTimeStamp"));
            Assert.Equal("135626467", doc.Get("1_Video_Info", "EndTimeStamp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
