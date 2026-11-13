using System.Diagnostics;

namespace Aging.Core;

/// <summary>Single writer for run state. Every system action is preceded by a durable checkpoint.</summary>
public sealed class AgingCoordinator(IRunStore store, ILegacyLogWriter logs, ITaskPlatform platform)
{
    private CancellationTokenSource? cancellation;
    private bool pauseRequested;
    private bool busy;
    public RunSnapshot? Current { get; private set; }
    public bool Busy => busy;
    public bool CanPause => busy && Current is { } r && (r.Phase is RunPhase.Countdown or RunPhase.Paused || r.Simulation && r.Phase == RunPhase.Running);
    public bool PauseRequested => pauseRequested;
    public TimeSpan SimulationTick { get; set; } = TimeSpan.FromMilliseconds(25);
    public event Action<RunSnapshot>? Changed;

    public void Attach(RunSnapshot run)
    {
        if (busy) throw new InvalidOperationException("队列正在运行");
        Current = run;
        Changed?.Invoke(run);
    }
    public void ClearCompleted()
    {
        if (busy || Current is { IsTerminal: false }) throw new InvalidOperationException("当前任务尚未结束");
        Current = null;
    }
    public static RunSnapshot Create(IEnumerable<AgingItem> queue, AppOptions options, bool simulation, string logRoot)
    {
        var list = queue.ToList();
        if (list.Count == 0) throw new InvalidOperationException("请先添加测试项目");
        foreach (var item in list) item.Validate();
        if (options.ShutdownAfterAging && list.Any(x => x.Kind == TaskKind.Video)) throw new InvalidOperationException("视频与完成后关机不能同时启用");
        var run = new RunSnapshot { Items = list.Select(x => new ItemRun { Item = x }).ToList(), Options = JsonData.Clone(options), Simulation = simulation };
        run.LogDirectory = Path.Combine(logRoot, $"{run.StartedAt:yyyyMMdd_HHmmss}_{run.RunId.ToString("N")[..8]}{(simulation ? "_SIM" : "")}");
        return run;
    }
    public void Pause()
    {
        if (!CanPause) throw new InvalidOperationException("当前阶段不支持暂停");
        pauseRequested = true;
    }
    public void Resume() => pauseRequested = false;
    public void Stop() { pauseRequested = false; cancellation?.Cancel(); }

