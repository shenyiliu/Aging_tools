using Aging.Core;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Aging.Windows;

public sealed class WindowsPlatform : ITaskPlatform
{
    private readonly OwnedProcessRunner runner = new();
    public Func<string, CancellationToken, Task<TaskResult>>? PlayVideoAsync { get; set; }
    public Func<CancellationToken, Task<bool>>? ConfirmShutdownAsync { get; set; }
    public Action? BeforeReboot { get; set; }
    public string BootId
    {
        get
        {
            var data = new byte[32];
            // SystemBootEnvironmentInformation supplies a boot GUID, unaffected by wall-clock corrections.
            int status = NtQuerySystemInformation(90, data, data.Length, out _);
            if (status != 0) throw new InvalidOperationException("无法读取系统启动标识：" + status);
            var id = new Guid(data.AsSpan(0, 16));
            if (id == Guid.Empty) throw new InvalidOperationException("系统启动标识为空");
            return id.ToString("D");
        }
    }
    public static bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    public async Task<PreflightResult> PreflightAsync(AgingItem item, RunSnapshot run, CancellationToken ct)
    {
        var check = await CheckItemAsync(item, run.Options, ct);
        var errors = check.CanRun ? new List<string>() : check.Messages.ToList();
        var queue = run.Items.Select(x => x.Item).ToList();
        if (RunRequirements.RequiresAdministrator(queue, run.Options) && !IsAdministrator)
            errors.Add("所选测试需要管理员权限，请点击启动窗口的“以管理员重新打开”");
        return new(errors.Count == 0, errors.Count == 0 ? ["预检通过；硬件行为仍需专机验证"] : errors);
    }

