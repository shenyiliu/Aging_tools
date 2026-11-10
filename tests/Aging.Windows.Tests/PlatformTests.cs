using Aging.Core;
using Aging.Windows;
using System.Diagnostics;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gw-aging-platform", Guid.NewGuid().ToString("N"));
    private readonly string probe = Path.Combine(AppContext.BaseDirectory, "probe", "Aging.Probe.exe");
    public PlatformTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task NativeBootGuidIsNonemptyAndStableAcrossProcessReopen()
    { string id = new WindowsPlatform().BootId; Assert.NotEqual(Guid.Empty, Guid.Parse(id)); await Task.Delay(50); Assert.Equal(id, new WindowsPlatform().BootId); }
    [Fact]
    public async Task ProcessRunnerCapturesOutputAndActualExitStatus()
    {
        var runner = new OwnedProcessRunner();
        var result = await runner.RunAsync(probe, ["--failure"], root, Path.Combine(root, "output"), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(7, result.ExitCode); Assert.Contains("harmless probe 中文", File.ReadAllText(result.StdoutPath)); Assert.Contains("diagnostic only", File.ReadAllText(result.StderrPath));
    }
    [Fact]
    public async Task CancellingJobKillsOwnedChildTree()
    {
        var runner = new OwnedProcessRunner(); using var ct = new CancellationTokenSource();
        string pidPath = Path.Combine(root, "child.pid");
        Task task = runner.RunAsync(probe, ["--tree", pidPath], root, Path.Combine(root, "tree"), TimeSpan.FromSeconds(20), ct.Token);
        var limit = Stopwatch.StartNew();
        while (!File.Exists(pidPath) && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
        Assert.True(File.Exists(pidPath), "probe child not started"); int pid = int.Parse(File.ReadAllText(pidPath));
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        bool alive;
        try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
        Assert.False(alive);
    }
    [Fact]
    public async Task TimeoutTerminatesOnlyTheOwnedProbe()
    {
        var runner = new OwnedProcessRunner();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(probe, ["--wait"], root, Path.Combine(root, "timeout"), TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }
    [Fact]
    public async Task MissingStressEngineFailsWithoutLaunchingAnEngine()
    {
        var item = AgingItem.Parse("BurnIn|0.1|0"); var run = AgingCoordinator.Create([item], new AppOptions { SwitchReboot = false }, false, root);
        var result = await new WindowsPlatform().PreflightAsync(item, run, CancellationToken.None);
        Assert.False(result.CanRun); Assert.Contains(result.Messages, x => x.Contains("未配置已验证引擎适配"));
    }
    [Fact]
    public async Task ItemPreviewDoesNotTreatContinuationSetupAsAComponentFailure()
    {
        var options = new AppOptions { SwitchReboot = true };
        var result = await new WindowsPlatform().CheckItemAsync(AgingItem.Parse("S3|1|30|60|N|N|N"), options, CancellationToken.None);
        Assert.False(result.CanRun); // Missing PwrTest remains a real dependency error.
        Assert.DoesNotContain(result.Messages, message => message.Contains("续测") || message.Contains("管理员"));
    }
    [Fact]
    public async Task ExecutionDoesNotRequireLoginTaskForSelectedRebootRun()
    {
        var video = AgingItem.Parse("Video");
        var run = AgingCoordinator.Create([AgingItem.Parse("Reboot|1|30|N|N|N|N"), video], new AppOptions { SwitchReboot = false }, false, root);
        var result = await new WindowsPlatform().PreflightAsync(video, run, CancellationToken.None);
        Assert.DoesNotContain(result.Messages, message => message.Contains("登录") || message.Contains("续测"));
        Assert.Contains(result.Messages, message => message.Contains("视频文件"));
    }
    [Fact]
    public async Task UnverifiedProfileCannotRunEvenIfExecutableExists()
    {
        var item = AgingItem.Parse("FurMark|0.1|8X|1920x1080|Y");
        var options = new AppOptions { SwitchReboot = false, Engines = new() { ["FurMark"] = new EngineProfile { Executable = probe, ReportPath = "{runDir}\\{itemIndex}.log" } } };
        var run = AgingCoordinator.Create([item], options, false, root);
        var result = await new WindowsPlatform().PreflightAsync(item, run, CancellationToken.None);
        Assert.False(result.CanRun); Assert.Contains(result.Messages, x => x.Contains("尚未完成专机验证"));
    }
    private static string WorkerExecutable()
    {
        if (Environment.GetEnvironmentVariable("GW_AGING_WORKER_EXE") is { Length: > 0 } exe) return exe;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgingTool.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Aging.WinForms", "bin", "Release", "net10.0-windows", "Test.exe");
    }
    [Fact]
    public async Task CurrentUserWorkerTransportsAnExplicitEngineFailure()
    {
        var item = AgingItem.Parse("FurMark|0.1|8X|1920x1080|Y");
        var profile = new EngineProfile { Executable = probe, Verified = true, VerificationNote = "Automated harmless fake-engine fixture", SupportsDuration = true,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(probe))), FileVersion = FileVersionInfo.GetVersionInfo(probe).FileVersion!,
            Arguments = ["--failure"], ReportPath = "{runDir}\\{itemIndex}.log", PassPattern = "^PASS$", FailPattern = "^FAIL$" };
        var run = AgingCoordinator.Create([item], new AppOptions { SwitchReboot = false, Engines = new() { ["FurMark"] = profile } }, false, root);
        var result = await new WorkerBridge(WorkerExecutable()).ExecuteAsync(item, run, _ => { }, CancellationToken.None);
        Assert.Equal(Outcome.Fail, result.Outcome); Assert.Contains("7", result.Message);
    }
    [Fact]
    public async Task WorkerRejectsRebootRequestsWithoutTouchingSystemPower()
    {
        var item = AgingItem.Parse("Reboot|1|30|N|N|N|N");
        var run = AgingCoordinator.Create([item], new AppOptions(), false, root);
        var exception = await Assert.ThrowsAsync<IOException>(() => new WorkerBridge(WorkerExecutable()).ExecuteAsync(item, run, _ => { }, CancellationToken.None));
        Assert.Contains("宿主协调", exception.Message);
    }
    [Fact]
    public async Task MachineReportReadsCapabilitiesWithoutRunningAnyTestEngine()
    {
        var report = await MachineCompatibility.CaptureAsync(new AppOptions());
        Assert.True(report.CanRunApplication); Assert.Equal("X64", report.Architecture);
        Assert.Contains(report.Items, x => x.Name == ".NET 运行环境");
        Assert.Contains(report.Items, x => x.Name == "Core Temp 温度");
        Assert.Contains(report.Items, x => x.Name == "风扇 IP3" && x.Status == "未就绪");
        Assert.Contains(report.Items, x => x.Name == "BurnIn" && x.Status.Contains("未配置"));
        Assert.Contains(report.Items, x => x.Name == "PwrTest x64" && x.Status.Contains("缺失"));
        string path = Path.Combine(root, "machine.json"); MachineCompatibility.Save(report, path);
        Assert.Contains("WindowsVersion", File.ReadAllText(path));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
