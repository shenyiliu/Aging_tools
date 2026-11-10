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
        Ui.Style(this, "老化测试", new Size(940, 574));
        summary = Ui.Label(this, "老化队列", new(20, 12, 660, 36)); summary.Font = new Font(Font.FontFamily, 14);
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
        summary.Text = run == null ? "真实老化队列 — 请先匹配工具路径并预检" : $"{(run.Simulation ? "流程演练，不代表硬件结果" : "真实测试")} · {Ui.Phase(run.Phase, run.Simulation)}";
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
        { if (detail != null) detail.Text = "首次使用：检查队列及 Tools 组件路径；重启需管理员并保存登录后续测。\r\n真实测试调用已复制的 PwrTest、BurnInTest 和视频组件。温度与风扇支持依机型而定。\r\nFurMark/3DMark 原包缺少可执行引擎；当前不会启动缺失项目。\r\n流程演练不运行硬件，演练结果不能证明机器通过。"; return; }
        var entry = run.Items[index];
        detail.Text = $"{entry.Item.Serialize()}\r\n{entry.Message}\r\n开始：{entry.StartedAt:yyyy-MM-dd HH:mm:ss}    结束：{entry.EndedAt:yyyy-MM-dd HH:mm:ss}\r\n日志：{run.LogDirectory}\r\n" + string.Join("\r\n", entry.Errors.Concat(entry.Reports));
    }
    protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
}
