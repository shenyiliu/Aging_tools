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
    public async Task VideoAdapterReturnsExecutionEvidenceForResultAndCancellation()
    {
        var item = AgingItem.Parse("Video");
        var run = AgingCoordinator.Create([item], new AppOptions(), false, root);
        var platform = new WindowsPlatform { PlayVideoAsync = (_, _) => Task.FromResult(new TaskResult(Outcome.Fail, "manual fail")) };
        var result = await platform.ExecuteAsync(item, run, _ => { }, CancellationToken.None);
        Assert.Equal(Outcome.Fail, result.Outcome); Assert.NotNull(result.Execution);
        Assert.Contains(run.LogDirectory, result.Execution.RawReports);
        platform.PlayVideoAsync = (_, token) => Task.FromCanceled<TaskResult>(token);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        result = await platform.ExecuteAsync(item, run, _ => { }, cancellation.Token);
        Assert.Equal(Outcome.Aborted, result.Outcome); Assert.Equal("Cancelled", result.Execution!.StartState);
    }
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
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulImmediateLauncherWaitsForItsActualLoadProcess(int repetition)
    {
        string marker = Path.Combine(root, $"child-completed-{repetition}.txt");
        var result = await new OwnedProcessRunner().RunAsync(probe, ["--launcher", marker], root, Path.Combine(root, "launcher"), TimeSpan.FromSeconds(10), CancellationToken.None, waitForDescendants: true);
        Assert.Equal(0, result.ExitCode); Assert.True(File.Exists(marker)); Assert.True(result.Elapsed.TotalSeconds >= 1.2);
        Assert.Contains("--launcher", File.ReadAllText(Path.Combine(root, "launcher.command.json")));
        Assert.Contains("finite child completed", File.ReadAllText(result.StdoutPath));
        Assert.Contains("finite child diagnostic", File.ReadAllText(result.StderrPath));
    }
    [Fact]
    public async Task CancellingAfterImmediateLauncherExitKillsItsActualLoadProcess()
    {
        using var ct = new CancellationTokenSource();
        int launcherPid = 0;
        string pidFile = Path.Combine(root, "launcher-child.pid");
        Task task = new OwnedProcessRunner().RunAsync(probe, ["--launcher-tree", pidFile], root, Path.Combine(root, "launcher-cancel"), TimeSpan.FromSeconds(20), ct.Token, pid => launcherPid = pid, waitForDescendants: true);
        var limit = Stopwatch.StartNew();
        while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
        Assert.True(File.Exists(pidFile), "launcher child not started");
        int pid = int.Parse(File.ReadAllText(pidFile));
        bool launcherExited = false;
        while (!launcherExited && limit.Elapsed < TimeSpan.FromSeconds(8))
        {
            try { using var launcher = Process.GetProcessById(launcherPid); launcherExited = launcher.HasExited; }
            catch (ArgumentException) { launcherExited = true; }
            if (!launcherExited) await Task.Delay(25);
        }
        Assert.True(launcherExited, "launcher must exit before cancellation targets its child");
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        try { using var child = Process.GetProcessById(pid); Assert.True(child.HasExited); }
        catch (ArgumentException) { /* The owned child was already reaped. */ }
    }
    [Fact]
    public async Task NativeLaunchPreservesEmptyUnicodeQuotedAndTrailingSlashArguments()
    {
        string[] arguments = ["", "中文 参数", "embedded\"quote", @"C:\path with spaces\", @"back\\slash\" + '\"' + "end", "plain"];
        var result = await new OwnedProcessRunner().RunAsync(probe, ["--echo-args", .. arguments], root, Path.Combine(root, "arguments"), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(result.StdoutPath)));
    }
    [Fact]
    public async Task NativeLaunchFailurePreservesTheAttemptedCommandAndEmptyOutputFiles()
    {
        bool started = false;
        string prefix = Path.Combine(root, "missing-engine");
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => new OwnedProcessRunner().RunAsync(Path.Combine(root, "does-not-exist.exe"), ["参数"], root, prefix, TimeSpan.FromSeconds(10), CancellationToken.None, _ => started = true));
        Assert.False(started);
        Assert.Contains("does-not-exist.exe", File.ReadAllText(prefix + ".command.json"));
        Assert.True(File.Exists(prefix + ".stdout.txt")); Assert.True(File.Exists(prefix + ".stderr.txt"));
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