    private void Commit()
    {
        var run = Current!;
        store.Save(run); // Must succeed before executing reboot or a worker.
        logs.Write(run);
        Changed?.Invoke(run);
    }
    private void Note(string text, string level = "INFO") => logs.Event(Current!, level, text);
    private void Stage(RunPhase phase, string message)
    {
        var run = Current!; var item = run.Items[run.CurrentIndex];
        run.Phase = phase; item.Phase = phase; item.Message = message;
        Commit();
    }
    public async Task RunAsync(RunSnapshot run, bool recovering = false, CancellationToken ct = default)
    {
        if (busy) throw new InvalidOperationException("已有任务正在运行");
        if (run.IsTerminal) throw new InvalidOperationException("已结束的任务不能恢复，请创建新任务");
        Current = run; busy = true; pauseRequested = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation = linked;
        try
        {
            if (recovering && run.Phase == RunPhase.AwaitingReboot)
            {
                if (!run.Simulation && !BootSession.HasChanged(run.ExpectedBootId, platform.BootId))
                { Changed?.Invoke(run); return; } // Merely reopening the application never adds a cycle.
                if (run.PendingOperation == "cycle")
                    await CompleteCycleAsync(run, Math.Max(0, (DateTimeOffset.Now - run.OperationStartedAt!.Value).TotalSeconds), linked.Token);
                else if (run.PendingOperation == "switch") run.SwitchedItems.Add(run.CurrentIndex);
                else if (run.PendingOperation == "start") run.InitialBootCompleted = true;
                else throw new InvalidDataException("无法识别待恢复系统动作");
                run.PendingOperation = ""; run.ExpectedBootId = ""; run.Phase = RunPhase.Preflight;
                Commit();
            }
            else if (recovering && run.Phase == RunPhase.Running)
            {
                FinishItem(Outcome.Uncertain, "运行期间程序中断，结果无法确认；不自动重复启动引擎");
                run.CurrentIndex++;
            }
            Note(recovering ? "恢复任务" : (run.Simulation ? "开始模拟任务；结果不代表硬件通过" : "开始真实任务"));
            if (!recovering && run.ExcludedItems.Count > 0) Note("仅执行本轮已选项目；未执行且不判通过：" + string.Join("；", run.ExcludedItems), "WARN");
            Commit();
            if (run.Options.AgingInNextBoot && !run.InitialBootCompleted && run.CurrentIndex == 0)
            {
                if (run.Simulation)
                {
                    run.InitialBootCompleted = true;
                    Note("演练：下次开机开始；未执行关机或重启"); Commit();
                }
                else
                {
                    run.ExpectedBootId = platform.BootId; run.PendingOperation = "start";
                    run.OperationId = Guid.NewGuid(); run.OperationStartedAt = DateTimeOffset.Now;
                    Stage(RunPhase.AwaitingReboot, "已安排下次开机开始；可退出后手动重启或关机，本工具不自动关机");
                    Note("等待手动重启或开机；尚未执行任何测试项目");
                    return;
                }
            }
            while (run.CurrentIndex < run.Items.Count)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (run.Options.StopOnFail && run.Items.Take(run.CurrentIndex).Any(x => x.Outcome is Outcome.Fail or Outcome.Uncertain))
                { SkipRemaining(); break; }
                var entry = run.Items[run.CurrentIndex];
                if (entry.Phase is RunPhase.Completed or RunPhase.Failed or RunPhase.Aborted) { run.CurrentIndex++; continue; }
                var item = entry.Item;
                Stage(RunPhase.Preflight, "预检中");
                var preflight = run.Simulation ? PreflightResult.Success("模拟适配器") : await platform.PreflightAsync(item, run, linked.Token);
                if (!preflight.CanRun)
                { FinishItem(Outcome.Fail, string.Join("；", preflight.Messages)); run.CurrentIndex++; continue; }
                bool switchBoot = run.Options.SwitchReboot && run.Items.Count > 1 && item.Kind != TaskKind.Reboot
                    && (run.CurrentIndex == 0 || run.Items[run.CurrentIndex - 1].Item.Kind != TaskKind.Reboot) && !run.SwitchedItems.Contains(run.CurrentIndex);
                if (switchBoot)
                {
                    await CountdownAsync(entry, run.Simulation ? 10 : 30, linked.Token);
                    if (!run.Simulation) { await RequestRebootAsync("switch", linked.Token); return; }
                    run.SwitchedItems.Add(run.CurrentIndex); Note("模拟：项目切换重启"); Commit();
                }
                entry.StartedAt ??= DateTimeOffset.Now;
                run.EventBookmark ??= DateTimeOffset.Now;
                if (item.HardwareCheck && run.DeviceBaseline == null && !run.Simulation)
                { run.DeviceBaseline = await platform.CaptureDevicesAsync(linked.Token); Commit(); }
                if (item.IsPower)
                {
                    while (entry.CompletedCycles < item.Cycles)
                    {
                        if (!await CycleCountdownAsync(entry, entry.RemainingSeconds > 0 ? entry.RemainingSeconds : item.DelaySeconds, linked.Token))
                        { FinishItem(Outcome.Fail, "周期准备失败，未执行系统动作"); break; }
                        if (!run.Simulation && item.Kind == TaskKind.Reboot) { await RequestRebootAsync("cycle", linked.Token); return; }
                        run.EventBookmark = DateTimeOffset.Now;
                        Stage(RunPhase.Running, run.Simulation ? "模拟系统动作" : "等待系统唤醒");
                        var watch = Stopwatch.StartNew();
                        if (run.Simulation) await SimulateDelayAsync(30, linked.Token);
                        else
                        {
                            var result = await platform.ExecuteAsync(item, run, text => { entry.Message = text; Changed?.Invoke(run); }, linked.Token);
                            entry.Execution = result.Execution;
                            if (result.Outcome == Outcome.Aborted) { linked.Cancel(); linked.Token.ThrowIfCancellationRequested(); }
                            if (result.Outcome != Outcome.Pass) { FinishItem(result.Outcome, result.Message); break; }
                        }
                        await CompleteCycleAsync(run, run.Simulation ? item.SleepSeconds + 2 : watch.Elapsed.TotalSeconds, linked.Token);
                        if (entry.Errors.Count > 0 && run.Options.StopOnFail) { FinishItem(Outcome.Fail, "周期检查失败"); break; }
                    }
                    if (entry.Phase != RunPhase.Failed) FinishItem(entry.Errors.Count == 0 ? Outcome.Pass : Outcome.Fail, "测试结束");
                }
                else
                {
                    Stage(RunPhase.Running, run.Simulation ? "模拟运行" : "运行中");
                    var watch = Stopwatch.StartNew();
                    TaskResult result;
                    if (run.Simulation)
                    {
                        await SimulateDelayAsync(120, linked.Token);
                        entry.ActiveSeconds = item.IsStress ? item.Hours * 3600 : 10;
                        result = new(Outcome.Pass, "模拟完成（未运行真实引擎）", item.Kind == TaskKind.Video ? 1 : 0, item.Kind == TaskKind.Video ? [10] : null);
                    }
                    else
                    {
                        double initial = entry.ActiveSeconds;
                        var operation = platform.ExecuteAsync(item, run, text => { entry.Message = text; Changed?.Invoke(run); }, linked.Token);
                        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                        Task<bool> tick = timer.WaitForNextTickAsync(linked.Token).AsTask();
                        try
                        {
                            while (!operation.IsCompleted)
                            {
                                if (await Task.WhenAny(operation, tick) == operation) break;
                                await tick;
                                entry.ActiveSeconds = initial + watch.Elapsed.TotalSeconds;
                                if ((int)watch.Elapsed.TotalSeconds % 10 == 0) Commit(); else Changed?.Invoke(run);
                                tick = timer.WaitForNextTickAsync(linked.Token).AsTask();
                            }
                            result = await operation;
                        }
                        catch
                        {
                            // A new run must not start while cancellation is still closing the previous process job.
                            linked.Cancel();
                            try { await operation; } catch (Exception) { /* original exception is propagated */ }
                            throw;
                        }
                        entry.ActiveSeconds = initial + watch.Elapsed.TotalSeconds;
                    }
                    entry.VideoPlays = result.VideoPlays;
                    entry.Execution = result.Execution;
                    entry.VideoRunSeconds = result.VideoRunSeconds ?? [];
                    if (result.Outcome == Outcome.Aborted) { linked.Cancel(); linked.Token.ThrowIfCancellationRequested(); }
                    FinishItem(result.Outcome, result.Message);
                }
                run.CurrentIndex++;
                run.DeviceBaseline = null; run.EventBookmark = null;
                Commit();
            }
            run.Outcome = run.Items.All(x => x.Outcome == Outcome.Pass) ? Outcome.Pass : Outcome.Fail;
            run.Phase = run.Outcome == Outcome.Pass ? RunPhase.Completed : RunPhase.Failed;
            run.EndedAt = DateTimeOffset.Now;
            Commit(); Note($"队列结束：{run.Outcome}");
            if (!run.Simulation && !run.CompletionActionClaimed)
            {
                run.CompletionActionClaimed = true; Commit(); // At-most-once completion action across crash/reopen.
                try { await platform.AfterRunAsync(run, linked.Token); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { Note("完成动作已取消"); }
                catch (Exception e)
                {
                    Note("完成动作失败：" + e.Message, "ERROR");
                    var last = run.Items[^1]; last.Errors.Add("完成动作失败：" + e.Message); last.Outcome = Outcome.Fail; last.Phase = RunPhase.Failed;
                    run.Outcome = Outcome.Fail; run.Phase = RunPhase.Failed; Commit();
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!run.IsTerminal)
            {
                if (run.CurrentIndex < run.Items.Count) FinishItem(Outcome.Aborted, "用户终止");
                SkipRemaining(); run.Phase = RunPhase.Aborted; run.Outcome = Outcome.Aborted; run.EndedAt = DateTimeOffset.Now;
                run.PendingOperation = ""; Commit(); Note("任务已终止");
            }
        }
        catch (Exception e)
        {
            if (run.CurrentIndex < run.Items.Count) FinishItem(Outcome.Fail, "执行异常：" + e.Message);
            SkipRemaining(); run.Phase = RunPhase.Failed; run.Outcome = Outcome.Fail; run.EndedAt = DateTimeOffset.Now;
            Commit(); Note($"任务异常：{e}，HRESULT=0x{e.HResult:X8}", "ERROR");
        }
        finally { cancellation = null; busy = false; pauseRequested = false; Changed?.Invoke(run); }
    }
    private void FinishItem(Outcome outcome, string message)
    {
        var run = Current!; var entry = run.Items[run.CurrentIndex];
        entry.Outcome = outcome; entry.Phase = outcome switch { Outcome.Pass => RunPhase.Completed, Outcome.Aborted => RunPhase.Aborted, _ => RunPhase.Failed };
        entry.EndedAt = DateTimeOffset.Now; entry.Message = message;
        run.Phase = RunPhase.Preflight;
        if (outcome is Outcome.Fail or Outcome.Uncertain or Outcome.Aborted) entry.Errors.Add(message);
        Note($"{run.CurrentIndex + 1}_{entry.Item.Name}：{message}", outcome == Outcome.Pass ? "INFO" : "ERROR");
        Commit();
    }
    private void SkipRemaining()
    {
        foreach (var item in Current!.Items.Where(x => x.Phase is RunPhase.Idle or RunPhase.Preflight))
        { item.Phase = RunPhase.Skipped; item.Message = "未执行"; }
    }
    private async Task RequestRebootAsync(string operation, CancellationToken ct)
    {
        var run = Current!;
        run.ExpectedBootId = platform.BootId; run.PendingOperation = operation;
        run.OperationId = Guid.NewGuid(); run.OperationStartedAt = DateTimeOffset.Now;
        Stage(RunPhase.AwaitingReboot, operation == "cycle" ? "等待真实重启，重开程序不增加周期" : "项目切换，等待重启");
        Note("检查点已提交，申请重启：" + run.OperationId);
        ct.ThrowIfCancellationRequested();
        await platform.RebootAsync(ct);
    }
    private async Task CompleteCycleAsync(RunSnapshot run, double seconds, CancellationToken ct)
    {
        var entry = run.Items[run.CurrentIndex];
        entry.Errors.AddRange(LegacyTiming.Check(entry.Item, entry.CycleSeconds, seconds));
        entry.CycleSeconds.Add(seconds); entry.CompletedCycles++; entry.RemainingSeconds = 0;
        if (!run.Simulation)
        {
            var errors = await platform.CheckAsync(entry.Item, run, ct);
            entry.Errors.AddRange(errors);
        }
        entry.Message = $"周期 {entry.CompletedCycles}/{entry.Item.Cycles}";
        run.PendingOperation = ""; run.ExpectedBootId = "";
        run.Phase = RunPhase.Countdown; entry.Phase = RunPhase.Countdown;
        Note($"{entry.Item.Name} 第 {entry.CompletedCycles} 次完成，耗时 {seconds:0.0} 秒"); Commit();
    }
    private async Task CountdownAsync(ItemRun entry, int seconds, CancellationToken ct)
    {
        entry.RemainingSeconds = seconds; Stage(RunPhase.Countdown, "倒计时");
        bool simulation = Current!.Simulation;
        var watch = Stopwatch.StartNew();
        while (entry.RemainingSeconds > 0)
        {
            watch.Stop();
            await PauseBoundaryAsync(ct);
            watch.Start();
            if (!simulation) entry.RemainingSeconds = Math.Max(0, (int)Math.Ceiling(seconds - watch.Elapsed.TotalSeconds));
            if (entry.RemainingSeconds == 0) break;
            entry.Message = $"{entry.RemainingSeconds} 秒后{(entry.Item.Kind == TaskKind.Reboot ? "重启" : "开始")}";
            Changed?.Invoke(Current!);
            await Task.Delay(simulation ? SimulationTick : TimeSpan.FromSeconds(Math.Min(1, Math.Max(0.001, seconds - watch.Elapsed.TotalSeconds))), ct);
            if (simulation)
            {
                if (pauseRequested) continue;
                entry.RemainingSeconds = Math.Max(0, entry.RemainingSeconds - 10);
            }
            else entry.RemainingSeconds = Math.Max(0, (int)Math.Ceiling(seconds - watch.Elapsed.TotalSeconds));
            Commit();
        }
        await PauseBoundaryAsync(ct);
        ct.ThrowIfCancellationRequested();
    }
    private async Task<bool> CycleCountdownAsync(ItemRun entry, int seconds, CancellationToken ct)
    {
        using var countdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Preparation may run alongside the countdown, but can never outlive its own
        // 60-second allowance. Await both tasks so cancellation also drains owned processes.
        preparationCancellation.CancelAfter(TimeSpan.FromSeconds(60));
        bool preparationFailed = false;
        async Task PrepareAsync()
        {
            IReadOnlyList<string> errors;
            try
            {
                errors = Current!.Simulation ? [] : await platform.PrepareCycleAsync(entry.Item, Current, preparationCancellation.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { errors = ["周期准备超时，已取消准备进程"]; }
            catch (Exception e) when (e is not OperationCanceledException)
            { errors = ["周期准备失败：" + e.Message]; }
            if (errors.Count == 0) return;
            preparationFailed = true;
            entry.Errors.AddRange(errors);
            foreach (string error in errors) Note(error, "ERROR");
            Commit();
            if (Current!.Options.StopOnFail) countdownCancellation.Cancel();
        }
        Task preparation = PrepareAsync();
        Task countdown = CountdownAsync(entry, seconds, countdownCancellation.Token);
        try
        {
            Task first = await Task.WhenAny(preparation, countdown);
            if (first.IsFaulted || first.IsCanceled) { preparationCancellation.Cancel(); countdownCancellation.Cancel(); }
            await Task.WhenAll(preparation, countdown);
        }
        catch (Exception error)
        {
            preparationCancellation.Cancel(); countdownCancellation.Cancel();
            // Task.WhenAll has already observed both completions. No helper is detached
            // when a cancellation, checkpoint failure or countdown error leaves this phase.
            if (error is OperationCanceledException && preparation.IsCompletedSuccessfully && !ct.IsCancellationRequested && preparationFailed && Current!.Options.StopOnFail) return false;
            throw;
        }
        ct.ThrowIfCancellationRequested();
        return !preparationFailed || !Current!.Options.StopOnFail;
    }
    private async Task SimulateDelayAsync(int seconds, CancellationToken ct)
    {
        for (int i = 0; i < seconds; i += 10)
        { await PauseBoundaryAsync(ct); await Task.Delay(SimulationTick, ct); }
    }
    private async Task PauseBoundaryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!pauseRequested) return;
        var old = Current!.Phase;
        var pause = Stopwatch.StartNew();
        Stage(RunPhase.Paused, "已暂停"); Note("暂停");
        while (pauseRequested) await Task.Delay(50, ct);
        Stage(old, "继续运行"); Note($"继续，暂停了 {pause.Elapsed.TotalSeconds:0} 秒");
    }
}
