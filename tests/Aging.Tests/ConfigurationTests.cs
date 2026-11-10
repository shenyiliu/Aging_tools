using Aging.Core;
using System.Text;
using Xunit;

namespace Aging.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gw-aging-tests", Guid.NewGuid().ToString("N"));
    public ConfigurationTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("Y", true)] [InlineData("1", true)] [InlineData("N", false)] [InlineData("4", false)]
    public void CheckboxStatesAreNotTimeThresholds(string value, bool expected) => Assert.Equal(expected, AgingItem.LegacyBool(value));
    [Fact]
    public void OriginalQueueNormalizesChecksAndKeepsOrderAndDuplicateKinds()
    {
        var doc = IniDocument.Parse("[AgingSwitch]\r\n8 = Reboot|50|30|1|4|1|N\r\n2 = S4|50|60|60|1|4|1\r\n1 = S4|3|30|60|N|Y|N\r\n0 = BurnIn|1|0");
        var queue = LegacyConfig.ReadQueue(doc);
        Assert.Equal(TaskKind.Reboot, queue[0].Kind); Assert.True(queue[0].HardwareCheck); Assert.False(queue[0].TimeCheck);
        Assert.Equal(2, queue.Count(x => x.Kind == TaskKind.S4)); Assert.Equal(3600, queue[^1].Hours * 3600);
        LegacyConfig.WriteQueue(doc, queue); Assert.Equal(queue, LegacyConfig.ReadQueue(doc));
    }
    [Theory]
    [InlineData("BurnIn|0.09|0")] [InlineData("FurMark|NaN|8X|1920x1080|Y")] [InlineData("Reboot|0|30|Y|N|N|N")]
    [InlineData("Reboot|1|29|Y|N|N|N")] [InlineData("S3|1|30|1|N|N|N")] [InlineData("BurnIn|1|7")]
    [InlineData("Unknown")] [InlineData("Video|1")] [InlineData("Reboot|1|30|2|N|N|N")]
    public void InvalidConfigurationCannotRun(string raw) => Assert.ThrowsAny<Exception>(() => AgingItem.Parse(raw));
    [Fact]
    public void UntouchedSectionsCommentsAndUtf16EncodingSurviveSave()
    {
        string path = Path.Combine(root, "配置 中文.ini");
        string text = "; 注释\r\n[Unknown]\r\n Secret = 不公开\r\n[AgingSwitch]\r\n0 = Video\r\n; queue comment\r\n[Last]\r\n Foo = Bar\r\n";
        File.WriteAllText(path, text, Encoding.Unicode);
        var doc = IniDocument.Load(path); LegacyConfig.WriteQueue(doc, [AgingItem.Parse("BurnIn|0.1|2"), AgingItem.Parse("Video")]); doc.Set("Last", "Foo", "Baz"); doc.Save(path);
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xff, 0xfe }));
        string result = File.ReadAllText(path); Assert.Contains(" Secret = 不公开\r\n", result); Assert.Contains("; queue comment\r\n", result);
        Assert.Equal(text, File.ReadAllText(path + ".bak")); Assert.Equal(2, LegacyConfig.ReadQueue(IniDocument.Load(path)).Count);
    }
    [Fact]
    public void NewKeyIsInsertedInItsOwnSection()
    {
        var doc = IniDocument.Parse("[A]\na=1\n[B]\nb=2\n[C]\nc=3"); doc.Set("A", "new", "value");
        Assert.Equal("value", doc.Get("A", "new")); Assert.Equal("", doc.Get("C", "new"));
    }
    [Fact]
    public void OldCliAcceptsChineseWindowsPathsAndClampsSleepTime()
    {
        var request = LegacyCommandLine.Parse("Sleep", @"/S4 /2 /30 /C:\日志 中文\本轮.log /10 /N /0 /Chinese /1 /4 /N /Y/2");
        Assert.Equal(30, request.Item.SleepSeconds); Assert.False(request.Item.HardwareCheck); Assert.True(request.Item.WifiCheck);
        Assert.Equal(2, request.ItemIndex); Assert.Equal(@"C:\日志 中文\本轮.log", request.LogPath);
    }
    [Fact]
    public void LegacyCliRejectsForwardSlashPathsWithAmbiguousFields() => Assert.Throws<FormatException>(() => LegacyCommandLine.Parse("Reboot", "/3 /30 /C:/logs/a.log /N /Chinese /Y /N /N /Y/1"));
    [Fact]
    public void VideoAndShutdownAreMutuallyExclusive() => Assert.Throws<InvalidOperationException>(() => AgingCoordinator.Create([AgingItem.Parse("Video")], new AppOptions { ShutdownAfterAging = true }, true, root));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
