using Aging.Core;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class GpuAdapterTests
{
    [Theory]
    [InlineData("0X", "0", true)]
    [InlineData("2X", "2", false)]
    [InlineData("4X", "4", true)]
    [InlineData("8X", "8", false)]
    public void FurMarkMapsActualArguments(string uiMsaa, string engineMsaa, bool windowed)
    {
        var item = new AgingItem { Kind = TaskKind.FurMark, Hours = .1, Resolution = "2560x1440", Msaa = uiMsaa, WindowMode = windowed };
        var args = FurMarkAdapter.CreateArguments(item);
        Assert.Contains("/msaa=" + engineMsaa, args);
        Assert.Contains("/max_time=360000", args);
        Assert.Contains("/width=2560", args); Assert.Contains("/height=1440", args);
        Assert.Equal(!windowed, args.Contains("/fullscreen=1"));
        Assert.Contains("/log_gpu_data", args); Assert.Contains("/enable_dyn_bkg=1", args);
        // FurMark's shipped start.bat pairs max_time with run_mode=1; stability mode ignores it.
        Assert.Contains("/run_mode=1", args); Assert.Contains("/noscore", args); Assert.Contains("/log_score", args);
    }

    [Fact]
    public void InvalidGpuArgumentsAndMissingEnginesAreActionable()
    {
        Assert.Throws<FormatException>(() => FurMarkAdapter.CreateArguments(new AgingItem { Msaa = "16X" }));
        Assert.Throws<FormatException>(() => GpuEngines.Resolution("1920x0"));
        Assert.NotEmpty(FurMarkAdapter.Validate("absent.exe", new AgingItem()));
        Assert.Contains(Mark3DAdapter.Validate("absent.exe", new AgingItem { MarkVersion = "bad" }, "missing"), s => s.Contains("商业授权"));
    }

    [Fact]
    public void FurMarkCannotPassFromDurationOrCopiedTextAlone()
    {
        Assert.Equal(Outcome.Fail, FurMarkAdapter.Evaluate(0, TimeSpan.FromSeconds(10), .1, []).Outcome);
        Assert.Equal(Outcome.Fail, FurMarkAdapter.Evaluate(4, TimeSpan.FromSeconds(360), .1, []).Outcome);
        Assert.Equal(Outcome.Uncertain, FurMarkAdapter.Evaluate(0, TimeSpan.FromSeconds(360), .1, []).Outcome);
        string root = Path.Combine(Path.GetTempPath(), "aging-gpu-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string report = Path.Combine(root, "furmark-gpu-0.csv");
            File.WriteAllText(report, "time,temp,load\n0,50,99\n1000,51,99\n2000,52,99\n");
            // Sampling is retained evidence, not an invented hardware PASS.
            Assert.Equal(Outcome.Uncertain, FurMarkAdapter.Evaluate(0, TimeSpan.FromSeconds(360), .1, [report]).Outcome);
            string score = Path.Combine(root, "FurMark-Scores.txt");
            File.WriteAllText(score, "GPU - [Score=1652] - [DurationInMs=360000] - [MSAA=8X]");
            Assert.Equal(Outcome.Uncertain, FurMarkAdapter.Evaluate(1652, TimeSpan.FromSeconds(366), .1, [report, score]).Outcome);
            Assert.Equal(Outcome.Fail, FurMarkAdapter.Evaluate(1653, TimeSpan.FromSeconds(366), .1, [report, score]).Outcome);
            File.WriteAllText(score, "GPU - [Score=1652] - [DurationInMs=10000]");
            Assert.Equal(Outcome.Fail, FurMarkAdapter.Evaluate(1652, TimeSpan.FromSeconds(366), .1, [report, score]).Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("3DMark11_Extreme", "custom_extreme_definition.xml")]
    [InlineData("3DMark11_Performance", "custom_performance_definition.xml")]
    [InlineData("3DMarkV2_AllItem", "custom_all.3dmdef")]
    [InlineData("3DMarkV2_Ice_Storm", "custom_icestorm_extreme.3dmdef")]
    [InlineData("3DMarkV2_Sky_Diver", "custom_skydiver.3dmdef")]
    [InlineData("3DMarkV2_Time_Spy", "custom_timespy_extreme.3dmdef")]
    [InlineData("3DMarkV2_Fire_Strike", "custom_firestrike_ultra.3dmdef")]
    public void AllOriginalScenesMapWithoutRegistrationSecrets(string scene, string file)
    {
        Assert.Equal(file, Path.GetFileName(Mark3DAdapter.Definition("root", new AgingItem { MarkVersion = scene })));
        var args = Mark3DAdapter.CreateArguments("scene", "result", "export", "log");
        Assert.Contains("--loop=1", args); Assert.Contains("--out=result", args); Assert.Contains("--export=export", args);
        Assert.DoesNotContain(args, s => s.Contains("register", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("enable_window_mode")]
    [InlineData("is_windowed")]
    public void DefinitionRewriteHandlesBothOriginalWindowSettingNames(string windowName)
    {
        string xml = "<benchmark xmlns='urn:test'><settings>" +
            $"<setting><name>{windowName}</name><value>0</value></setting>" +
            "<setting><name>screen_width</name><value>1</value></setting><setting><name>screen_height</name><value>1</value></setting></settings></benchmark>";
        string changed = Mark3DAdapter.RewriteDefinition(xml, new AgingItem { Resolution = "1280x720", WindowMode = true });
        Assert.Contains("<value>1280</value>", changed); Assert.Contains("<value>720</value>", changed); Assert.Contains("<value>1</value>", changed);
        Assert.Throws<FormatException>(() => Mark3DAdapter.RewriteDefinition("<benchmark/>", new AgingItem()));
    }

    [Fact]
    public void MarkResultsRequireEvidenceAndNeverInferHardwarePassFromScores()
    {
        Assert.Equal(Outcome.Uncertain, Mark3DAdapter.EvaluateReport("<result/>").Outcome);
        Assert.Equal(Outcome.Uncertain, Mark3DAdapter.EvaluateReport("<result><OverallScore>123</OverallScore></result>").Outcome);
        Assert.Equal(Outcome.Fail, Mark3DAdapter.EvaluateReport("<result><error>workload failed</error><OverallScore>123</OverallScore></result>").Outcome);
        Assert.Equal(Outcome.Uncertain, Mark3DAdapter.EvaluateReport("invalid").Outcome);
    }

    [Fact]
    public void EveryCopiedOriginalSceneAcceptsResolutionAndWindowRewrite()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Tools", "3DMark"))) directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (string scene in Mark3DAdapter.Scenes.Keys)
        {
            var item = new AgingItem { MarkVersion = scene, Resolution = "1600x900", WindowMode = true };
            string xml = File.ReadAllText(Mark3DAdapter.Definition(Path.Combine(directory!.FullName, "Tools"), item));
            string changed = Mark3DAdapter.RewriteDefinition(xml, item);
            Assert.Contains("<value>1600</value>", changed); Assert.Contains("<value>900</value>", changed);
        }
    }
}
