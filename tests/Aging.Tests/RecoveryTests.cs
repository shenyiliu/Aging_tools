using Aging.Core;
using System.Text;
using Xunit;

namespace Aging.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gw-aging-tests", Guid.NewGuid().ToString("N"));
    private readonly MemoryStore store = new();
    private readonly FakePlatform platform = new();
    private readonly NullLogs logs = new();
    public RecoveryTests() => Directory.CreateDirectory(root);
    private RunSnapshot Run(params string[] items) => AgingCoordinator.Create(items.Select(AgingItem.Parse), new AppOptions { SwitchReboot = false, StopOnFail = true }, false, root);
    private AgingCoordinator Coordinator() => new(store, logs, platform) { SimulationTick = TimeSpan.Zero };
    private static RunSnapshot AwaitBoot(RunSnapshot run)
    {
        run.Phase = RunPhase.AwaitingReboot; run.Items[0].Phase = RunPhase.AwaitingReboot; run.ExpectedBootId = "boot-A";
        run.PendingOperation = "cycle"; run.OperationId = Guid.NewGuid(); run.OperationStartedAt = DateTimeOffset.Now.AddSeconds(-10);
        return run;
    }
    [Fact]
    public async Task ReopeningWithoutBootDoesNotIncrementOrRunAnything()
    {
        var run = AwaitBoot(Run("Reboot|1|30|N|N|N|N"));
        await Coordinator().RunAsync(run, true);
        Assert.Equal(0, run.Items[0].CompletedCycles); Assert.Equal(0, platform.Executions); Assert.Equal(RunPhase.AwaitingReboot, run.Phase);
    }
    [Fact]
    public async Task ChangedBootCompletesExactlyOneCycleAndCannotResumeTerminalRun()
    {
        var run = AwaitBoot(Run("Reboot|1|30|N|N|N|N")); platform.BootId = "boot-B";
        var coordinator = Coordinator(); await coordinator.RunAsync(run, true);
        Assert.Equal(1, run.Items[0].CompletedCycles); Assert.Equal(Outcome.Pass, run.Outcome); Assert.Equal(0, platform.Reboots);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(run, true));
    }
    [Fact]
    public async Task CrashDuringExternalEngineNeverSilentlyRerunsOrPasses()
    {
        var run = Run("BurnIn|0.1|0", "Video"); run.Phase = RunPhase.Running; run.Items[0].Phase = RunPhase.Running;
        await Coordinator().RunAsync(run, true);
        Assert.Equal(Outcome.Uncertain, run.Items[0].Outcome); Assert.Equal(RunPhase.Skipped, run.Items[1].Phase);
        Assert.Equal(0, platform.Executions); Assert.Equal(Outcome.Fail, run.Outcome);
    }
    [Fact]
    public async Task CrashAfterCompletedItemCommitDoesNotOverwritePass()
    {
        var run = Run("BurnIn|0.1|0"); run.Phase = RunPhase.Preflight; run.Items[0].Phase = RunPhase.Completed; run.Items[0].Outcome = Outcome.Pass;
        await Coordinator().RunAsync(run, true); Assert.Equal(Outcome.Pass, run.Outcome); Assert.Equal(0, platform.Executions);
    }
    [Fact]
    public async Task MissingEngineStopsQueueAndNeverRunsCompletionInSimulation()
    {
        var run = Run("BurnIn|0.1|0", "Video"); platform.Preflight = PreflightResult.Failure("engine missing");
        await Coordinator().RunAsync(run);
        Assert.Equal(RunPhase.Failed, run.Phase); Assert.Equal(RunPhase.Skipped, run.Items[1].Phase); Assert.Equal(0, platform.Executions);
        var simulation = Run("BurnIn|0.1|0"); simulation.Simulation = true; int before = platform.AfterRuns;
        await Coordinator().RunAsync(simulation); Assert.Equal(Outcome.Pass, simulation.Outcome); Assert.Equal(before, platform.AfterRuns);
    }
    [Fact]
    public async Task ContinueOnFailureRetainsFailedOverallOutcome()
    {
        var run = Run("BurnIn|0.1|0", "Video"); run.Options.StopOnFail = false; platform.Result = new(Outcome.Fail, "report failure");
        await Coordinator().RunAsync(run); Assert.Equal(2, platform.Executions); Assert.Equal(Outcome.Fail, run.Outcome);
    }
    [Fact]
    public async Task SimulationCountsEveryConfiguredCycleWithNoHardwareCalls()
    {
        var run = Run("Reboot|50|30|1|4|1|N", "S4|50|60|60|Y|N|N", "BurnIn|0.1|0"); run.Simulation = true;
        await Coordinator().RunAsync(run);
        Assert.Equal(50, run.Items[0].CompletedCycles); Assert.Equal(50, run.Items[1].CompletedCycles);
        Assert.Equal(360, run.Items[2].ActiveSeconds); Assert.Equal(Outcome.Pass, run.Outcome); Assert.Equal(0, platform.Executions + platform.Reboots + platform.AfterRuns + platform.Probes);
    }
    [Fact]
    public async Task StopDuringCountdownPersistsAbortedAndPreventsReboot()
    {
        var run = Run("Reboot|3|30|N|N|N|N"); var coordinator = Coordinator();
        coordinator.Changed += r => { if (r.Phase == RunPhase.Countdown) coordinator.Stop(); };
        await coordinator.RunAsync(run); Assert.Equal(Outcome.Aborted, run.Outcome); Assert.Equal(0, platform.Reboots);
        Assert.Equal(Outcome.Aborted, store.Saved!.Outcome);
    }
    [Fact]
    public async Task StateIsDurableBeforeAnyRebootRequest()
    {
        var run = Run("Reboot|1|30|N|N|N|N"); run.Simulation = true;
        // A resumed real countdown can have one second remaining; avoid a 30-second unit test.
        run.Simulation = false; run.Items[0].RemainingSeconds = 1;
        platform.OnReboot = () =>
        {
            Assert.Equal(RunPhase.AwaitingReboot, store.Saved!.Phase); Assert.Equal("boot-A", store.Saved.ExpectedBootId);
            Assert.NotEqual(Guid.Empty, store.Saved.OperationId); Assert.Equal("cycle", store.Saved.PendingOperation);
        };
        await Coordinator().RunAsync(run); Assert.Equal(1, platform.Reboots); Assert.Equal(0, run.Items[0].CompletedCycles);
    }
    [Fact]
    public async Task PauseAndStopDoNotConsumeSimulatedCountdown()
    {
        var run = Run("Reboot|1|30|N|N|N|N"); run.Simulation = true;
        var coordinator = Coordinator(); coordinator.SimulationTick = TimeSpan.FromMilliseconds(10);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += r =>
        {
            if (r.Phase == RunPhase.Countdown && !coordinator.PauseRequested) coordinator.Pause();
            if (r.Phase == RunPhase.Paused) paused.TrySetResult();
        };
        Task task = coordinator.RunAsync(run); await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int remaining = run.Items[0].RemainingSeconds; await Task.Delay(80); Assert.Equal(remaining, run.Items[0].RemainingSeconds);
        coordinator.Stop(); await task; Assert.Equal(Outcome.Aborted, run.Outcome);
    }
    [Fact]
    public void CorruptPrimaryFallsBackToChecksumVerifiedBackup()
    {
        string path = Path.Combine(root, "active.json"); var disk = new JsonRunStore(path); var run = Run("Video");
        disk.Save(run); disk.Save(run); File.WriteAllText(path, "{ torn write");
        var restored = disk.Load()!; Assert.True(disk.RecoveredFromBackup); Assert.Equal(1, restored.Revision);
        disk.Save(restored); Assert.NotNull(disk.Load()); Assert.Equal(1, new JsonRunStore(path + ".bak").Load()!.Revision);
    }
    [Fact]
    public void CorruptBothCheckpointsIsAnErrorRatherThanNewRun()
    { string path = Path.Combine(root, "active.json"); File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "also broken"); Assert.Throws<InvalidDataException>(() => new JsonRunStore(path).Load()); }
    [Fact]
    public async Task BriefViewerLockIsRetriedWithoutTruncatingLastCheckpoint()
    {
        string path = Path.Combine(root, "busy.json"); var disk = new JsonRunStore(path); var run = Run("Video"); disk.Save(run);
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task save = Task.Run(() => disk.Save(run));
        await Task.Delay(100); Assert.False(save.IsCompleted); reader.Dispose(); await save;
        Assert.Equal(2, disk.Load()!.Revision); Assert.Equal(1, new JsonRunStore(path + ".bak").Load()!.Revision);
    }
    [Fact]
    public void EndWithErrorsIsNeverDisplayedAsPassInLegacyLog()
    {
        var run = Run("Video", "Video"); run.Phase = RunPhase.Failed; run.Outcome = Outcome.Fail;
        run.Items[0].Phase = RunPhase.Failed; run.Items[0].Outcome = Outcome.Fail; run.Items[0].Errors.Add("人工判定失败");
        run.Items[1].Phase = RunPhase.Skipped;
        var writer = new LegacyLogWriter(); writer.Write(run);
        var doc = IniDocument.Load(LegacyLogWriter.LogPath(run)); Assert.Equal("END", doc.Get("1_Video_Info", "State")); Assert.Equal("1", doc.Get("1_Video_Error", "Num"));
        Assert.Equal("Fail", doc.Get("MAIN", "Outcome")); Assert.Equal("STOP", doc.Get("2_Video_Info", "State"));
        Assert.True(File.ReadAllBytes(LegacyLogWriter.LogPath(run)).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
    }
    [Fact]
    public void OldWrapperProjectionPreservesOtherItemsAndOverallMainState()
    {
        string path = Path.Combine(root, "old.log");
        File.WriteAllText(path, "[MAIN]\r\nAgingState=RUN\r\n[1_Video_Error]\r\nNum=1\r\n1=old failure\r\n[AgingItem]\r\n1=Video\r\n2=BurnIn|0.1|0\r\n", Encoding.Unicode);
        var run = Run("BurnIn|0.1|0"); run.LegacyLogPath = path; run.LegacyItemIndex = 2;
        run.Items[0].Phase = RunPhase.Completed; run.Items[0].Outcome = Outcome.Pass; run.Phase = RunPhase.Completed; run.Outcome = Outcome.Pass;
        new LegacyLogWriter().Write(run);
        var doc = IniDocument.Load(path); Assert.Equal("RUN", doc.Get("MAIN", "AgingState")); Assert.Equal("", doc.Get("MAIN", "Outcome"));
        Assert.Equal("old failure", doc.Get("1_Video_Error", "1")); Assert.Equal("END", doc.Get("2_BurnIn_Info", "State")); Assert.Equal("6", doc.Get("2_BurnIn_Info", "TargetTime"));
        Assert.Equal("Pass", doc.Get("Replica_2", "Outcome")); Assert.True(File.Exists(LegacyLogWriter.LogPath(run)));
    }
    [Fact]
    public void LegacyPowerTargetIsCyclesAndVideoRetainsIndividualDurations()
    {
        var run = Run("Reboot|50|30|N|N|N|N", "Video"); run.Items[1].VideoRunSeconds = [12, 7]; run.Items[1].VideoPlays = 2;
        new LegacyLogWriter().Write(run); var doc = IniDocument.Load(LegacyLogWriter.LogPath(run));
        Assert.Equal("50", doc.Get("1_Reboot_Info", "TargetTime")); Assert.Equal("第 2 次视频播放了7秒", doc.Get("2_Video_Info", "RunTime2"));
    }
    [Fact]
    public async Task ClosingInteractiveTaskAbortsRemainingQueueEvenWhenContinueOnFail()
    {
        var run = Run("Video", "BurnIn|0.1|0"); run.Options.StopOnFail = false; platform.Result = new(Outcome.Aborted, "window closed");
        await Coordinator().RunAsync(run); Assert.Equal(Outcome.Aborted, run.Outcome); Assert.Equal(1, platform.Executions); Assert.Equal(RunPhase.Skipped, run.Items[1].Phase);
    }
    [Fact]
    public async Task FailedPreparationPreventsRebootBeforeCountdownFinishes()
    {
        var run = Run("Reboot|1|30|N|N|N|IP3");
        platform.Prepare = (_, _) => Task.FromResult<IReadOnlyList<string>>(["fan measurement failed"]);
        await Coordinator().RunAsync(run);
        Assert.Equal(0, platform.Reboots); Assert.Equal(Outcome.Fail, run.Outcome);
        Assert.Contains("fan measurement failed", run.Items[0].Errors);
        Assert.Equal(0, run.Items[0].CompletedCycles);
    }
    [Fact]
    public async Task StopWaitsForPreparationCleanupBeforeReturning()
    {
        var run = Run("Reboot|1|30|N|N|N|IP3");
        bool cleaned = false;
        platform.Prepare = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); return []; }
            finally { await Task.Delay(30); cleaned = true; }
        };
        var coordinator = Coordinator();
        coordinator.Changed += r => { if (r.Phase == RunPhase.Countdown) coordinator.Stop(); };
        await coordinator.RunAsync(run);
        Assert.True(cleaned); Assert.Equal(0, platform.Reboots); Assert.Equal(Outcome.Aborted, run.Outcome);
    }
    [Fact]
    public async Task ExplicitContinueOnFailureRetainsPreparationError()
    {
        var run = Run("S3|1|30|60|N|N|N"); run.Options.StopOnFail = false; run.Items[0].RemainingSeconds = 1;
        platform.Prepare = (_, _) => Task.FromResult<IReadOnlyList<string>>(["preparation error"]);
        await Coordinator().RunAsync(run);
        Assert.Equal(1, platform.Executions); Assert.Equal(Outcome.Fail, run.Outcome);
        Assert.Contains("preparation error", run.Items[0].Errors);
    }
    [Fact]
    public async Task NextBootArmsDurablyWithoutAnyPhysicalAction()
    {
        var run = Run("BurnIn|0.1|0"); run.Options.AgingInNextBoot = true;
        var coordinator = Coordinator(); await coordinator.RunAsync(run);
        Assert.Equal(RunPhase.AwaitingReboot, run.Phase); Assert.Equal("start", run.PendingOperation);
        Assert.Equal("boot-A", run.ExpectedBootId); Assert.False(run.InitialBootCompleted);
        Assert.Equal("start", store.Saved!.PendingOperation);
        Assert.Equal(0, platform.Executions + platform.Reboots + platform.Probes + platform.AfterRuns);
        await coordinator.RunAsync(run, true);
        Assert.Equal(RunPhase.AwaitingReboot, run.Phase); Assert.False(run.InitialBootCompleted);
        Assert.Equal(0, platform.Executions + platform.Reboots);
    }
    [Fact]
    public async Task NextBootStartsOnceWithoutAddingAnItemCycle()
    {
        var run = Run("BurnIn|0.1|0"); run.Options.AgingInNextBoot = true;
        var coordinator = Coordinator(); await coordinator.RunAsync(run);
        platform.BootId = "boot-B";
        await coordinator.RunAsync(run, true);
        Assert.True(run.InitialBootCompleted); Assert.Equal("", run.PendingOperation);
        Assert.Equal(1, platform.Executions); Assert.Equal(0, platform.Reboots);
        Assert.Equal(0, run.Items[0].CompletedCycles); Assert.Equal(Outcome.Pass, run.Outcome);
    }
    [Fact]
    public async Task SimulationNextBootDoesNotScheduleOrExecuteReboot()
    {
        var run = Run("BurnIn|0.1|0"); run.Options.AgingInNextBoot = true; run.Simulation = true;
        await Coordinator().RunAsync(run);
        Assert.True(run.InitialBootCompleted); Assert.Equal(Outcome.Pass, run.Outcome);
        Assert.Equal(0, platform.Reboots + platform.Executions + platform.AfterRuns);
        Assert.False(run.HardwarePassed);
    }
    [Fact]
    public async Task CrashAfterInitialBootAcknowledgementDoesNotArmAgain()
    {
        var run = Run("BurnIn|0.1|0"); run.Options.AgingInNextBoot = true;
        run.InitialBootCompleted = true; run.Phase = RunPhase.Preflight;
        await Coordinator().RunAsync(run, true);
        Assert.Equal(1, platform.Executions); Assert.Equal(0, platform.Reboots);
        Assert.Equal(Outcome.Pass, run.Outcome);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class MemoryStore : IRunStore
    { public RunSnapshot? Saved; public void Save(RunSnapshot snapshot) { snapshot.Revision++; Saved = JsonData.Clone(snapshot); } public RunSnapshot? Load() => Saved; }
    private sealed class NullLogs : ILegacyLogWriter { public void Write(RunSnapshot run) { } public void Event(RunSnapshot run, string level, string message) { } }
    private sealed class FakePlatform : ITaskPlatform
    {
        public string BootId { get; set; } = "boot-A";
        public int Reboots, Executions, AfterRuns, Probes;
        public Action? OnReboot;
        public PreflightResult Preflight = PreflightResult.Success();
        public TaskResult Result = new(Outcome.Pass, "actual report");
        public Func<AgingItem, CancellationToken, Task<IReadOnlyList<string>>>? Prepare;
        public Task<PreflightResult> PreflightAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => Task.FromResult(Preflight);
        public Task<IReadOnlyList<string>> PrepareCycleAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => Prepare?.Invoke(item, ct) ?? Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DeviceSnapshot> CaptureDevicesAsync(CancellationToken ct) { Probes++; return Task.FromResult(new DeviceSnapshot([], 1024)); }
        public Task<IReadOnlyList<string>> CheckAsync(AgingItem item, RunSnapshot run, CancellationToken ct) { Probes++; return Task.FromResult<IReadOnlyList<string>>([]); }
        public Task RebootAsync(CancellationToken ct) { OnReboot?.Invoke(); Reboots++; return Task.CompletedTask; }
        public Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct) { Executions++; return Task.FromResult(Result); }
        public Task AfterRunAsync(RunSnapshot run, CancellationToken ct) { AfterRuns++; return Task.CompletedTask; }
    }
}
