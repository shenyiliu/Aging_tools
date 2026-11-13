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
    private static int burnInMode;
    private static bool videoPass;
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        output = Path.GetFullPath(args.Length > 0 ? args[0] : "e2e-results"); Directory.CreateDirectory(output);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, error) =>
        {
            results.Add(new { Case = "UnhandledUiFailure", Passed = false, Error = error.Exception.ToString() });
            File.WriteAllText(Path.Combine(output, "ui-thread-exception.txt"), error.Exception.ToString());
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(1);
        };
        if (args.Contains("--files-only"))
        {
            try { AppFilesMigrationRegressions(); }
            catch (Exception e) { results.Add(new { Case = "UnhandledFailure", Passed = false, Error = e.ToString() }); Environment.ExitCode = 1; }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return Environment.ExitCode;
        }
        shell = new Form { Text = "GMK Aging E2E", ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        shell.Shown += async (_, _) =>
        {
            try
            {
                if (args.Contains("--compatibility-only")) { await CompatibilityReportRegressionsAsync(); return; }
                int powerAt = Array.IndexOf(args, "--power-item");
                if (powerAt >= 0) { await PowerThroughStartupAsync(args[powerAt + 1]); return; }
                if (args.Contains("--gpu-only")) { await ThroughStartupAsync("real-furmark-six-minute", TaskKind.FurMark, false, true); return; }
                if (args.Contains("--hardware-only"))
                {
                    int modeAt = Array.IndexOf(args, "--burnin-mode");
                    if (modeAt >= 0) { burnInMode = int.Parse(args[modeAt + 1]); await BurnInThroughStartupAsync(); }
                    else for (burnInMode = 0; burnInMode < 3; burnInMode++) await BurnInThroughStartupAsync();
                    return;
                }
                AppFilesMigrationRegressions();
                await CompatibilityReportRegressionsAsync();
                ToolbarImagesSurviveControlDisposal();
                DdrEditorAndQueueRoundTrip();
                await BootIdentityRegressionAsync();
                await StartupSelectionAsync();
                await DependencyPreviewWithoutLoginAsync();
                await ReopenModelessWindowsAsync();
                await LegacyResumeRequiresManualActionAsync();
                await CancelDetectionAsync();
                await AllModulesSimulationAsync();
                await VideoThroughStartupAsync(false);
                await VideoThroughStartupAsync(true);
                videoPass = true; await VideoThroughStartupAsync(false); videoPass = false;
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

    private static void ToolbarImagesSurviveControlDisposal()
    {
        var owner = new MainForm(Files("toolbar-image-lifetime"));
        int observed = 0;
        foreach (var button in owner.Controls.OfType<Button>())
        {
            if (button.BackgroundImage is not { } image) continue;
            button.Disposed += (_, _) => { _ = image.Flags; observed++; };
        }
        owner.Dispose(); owner.Dispose();
        Require(observed > 0, "Toolbar image regression must exercise actual background images");
        results.Add(new { Case = "ToolbarImagesRemainValidUntilTheirControlsAreDisposed", Passed = true, CheckedControls = observed });
    }

    private static void DdrEditorAndQueueRoundTrip()
    {
        var files = Files("ddr-configuration");
        var item = ItemEditor.CreateDefault("DDR Test", files.ConfigPath);
        Require(item.Kind == TaskKind.BurnIn && item.BurnInMode == 3, "DDR menu must create the dedicated memory mode");
        using var editor = new ItemEditor(item, files.ConfigPath);
        editor.Show(shell);
        Require(editor.Controls.OfType<ComboBox>().Single().SelectedIndex == 3, "DDR editor did not select memory mode");
        editor.Controls.OfType<Button>().Single(x => x.Text == "保存").PerformClick();
        Require(editor.Result?.BurnInMode == 3, "Saving DDR editor changed its mode");
        files.Save([editor.Result!], files.Options); files.Reload();
        Require(files.Queue.Single().BurnInMode == 3 && StartRunForm.Describe(files.Queue[0]).Contains("DDR"), "DDR mode must survive queue persistence and be identified at startup");
        using var settings = new SettingsForm(files);
        Require(settings.Controls.Count > 0, "DDR queue failed to render in settings");
        results.Add(new { Case = "DdrEditorAndQueueRoundTrip", Passed = true, PhysicalActions = false });
    }

    private static async Task BootIdentityRegressionAsync()
    {
        string xml = WindowsBootIdentity.ReadEvent();
        string actual = WindowsBootIdentity.FromEvent(xml);
        Require(new WindowsPlatform().BootId == actual, "Boot identity must be stable in the current boot");
        File.WriteAllText(Path.Combine(output, "current-kernel-start.xml"), xml);
        string Event(long record, string time) => $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-General'/><EventID>12</EventID><EventRecordID>{record}</EventRecordID></System><EventData><Data Name='StartTime'>{time}</Data></EventData></Event>";
        string before = WindowsBootIdentity.FromEvent(Event(100, "2026-11-13T01:03:28Z"));
        string after = WindowsBootIdentity.FromEvent(Event(200, "2026-11-13T03:01:59Z"));
        Require(BootSession.HasChanged(before, after) && !BootSession.HasChanged(after, after), "Only a new kernel start may change boot identity");
        Require(!BootSession.CanCompare("c30debed-de3b-11f1-81f7-ecc34bd5b6db", actual), "Old GUID checkpoints cannot be counted after a format upgrade");
        Require(!BootSession.HasChanged("", actual) && !BootSession.HasChanged(BootSession.EventPrefix + "broken", actual), "Missing or malformed identity must fail closed");
        try { WindowsBootIdentity.FromEvent(xml.Replace("Microsoft-Windows-Kernel-General", "Other-Provider")); throw new InvalidOperationException("Wrong provider was accepted"); }
        catch (IOException) { }
        foreach (var scenario in new[] { (Name: "same", Previous: after, Count: 0), (Name: "changed", Previous: before, Count: 1), (Name: "legacy", Previous: "old-guid", Count: 0) })
        {
            string root = Path.Combine(output, "boot-recovery-" + scenario.Name);
            var run = AgingCoordinator.Create([AgingItem.Parse("Reboot|1|30|N|N|N|N")], new AppOptions { SwitchReboot = false }, false, Path.Combine(root, "logs"));
            run.Phase = RunPhase.AwaitingReboot; run.Items[0].Phase = RunPhase.AwaitingReboot;
            run.ExpectedBootId = scenario.Previous; run.PendingOperation = "cycle"; run.OperationId = Guid.NewGuid(); run.OperationStartedAt = DateTimeOffset.Now.AddSeconds(-10);
            var coordinator = new AgingCoordinator(new JsonRunStore(Path.Combine(root, "state.json")), new LegacyLogWriter(), new BootEvidencePlatform(after));
            await coordinator.RunAsync(run, true);
            Require(run.Items[0].CompletedCycles == scenario.Count && run.HardwarePassed == (scenario.Count == 1), "Reboot recovery counted an unproven boot: " + scenario.Name);
        }
        results.Add(new { Case = "KernelStartIdentityAndRecoveryCountsOnlyNewBoots", Passed = true, PhysicalActions = false, CurrentBootId = actual });
    }
    private sealed class BootEvidencePlatform(string bootId) : ITaskPlatform
    {
        public string BootId => bootId;
        public Task<PreflightResult> PreflightAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => Task.FromResult(PreflightResult.Success());
        public Task<DeviceSnapshot> CaptureDevicesAsync(CancellationToken ct) => Task.FromResult(new DeviceSnapshot([], 0));
        public Task<IReadOnlyList<string>> CheckAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RebootAsync(CancellationToken ct) => throw new InvalidOperationException("Boot evidence regression must never reboot");
        public Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct) => throw new InvalidOperationException("Boot evidence regression must never launch hardware loads");
        public Task AfterRunAsync(RunSnapshot run, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task CompatibilityReportRegressionsAsync()
    {
        string bundle = AppFiles.FindBundledTools();
        var defaults = AppFiles.DiscoverTools(bundle);
        Require(File.Exists(defaults.Power) && File.Exists(defaults.Video), "Compatibility regression needs the bundled components");
        async Task<JsonDocument> CaptureReport(string data, string name)
        {
            string path = Path.Combine(output, name + ".json");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Test.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--data", data, "--compatibility-report", path }
            };
            using var process = Process.Start(start)!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Require(process.ExitCode == 0 && File.Exists(path), "Compatibility CLI did not produce a report");
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        static JsonElement Item(JsonDocument report, string name) => report.RootElement.GetProperty("Items").EnumerateArray().Single(x => x.GetProperty("Name").GetString() == name);
        void AssertTools(JsonDocument report, string video)
        {
            Require(Item(report, "PwrTest x64").GetProperty("Detail").GetString() == defaults.Power, "Compatibility CLI incorrectly reports the bundled PwrTest as missing");
            Require(Item(report, "视频文件").GetProperty("Detail").GetString() == video, "Compatibility CLI failed to resolve the configured video");
            Require(Item(report, "BurnIn").GetProperty("Status").GetString() != "未配置已验证引擎", "Compatibility CLI incorrectly reports the bundled BurnIn as missing");
        }
        string fresh = Path.Combine(output, "compatibility-fresh-data");
        Require(!Directory.Exists(fresh), "Fresh compatibility data must not already exist");
        using (var report = await CaptureReport(fresh, "compatibility-fresh")) AssertTools(report, defaults.Video);
        Require(!Directory.Exists(fresh), "A read-only compatibility report must not initialize user data");
        results.Add(new { Case = "FreshCompatibilityReportDiscoversBundledToolsWithoutWritingUserData", Passed = true, PhysicalActions = false });

        var files = Files("compatibility-existing");
        var options = JsonData.Clone(files.Options);
        string external = Path.Combine(output, "compatibility-external-video.mp4");
        File.WriteAllText(external, "Read-only path resolution fixture; never played");
        options.ReferenceRoot = Path.Combine(output, "removed-old-install");
        options.PwrTestPath = Path.Combine(options.ReferenceRoot, "Sleep", "pwrtest_x64.exe");
        options.BurnInPath = Path.Combine(options.ReferenceRoot, "BurnInTest", "BurnInTest", "bit.exe");
        options.VideoPath = external;
        files.Save(DefaultQueue(), options);
        var config = File.ReadAllBytes(files.ConfigPath); var saved = File.ReadAllBytes(files.OptionsPath);
        using (var report = await CaptureReport(files.Root, "compatibility-existing")) AssertTools(report, external);
        Require(config.SequenceEqual(File.ReadAllBytes(files.ConfigPath)) && saved.SequenceEqual(File.ReadAllBytes(files.OptionsPath)), "Compatibility inspection must not persist migration or alter the queue/options");
        results.Add(new { Case = "CompatibilityReportResolvesMigrationAndPreservesExternalOverridesWithoutWriting", Passed = true, PhysicalActions = false });
    }

    private static void AppFilesMigrationRegressions()
    {
        string bundle = AppFiles.FindBundledTools(), ownEngine = AppFiles.DiscoverBurnIn(bundle);
        Require(bundle.Length > 0 && File.Exists(ownEngine), "File migration regression needs the owned tool bundle");
        string bundleHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownEngine)));
        string fixture = Path.Combine(output, "file-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        List<AgingItem> queue = [AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("Video"), AgingItem.Parse("BurnIn|0.1|2")];
        AppOptions Rooted(string root) => new()
        {
            ReferenceRoot = root, SwitchReboot = false,
            PwrTestPath = Path.Combine(root, "Sleep", "pwrtest_x64.exe"), VideoPath = Path.Combine(root, "SFT", "TestVideo.mp4"),
            SftPath = Path.Combine(root, "SFT", "SFTClassic.exe"), BurnInPath = Path.Combine(root, "BurnInTest", "BurnInTest", "bit.exe"),
            FurMarkPath = Path.Combine(root, "FurMark", "engine", "FurMark.exe"),
            Mark3DPath = Path.Combine(root, "3DMark", "engine", "3DMarkCmd.exe"), Mark11Path = Path.Combine(root, "3DMark", "engine11", "3DMark11Cmd.exe")
        };
        AppFiles ReloadFixture(string name, AppOptions options)
        {
            var files = new AppFiles(Path.Combine(fixture, name, "data"));
            files.Save(queue, options); files.Reload();
            Require(files.Queue.Select(x => x.Serialize()).SequenceEqual(queue.Select(x => x.Serialize())), "Tool migration must preserve ordered queue entries, including repeated BurnIn items");
            var persisted = JsonSerializer.Deserialize<AppOptions>(File.ReadAllBytes(files.OptionsPath), JsonData.Options)!;
            Require(persisted.ReferenceRoot == files.Options.ReferenceRoot && persisted.FurMarkPath == files.Options.FurMarkPath, "Migrated defaults must persist for a later application launch");
            return files;
        }
        void AssertBundlePaths(AppOptions options)
        {
            var defaults = AppFiles.DiscoverTools(bundle);
            Require(options.ReferenceRoot == bundle && options.PwrTestPath == defaults.Power && options.VideoPath == defaults.Video && options.BurnInPath == ownEngine,
                "Missing old suite paths must move to the shipped copy");
            Require(options.FurMarkPath == GpuEngines.FindFurMark(bundle) && options.Mark3DPath == GpuEngines.FindMark3D(bundle, false) && options.Mark11Path == GpuEngines.FindMark3D(bundle, true),
                "GPU paths under the old suite must migrate or become explicit missing dependencies");
        }

        string removedRoot = Path.Combine(fixture, "removed-suite");
        Require(!Directory.Exists(removedRoot), "Deleted-suite fixture must start absent");
        var removed = ReloadFixture("removed-root", Rooted(removedRoot)); AssertBundlePaths(removed.Options);
        results.Add(new { Case = "AppFilesDeletedSuiteMigratesAllAgingPaths", Passed = true, PhysicalActions = false });

        // The sibling deliberately shares the old directory's text prefix: it is an explicit override, not a suite child.
        string externalRoot = removedRoot + "_external"; Directory.CreateDirectory(externalRoot);
        var external = Rooted(externalRoot); external.ReferenceRoot = removedRoot;
        foreach (string path in new[] { external.PwrTestPath, external.VideoPath, external.SftPath, external.BurnInPath, external.FurMarkPath, external.Mark3DPath, external.Mark11Path })
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Harmless file migration fixture; never executed"); }
        var preserved = ReloadFixture("explicit-overrides", external);
        Require(preserved.Options.ReferenceRoot == bundle && preserved.Options.PwrTestPath == external.PwrTestPath && preserved.Options.VideoPath == external.VideoPath &&
            preserved.Options.SftPath == external.SftPath && preserved.Options.BurnInPath == external.BurnInPath && preserved.Options.FurMarkPath == external.FurMarkPath &&
            preserved.Options.Mark3DPath == external.Mark3DPath && preserved.Options.Mark11Path == external.Mark11Path, "Root migration must preserve every explicit external tool override");
        results.Add(new { Case = "AppFilesPreservesExternalOverridesAndDirectoryBoundary", Passed = true, PhysicalActions = false });

        string matchingRoot = Path.Combine(fixture, "matching-suite"); var matchingOptions = Rooted(matchingRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(matchingOptions.BurnInPath)!); File.Copy(ownEngine, matchingOptions.BurnInPath);
        var matching = ReloadFixture("matching-identity", matchingOptions); AssertBundlePaths(matching.Options);
        results.Add(new { Case = "AppFilesMatchingEngineIdentityAdoptsOwnedBundle", Passed = true, EngineSHA256 = bundleHash, PhysicalActions = false });

        string distinctRoot = Path.Combine(fixture, "distinct-suite"); var distinctOptions = Rooted(distinctRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(distinctOptions.BurnInPath)!); File.WriteAllText(distinctOptions.BurnInPath, "Distinct test engine identity; never executed");
        var distinct = ReloadFixture("different-identity", distinctOptions);
        Require(distinct.Options.ReferenceRoot == distinctRoot && distinct.Options.BurnInPath == distinctOptions.BurnInPath && distinct.Options.FurMarkPath == distinctOptions.FurMarkPath,
            "An existing suite with a different engine identity must remain an explicit user choice");
        results.Add(new { Case = "AppFilesDifferentEngineIdentityPreservesUserSuite", Passed = true, PhysicalActions = false });
        Require(bundleHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownEngine))), "Migration regression must never change the owned engine");
    }

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
        Require(run.ExcludedItems.Count == 1 && queue.Count == 5 && Control<Label>(form, "ResumeHint").Text.Contains("自动续测"), "Selected reboot run must describe automatic recovery and preserve the saved queue");
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
    private static async Task ReopenModelessWindowsAsync()
    {
        using var owner = new MainForm(Files("modeless-reentry")); owner.Show(shell); await Task.Delay(100);
        var popup = (Form)typeof(MainForm).GetField("popup", privateInstance)!.GetValue(owner)!;
        var ui = typeof(MainForm).Assembly.GetType("Aging.WinForms.Ui")!;
        var show = ui.GetMethod("ShowModeless", BindingFlags.Public | BindingFlags.Static)!;
        for (int i = 0; i < 5; i++) show.Invoke(null, [popup, owner]);
        owner.OpenCompatibility(); owner.OpenCompatibility();
        var compatibility = Application.OpenForms.OfType<CompatibilityForm>().Single();
        await ((Task)typeof(CompatibilityForm).GetProperty("ReportReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compatibility)!);
        owner.OpenCompatibility(); Capture(compatibility, "compatibility-reentry.png");
        owner.Close(); results.Add(new { Case = "RepeatedPopupAndCompatibilityReentry", Passed = true });
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
        // Keep desktop input from answering the full-screen video's manual result buttons
        // while this test drives its own FAIL/stop action through the application.
        var videoInput = new VideoInputFilter();
        Application.AddMessageFilter(videoInput);
        try { return await ThroughStartupWithControlledInputAsync(name, selected, stopVideo, hardware); }
        finally { Application.RemoveMessageFilter(videoInput); }
    }
    private sealed class VideoInputFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message message)
        {
            bool input = message.Msg is >= 0x0100 and <= 0x0109 or >= 0x0200 and <= 0x020E;
            return input && System.Windows.Forms.Control.FromChildHandle(message.HWnd)?.FindForm() is VideoForm;
        }
    }
    private static async Task<RunSnapshot> ThroughStartupWithControlledInputAsync(string name, TaskKind selected, bool stopVideo, bool hardware)
    {
        var files = Files(name);
        if (hardware)
        {
            string referenceEngine = AppFiles.DiscoverBurnIn(files.Options.ReferenceRoot);
            Require(File.Exists(referenceEngine), "Bundled BurnIn engine is missing");
            var options = JsonData.Clone(files.Options); options.BurnInPath = referenceEngine;
            var queue = DefaultQueue(); queue[2] = selected == TaskKind.FurMark ? AgingItem.Parse("FurMark|0.1|8X|1920x1080|Y") : AgingItem.Parse("BurnIn|0.1|" + burnInMode); files.Save(queue, options);
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
                            var button = video.Controls.OfType<Button>().Single(x => x.Text.StartsWith(videoPass ? "通过" : "失败")); button.PerformClick();
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
            Require(run.Outcome == (stopVideo ? Outcome.Aborted : videoPass ? Outcome.Pass : Outcome.Fail), $"Video result control mismatch: actual={run.Outcome}, stopVideo={stopVideo}, stopped={stopped}, step={step}, message={run.Items[0].Message}, elapsed={watch.Elapsed}");
        }
        Capture(Application.OpenForms.OfType<DashboardForm>().Single(), name + "-result.png");
        owner.Close();
        results.Add(new { Case = name, Passed = true, Simulation = false, RealVideoPlayed = playingObserved, PlaybackSeconds = maxPosition, HumanVisualAudioInspection = false, StopRequested = stopped, Outcome = run.Outcome.ToString(), run.HardwarePassed, LogDirectory = run.LogDirectory, ExcludedCount = run.ExcludedItems.Count });
        return run;
    }
    private static async Task VideoThroughStartupAsync(bool stop) => await ThroughStartupAsync(stop ? "real-video-stop" : videoPass ? "real-video-pass-control" : "real-video-fail", TaskKind.Video, stop, false);
    private static async Task PowerThroughStartupAsync(string kind)
    {
        Require(kind is "S3" or "S4" or "Reboot", "Explicit power test kind is required");
        string name = "real-" + kind.ToLowerInvariant();
        var files = Files(name);
        var item = AgingItem.Parse(kind == "Reboot" ? "Reboot|1|30|Y|N|N|N" : $"{kind}|1|30|60|Y|N|N");
        var options = JsonData.Clone(files.Options); options.SwitchReboot = false; options.AgingInNextBoot = false; options.TestWithMaxVolume = false;
        files.Save([item], options);
        using var owner = new MainForm(files); owner.Show(shell); await Task.Delay(100); owner.OpenDashboard();
        owner.Coordinator.Changed += run =>
        {
            if (run.Phase == RunPhase.AwaitingReboot)
                File.WriteAllText(Path.Combine(output, "reboot-prepared.json"), JsonSerializer.Serialize(new { run.RunId, run.ExpectedBootId, run.ResumeExecutable, run.ResumeDataRoot, run.ResumeVersion, run.PendingOperation, PreparedAt = DateTimeOffset.Now }, JsonData.Options));
        };
        bool confirmed = false, blocked = false; string? failure = null;
        var watch = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed > TimeSpan.FromMinutes(8)) throw new TimeoutException("Power test did not finish in deadline");
                var setup = Application.OpenForms.OfType<StartRunForm>().FirstOrDefault();
                if (!confirmed && setup != null && setup.DetectionReady.IsCompleted)
                {
                    Control<CheckBox>(setup, "SwitchReboot").Checked = false;
                    Control<CheckBox>(setup, "MaximumVolume").Checked = false;
                    Control<CheckBox>(setup, "NextBoot").Checked = false;
                    Capture(setup, name + "-startup.png");
                    if (!Control<Button>(setup, "BeginRealRun").Enabled)
                    {
                        var grid = Control<DataGridView>(setup, "StartupItems");
                        string reason = grid.Rows[0].Cells[4].Value?.ToString() ?? "";
                        Require(kind == "S3" && reason.Contains("系统不支持 S3"), "Power dependency failure: " + reason);
                        blocked = true; confirmed = true;
                        results.Add(new { Case = "UnsupportedS3IsBlockedOnActualMachine", Passed = true, Supported = false, HardwarePassed = false, Detail = reason });
                        setup.Close(); return;
                    }
                    confirmed = true; Control<Button>(setup, "BeginRealRun").PerformClick(); return;
                }
                IntPtr dialog = FindWindow(null, "开始真实老化");
                if (dialog != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(dialog, out uint pid);
                    Require(pid == Environment.ProcessId, "Never confirm another process's power operation");
                    PostMessage(GetDlgItem(dialog, 1), 0x00F5, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception e)
            {
                failure = e.ToString(); timer.Stop(); owner.StopRun();
                foreach (var setup in Application.OpenForms.OfType<StartRunForm>().ToArray()) setup.Close();
            }
        };
        timer.Start(); await owner.StartRunAsync(false); timer.Stop();
        if (failure != null) throw new InvalidOperationException(failure);
        if (!blocked)
        {
            var run = owner.Coordinator.Current ?? throw new IOException("Power test did not reach the coordinator");
            if (kind == "Reboot" && run.Phase == RunPhase.AwaitingReboot)
            {
                // Windows terminates this harness during reboot. The registered production
                // Test.exe must recover the durable checkpoint after the next user logon.
                await Task.Delay(TimeSpan.FromMinutes(2));
                throw new TimeoutException("Windows did not complete the requested reboot");
            }
            results.Add(new { Case = name, Passed = run.HardwarePassed && run.Items[0].CompletedCycles == 1, Simulation = run.Simulation, run.HardwarePassed, Outcome = run.Outcome.ToString(), CompletedCycles = run.Items[0].CompletedCycles, run.LogDirectory });
            Require(run.HardwarePassed && run.Items[0].CompletedCycles == 1, "Power test failed: " + string.Join(";", run.Items[0].Errors));
        }
        owner.Close();
    }
    private static async Task BurnInThroughStartupAsync()
    {
        var run = await ThroughStartupAsync("real-burnin-mode" + burnInMode + "-six-minute", TaskKind.BurnIn, false, true);
        if (run.Outcome != Outcome.Pass)
        {
            results.Add(new { Case = "BurnInHardwareAcceptance", Passed = false, Detail = "Actual engine returned non-PASS; retained raw report, not overridden", Errors = run.Items.SelectMany(x => x.Errors).ToList() });
            Environment.ExitCode = 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
