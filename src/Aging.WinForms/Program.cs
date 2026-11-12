using System.Security.Principal;

using Aging.Core;

namespace Aging.WinForms;

internal static class Program
{
    internal static string MainExecutable => Path.Combine(AppContext.BaseDirectory, "Test.exe");
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string Value(string key, string fallback)
        { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
        if (args.Contains("--uninstall-cleanup"))
        {
            try { Aging.Windows.LogonResume.RemoveForInstallation(AppContext.BaseDirectory); Environment.ExitCode = 0; }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        var platformErrors = Aging.Windows.MachineCompatibility.ApplicationErrors();
        if (platformErrors.Count > 0)
        {
            if (!args.Contains("--worker")) MessageBox.Show(string.Join(Environment.NewLine, platformErrors), "不支持的系统");
            Environment.ExitCode = 2; return;
        }
        if (args.Contains("--compatibility-report"))
        {
            try
            {
                var options = new AppOptions();
                string data = Value("--data", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GW.AgingTool"));
                if (File.Exists(Path.Combine(data, "options.json"))) options = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(File.ReadAllBytes(Path.Combine(data, "options.json")), JsonData.Options) ?? options;
                var report = Aging.Windows.MachineCompatibility.CaptureAsync(options).GetAwaiter().GetResult();
                Aging.Windows.MachineCompatibility.Save(report, Value("--compatibility-report", Path.Combine(data, "compatibility", "latest.json")));
            }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--deployment-probe"))
        {
            try
            {
                var files = new AppFiles(Value("--data", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GW.AgingTool")));
                var result = new { Version = BuildInfo.Version, Directory = AppContext.BaseDirectory, RuntimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), files.Root, files.Options, Queue = files.Queue.Select(x => x.Serialize()).ToList() };
                AtomicFile.Write(Value("--deployment-probe", Path.Combine(files.Root, "deployment-probe.json")), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, JsonData.Options));
            }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--worker"))
        {
            try { Environment.ExitCode = Aging.Windows.WorkerBridge.RunWorkerAsync(Value("--request", ""), Value("--pipe", ""), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception) { Environment.ExitCode = 2; }
            return;
        }
        string module = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).ToLowerInvariant();
        if (args.Contains("--alias-probe"))
        {
            File.WriteAllText(Value("--alias-probe", "alias.txt"), module + Environment.NewLine + AppContext.BaseDirectory);
            return;
        }
        string root = Value("--data", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GW.AgingTool"));
        string? smoke = args.Contains("--smoke") ? Path.GetFullPath(Value("--smoke", Path.Combine(root, "smoke"))) : null;
        if (smoke != null) root = Path.Combine(smoke, "data");
        using var identity = WindowsIdentity.GetCurrent();
        using var installIndicator = new Mutex(false, "Local\\GW_AgingTool_Running");
        // Smoke runs always use isolated data and simulation. They must not block on the user's open application.
        string instanceSuffix = smoke == null ? "" : "_Smoke_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\GW_AgingTool_" + identity.User!.Value + instanceSuffix);
        bool owns = false;
        try
        {
            try { owns = mutex.WaitOne(args.Contains("--wait-for-host-exit") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); } catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                if (smoke != null) { Directory.CreateDirectory(smoke); File.WriteAllText(Path.Combine(smoke, "error.txt"), "同一隔离目录的演练已在运行"); Environment.ExitCode = 1; }
                else if (!args.Contains("--resume")) MessageBox.Show("同一用户已有老化测试工具正在运行，请从系统托盘打开。", BuildInfo.Name);
                return;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => { Directory.CreateDirectory(root); File.AppendAllText(Path.Combine(root, "crash.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine); Ui.Error(null, e.Exception); };
            var files = new AppFiles(root);
            LegacyRequest? legacy = null;
            if (module is "reboot" or "sleep" or "burnintest" or "furmarktest" or "3dmarktest") legacy = LegacyCommandLine.Parse(module, string.Join(" ", args));
            Guid resumeRun = Guid.TryParse(Value("--resume-run", ""), out var resumeId) ? resumeId : Guid.Empty;
            int resumeVersion = int.TryParse(Value("--resume-version", "0"), out var parsedVersion) ? parsedVersion : 0;
            using var form = new MainForm(files, args.Contains("--resume"), smoke, legacy, resumeRun, resumeVersion);
            if (args.Contains("--show-dashboard")) form.Shown += (_, _) => form.OpenDashboard();
            if (args.Contains("--settings") || module == "controlpanel") form.Shown += (_, _) => form.OpenSettings();
            if (args.Contains("--logs") || module == "viewlog") form.Shown += (_, _) => form.OpenLogs();
            if (args.Contains("--compatibility")) form.Shown += (_, _) => form.OpenCompatibility();
            Application.Run(form);
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(root); File.AppendAllText(Path.Combine(root, "crash.log"), DateTimeOffset.Now + " " + e + Environment.NewLine);
            if (smoke == null) Ui.Error(null, e); else File.WriteAllText(Path.Combine(smoke, "error.txt"), e.ToString()); Environment.ExitCode = 1;
        }
        finally { if (owns) mutex.ReleaseMutex(); }
    }
}