    // Dependency preview is read-only. Permission and login-task setup are handled once for the selected run.
    public async Task<PreflightResult> CheckItemAsync(AgingItem item, AppOptions options, CancellationToken ct)
    {
        var errors = new List<string>();
        errors.AddRange(MachineCompatibility.ApplicationErrors());
        string originalMain = Path.Combine(options.ReferenceRoot, "Test.exe");
        foreach (var process in Process.GetProcessesByName("Test"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.Equals(originalMain, StringComparison.OrdinalIgnoreCase) == true)
                        errors.Add("原版 Test.exe 仍在运行，请先退出原版，避免并行续测和日志冲突");
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { /* inaccessible unrelated session */ }
            }
        }
        if (item.FanProfile != "N") errors.AddRange(FanTestAdapter.Validate(options.ReferenceRoot, item.FanProfile, false));
        if (item.Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS)
        {
            if (!File.Exists(options.PwrTestPath)) errors.Add("缺少 PwrTest：" + options.PwrTestPath);
            var caps = new byte[128];
            if (!GetPwrCapabilities(caps)) errors.Add("无法读取系统电源能力");
            else if (item.Kind == TaskKind.S3 && caps[5] == 0 || item.Kind == TaskKind.S4 && (caps[6] == 0 || caps[8] == 0) || item.Kind == TaskKind.CS && caps[20] == 0)
                errors.Add($"系统不支持 {item.Name}（系统电源能力）");
        }
        if (item.Kind == TaskKind.Video)
        {
            if (!File.Exists(options.VideoPath)) errors.Add("视频文件不存在：" + options.VideoPath);
            if (Type.GetTypeFromProgID("WMPlayer.OCX") == null) errors.Add("Windows Media Player ActiveX 未安装");
        }
        if (item.Kind == TaskKind.BurnIn && !string.IsNullOrWhiteSpace(options.BurnInPath))
            errors.AddRange(BurnInAdapter.Validate(options.BurnInPath, item, false));
        else if (item.Kind == TaskKind.FurMark && (!string.IsNullOrWhiteSpace(options.FurMarkPath) || !options.Engines.ContainsKey(ProfileKey(item))))
            errors.AddRange(FurMarkAdapter.Validate(options.FurMarkPath, item));
        else if (item.Kind == TaskKind.Mark3D && (!string.IsNullOrWhiteSpace(Mark3DAdapter.IsLegacy11(item) ? options.Mark11Path : options.Mark3DPath) || !options.Engines.ContainsKey(ProfileKey(item))))
            errors.AddRange(Mark3DAdapter.Validate(Mark3DAdapter.IsLegacy11(item) ? options.Mark11Path : options.Mark3DPath, item, options.ReferenceRoot));
        else if (item.IsStress)
        {
            if (!options.Engines.TryGetValue(ProfileKey(item), out var profile)) errors.Add("未配置已验证引擎适配：" + ProfileKey(item));
            else
            {
                if (!File.Exists(profile.Executable)) errors.Add("引擎不存在：" + profile.Executable);
                else
                {
                    if (profile.Sha256.Length != 64 || !profile.Sha256.Equals(await HashAsync(profile.Executable, ct), StringComparison.OrdinalIgnoreCase)) errors.Add("引擎 SHA256 不匹配或未冻结");
                    if (profile.FileVersion != FileVersionInfo.GetVersionInfo(profile.Executable).FileVersion) errors.Add("引擎文件版本不匹配");
                }
                if (!profile.Verified || string.IsNullOrWhiteSpace(profile.VerificationNote) || !profile.SupportsDuration)
                    errors.Add("引擎参数/时长/报告尚未完成专机验证");
                if (!profile.ReportPath.Contains("{runDir}", StringComparison.Ordinal) || !profile.ReportPath.Contains("{itemIndex}", StringComparison.Ordinal))
                    errors.Add("报告路径必须包含 {runDir} 和 {itemIndex}，避免重复项目覆盖和读取旧报告");
                if (string.IsNullOrWhiteSpace(profile.PassPattern) || string.IsNullOrWhiteSpace(profile.FailPattern)) errors.Add("缺少明确 PASS/FAIL 报告匹配规则");
                try { _ = new Regex(profile.PassPattern, RegexOptions.None, TimeSpan.FromSeconds(1)); _ = new Regex(profile.FailPattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException) { errors.Add("报告规则正则表达式无效"); }
            }
        }
        if (item.WifiCheck)
        {
            try { var wifi = await WifiScanner.ScanAsync(ct); if (wifi == 0) errors.Add("Wi-Fi 扫描未发现热点（请检查接口、驱动及位置权限）"); }
            catch (Exception e) { errors.Add("Wi-Fi 扫描失败：" + e.Message); }
        }
        return new(errors.Count == 0, errors.Count == 0 ? ["预检通过；硬件行为仍需专机验证"] : errors);
    }
    public static string ProfileKey(AgingItem item) => item.Kind == TaskKind.BurnIn ? $"BurnIn:{item.BurnInMode}" : item.Kind == TaskKind.Mark3D ? "3DMark:" + item.MarkVersion : item.Name;
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }

    public Task<DeviceSnapshot> CaptureDevicesAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var devices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID,ConfigManagerErrorCode,ClassGuid,Name FROM Win32_PnPEntity");
        using var results = searcher.Get();
        string[] excluded = ["{8ECC055D-047F-11D1-A537-0000F8753ED1}", "{4d36e96b-e325-11ce-bfc1-08002be10318}", "{4d36e96f-e325-11ce-bfc1-08002be10318}", "{62f9c741-b25a-46ce-b54c-9bccce08b6f2}", "{1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc}", "{4D36E96E-E325-11CE-BFC1-08002BE10318}"];
        foreach (ManagementObject device in results)
        {
            using (device)
            {
                ct.ThrowIfCancellationRequested();
                string id = Convert.ToString(device["PNPDeviceID"]) ?? "";
                string name = Convert.ToString(device["Name"]) ?? "";
                if (id.Length > 0 && !excluded.Contains(Convert.ToString(device["ClassGuid"]), StringComparer.OrdinalIgnoreCase)
                    && !name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) && !name.Contains("UMBus Enumerator", StringComparison.OrdinalIgnoreCase))
                    devices[id] = Convert.ToInt32(device["ConfigManagerErrorCode"]);
            }
        }
        if (devices.Count == 0) throw new IOException("设备探针返回空列表");
        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref mem)) throw new System.ComponentModel.Win32Exception();
        return new DeviceSnapshot(devices, mem.TotalPhysical);
    }, ct);

    public async Task<IReadOnlyList<string>> CheckAsync(AgingItem item, RunSnapshot run, CancellationToken ct)
    {
        var errors = new List<string>();
        if (item.HardwareCheck)
        {
            var now = await CaptureDevicesAsync(ct);
            if (run.DeviceBaseline == null) errors.Add("缺失设备基线");
            else
            {
                foreach (var baseline in run.DeviceBaseline.Devices)
                {
                    if (!now.Devices.TryGetValue(baseline.Key, out int status)) errors.Add("设备丢失：" + baseline.Key);
                    else if (status != baseline.Value) errors.Add($"设备状态变化：{baseline.Key}，{baseline.Value} → {status}");
                }
                if (Math.Abs((double)now.MemoryBytes - run.DeviceBaseline.MemoryBytes) > 512 * 1024 * 1024) errors.Add("总内存变化超过 512 MiB");
            }
        }
        if (item.WifiCheck && await WifiScanner.ScanAsync(ct) == 0) errors.Add("Wi-Fi 扫描未发现热点");
        if (run.EventBookmark is { } since)
        {
            foreach (var ev in await ReadEventsAsync(since, "Level=1 or Level=2", ct)) errors.Add("System 事件错误：" + ev);
            run.EventBookmark = DateTimeOffset.Now;
        }
        return errors;
    }

    public async Task<IReadOnlyList<string>> PrepareCycleAsync(AgingItem item, RunSnapshot run, CancellationToken ct)
    {
        if (item.Kind != TaskKind.Reboot || item.FanProfile == "N") return [];
        var fan = await FanTestAdapter.ExecuteAsync(item, run, ct);
        run.Items[run.CurrentIndex].Reports.Add(fan.Message);
        return fan.Outcome == Outcome.Pass ? [] : [fan.Message];
    }

    public async Task RebootAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BeforeReboot?.Invoke(); // Scheduler registration must already be verified.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/r", "/t", "0" } }) ?? throw new IOException("重启进程启动失败");
        await process.WaitForExitAsync(CancellationToken.None);
        if (process.ExitCode != 0) throw new IOException("系统拒绝重启，退出码 " + process.ExitCode);
    }

    public async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await ExecuteCoreAsync(item, run, progress, ct);
            return result.Execution == null ? result with { Execution = new("Exited", watch.Elapsed.TotalSeconds, [run.LogDirectory]) } : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return new(Outcome.Aborted, "用户停止，已保留本轮输出") { Execution = new("Cancelled", watch.Elapsed.TotalSeconds, [run.LogDirectory]) }; }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        { return new(Outcome.Fail, e.Message) { Execution = new(e is TimeoutException ? "TimedOut" : "ExecutionFailed", watch.Elapsed.TotalSeconds, [run.LogDirectory]) }; }
    }

    private async Task<TaskResult> ExecuteCoreAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        int index = run.CurrentIndex + 1;
        if (item.Kind == TaskKind.Video)
            return PlayVideoAsync == null ? new(Outcome.Fail, "视频窗口未连接") : await PlayVideoAsync(run.Options.VideoPath, ct);
        if (item.Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS)
        {
            var args = new List<string> { item.Kind == TaskKind.CS ? "/cs" : "/sleep", "/c:1", $"/p:{item.SleepSeconds}" };
            if (item.Kind != TaskKind.CS) { args.Add($"/h:{run.Options.HybridSleep}"); args.Add(item.Kind == TaskKind.S3 ? "/s:3" : "/s:4"); }
            string work = Path.Combine(run.LogDirectory, $"{index}_{item.Name}_{run.Items[run.CurrentIndex].CompletedCycles + 1}");
            Directory.CreateDirectory(work);
            var start = DateTimeOffset.Now;
            var result = await runner.RunAsync(run.Options.PwrTestPath, args, work, Path.Combine(work, "pwrtest"), TimeSpan.FromSeconds(item.SleepSeconds + 300), ct);
            if (result.ExitCode != 0) return new(Outcome.Fail, "PwrTest 失败，退出码 " + result.ExitCode);
            var evidence = await ReadEventsAsync(start, item.Kind == TaskKind.CS ? "EventID=506 or EventID=507" : "EventID=42 or EventID=1", ct);
            bool entry = evidence.Any(x => x.StartsWith(item.Kind == TaskKind.CS ? "506:" : "42:", StringComparison.Ordinal));
            bool wake = evidence.Any(x => x.StartsWith(item.Kind == TaskKind.CS ? "507:" : "1:", StringComparison.Ordinal));
            if (!entry || !wake) return new(Outcome.Uncertain, "PwrTest 已退出，但缺少进入/唤醒事件证据；请检查原始日志");
            return new(Outcome.Pass, "系统进入/唤醒事件已记录");
        }
        if (!item.IsStress) return new(Outcome.Fail, "不支持的执行类型");
        if (item.Kind == TaskKind.BurnIn && !string.IsNullOrWhiteSpace(run.Options.BurnInPath))
            return await BurnInAdapter.ExecuteAsync(item, run, progress, ct);
        if (item.Kind == TaskKind.FurMark && (!string.IsNullOrWhiteSpace(run.Options.FurMarkPath) || !run.Options.Engines.ContainsKey(ProfileKey(item))))
            return await FurMarkAdapter.ExecuteAsync(item, run, progress, ct);
        if (item.Kind == TaskKind.Mark3D && (!string.IsNullOrWhiteSpace(Mark3DAdapter.IsLegacy11(item) ? run.Options.Mark11Path : run.Options.Mark3DPath) || !run.Options.Engines.ContainsKey(ProfileKey(item))))
            return await Mark3DAdapter.ExecuteAsync(item, run, progress, ct);
        var profile = run.Options.Engines[ProfileKey(item)];
        var replacements = new Dictionary<string, string>
        {
            ["{runDir}"] = run.LogDirectory, ["{itemIndex}"] = index.ToString(), ["{hours}"] = item.Hours.ToString(CultureInfo.InvariantCulture),
            ["{minutes}"] = (item.Hours * 60).ToString(CultureInfo.InvariantCulture), ["{seconds}"] = (item.Hours * 3600).ToString(CultureInfo.InvariantCulture),
            ["{mode}"] = item.BurnInMode.ToString(), ["{resolution}"] = item.Resolution, ["{width}"] = item.Resolution.Split('x')[0],
            ["{height}"] = item.Resolution.Split('x')[1], ["{msaa}"] = item.Msaa, ["{windowMode}"] = item.WindowMode ? "1" : "0", ["{version}"] = item.MarkVersion
        };
        string Expand(string value) { foreach (var pair in replacements) value = value.Replace(pair.Key, pair.Value, StringComparison.Ordinal); return value; }
        string report = Path.GetFullPath(Expand(profile.ReportPath));
        if (!report.StartsWith(Path.GetFullPath(run.LogDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new(Outcome.Fail, "报告路径必须在本轮日志目录内");
        if (File.Exists(report)) return new(Outcome.Fail, "本轮报告在引擎启动前已存在，拒绝复用");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        progress($"{item.Name}，目标 {item.Hours:0.###} 小时");
        var started = DateTime.UtcNow;
        var executed = await runner.RunAsync(profile.Executable, profile.Arguments.Select(Expand), Path.GetDirectoryName(profile.Executable)!,
            Path.Combine(run.LogDirectory, $"{index}_{item.Name}"), TimeSpan.FromHours(item.Hours) + TimeSpan.FromMinutes(5), ct, waitForDescendants: true);
        TaskResult Result(Outcome outcome, string message) => new(outcome, message) { Execution = new("Started", executed.Elapsed.TotalSeconds, [executed.StdoutPath, executed.StderrPath, .. File.Exists(report) ? new[] { report } : Array.Empty<string>()], executed.ExitCode) };
        if (executed.ExitCode != 0) return Result(Outcome.Fail, "引擎退出码：" + executed.ExitCode);
        if (executed.Elapsed.TotalSeconds < item.Hours * 3600 * 0.95) return Result(Outcome.Fail, "引擎提前退出，未完成目标时长");
        if (!File.Exists(report) || File.GetLastWriteTimeUtc(report) < started.AddSeconds(-2)) return Result(Outcome.Uncertain, "缺少本轮有效报告");
        string content = await File.ReadAllTextAsync(report, ct);
        run.Items[run.CurrentIndex].Reports.Add("原始报告：" + report);
        if (Regex.IsMatch(content, profile.FailPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))) return Result(Outcome.Fail, "引擎报告包含失败记录");
        if (!Regex.IsMatch(content, profile.PassPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))) return Result(Outcome.Uncertain, "报告没有明确通过标记");
        return Result(Outcome.Pass, "目标时长及真实报告判定通过");
    }

    public async Task AfterRunAsync(RunSnapshot run, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(run.Options.AfterAgingScript))
        {
            string script = run.Options.AfterAgingScript;
            if (!File.Exists(script)) throw new FileNotFoundException("完成脚本不存在", script);
            string exe = script; List<string> args = [];
            if (Path.GetExtension(script).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            { exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"); args = ["-NoProfile", "-File", script]; }
            else if (Path.GetExtension(script).Equals(".cmd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(script).Equals(".bat", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("完成脚本当前支持 EXE/PS1；CMD/BAT 请改用独立 EXE，避免 shell 字符转义歧义");
            var result = await runner.RunAsync(exe, args, Path.GetDirectoryName(script)!, Path.Combine(run.LogDirectory, "after-aging"), TimeSpan.FromMinutes(5), ct);
            if (result.ExitCode != 0) throw new IOException("完成脚本退出码 " + result.ExitCode);
        }
        if (run.Options.ShutdownAfterAging && ConfirmShutdownAsync != null && await ConfirmShutdownAsync(ct))
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/s", "/t", "0" } }) ?? throw new IOException("无法申请关机");
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new IOException("系统拒绝关机");
        }
    }
    private static Task<List<string>> ReadEventsAsync(DateTimeOffset since, string predicate, CancellationToken ct) => Task.Run(() =>
    {
        string utc = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = new EventLogQuery("System", PathType.LogName, $"*[System[TimeCreated[@SystemTime>='{utc}'] and ({predicate})]]");
        using var reader = new EventLogReader(query);
        var events = new List<string>();
        while (reader.ReadEvent() is { } ev)
        {
            using (ev)
            {
                ct.ThrowIfCancellationRequested();
                // Only named power providers count as transition evidence.
                if (predicate.StartsWith("EventID", StringComparison.Ordinal) && ev.ProviderName is not ("Microsoft-Windows-Kernel-Power" or "Microsoft-Windows-Power-Troubleshooter")) continue;
                events.Add($"{ev.Id}:{ev.ProviderName} @{ev.TimeCreated:O} #{ev.RecordId}");
            }
            if (events.Count >= 1000) { events.Add("事件数量超过上限，请查看 System 日志"); break; }
        }
        return events;
    }, ct);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailPhysical, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, byte[] information, int length, out int returnLength);
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool GetPwrCapabilities(byte[] capabilities);
}
