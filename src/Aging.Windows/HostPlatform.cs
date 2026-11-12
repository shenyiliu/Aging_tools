using Aging.Core;

namespace Aging.Windows;

public sealed class HostPlatform(WindowsPlatform local, WorkerBridge workers) : ITaskPlatform
{
    public string BootId => local.BootId;
    public Task<PreflightResult> PreflightAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => local.PreflightAsync(item, run, ct);
    public Task<DeviceSnapshot> CaptureDevicesAsync(CancellationToken ct) => local.CaptureDevicesAsync(ct);
    public Task<IReadOnlyList<string>> CheckAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => local.CheckAsync(item, run, ct);
    public Task<IReadOnlyList<string>> PrepareCycleAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => local.PrepareCycleAsync(item, run, ct);
    public Task RebootAsync(CancellationToken ct) => local.RebootAsync(ct);
    public async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { return await (item.Kind == TaskKind.Video ? local.ExecuteAsync(item, run, progress, ct) : workers.ExecuteAsync(item, run, progress, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return new(Outcome.Aborted, "用户停止，已保留宿主及引擎输出") { Execution = new("Cancelled", watch.Elapsed.TotalSeconds, [run.LogDirectory]) }; }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        { return new(Outcome.Fail, "执行失败：" + e.Message) { Execution = new(e is TimeoutException ? "TimedOut" : "WorkerFailed", watch.Elapsed.TotalSeconds, [run.LogDirectory]) }; }
    }
    public Task AfterRunAsync(RunSnapshot run, CancellationToken ct) => local.AfterRunAsync(run, ct);
}
