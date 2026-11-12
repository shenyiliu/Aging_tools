namespace Aging.Core;

public static class RunRequirements
{
    public static bool RequiresManualRecovery(IReadOnlyList<AgingItem> queue, AppOptions options) =>
        queue.Count > 0 && (queue.Any(x => x.Kind == TaskKind.Reboot) || options.AgingInNextBoot || options.SwitchReboot && queue.Count > 1);

    public static bool RequiresAdministrator(IReadOnlyList<AgingItem> queue, AppOptions options) =>
        queue.Any(x => x.IsPower || x.Kind == TaskKind.BurnIn || x.FanProfile != "N") || options.AgingInNextBoot || options.SwitchReboot && queue.Count > 1;
}

public static class RunSelection
{
    public static RunSnapshot Create(IReadOnlyList<AgingItem> configured, IEnumerable<int> selectedIndices, AppOptions options, bool simulation, string logRoot)
    {
        var selected = selectedIndices.ToHashSet();
        if (selected.Count == 0) throw new InvalidOperationException("请至少选择一个本轮执行项目");
        if (selected.Any(i => i < 0 || i >= configured.Count)) throw new ArgumentOutOfRangeException(nameof(selectedIndices));
        var run = AgingCoordinator.Create(configured.Where((_, i) => selected.Contains(i)), options, simulation, logRoot);
        run.ExcludedItems = configured.Where((_, i) => !selected.Contains(i)).Select(x => x.Serialize()).ToList();
        return run;
    }
}
