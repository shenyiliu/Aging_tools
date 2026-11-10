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
    public Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct) =>
        item.Kind == TaskKind.Video ? local.ExecuteAsync(item, run, progress, ct) : workers.ExecuteAsync(item, run, progress, ct);
    public Task AfterRunAsync(RunSnapshot run, CancellationToken ct) => local.AfterRunAsync(run, ct);
}
