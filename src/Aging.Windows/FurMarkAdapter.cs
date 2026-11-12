using Aging.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aging.Windows;

/// <summary>FurMark 1.x bridge. Every invocation gets a private engine directory and fresh output.</summary>
public static class FurMarkAdapter
{
    public const string SupportedVersion = "1.30.0.0";
    public const string SupportedSha256 = "38EF012023F23685ED395633357765909BA81CCD92BC250EA7F7306781AE14D9";
    public static IReadOnlyList<string> Validate(string executable, AgingItem item)
    {
        var errors = new List<string>();
        if (!File.Exists(executable)) errors.Add("缺少 FurMark 1.x 引擎，请选择 FurMark.exe（原版最低 1.30）");
        else
        {
            var version = FileVersionInfo.GetVersionInfo(executable);
            if (version.FileVersion != SupportedVersion) errors.Add("FurMark 专用适配器冻结版本为 " + SupportedVersion + "；请选择匹配的 FurMark.exe");
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))) != SupportedSha256)
                errors.Add("FurMark 引擎 SHA256 与冻结的官方 1.30.0.0 不一致；此文件需单独验证");
        }
        try { _ = CreateArguments(item); } catch (FormatException e) { errors.Add(e.Message); }
        return errors;
    }

    public static IReadOnlyList<string> CreateArguments(AgingItem item)
    {
        var (width, height) = GpuEngines.Resolution(item.Resolution);
        string msaa = item.Msaa.Trim().TrimEnd('X', 'x');
        if (msaa is not ("0" or "2" or "4" or "8")) throw new FormatException("FurMark 抗锯齿必须为 0X/2X/4X/8X");
        if (!double.IsFinite(item.Hours) || item.Hours <= 0) throw new FormatException("FurMark 测试时长无效");
        var arguments = new List<string> { "/msaa=" + msaa, "/max_time=" + Math.Ceiling(item.Hours * 3600000).ToString(CultureInfo.InvariantCulture),
            "/run_mode=1", "/noscore", "/log_score", "/nogui", "/width=" + width, "/height=" + height, "/log_gpu_data", "/enable_dyn_bkg=1" };
        if (!item.WindowMode) arguments.Add("/fullscreen=1");
        return arguments;
    }

    public static TaskResult Evaluate(int exitCode, TimeSpan elapsed, double hours, IReadOnlyList<string> reports)
    {
        string[] scoreFiles = reports.Where(p => Path.GetFileName(p).Equals("FurMark-Scores.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
        var scores = scoreFiles.SelectMany(File.ReadLines).Select(line => Regex.Match(line, @"\[Score=(\d+)\].*\[DurationInMs=(\d+)\]")).Where(m => m.Success).ToArray();
        // The frozen engine's timed benchmark returns its score as the process exit code.
        // A matching, fresh score file is required before accepting a nonzero code as normal completion.
        bool scoreExit = scores.Length == 1 && int.TryParse(scores[0].Groups[1].Value, out int score) && score > 0 && score == exitCode;
        if (exitCode != 0 && !scoreExit) return new(Outcome.Fail, "FurMark 非零退出码且无匹配的本轮分数报告：" + exitCode);
        if (elapsed.TotalSeconds < hours * 3600 * .95) return new(Outcome.Fail, $"FurMark 提前退出：{elapsed.TotalSeconds:0} 秒，目标 {hours * 3600:0} 秒");
        if (scores.Length != 1 || !double.TryParse(scores[0].Groups[2].Value, CultureInfo.InvariantCulture, out double durationMs))
            return new(Outcome.Uncertain, "FurMark 缺少唯一的本轮计时结果报告，不能判定硬件通过");
        if (durationMs < hours * 3600000 * .95) return new(Outcome.Fail, "FurMark 原始结果的负载时长不足");
        var csv = reports.Where(p => Path.GetFileName(p).StartsWith("furmark-gpu-", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Logs alone establish only that the wrapper started. Require a nonempty workload sampling series.
        if (!csv.Any(path => File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Take(4).Count() >= 4))
            return new(Outcome.Uncertain, "FurMark 缺少本轮 GPU 原始采样报告，不能判定硬件通过");
        string text = string.Join("\n", reports.Where(p => Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        if (Regex.IsMatch(text, @"(?im)(fatal error|out of memory|OpenGL.*(?:failed|error)|GPU.*(?:crash|failed))")) return new(Outcome.Fail, "FurMark 原始日志包含错误，请检查报告");
        // FurMark does not expose a hardware PASS verdict. Duration and samples are execution evidence only.
        return new(Outcome.Uncertain, $"FurMark 已运行 {elapsed.TotalSeconds:0} 秒并保存 GPU 采样；引擎无硬件通过报告，待验收");
    }

    public static async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        string executable = run.Options.FurMarkPath;
        var errors = Validate(executable, item);
        if (errors.Count > 0) return new(Outcome.Fail, string.Join("；", errors)) { Execution = new("DependencyMissing", 0, []) };
        string output = Path.Combine(run.LogDirectory, $"{run.CurrentIndex + 1}_FurMark", Guid.NewGuid().ToString("N"));
        string work = Path.Combine(output, "engine");
        Directory.CreateDirectory(work);
        var reports = new List<string>(); var watch = Stopwatch.StartNew(); int? exitCode = null; string state = "Starting";
        run.Items[run.CurrentIndex].Reports.Add("FurMark 原始报告目录：" + output);
        try
        {
            string sourceRoot = Path.GetDirectoryName(executable)!;
            foreach (string path in Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FurMark_", StringComparison.OrdinalIgnoreCase) || name.Equals("FurMark-Scores.txt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("furmark-gpu-", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(work, Path.GetRelativePath(sourceRoot, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
            }
            await GpuEngines.WriteIdentityAsync(executable, output, ct);
            var result = await new OwnedProcessRunner().RunAsync(Path.Combine(work, Path.GetFileName(executable)), CreateArguments(item), work, Path.Combine(output, "process"),
                TimeSpan.FromHours(item.Hours) + TimeSpan.FromMinutes(1), ct, pid => { state = "Started"; progress($"FurMark 已启动 PID={pid}，目标 {item.Hours * 60:0.##} 分钟"); }, waitForDescendants: true);
            exitCode = result.ExitCode; watch.Stop();
            reports = CollectReports(work);
            return Evaluate(result.ExitCode, result.Elapsed, item.Hours, reports) with { Execution = new("Exited", result.Elapsed.TotalSeconds, reports, result.ExitCode) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(Outcome.Aborted, "用户停止 FurMark，已保留原始报告") { Execution = new("Cancelled", watch.Elapsed.TotalSeconds, CollectReports(work), exitCode) }; }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        { return new(Outcome.Fail, "FurMark " + e.Message) { Execution = new(state, watch.Elapsed.TotalSeconds, CollectReports(work), exitCode) }; }
    }

    private static List<string> CollectReports(string work) => Directory.Exists(work)
        ? Directory.EnumerateFiles(work).Where(p => Path.GetFileName(p).StartsWith("FurMark_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("FurMark-Scores.txt", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("furmark-gpu-", StringComparison.OrdinalIgnoreCase)).ToList() : [];
}
