using Aging.Core;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Aging.Windows;

/// <summary>PassMark 9.2 script bridge. Executes the supplied engine; never generates a PASS report.</summary>
public static class BurnInAdapter
{
    public const string SupportedVersion = "9.2.1006.0";
    public static string Configuration(string executable, int mode) => Path.Combine(Path.GetDirectoryName(executable) ?? "", $"LastUsed_{mode}.bitcfg");
    public static IReadOnlyList<string> Validate(string executable, AgingItem item)
    {
        var errors = new List<string>();
        if (!File.Exists(executable)) { errors.Add("请选择 BurnInTest 9.2 的 bit.exe，或选择包含它的 GW_TEST 工具目录"); return errors; }
        if (!WindowsPlatform.IsAdministrator) errors.Add("BurnInTest 引擎要求管理员权限，请使用面板的“以管理员重新启动”");
        if (FileVersionInfo.GetVersionInfo(executable).FileVersion != SupportedVersion) errors.Add("内置 BurnIn 适配只支持文件版本 " + SupportedVersion);
        if (!File.Exists(Configuration(executable, item.BurnInMode))) errors.Add("缺少测试模式配置：" + Configuration(executable, item.BurnInMode));
        return errors;
    }
    public static string CreateScript(string configuration, string report, double hours)
    {
        static string Quote(string path)
        {
            if (path.Any(c => c < 32 || c > 126 || c == '"')) throw new IOException("BurnIn 脚本工作目录需要 ASCII 路径；用户日志会另存到原日志目录");
            return "\"" + path + "\"";
        }
        return $"LOAD {Quote(configuration)}\r\n" +
            "SETPRETEST ACTION NONE\r\n" +
            "SETPOSTTEST AUTO_STOP_P NORMAL MAN_STOP_P NORMAL RESULT_P NO\r\n" +
            "SETPOSTTEST AUTO_STOP_F NORMAL MAN_STOP_F NORMAL RESULT_F NO\r\n" +
            $"SETLOG LOG YES NAME {Quote(report)} PREFIX \"\" TIME NO REPORT TEXT LOGLEVEL YES TRACELEVEL NO SUM NO FILE NEW LINES 100000\r\n" +
            "SETCYCLES 0\r\n" +
            "SETDURATION " + (hours * 60).ToString("0.########", CultureInfo.InvariantCulture) + "\r\nRUN CONFIG\r\nEXIT\r\n";
    }
    public static TaskResult EvaluateReport(string content, double hours, int mode)
    {
        var failedRow = Regex.Match(content, @"(?im)^[^\r\n]*\sFAIL\s+\d+\s[^\r\n]*");
        if (Regex.IsMatch(content, @"(?im)^\s*TEST RUN FAILED\s*$") || failedRow.Success)
            return new(Outcome.Fail, "BurnInTest 原始报告失败：" + (failedRow.Success ? Regex.Replace(failedRow.Value.Trim(), @"\s+", " ") : "TEST RUN FAILED"));
        if (!Regex.IsMatch(content, @"(?im)^\s*TEST RUN PASSED\s*$")) return new(Outcome.Uncertain, "BurnInTest 报告没有本轮明确通过标记");
        var duration = Regex.Match(content, @"(?im)^Test Duration:\s*(\d+)h\s*(\d+)m\s*(\d+)s\s*$");
        if (!duration.Success) return new(Outcome.Uncertain, "BurnInTest 报告缺少实际测试时长");
        double seconds = double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + double.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
        if (seconds < hours * 3600 * 0.95) return new(Outcome.Fail, $"BurnInTest 实测仅 {seconds:0} 秒，未达到目标 {hours * 3600:0} 秒");
        string[] rows = mode == 2
            ? [@"(?im)^\s*Disk\s*\([^\r\n]+\)\s+\d+\s+[^\r\n]+\sPASS\s+0\s+"]
            : [@"(?im)^\s*CPU\s+\d+\s+[^\r\n]+\sPASS\s+0\s+", @"(?im)^\s*Memory \(RAM\)\s+\d+\s+[^\r\n]+\sPASS\s+0\s+"];
        if (rows.Any(row => !Regex.IsMatch(content, row))) return new(Outcome.Uncertain, "BurnInTest 报告缺少所选模式的实际测试结果行");
        return new(Outcome.Pass, $"BurnInTest 原始报告通过，实际负载运行 {seconds:0} 秒");
    }
    public static string CreateEngineCopy(string executable, string selectedConfig, string work, CancellationToken ct)
    {
        string isolated = Path.Combine(work, "engine"), sourceDirectory = Path.GetDirectoryName(executable)!;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(source).StartsWith("unins", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(source).Equals(".log", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(isolated, Path.GetRelativePath(sourceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
        }
        File.Copy(selectedConfig, Path.Combine(isolated, "LastUsed.bitcfg"), true);
        return Path.Combine(isolated, Path.GetFileName(executable));
    }
    public static async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        var errors = Validate(run.Options.BurnInPath, item);
        if (errors.Count > 0) return new(Outcome.Fail, string.Join("；", errors));
        string output = Path.Combine(run.LogDirectory, $"{run.CurrentIndex + 1}_BurnIn");
        Directory.CreateDirectory(output);
        // Keep script contents ASCII for the old engine's parser, independently of Chinese user/log paths.
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GW.AgingTool", "engine-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(true, false);
        using (var identity = WindowsIdentity.GetCurrent())
        {
            permissions.SetOwner(identity.User!);
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(work).SetAccessControl(permissions);
        string config = Path.Combine(work, "mode.bitcfg"), report = Path.Combine(work, "result.log"), script = Path.Combine(work, "run.bits");
        string original = Configuration(run.Options.BurnInPath, item.BurnInMode);
        string isolatedEngine = Path.Combine(work, "engine");
        try
        {
        File.Copy(original, config);
        // The old engine writes BIT_log.log and LastUsed.bitcfg even with scripted logging.
        // Run a private copy so its writes and helper executables stay outside the reference suite.
        string copiedExe = CreateEngineCopy(run.Options.BurnInPath, config, work, ct);
        string commands = CreateScript(config, report, item.Hours);
        await File.WriteAllTextAsync(script, commands, Encoding.ASCII, ct);
        File.Copy(config, Path.Combine(output, "selected-mode.bitcfg"));
        await File.WriteAllTextAsync(Path.Combine(output, "executed.bits"), commands, Encoding.UTF8, ct);
        string fingerprint;
        await using (var engine = File.OpenRead(run.Options.BurnInPath)) fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(engine, ct));
        await File.WriteAllTextAsync(Path.Combine(output, "engine.txt"), $"Version={SupportedVersion}\r\nSHA256={fingerprint}\r\nMode={item.BurnInMode}\r\nConfiguration={original}\r\nExecutable={run.Options.BurnInPath}\r\n", ct);
        run.Items[run.CurrentIndex].Reports.Add("BurnIn 原始结果目录：" + output);
        progress($"正在启动真实 BurnInTest，模式 {item.BurnInMode}，目标 {item.Hours * 60:0.##} 分钟");
            var result = await new OwnedProcessRunner().RunAsync(copiedExe, ["-P", "-S", script], isolatedEngine, Path.Combine(output, "engine"), TimeSpan.FromHours(item.Hours) + TimeSpan.FromMinutes(1), ct,
                pid => progress($"BurnInTest 已启动 PID={pid}；真实负载目标 {item.Hours * 60:0.##} 分钟，停止会关闭本轮进程树"));
            if (!File.Exists(report)) return new(result.ExitCode == 0 ? Outcome.Uncertain : Outcome.Fail, $"BurnInTest 退出码 {result.ExitCode}，没有本轮报告；请检查启动/授权/脚本执行错误");
            var evaluated = EvaluateReport(File.ReadAllText(report), item.Hours, item.BurnInMode);
            if (evaluated.Outcome == Outcome.Fail) return evaluated;
            return result.ExitCode != 0 ? new(Outcome.Fail, "BurnInTest 非零退出码 " + result.ExitCode + "，即使存在通过文本也不判通过") : evaluated;
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(work)) File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
            if (Directory.Exists(isolatedEngine)) foreach (string file in Directory.EnumerateFiles(isolatedEngine, "*.log")) File.Copy(file, Path.Combine(output, "engine-" + Path.GetFileName(file)), true);
            // Delete only this unique, task-owned staging directory; never touch the engine installation.
            Directory.Delete(work, true);
        }
    }
}
