using Aging.Core;
using Aging.Windows;
using System.Diagnostics;
using System.Text.Json;

namespace Aging.WinForms;

public sealed class MainForm : Form
{
    public AppFiles Files { get; }
    public AgingCoordinator Coordinator { get; }
    public bool RecoveryBlocked { get; private set; }
    private readonly LegacyRequest? legacyRequest;
    public IReadOnlyList<AgingItem> DisplayQueue => legacyRequest == null ? Files.Queue : [legacyRequest.Item];
    private readonly WindowsPlatform platform;
    private readonly JsonRunStore store;
    private readonly LegacyLogWriter logs = new();
    private readonly TelemetryCollector telemetry = new();
    private readonly Form popup = new() { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, BackColor = Color.FromArgb(0, 120, 215), StartPosition = FormStartPosition.Manual, TopMost = true };
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly Label title;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly List<Image> images = [];
    private DashboardForm? dashboard;
    private PowerTestForm? powerWindow;
    private Button agingButton = null!;
    private MaximumVolumeSession? maximumVolume;
    private bool exiting;
    private bool preparingRun;
    private readonly string? smokeDirectory;
    public MainForm(AppFiles files, bool resumeOnStart = false, string? smokeDirectory = null, LegacyRequest? legacyRequest = null)
    {
        Files = files; this.smokeDirectory = smokeDirectory; this.legacyRequest = legacyRequest;
        Ui.Style(this, BuildInfo.Product, new Size(446, 71));
        FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; ClientSize = new(446, 71); ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        platform = new WindowsPlatform(); store = new JsonRunStore(files.CheckpointPath);
        Coordinator = new AgingCoordinator(store, logs, new HostPlatform(platform, new WorkerBridge(Program.MainExecutable)));
        Coordinator.Changed += run => { dashboard?.UpdateState(); UpdatePopup(); UpdateToolbar(); UpdatePowerWindow(run); };
        platform.PlayVideoAsync = (path, ct) => { using var video = new VideoForm(path, Path.Combine(Files.Options.ReferenceRoot, "Other", "ICON")); return Task.FromResult(video.Play(dashboard is { Visible: true } ? dashboard : this, ct)); };
        platform.ConfirmShutdownAsync = ConfirmShutdownAsync;
        platform.BeforeReboot = () => maximumVolume?.Dispose();
        var brand = AppBranding.Logo(this, new(6, 1, 92, 20)); Ui.Drag(this, brand);
        title = Ui.Label(this, BuildInfo.Name, new(107, 1, 120, 20)); title.Font = new Font("Microsoft YaHei UI", 8); title.ForeColor = Ui.Teal; Ui.Drag(this, title);
        IconButton("SN1.bmp", "SN / KEY", new(230, 0, 38, 20), (_, _) => Launch(Files.Options.SnKeyPath));
        IconButton("Setting1.bmp", "参数设置", new(282, 0, 38, 20), (_, _) => OpenSettings());
        IconButton("Info1.bmp", "系统信息", new(334, 0, 38, 20), (_, _) => ShowSystemInfo());
        IconButton("menu4.bmp", "菜单", new(418, 0, 22, 20), (_, _) => menu.Show(this, new Point(340, 20)));
        ImageButton("RunBtn.bmp", "RunBtn_H.bmp", "退出", new(6, 26, 92, 40), (_, _) => Exit());
        ImageButton("DefaultBtn.bmp", "DefaultBtn_H.bmp", "功能测试", new(106, 26, 92, 40), (_, _) => Launch(Files.Options.SftPath));
        var aging = agingButton = ImageButton("DefaultBtn.bmp", "DefaultBtn_H.bmp", "老化测试", new(204, 26, 92, 40), async (_, _) =>
        {
            if (Coordinator.Current is { IsTerminal: false }) OpenCurrentItem();
            else await StartRunAsync(false);
        });
        aging.MouseEnter += (_, _) => { UpdatePopup(); popup.Show(this); PositionPopup(); };
        aging.MouseLeave += (_, _) => UpdateToolbar();
        ImageButton("ToolBox.bmp", "ToolBox_H.bmp", "", new(302, 26, 40, 40), (_, _) => menu.Show(this, new Point(260, 24)));
        ImageButton("DefaultBtn.bmp", "DefaultBtn_H.bmp", "写 SN+KEY", new(348, 26, 92, 40), (_, _) => Launch(Files.Options.SnKeyPath));
        menu.Items.Add("老化队列与流程演练", null, (_, _) => OpenDashboard()); menu.Items.Add("参数设置", null, (_, _) => OpenSettings());
        menu.Items.Add("日志查看", null, (_, _) => OpenLogs()); menu.Items.Add("系统信息", null, (_, _) => ShowSystemInfo());
        menu.Items.Add("本机兼容性检测", null, (_, _) => { var form = new CompatibilityForm(Files); form.Show(this); });
        menu.Items.Add("打开数据目录", null, (_, _) => Ui.OpenFolder(Files.Root)); menu.Items.Add("退出", null, (_, _) => Exit());
        ContextMenuStrip = menu;
        tray = new NotifyIcon { Icon = AppBranding.Icon, Text = BuildInfo.Product, ContextMenuStrip = menu, Visible = smokeDirectory == null };
        tray.DoubleClick += (_, _) => OpenDashboard();
        telemetry.Sampled += sample => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => dashboard?.SetTelemetry(sample))); };
        telemetry.Error += message => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (Coordinator.Current is { } run) logs.Event(run, "WARN", "采集错误：" + message); })); };
        timer.Tick += (_, _) => { if (!popup.Bounds.Contains(Cursor.Position) && !Bounds.Contains(Cursor.Position)) popup.Hide(); };
        timer.Start(); Move += (_, _) => PositionPopup();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Shown += async (_, _) => await OnShownAsync();
        FormClosing += (_, e) =>
        {
            if (!exiting && Coordinator.Busy) { e.Cancel = true; OpenDashboard(); MessageBox.Show(this, "请先终止正在运行的任务。", "老化测试"); }
        };
        PositionToolbar();
    }
    private async Task OnShownAsync()
    {
        try
        {
            var run = store.Load(); if (run != null) Coordinator.Attach(run);
            if (store.RecoveredFromBackup && run is { IsTerminal: false })
            {
                // An older backup may precede an irreversible action. Reconcile as interrupted, never repeat it automatically.
                run.Phase = RunPhase.Running; run.PendingOperation = "";
                logs.Event(run, "WARN", "主检查点损坏，使用备份；当前项目结果待确认，不自动重做系统动作");
                MessageBox.Show(this, "主检查点损坏，已加载有效备份。当前项目将记为结果未确认，不自动重复系统动作。", "状态恢复");
            }
            else if (store.RecoveredFromBackup && run != null)
            {
                run.Outcome = Outcome.Uncertain; run.Phase = RunPhase.Failed;
                run.Items[^1].Outcome = Outcome.Uncertain; run.Items[^1].Phase = RunPhase.Failed;
                run.Items[^1].Errors.Add("最新检查点损坏；备份来自已结束任务，无法确认是否丢失后续运行状态");
                store.Save(run); logs.Write(run); Coordinator.Attach(run);
                MessageBox.Show(this, "最新检查点损坏。已保留备份，但无法确认后续运行状态，记录为未确认。", "状态恢复");
            }
            if (smokeDirectory != null) { await RunSmokeAsync(smokeDirectory); return; }
            // Legacy --resume launches also require an explicit manual recovery.
            if (run is { IsTerminal: false })
            { OpenDashboard(); MessageBox.Show(dashboard, "发现未结束的任务。可选择恢复或终止；仅重开程序不会计为重启。", "任务恢复"); }
            else if (legacyRequest != null) OpenDashboard();
        }
        catch (Exception e)
        {
            RecoveryBlocked = true; dashboard?.UpdateState(); Ui.Error(this, e);
            if (smokeDirectory != null) { File.WriteAllText(Path.Combine(smokeDirectory, "error.txt"), e.ToString()); Environment.ExitCode = 1; exiting = true; Close(); }
        }
    }
    private void DisplayChanged(object? sender, EventArgs e) { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(PositionToolbar)); }
    private void PositionToolbar()
    { var area = (Screen.FromControl(this)).WorkingArea; Location = new(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 9); }
    private void PositionPopup() => popup.Location = new(Left + (int)(204 * DeviceDpi / 96F), Top - popup.Height);
    private Button ImageButton(string normal, string hover, string text, Rectangle bounds, EventHandler click)
    {
        var b = Ui.Button(this, text, bounds, click); b.FlatAppearance.BorderSize = 0; b.ForeColor = Color.White;
        b.BackgroundImage = LoadImage(normal); b.BackgroundImageLayout = ImageLayout.Stretch;
        b.MouseEnter += (_, _) => b.BackgroundImage = LoadImage(hover); b.MouseLeave += (_, _) => b.BackgroundImage = LoadImage(normal); return b;
    }
    private readonly Dictionary<string, Image?> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private Image? LoadImage(string name)
    {
        if (imageCache.TryGetValue(name, out var cached)) return cached;
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(file)) file = Path.Combine(Files.Options.ReferenceRoot, "Other", "ICON", name);
        Image? image = null;
        if (File.Exists(file)) { using var source = Image.FromFile(file); image = new Bitmap(source); images.Add(image); }
        imageCache[name] = image; return image;
    }
    private void IconButton(string image, string hint, Rectangle bounds, EventHandler click)
    { var b = ImageButton(image, image.Replace('1', '2'), "", bounds, click); b.AccessibleName = hint; var tooltip = new ToolTip(); tooltip.SetToolTip(b, hint); componentsTooltips.Add(tooltip); }
    private readonly List<ToolTip> componentsTooltips = [];
    private void UpdateToolbar()
    {
        var run = Coordinator.Current;
        title.Text = run is { IsTerminal: false } ? $"{(run.Simulation ? "演练" : "真实")} {Ui.Phase(run.Phase)}" : BuildInfo.Name;
        string image = run == null ? "DefaultBtn.bmp" : run.Simulation ? "DefaultBtn.bmp" : run.HardwarePassed ? "PassBtn.bmp" : run.Phase is RunPhase.Failed or RunPhase.Aborted ? "FailBtn.bmp" : run.IsTerminal ? "DefaultBtn.bmp" : "RunBtn.bmp";
        agingButton.BackgroundImage = LoadImage(image);
        agingButton.Text = run == null ? "老化测试" : run.Simulation ? (run.IsTerminal ? "演练完成" : "流程演练") : run.HardwarePassed ? (run.ExcludedItems.Count > 0 ? "已选项通过" : "老化通过") : run.Phase is RunPhase.Failed or RunPhase.Aborted ? "老化失败" : run.IsTerminal ? "老化结束" : "老化测试";
    }
    private void UpdatePopup()
    {
        if (popup.IsDisposed) return;
        var entries = Coordinator.Current?.Items ?? DisplayQueue.Select(x => new ItemRun { Item = x }).ToList();
        popup.SuspendLayout();
        if (popup.Controls.Count != entries.Count) { foreach (Control c in popup.Controls.Cast<Control>().ToArray()) c.Dispose(); }
        float scale = DeviceDpi / 96F; int rowHeight = (int)(16 * scale);
        popup.Size = new((int)(94 * scale), Math.Max((int)(70 * scale), entries.Count * rowHeight + (int)(10 * scale)));
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i]; Panel row;
            if (popup.Controls.Count <= i)
            {
                row = new Panel();
                var name = new Label { Font = new Font("Microsoft YaHei", 8.5F), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleRight, UseCompatibleTextRendering = true };
                var status = new Label { Font = name.Font, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, UseCompatibleTextRendering = true };
                name.Click += (_, _) => OpenItem((int)row.Tag!); status.Click += (_, _) => OpenItem((int)row.Tag!);
                row.Controls.Add(name); row.Controls.Add(status); popup.Controls.Add(row);
            }
            else row = (Panel)popup.Controls[i];
            row.Tag = i;
            row.Bounds = new(1, (int)(5 * scale) + i * rowHeight, popup.Width - 2, rowHeight);
            string state = entry.Phase switch { RunPhase.Idle => "WAIT", RunPhase.Running => "RUN", RunPhase.Countdown => entry.RemainingSeconds.ToString(), RunPhase.Completed => Coordinator.Current?.Simulation == true ? "演练" : "PASS", RunPhase.Failed => "FAIL", RunPhase.Paused => "STOP", RunPhase.Aborted => "STOP", _ => Ui.Phase(entry.Phase) };
            row.Controls[0].Bounds = new(0, 0, (int)(44 * scale), rowHeight); row.Controls[0].Text = entry.Item.Name;
            row.Controls[1].Bounds = new((int)(48 * scale), 0, popup.Width - (int)(49 * scale), rowHeight); row.Controls[1].Text = state;
        }
        popup.ResumeLayout(); PositionPopup();
    }
    private void OpenCurrentItem() => OpenItem(Coordinator.Current?.CurrentIndex ?? 0);
    private void OpenItem(int index)
    {
        var item = Coordinator.Current?.Items.ElementAtOrDefault(index)?.Item ?? DisplayQueue.ElementAtOrDefault(index);
        if (item?.IsPower != true) { OpenDashboard(); return; }
        if (powerWindow == null || powerWindow.IsDisposed || powerWindow.ItemIndex != index)
        { powerWindow?.Dispose(); powerWindow = new PowerTestForm(this, index); }
        powerWindow.UpdateState(); if (!powerWindow.Visible) powerWindow.Show(this); powerWindow.Activate();
    }
    private void UpdatePowerWindow(RunSnapshot run)
    {
        if (powerWindow is { IsDisposed: false } old && old.ItemIndex != run.CurrentIndex)
        { old.Dispose(); powerWindow = null; }
        if (powerWindow is { IsDisposed: false }) powerWindow.UpdateState();
        if (smokeDirectory == null && run.CurrentIndex < run.Items.Count && run.Items[run.CurrentIndex].Item.IsPower && run.Phase is RunPhase.Countdown or RunPhase.Running or RunPhase.Paused or RunPhase.AwaitingReboot)
        {
            if (powerWindow == null || powerWindow.IsDisposed || powerWindow.ItemIndex != run.CurrentIndex) OpenItem(run.CurrentIndex);
        }
    }
    public void OpenDashboard()
    {
        popup.Hide(); if (dashboard == null || dashboard.IsDisposed) dashboard = new DashboardForm(this);
        dashboard.UpdateState(); if (!dashboard.Visible) dashboard.Show(this); dashboard.Activate();
    }
    public void OpenSettings()
    {
        if (Coordinator.Current is { IsTerminal: false }) { MessageBox.Show(this, "请先结束当前任务，再修改配置。", "参数设置"); return; }
        while (true)
        {
            using var settings = new SettingsForm(Files); var result = settings.ShowDialog(this);
            Files.Reload(); if (result == DialogResult.Retry) continue;
            if (result == DialogResult.OK) Coordinator.ClearCompleted();
            dashboard?.UpdateState(); UpdatePopup(); UpdateToolbar(); break;
        }
    }
    public void OpenLogs() { var viewer = new LogViewerForm(Files.LogsRoot); viewer.Show(this); }
    public async Task StartRunAsync(bool simulation)
    {
        if (preparingRun) return;
        preparingRun = true;
        try
        {
            if (RecoveryBlocked) throw new InvalidOperationException("状态恢复未完成，已禁止新建任务。请先备份并处理数据目录中的 active-run.json 及 .bak，再重新启动。");
            if (Coordinator.Busy || Coordinator.Current is { IsTerminal: false }) throw new InvalidOperationException("已有未结束任务，请先恢复或终止");
            Files.Reload();
            var options = JsonData.Clone(Files.Options);
            if (legacyRequest != null)
            {
                options.StopOnFail = legacyRequest.StopOnFail;
                if (legacyRequest.EnginePath.Length > 0 && options.Engines.TryGetValue(WindowsPlatform.ProfileKey(legacyRequest.Item), out var profile)) profile.Executable = legacyRequest.EnginePath;
            }
            var run = AgingCoordinator.Create(DisplayQueue, options, simulation, Files.LogsRoot);
            if (!simulation)
            {
                using (var setup = new StartRunForm(DisplayQueue, options))
                {
                    var choice = setup.ShowDialog(dashboard ?? (IWin32Window)this);
                    if (choice == DialogResult.Ignore) { OpenSettings(); return; }
                    if (choice == DialogResult.Retry) { RestartElevated(); return; }
                    if (choice != DialogResult.OK) return;
                    run = setup.CreateRun(Files.LogsRoot); options = run.Options;
                }
                string details = "本轮项目：\r\n" + string.Join("\r\n", run.Items.Select(x => x.Item.Name + "：" + StartRunForm.Describe(x.Item)));
                if (run.ExcludedItems.Count > 0) details += "\r\n\r\n本轮未执行（不会判为通过）：" + string.Join("、", run.ExcludedItems.Select(x => AgingItem.Parse(x).Name));
                details += options.AgingInNextBoot ? "\r\n本次只保存等待状态，请随后手动重启或关机再开机。" : "\r\n将执行以上项目的真实负载或电源操作。";
                bool needsResume = RunRequirements.RequiresManualRecovery(run.Items.Select(x => x.Item).ToList(), options);
                if (needsResume) details += "\r\n重启后请手动打开工具，点击“恢复任务”继续测试。";
                if (options.TestWithMaxVolume) details += "\r\n已选择测试时使用最大音量。";
                if (MessageBox.Show(dashboard ?? (IWin32Window)this, details + "\r\n\r\n确认开始？", "开始真实老化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                var missing = new List<string>();
                foreach (var entry in run.Items)
                {
                    var check = await platform.PreflightAsync(entry.Item, run, CancellationToken.None);
                    if (!check.CanRun) missing.Add(entry.Item.Name + "：" + string.Join("；", check.Messages));
                }
                if (missing.Count > 0) { ShowPreflight(missing, "依赖发生变化，未开始测试；请重新选择项目或修改参数"); return; }
            }
            if (legacyRequest != null && !simulation) { run.LegacyLogPath = legacyRequest.LogPath; run.LegacyItemIndex = legacyRequest.ItemIndex; }
            await ExecuteCoordinatorAsync(run, false);
        }
        catch (Exception e) { Ui.Error(dashboard ?? (IWin32Window)this, e); }
        finally { preparingRun = false; }
    }
    private async Task ExecuteCoordinatorAsync(RunSnapshot run, bool recovering)
    {
        bool waitingForStart = run.Options.AgingInNextBoot && !run.InitialBootCompleted && (run.Phase != RunPhase.AwaitingReboot || platform.BootId == run.ExpectedBootId);
        await using var provider = waitingForStart ? null : CoreTempSession.Start(run, message => logs.Event(run, "INFO", message));
        using var volume = MaximumVolumeSession.Start(!run.Simulation && !waitingForStart && run.Options.TestWithMaxVolume, message => logs.Event(run, "INFO", message));
        maximumVolume = volume;
        if (!run.Simulation && !waitingForStart) telemetry.Start(run);
        try { await Coordinator.RunAsync(run, recovering); }
        finally { telemetry.Stop(); maximumVolume = null; dashboard?.UpdateState(); }
        if (run.Phase == RunPhase.AwaitingReboot && run.PendingOperation == "start")
        { OpenDashboard(); MessageBox.Show(dashboard, "已保存下次开机启动任务。现在可以退出工具，然后手动重启或关机再开机。\r\n开机后手动打开工具，点击“恢复任务”继续；同一次开机内重开软件不会启动。", "等待下次开机"); }
    }
    public async Task RecoverAsync()
    {
        try
        {
            var run = Coordinator.Current ?? throw new InvalidOperationException("没有可恢复任务");
            if (run.IsTerminal || Coordinator.Busy) throw new InvalidOperationException("当前任务不能恢复");
            if (run.Phase == RunPhase.AwaitingReboot && !run.Simulation && platform.BootId == run.ExpectedBootId)
            { MessageBox.Show(dashboard, "尚未发生真实重启，周期保持不变。请重启专用测试机后续测，或终止此任务。", "等待系统重启"); return; }
            await ExecuteCoordinatorAsync(run, true);
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    public void StopRun()
    {
        if (Coordinator.Busy) { Coordinator.Stop(); return; }
        if (Coordinator.Current is not { IsTerminal: false } run) return;
        if (run.CurrentIndex < run.Items.Count)
        {
            var current = run.Items[run.CurrentIndex]; current.Phase = RunPhase.Aborted; current.Outcome = Outcome.Aborted;
            current.Errors.Add("用户终止待恢复任务"); current.Message = "已终止"; current.EndedAt = DateTimeOffset.Now;
        }
        foreach (var item in run.Items.Where(x => x.Phase == RunPhase.Idle)) { item.Phase = RunPhase.Skipped; item.Message = "未执行"; }
        run.PendingOperation = ""; run.Phase = RunPhase.Aborted; run.Outcome = Outcome.Aborted; run.EndedAt = DateTimeOffset.Now;
        try { store.Save(run); logs.Write(run); logs.Event(run, "WARN", "用户终止待恢复任务"); Coordinator.Attach(run); }
        catch (Exception e) { Ui.Error(this, e); }
    }
    public async Task PreflightAsync(bool simulation)
    {
        try
        {
            Files.Reload();
            var run = AgingCoordinator.Create(DisplayQueue, Files.Options, false, Files.LogsRoot);
            var lines = new List<string>();
            foreach (var entry in run.Items)
            {
                var result = await platform.CheckItemAsync(entry.Item, run.Options, CancellationToken.None);
                lines.Add($"{entry.Item.Name}：{(result.CanRun ? "可执行" : "未就绪")}\r\n  " + string.Join("\r\n  ", result.Messages));
            }
            ShowPreflight(lines, "真实模式依赖预检（演练开关不影响检测）");
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void ShowPreflight(IEnumerable<string> lines, string caption)
    {
        using var report = new Form(); Ui.Style(report, caption, new Size(800, 540));
        report.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = string.Join("\r\n\r\n", lines) }); report.ShowDialog(dashboard ?? (IWin32Window)this);
    }
    public void RestartElevated()
    {
        try
        {
            if (Coordinator.Busy || Coordinator.Current is { IsTerminal: false }) throw new InvalidOperationException("请先终止当前任务，再切换管理员权限");
            var info = new ProcessStartInfo(Program.MainExecutable) { UseShellExecute = true, Verb = "runas" };
            foreach (var argument in new[] { "--data", Files.Root, "--wait-for-host-exit", "--show-dashboard" }) info.ArgumentList.Add(argument);
            if (Process.Start(info) == null) throw new IOException("管理员程序未能启动");
            exiting = true; dashboard?.Dispose(); Close();
        }
        catch (Exception e) { Ui.Error(dashboard ?? (IWin32Window)this, e); }
    }
    private void Launch(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("工具未配置或不存在，请在参数设置中选择原工具。", path);
            if (Path.GetFullPath(path).Equals(Path.Combine(Files.Options.ReferenceRoot, "Test.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("此入口不能启动原版 Test.exe");
            Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = true });
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void ShowSystemInfo() => MessageBox.Show(this, $"{Environment.MachineName}\r\n{Environment.OSVersion}\r\n{RuntimeInformation()}\r\n管理员：{WindowsPlatform.IsAdministrator}\r\n数据目录：{Files.Root}\r\n版本：{BuildInfo.Version}", "系统信息");
    private static string RuntimeInformation() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " / " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
    private Task<bool> ConfirmShutdownAsync(CancellationToken ct)
    {
        using var form = new Form(); Ui.Style(form, "老化结束", new Size(400, 150)); form.TopMost = true;
        int seconds = 10; bool confirmed = false;
        var label = Ui.Label(form, "10 秒后关机", new(24, 24, 350, 40));
        Ui.Button(form, "取消关机", new(236, 94, 130, 32), (_, _) => form.Close());
        using var countdown = new System.Windows.Forms.Timer { Interval = 1000 };
        countdown.Tick += (_, _) => { label.Text = $"{--seconds} 秒后关机"; if (seconds <= 0) { confirmed = true; form.Close(); } }; countdown.Start();
        using var registration = ct.Register(() => { if (form.IsHandleCreated) form.BeginInvoke(new Action(form.Close)); });
        form.ShowDialog(dashboard ?? (IWin32Window)this); return Task.FromResult(confirmed);
    }
    private async Task RunSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var smokeOptions = JsonData.Clone(Files.Options);
        smokeOptions.SwitchReboot = false; smokeOptions.ShutdownAfterAging = false; smokeOptions.StopOnFail = true; smokeOptions.AgingInNextBoot = false; smokeOptions.TestWithMaxVolume = false;
        Files.Save([AgingItem.Parse("Reboot|3|30|N|N|N|N"), AgingItem.Parse("S3|3|30|60|N|N|N"), AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("Video")], smokeOptions);
        OpenDashboard();
        await Task.Delay(150); Ui.Capture(this, Path.Combine(directory, "toolbar.png"));
        using (var settings = new SettingsForm(Files)) { settings.Show(); await Task.Delay(150); Ui.Capture(settings, Path.Combine(directory, "settings.png")); settings.Close(); }
        using (var editor = new ItemEditor()) { editor.Show(); await Task.Delay(150); Ui.Capture(editor, Path.Combine(directory, "item-editor.png")); editor.Close(); }
        using (var compatibility = new CompatibilityForm(Files)) { compatibility.Show(); await compatibility.ReportReady; Ui.Capture(compatibility, Path.Combine(directory, "compatibility.png")); compatibility.Close(); }
        if (!File.Exists(Path.Combine(Files.Root, "compatibility", "latest.json"))) throw new IOException("兼容性检测未生成本机报告");
        await StartRunAsync(true);
        Ui.Capture(dashboard!, Path.Combine(directory, "dashboard.png"));
        UpdatePopup(); popup.Show(this); Ui.Capture(popup, Path.Combine(directory, "aging-popup.png")); popup.Hide();
        using (var power = new PowerTestForm(this, 0)) { power.Show(); await Task.Delay(100); Ui.Capture(power, Path.Combine(directory, "reboot-window.png")); power.Close(); }
        using (var power = new PowerTestForm(this, 1)) { power.Show(); await Task.Delay(100); Ui.Capture(power, Path.Combine(directory, "sleep-window.png")); power.Close(); }
        using (var viewer = new LogViewerForm(Files.LogsRoot)) { viewer.Show(); viewer.LoadTree(); await Task.Delay(150); Ui.Capture(viewer, Path.Combine(directory, "logs.png")); viewer.Close(); }
        var snapshot = Coordinator.Current ?? throw new IOException("模拟运行未生成状态");
        using (var viewer = new LogViewerForm(Files.LogsRoot))
        {
            viewer.Show();
            await Task.Delay(100); // Let Shown populate the tree before explicitly selecting a historical file.
            string legacy = Path.Combine(Files.Options.ReferenceRoot, "LOG", "GWTEST_Aging_20261105180058.log");
            if (File.Exists(legacy)) { viewer.LoadFile(legacy); await Task.Delay(100); Ui.Capture(viewer, Path.Combine(directory, "legacy-log.png")); }
            string emptyTelemetry = Path.Combine(snapshot.LogDirectory, "GW_CoreTemp.log");
            File.WriteAllText(emptyTelemetry, "Item\tTemp\tCPU%\tBAT%\tPower\tCharge\tDate\r\n");
            viewer.LoadFile(emptyTelemetry); await Task.Delay(100); Ui.Capture(viewer, Path.Combine(directory, "empty-telemetry.png")); viewer.Close();
        }
        File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(snapshot, JsonData.Options));
        if (snapshot.Outcome != Outcome.Pass) Environment.ExitCode = 1;
        exiting = true; dashboard?.Dispose(); Close();
    }
    private void Exit()
    {
        if (Coordinator.Busy) { OpenDashboard(); MessageBox.Show(this, "请先终止正在运行的任务。", "退出"); return; }
        exiting = true; dashboard?.Dispose(); Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            timer.Dispose(); telemetry.Dispose(); popup.Dispose(); powerWindow?.Dispose(); tray.Dispose(); menu.Dispose();
            foreach (var image in images) image.Dispose(); foreach (var tooltip in componentsTooltips) tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
