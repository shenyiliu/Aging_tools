using Aging.Core;
using Aging.Windows;
using Aging.WinForms;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private static readonly List<object> results = [];
    private static readonly BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string output = "";
    private static Form shell = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        output = Path.GetFullPath(args.Length > 0 ? args[0] : "e2e-results"); Directory.CreateDirectory(output);
        shell = new Form { Text = "GMK Aging E2E", ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        shell.Shown += async (_, _) =>
        {
            try
            {
                await StartupSelectionAsync();
                await DependencyPreviewWithoutLoginAsync();
                await LegacyResumeRequiresManualActionAsync();
                await CancelDetectionAsync();
                await AllModulesSimulationAsync();
                await VideoThroughStartupAsync(false);
                await VideoThroughStartupAsync(true);
                if (args.Contains("--hardware")) await BurnInThroughStartupAsync();
            }
            catch (Exception e) { results.Add(new { Case = "UnhandledFailure", Passed = false, Error = e.ToString() }); Environment.ExitCode = 1; }
            finally
            {
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                shell.Close();
            }
        };
        Application.Run(shell); shell.Dispose();
        return Environment.ExitCode;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Control<T>(Form form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
    private static void Capture(Form form, string name)
    {
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, name));
    }
    private static List<AgingItem> DefaultQueue() => [AgingItem.Parse("Reboot|1|30|N|N|N|N"), AgingItem.Parse("S3|1|30|60|N|N|N"), AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("S4|1|30|60|N|N|N"), AgingItem.Parse("Video")];

    private static async Task StartupSelectionAsync()
    {
        var queue = DefaultQueue(); var options = new AppOptions { SwitchReboot = true };
        using var form = new StartRunForm(queue, options, (item, _, _) => Task.FromResult(item.Kind == TaskKind.S3 ? new PreflightResult(false, ["本 E2E 固定的不支持 S3 场景"]) : PreflightResult.Success("fixture dependency preview")));
        form.Show(shell); await form.DetectionReady;
        Require(!Control<Button>(form, "BeginRealRun").Enabled, "Unsupported selected S3 must block start");
        Capture(form, "startup-unavailable.png");
        Control<Button>(form, "SelectRunnable").PerformClick();
        Require(form.SelectedIndices.SequenceEqual(new[] { 0, 2, 3, 4 }), "Runnable selection must explicitly exclude S3 only");
        var run = form.CreateRun(Path.Combine(output, "selection"));
        Require(run.ExcludedItems.Count == 1 && queue.Count == 5 && Control<Label>(form, "ResumeHint").Text.Contains("手动"), "Selected reboot run must describe manual recovery and preserve the saved queue");
        var grid = Control<DataGridView>(form, "StartupItems");
        foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = row.Index is 2 or 4;
        Control<CheckBox>(form, "SwitchReboot").Checked = false;
        run = form.CreateRun(Path.Combine(output, "selection"));
        Require(!RunRequirements.RequiresManualRecovery(run.Items.Select(x => x.Item).ToList(), run.Options), "Load/video without switching must not require recovery");
        Require(Control<Button>(form, "BeginRealRun").Enabled == WindowsPlatform.IsAdministrator, "Elevation requirement must be actionable");
        Capture(form, "startup-load-only.png"); form.Close();
        results.Add(new { Case = "StartupSelectionAndContinuation", Passed = true, OriginalQueueUnchanged = true, PhysicalActions = false });
    }
    private static async Task DependencyPreviewWithoutLoginAsync()
    {
        var files = Files("dependency-preview");
        using var owner = new MainForm(files); owner.Show(shell); await Task.Delay(100);
        using (var settings = new SettingsForm(files))
        {
            settings.Show(shell);
            static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            Require(!Descendants(settings).OfType<CheckBox>().Any(c => c.Text.Contains("登录")), "Settings must remove the login continuation switch");
            Capture(settings, "settings-without-login.png"); settings.Close();
        }
        string? report = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text.StartsWith("真实模式依赖预检"));
            if (form == null) return;
            report = form.Controls.OfType<TextBox>().Single().Text; Capture(form, "dependency-preview-no-login.png"); form.Close();
        };
        timer.Start(); await owner.PreflightAsync(false); timer.Stop();
        Require(report != null && !report.Contains("登录") && !report.Contains("管理员"), "Component preview must not report queue-wide login/elevation conditions");
        Require(report!.Contains("Reboot：可执行"), "Reboot preview should succeed when no optional component is required");
        owner.Close(); results.Add(new { Case = "DependencyPreviewAndSettingsWithoutLogin", Passed = true, PhysicalActions = false });
    }
    private static async Task LegacyResumeRequiresManualActionAsync()
    {
        var files = Files("legacy-manual-recovery");
        var options = JsonData.Clone(files.Options); options.AgingInNextBoot = true; options.VideoPath = "";
        // A video-only checkpoint cannot perform a power action, even if this regression fails.
        var run = AgingCoordinator.Create([AgingItem.Parse("Video")], options, false, files.LogsRoot);
        run.Phase = RunPhase.AwaitingReboot; run.PendingOperation = "start"; run.ExpectedBootId = "previous-boot";
        var store = new JsonRunStore(files.CheckpointPath); store.Save(run); string before = File.ReadAllText(files.CheckpointPath);
        using var owner = new MainForm(files, resumeOnStart: true);
        bool prompted = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            IntPtr dialog = FindWindow(null, "任务恢复");
            if (dialog == IntPtr.Zero) return;
            GetWindowThreadProcessId(dialog, out uint pid); if (pid != Environment.ProcessId) return;
            prompted = true; PostMessage(GetDlgItem(dialog, 1), 0x00F5, IntPtr.Zero, IntPtr.Zero);
        };
        timer.Start(); owner.Show(shell); await Task.Delay(600); timer.Stop();
        Require(prompted && !owner.Coordinator.Busy && owner.Coordinator.Current?.Phase == RunPhase.AwaitingReboot, "Legacy --resume must prompt for manual recovery without running a module");
        Require(before == File.ReadAllText(files.CheckpointPath), "Opening a pending run must not advance its checkpoint");
        Capture(Application.OpenForms.OfType<DashboardForm>().Single(), "manual-recovery-pending.png");
        owner.StopRun(); owner.Close();
        results.Add(new { Case = "LegacyResumeWaitsForManualRecovery", Passed = true, PhysicalActions = false, CheckpointUnchanged = true });
    }
    private static async Task CancelDetectionAsync()
    {
        using var form = new StartRunForm([AgingItem.Parse("Video")], new AppOptions(), async (_, _, ct) => { await Task.Delay(5000, ct); return PreflightResult.Success("late result"); });
        form.Show(shell); await Task.Delay(100); form.Close(); await form.DetectionReady.WaitAsync(TimeSpan.FromSeconds(2));
        results.Add(new { Case = "CancelPendingDetection", Passed = true, PhysicalActions = false });
    }
    private static AppFiles Files(string name)
    {
        var files = new AppFiles(Path.Combine(output, name, "data"));
        var options = JsonData.Clone(files.Options); options.SwitchReboot = false;
        options.AgingInNextBoot = false; options.TestWithMaxVolume = false; options.ShutdownAfterAging = false; options.AfterAgingScript = "";
        files.Save(DefaultQueue(), options); return files;
    }
    private static async Task AllModulesSimulationAsync()
    {
        var files = Files("all-modules-simulation");
        files.Save([.. DefaultQueue(), AgingItem.Parse("CS|1|30|60|N|N|N"), AgingItem.Parse("FurMark|0.1|8X|1920x1080|Y"), AgingItem.Parse("3DMark|0.1|3DMarkV2_AllItem|1920x1080|Y")], files.Options);
        using var owner = new MainForm(files); owner.Show(shell); owner.Coordinator.SimulationTick = TimeSpan.FromMilliseconds(1);
        Require(owner.Text == BuildInfo.Product && owner.Icon != null, "Main window must expose the GMK product name and icon");
        Require(Control<PictureBox>(owner, "BrandLogo").Image != null, "GMKtec logo must load from embedded resources");
        await Task.Delay(100); owner.OpenDashboard(); await owner.StartRunAsync(true);
        var run = owner.Coordinator.Current!;
        Require(run.IsTerminal && run.Outcome == Outcome.Pass && run.Simulation && !run.HardwarePassed && run.Items.Count == 8, "All module orchestration must finish as simulation only");
        Capture(Application.OpenForms.OfType<DashboardForm>().Single(), "all-modules-simulation.png");
        owner.Close();
        results.Add(new { Case = "AllEightModulesSimulation", Passed = true, HardwarePassed = false, Simulation = true });
    }

    private static async Task<RunSnapshot> ThroughStartupAsync(string name, TaskKind selected, bool stopVideo, bool hardware)
    {
        var files = Files(name);
        if (hardware)
        {
            string referenceEngine = Path.Combine(files.Options.ReferenceRoot, "ReferenceEngines", "Reinstalled-BurnInTest-9.2.1006.0", "bit.exe");
            Require(File.Exists(referenceEngine), "Reinstalled reference BurnIn engine is missing");
            var options = JsonData.Clone(files.Options); options.BurnInPath = referenceEngine; files.Save(DefaultQueue(), options);
        }
        string beforeConfig = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(files.ConfigPath)));
        using var owner = new MainForm(files); owner.Show(shell); await Task.Delay(100); owner.OpenDashboard();
        int step = 0; string? failure = null; double maxPosition = 0; bool playingObserved = false; bool stopped = false;
        var watch = Stopwatch.StartNew(); var videoWatch = new Stopwatch();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed > (hardware ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(35))) throw new TimeoutException("UI run did not finish in deadline");
                var setup = Application.OpenForms.OfType<StartRunForm>().FirstOrDefault();
                if (step == 0 && setup != null && setup.DetectionReady.IsCompleted)
                {
                    var grid = Control<DataGridView>(setup, "StartupItems");
                    foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = files.Queue[row.Index].Kind == selected;
                    Control<CheckBox>(setup, "SwitchReboot").Checked = false;
                    Control<CheckBox>(setup, "MaximumVolume").Checked = false;
                    Control<CheckBox>(setup, "NextBoot").Checked = false;
                    Require(Control<Button>(setup, "BeginRealRun").Enabled, "Selected real module did not become runnable");
                    Capture(setup, name + "-startup.png"); step = 1;
                    Control<Button>(setup, "BeginRealRun").PerformClick(); return;
                }
                if (step == 1)
                {
                    IntPtr dialog = FindWindow(null, "开始真实老化");
                    if (dialog != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(dialog, out uint pid);
                        Require(pid == Environment.ProcessId, "Never automate another process's confirmation");
                        step = 2; PostMessage(GetDlgItem(dialog, 1), 0x00F5, IntPtr.Zero, IntPtr.Zero); return;
                    }
                }
                var video = Application.OpenForms.OfType<VideoForm>().FirstOrDefault();
                if (video != null && step == 2)
                {
                    var host = (Control)typeof(VideoForm).GetField("host", privateInstance)!.GetValue(video)!;
                    dynamic player = host.GetType().GetProperty("Player")!.GetValue(host)!;
                    player.settings.mute = true;
                    maxPosition = Math.Max(maxPosition, (double)player.controls.currentPosition);
                    if ((int)player.playState == 3) { playingObserved = true; if (!videoWatch.IsRunning) videoWatch.Start(); }
                    if (videoWatch.Elapsed > TimeSpan.FromSeconds(3))
                    {
                        step = 3;
                        if (stopVideo) { stopped = true; owner.StopRun(); }
                        else
                        {
                            var message = Message.Create(video.Handle, 0x0202, IntPtr.Zero, IntPtr.Zero); video.PreFilterMessage(ref message);
                            var fail = video.Controls.OfType<Button>().Single(x => x.Text.StartsWith("失败")); fail.PerformClick();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failure = e.ToString(); timer.Stop(); owner.StopRun();
                foreach (var form in Application.OpenForms.Cast<Form>().Where(x => x is StartRunForm or VideoForm).ToArray()) form.Close();
            }
        };
        timer.Start(); await owner.StartRunAsync(false); timer.Stop();
        if (failure != null) throw new InvalidOperationException(failure);
        var run = owner.Coordinator.Current ?? throw new IOException("Startup never reached the actual coordinator");
        Require(!run.Simulation && run.IsTerminal && run.Items.Count == 1 && run.ExcludedItems.Count == 4, "Real selected scope or terminal state is wrong");
        Require(!run.Options.SwitchReboot && !run.Options.TestWithMaxVolume, "Load-only run unexpectedly requested reboot/volume changes");
        Require(beforeConfig == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(files.ConfigPath))), "Saved queue must not change when choosing this run");
        if (!hardware)
        {
            Require(playingObserved && maxPosition > 1, "Real video did not play");
            Require(run.Outcome == (stopVideo ? Outcome.Aborted : Outcome.Fail), "Real manual FAIL/stop must not become PASS");
        }
        Capture(Application.OpenForms.OfType<DashboardForm>().Single(), name + "-result.png");
        owner.Close();
        results.Add(new { Case = name, Passed = true, Simulation = false, RealVideoPlayed = playingObserved, PlaybackSeconds = maxPosition, StopRequested = stopped, Outcome = run.Outcome.ToString(), run.HardwarePassed, LogDirectory = run.LogDirectory, ExcludedCount = run.ExcludedItems.Count });
        return run;
    }
    private static async Task VideoThroughStartupAsync(bool stop) => await ThroughStartupAsync(stop ? "real-video-stop" : "real-video-fail", TaskKind.Video, stop, false);
    private static async Task BurnInThroughStartupAsync()
    {
        var run = await ThroughStartupAsync("real-burnin-six-minute", TaskKind.BurnIn, false, true);
        if (run.Outcome != Outcome.Pass) results.Add(new { Case = "BurnInHardwareAcceptance", Passed = false, Detail = "Actual engine returned non-PASS; retained raw report, not overridden", Errors = run.Items.SelectMany(x => x.Errors).ToList() });
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
