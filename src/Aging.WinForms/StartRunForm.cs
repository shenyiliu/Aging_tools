using Aging.Core;
using Aging.Windows;

namespace Aging.WinForms;

public sealed class StartRunForm : Form
{
    private readonly IReadOnlyList<AgingItem> configured;
    private readonly AppOptions options;
    private readonly Func<AgingItem, AppOptions, CancellationToken, Task<PreflightResult>> check;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DataGridView grid;
    private readonly Label selection, resumeHint, status;
    private readonly CheckBox switchBoot, nextBoot, maxVolume;
    private readonly Button begin, eligible, recheck, elevate;
    private readonly PreflightResult?[] results;
    private bool checking;
    private bool lifetimeDisposed;
    private readonly TaskCompletionSource ready = new();
    public Task DetectionReady => ready.Task;
    public IReadOnlyList<int> SelectedIndices => grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => r.Index).ToList();

    public StartRunForm(IReadOnlyList<AgingItem> queue, AppOptions source,
        Func<AgingItem, AppOptions, CancellationToken, Task<PreflightResult>>? checker = null)
    {
        configured = queue; options = JsonData.Clone(source); results = new PreflightResult?[queue.Count];
        check = checker ?? new WindowsPlatform().CheckItemAsync;
        Ui.Style(this, "开始老化测试 — 选择本轮项目", new Size(900, 640));
        TopMost = true;
        Ui.Label(this, "勾选本轮要执行的真实测试。检测失败的项目需修复或取消勾选；原保存队列不会被修改。", new(20, 12, 860, 46));
        grid = new DataGridView { Name = "StartupItems", Bounds = new(20, 68, 860, 264), AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "本轮执行", FillWeight = 55 });
        foreach (var (name, weight) in new[] { ("项目", 65), ("次数 / 时长", 105), ("检测结果", 75), ("说明", 290) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, ReadOnly = true, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.RowTemplate.Height = 46; grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 239, 249); grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        foreach (var item in queue) grid.Rows.Add(true, item.Name, Describe(item), "检测中", "");
        Controls.Add(grid);
        selection = Ui.Label(this, "", new(20, 340, 860, 44));
        switchBoot = Ui.Check(this, "按原规则切项重启", options.SwitchReboot, new(20, 390, 250, 28)); switchBoot.Name = "SwitchReboot";
        maxVolume = Ui.Check(this, "测试时最大音量", options.TestWithMaxVolume, new(300, 390, 250, 28)); maxVolume.Name = "MaximumVolume";
        nextBoot = Ui.Check(this, "下次开机开始", options.AgingInNextBoot, new(580, 390, 280, 28)); nextBoot.Name = "NextBoot";
        resumeHint = Ui.Label(this, "", new(20, 428, 860, 48)); resumeHint.Name = "ResumeHint";
        status = Ui.Label(this, "正在读取本机能力和组件依赖……", new(20, 480, 860, 42)); status.Name = "StartupStatus";
        recheck = Ui.Button(this, "重新检测", new(20, 532, 110, 32), async (_, _) => await DetectAsync());
        eligible = Ui.Button(this, "只选可运行项目", new(142, 532, 158, 32), (_, _) => SelectRunnable()); eligible.Name = "SelectRunnable";
        Ui.Button(this, "参数设置", new(312, 532, 110, 32), (_, _) => { DialogResult = DialogResult.Ignore; Close(); });
        elevate = Ui.Button(this, "以管理员重新打开", new(434, 532, 208, 32), (_, _) => { DialogResult = DialogResult.Retry; Close(); }); elevate.Name = "Elevate";
        begin = Ui.Button(this, "开始真实老化", new(550, 590, 210, 34), (sender, _) => { grid.EndEdit(); if (sender is not Button { Enabled: true }) return; DialogResult = DialogResult.OK; Close(); });
        begin.Name = "BeginRealRun"; begin.BackColor = Ui.Teal; begin.ForeColor = Color.White;
        var cancel = Ui.Button(this, "取消", new(774, 590, 106, 34), (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        CancelButton = cancel;
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, _) => UpdateSelection();
        switchBoot.CheckedChanged += (_, _) => UpdateSelection(); nextBoot.CheckedChanged += (_, _) => UpdateSelection();
        Shown += async (_, _) => await DetectAsync();
        UpdateSelection();
    }

    public static string Describe(AgingItem item) => item.IsPower ? $"{item.Cycles} 次，每次倒计时 {item.DelaySeconds} 秒" +
        (item.Kind == TaskKind.Reboot ? "" : $"，睡眠 {item.SleepSeconds} 秒") : item.IsStress ? $"{item.Hours * 60:0.##} 分钟" : "人工确认画面和声音";

    private AppOptions SelectedOptions()
    {
        var selectedOptions = JsonData.Clone(options);
        selectedOptions.SwitchReboot = switchBoot.Checked; selectedOptions.AgingInNextBoot = nextBoot.Checked; selectedOptions.TestWithMaxVolume = maxVolume.Checked;
        return selectedOptions;
    }
    public RunSnapshot CreateRun(string logRoot) => RunSelection.Create(configured, SelectedIndices, SelectedOptions(), false, logRoot);

    private async Task DetectAsync()
    {
        if (checking) return;
        checking = true; UpdateSelection();
        try
        {
            for (int i = 0; i < configured.Count; i++)
            {
                try { results[i] = await check(configured[i], options, lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception e) { results[i] = new(false, ["检测未完成：" + e.Message]); }
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                grid.Rows[i].Cells[3].Value = results[i]!.CanRun ? "可运行" : "未就绪";
                grid.Rows[i].Cells[3].Style.ForeColor = results[i]!.CanRun ? Ui.Pass : Ui.Fail;
                grid.Rows[i].Cells[4].Value = results[i]!.CanRun ? "依赖就绪，实际结果由测试产生" : string.Join("；", results[i]!.Messages);
            }
        }
        finally
        {
            checking = false;
            if (!IsDisposed && !lifetime.IsCancellationRequested) UpdateSelection();
            ready.TrySetResult();
        }
    }

    public void SelectRunnable()
    {
        if (checking) return;
        foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = results[row.Index]?.CanRun == true;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (selection == null || begin == null) return;
        var indices = SelectedIndices; var items = indices.Select(i => configured[i]).ToList();
        var currentOptions = SelectedOptions();
        string omitted = string.Join("、", configured.Where((_, i) => !indices.Contains(i)).Select(x => x.Name));
        selection.Text = $"本轮执行 {items.Count}/{configured.Count} 项：{string.Join(" → ", items.Select(x => x.Name))}" +
            (omitted.Length == 0 ? "" : $"\r\n本轮不执行（不会判为通过）：{omitted}");
        bool needsResume = RunRequirements.RequiresManualRecovery(items, currentOptions);
        bool needsAdmin = RunRequirements.RequiresAdministrator(items, currentOptions) && !WindowsPlatform.IsAdministrator;
        resumeHint.Text = needsResume ? "本轮将在重启后自动续测：进度将保存到检查点。\r\n进入当前 Windows 用户桌面后自动继续；同次开机重开软件不会增加周期。" : "本轮无需重启续测。可关闭切项重启，仅运行当前选择的负载或视频项目。";
        bool blocked = indices.Any(i => results[i]?.CanRun != true);
        status.Text = checking ? "正在检测，请稍候……" : items.Count == 0 ? "请至少勾选一项测试。" : blocked ? "所选项目有未就绪项：请修复、取消该项勾选，或点击“只选可运行项目”。" : needsAdmin ? "所选测试需要管理员权限，请点击下方按钮授权后重新打开。" : "所选项目可开始；点击开始后确认项目和次数，才会执行真实测试。";
        status.ForeColor = blocked || needsAdmin || items.Count == 0 ? Ui.Fail : Ui.Pass;
        begin.Enabled = !checking && items.Count > 0 && !blocked && !needsAdmin;
        eligible.Enabled = !checking; recheck.Enabled = !checking; elevate.Visible = needsAdmin;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetimeDisposed) { lifetimeDisposed = true; lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
