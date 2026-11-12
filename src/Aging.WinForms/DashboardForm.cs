using Aging.Core;

namespace Aging.WinForms;

public sealed class DashboardForm : Form
{
    private readonly MainForm owner;
    private readonly DataGridView grid = new() { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox detail;
    private readonly Label summary, telemetry;
    private readonly CheckBox simulation;
    private readonly Button start, pause, stop, recover, settings, preflight;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    public DashboardForm(MainForm owner)
    {
        this.owner = owner;
        Ui.Style(this, BuildInfo.Product, new Size(940, 574));
        AppBranding.Logo(this, new(20, 14, 132, 30));
        summary = Ui.Label(this, "老化队列", new(170, 12, 536, 36)); summary.Font = new Font(Font.FontFamily, 14);
        simulation = Ui.Check(this, "仅演练（不测硬件）", false, new(716, 18, 204, 26));
        grid.Bounds = new(20, 60, 900, 224); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        grid.ColumnHeadersHeight = 32; grid.RowTemplate.Height = 30; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 239, 249); grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        foreach (string col in new[] { "序号", "测试项目", "状态", "周期 / 时长", "错误", "当前信息" }) grid.Columns.Add(col, col);
        grid.Columns[0].FillWeight = 38; grid.Columns[1].FillWeight = 75; grid.Columns[2].FillWeight = 72; grid.Columns[3].FillWeight = 100; grid.Columns[4].FillWeight = 40; grid.Columns[5].FillWeight = 240;
        Controls.Add(grid); grid.SelectionChanged += (_, _) => ShowDetail();
        start = Ui.Button(this, "开始测试", new(20, 302, 112, 36), async (_, _) => await owner.StartRunAsync(simulation.Checked)); start.BackColor = Ui.Teal; start.ForeColor = Color.White;
        simulation.CheckedChanged += (_, _) => { start.Text = simulation.Checked ? "开始流程演练" : "开始真实测试"; ShowDetail(); };
        pause = Ui.Button(this, "暂停", new(144, 302, 94, 36), (_, _) => { try { if (owner.Coordinator.PauseRequested) owner.Coordinator.Resume(); else owner.Coordinator.Pause(); UpdateState(); } catch (Exception e) { Ui.Error(this, e); } });
        stop = Ui.Button(this, "终止", new(250, 302, 94, 36), (_, _) => owner.StopRun());
        recover = Ui.Button(this, "恢复任务", new(356, 302, 110, 36), async (_, _) => await owner.RecoverAsync());
        preflight = Ui.Button(this, "依赖预检", new(478, 302, 110, 36), async (_, _) => await owner.PreflightAsync(simulation.Checked));
        settings = Ui.Button(this, "参数设置", new(600, 302, 110, 36), (_, _) => owner.OpenSettings());
        Ui.Button(this, "查看日志", new(722, 302, 110, 36), (_, _) => owner.OpenLogs());
        telemetry = Ui.Label(this, "温度：未获取    CPU：—    电源：—", new(20, 352, 900, 28)); telemetry.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        if (!Aging.Windows.WindowsPlatform.IsAdministrator) Ui.Button(this, "以管理员重新启动", new(714, 350, 206, 30), (_, _) => owner.RestartElevated());
        detail = new TextBox { Bounds = new(20, 390, 900, 164), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 249, 250) }; Controls.Add(detail);
        refresh.Tick += (_, _) => UpdateState(); refresh.Start(); UpdateState();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    public void SetTelemetry(TelemetrySample sample)
    { telemetry.Text = $"温度：{(sample.Temperature.HasValue ? sample.Temperature.Value.ToString("0.0") + "°C" : "未获取")}    CPU：{sample.CpuPercent?.ToString("0.0") ?? "—"}%    电池：{sample.BatteryPercent?.ToString() ?? "—"}%    电源：{sample.Power} / {sample.Charge}    {sample.Time:HH:mm:ss}"; }
    public void UpdateState()
    {
        var run = owner.Coordinator.Current; bool pending = run != null && !run.IsTerminal;
        int selected = grid.CurrentRow?.Index ?? 0;
        var items = run?.Items ?? owner.DisplayQueue.Select(x => new ItemRun { Item = x }).ToList();
        grid.SuspendLayout();
        if (grid.Rows.Count != items.Count) { grid.Rows.Clear(); foreach (var _ in items) grid.Rows.Add(); }
        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i]; string cycles = entry.Item.IsPower ? $"{entry.CompletedCycles}/{entry.Item.Cycles}" : entry.Item.IsStress ? $"{entry.ActiveSeconds / 60:0.#}/{entry.Item.Hours * 60:0.#} 分钟" : $"播放 {entry.VideoPlays} 次";
            grid.Rows[i].SetValues(i + 1, entry.Item.Name, Ui.Phase(entry.Phase, run?.Simulation == true), cycles, entry.Errors.Count, entry.Message);
            grid.Rows[i].Cells[2].Style.ForeColor = Ui.StateColor(entry.Phase);
        }
        if (grid.Rows.Count > 0 && selected < grid.Rows.Count) grid.Rows[selected].Selected = true;
        grid.ResumeLayout();
        summary.Text = run == null ? "真实老化队列 — 点击开始后选择本轮项目" : $"{(run.Simulation ? "流程演练，不代表硬件结果" : "真实测试")} · {Ui.Phase(run.Phase, run.Simulation)}" + (run.ExcludedItems.Count > 0 ? $" · 另有 {run.ExcludedItems.Count} 项未执行" : "");
        start.Text = simulation.Checked ? "开始流程演练" : "开始真实测试";
        start.Enabled = !owner.Coordinator.Busy && !pending && !owner.RecoveryBlocked; settings.Enabled = !pending; simulation.Enabled = !pending;
        if (pending) simulation.Checked = run!.Simulation;
        pause.Enabled = owner.Coordinator.CanPause; pause.Text = owner.Coordinator.PauseRequested ? "继续" : "暂停";
        stop.Enabled = pending && run!.Phase != RunPhase.AwaitingReboot || pending && !owner.Coordinator.Busy;
        recover.Enabled = pending && !owner.Coordinator.Busy; preflight.Enabled = !owner.Coordinator.Busy;
        ShowDetail();
    }
    private void ShowDetail()
    {
        var run = owner.Coordinator.Current; int index = grid.CurrentRow?.Index ?? -1;
        if (run == null || index < 0 || index >= run.Items.Count)
        { if (detail != null) detail.Text = "点击开始后选择本轮测试；保存的队列不变。\r\n含重启时保存检查点，进入当前 Windows 用户桌面后自动续测。\r\n负载引擎按队列参数启动，原始报告与 CPU/GPU/内存/磁盘采样保存在日志目录。\r\n缺少引擎、授权、报告或设备支持时不能判为通过；流程演练不能证明硬件通过。"; return; }
        var entry = run.Items[index];
        detail.Text = $"{entry.Item.Serialize()}\r\n{entry.Message}\r\n开始：{entry.StartedAt:yyyy-MM-dd HH:mm:ss}    结束：{entry.EndedAt:yyyy-MM-dd HH:mm:ss}\r\n日志：{run.LogDirectory}\r\n" + (run.ExcludedItems.Count > 0 ? "本轮未执行：" + string.Join("；", run.ExcludedItems) + "\r\n" : "") + string.Join("\r\n", entry.Errors.Concat(entry.Reports));
    }
    protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
}
