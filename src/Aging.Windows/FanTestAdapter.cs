using Aging.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aging.Windows;

public sealed record FanRestoreCommand(string Executable, IReadOnlyList<string> Arguments, int DelayAfterSeconds = 0);

/// <summary>Runs only a selected, known vendor fan script in a private directory, with an independent restore path.</summary>
public static class FanTestAdapter
{
    private static readonly Dictionary<string, string> ScriptHashes = new(StringComparer.Ordinal)
    {
        ["IP3"] = "B2AAD833BB7D1D9F37F5772C597B5607F97F8D51299B11CAC449145C3DC13334",
        ["WB"] = "1A910E8F8584876388A266AE7ACB2FD3D5FE479529EFF953C802440F818F00FD",
        ["Ed"] = "AA5E4DC5151214A699176F2FB8D1D45C578C25680F5D74CCF06ABECEA8332116"
    };

    public static string DirectoryFor(string root, string profile) => Path.Combine(root, "Reboot", "cpufan", profile);

    public static IReadOnlyList<string> Validate(string root, string profile)
    {
        if (profile == "N") return [];
        var errors = new List<string>();
        if (!ScriptHashes.TryGetValue(profile, out string? hash)) return ["不支持的风扇方案：" + profile];
        if (!WindowsPlatform.IsAdministrator) errors.Add("厂商风扇检查需要管理员权限");
        string directory = DirectoryFor(root, profile), script = Path.Combine(directory, "CPUFUN.cmd");
        if (!File.Exists(script)) errors.Add("风扇脚本不存在：" + script);
        else if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script))).Equals(hash, StringComparison.Ordinal))
            errors.Add(profile + " 风扇脚本与已核对版本不一致，无法保证自动模式恢复序列");
        string[] required = profile switch
        {
            "WB" => ["wbFanTool.exe", "inpoutx64.dll"],
            "Ed" => ["Rw.exe", "io.dll"],
            _ => ["Rw.exe", "Rw.ini", "WinRing0.dll", "WinRing0x64.dll", "WinRing0.sys", "WinRing0x64.sys"]
        };
        foreach (string name in required)
            if (!File.Exists(Path.Combine(directory, name))) errors.Add("风扇组件缺失：" + Path.Combine(directory, name));
        return errors;
    }

    public static string CreateRuntimeScript(string profile, byte[] original)
    {
        if (!ScriptHashes.TryGetValue(profile, out string? expected) || Convert.ToHexString(SHA256.HashData(original)) != expected)
            throw new IOException("仅允许对已核对哈希的原风扇脚本生成私有运行副本");
        var lines = Encoding.ASCII.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = TransformKnownLine(lines[i], profile);
        return string.Join("\r\n", lines);
    }

    public static string TransformKnownLine(string line, string profile)
    {
        var delay = Regex.Match(line, @"^timeout /t (?<seconds>\d+)$", RegexOptions.IgnoreCase);
        if (delay.Success)
            return "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds " + delay.Groups["seconds"].Value + "\"";
        if (profile is "WB" or "Ed")
        {
            var condition = Regex.Match(line, @"^(?<if>if %fanr[24](?::~\d+,4)?% (?:LSS|GTR) \d+) (?:set )?(?<message>errmsg=.*) & goto fail$", RegexOptions.IgnoreCase);
            if (condition.Success) return condition.Groups["if"].Value + " (set " + condition.Groups["message"].Value + " & goto fail)";
        }
        return line;
    }

    public static IReadOnlyList<FanRestoreCommand> RestoreCommands(string profile)
    {
        if (profile == "WB") return [new("wbFanTool.exe", ["/set", "0"])];
        if (profile == "Ed") return [new("Rw.exe", ["-c"])];
        if (profile != "IP3") throw new ArgumentException("未知风扇恢复方案", nameof(profile));
        // Exact two reset groups in the original IP3 CPUFUN.cmd :pass and :fail tails.
        string[] commands = [
            "oid 0x4e 0x4f 0x2e 0x10", "oid 0x4e 0x4f 0x2f 0x08",
            "oid 0x4e 0x4f 0x2e 0x11", "oid 0x4e 0x4f 0x2f 0x18",
            "oid 0x4e 0x4f 0x2e 0x12", "oid 0x4e 0x4f 0x2f 0x00",
            "oid 0x4e 0x4f 0x2e 0x10", "oid 0x4e 0x4f 0x2f 0x19",
            "oid 0x4e 0x4f 0x2e 0x11", "oid 0x4e 0x4f 0x2f 0x08",
            "oid 0x4e 0x4f 0x2e 0x12", "oid 0x4e 0x4f 0x2f 0x00"
        ];
        return commands.Select((command, i) => new FanRestoreCommand("Rw.exe", ["/command=" + command, "/nologo", "/stdout"], i is 5 or 11 ? 2 : 0)).ToArray();
    }

    public static TaskResult Evaluate(string profile, IReadOnlyList<string> samples, bool errorFile, int exitCode)
    {
        if (errorFile || exitCode != 0) return new(Outcome.Fail, $"{profile} 风扇脚本失败（Error.txt={errorFile}，退出码={exitCode}）");
        if (samples.Count != 4) return new(Outcome.Fail, profile + " 风扇缺少本轮四份转速读数");
        double a, b;
        if (profile == "IP3")
        {
            int? Pair(string first, string second)
            {
                var high = Regex.Match(first.Trim(), @"=\s*0x[0-9a-f]{0,14}([0-9a-f]{2})$", RegexOptions.IgnoreCase);
                var low = Regex.Match(second.Trim(), @"=\s*0x[0-9a-f]{0,14}([0-9a-f]{2})$", RegexOptions.IgnoreCase);
                return high.Success && low.Success ? int.Parse(high.Groups[1].Value + low.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            }
            int? first = Pair(samples[0], samples[1]), second = Pair(samples[2], samples[3]);
            if (!first.HasValue || !second.HasValue) return new(Outcome.Fail, "IP3 风扇十六进制寄存器读数无效");
            a = first.Value; b = second.Value;
            if (a <= 3000 || a > 7000 || b <= 5300) return new(Outcome.Fail, $"IP3 风扇转速不符合原脚本阈值，阶段1={a:0}，阶段2={b:0} RPM");
        }
        else if (profile is "WB" or "Ed")
        {
            var values = new double[4];
            for (int i = 0; i < values.Length; i++)
                if (!double.TryParse(samples[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]) || values[i] <= 0)
                    return new(Outcome.Fail, profile + " 风扇十进制转速读数无效");
            a = Math.Round((values[0] + values[1]) / 2); b = Math.Round((values[2] + values[3]) / 2);
            if (values[1] < 5000 || values[3] < 2000 || values[3] > 4000)
                return new(Outcome.Fail, $"{profile} 风扇转速不符合原脚本阈值，100 PWM 平均={a:0}，30 PWM 平均={b:0} RPM");
        }
        else return new(Outcome.Fail, "未知风扇方案：" + profile);
        return new(Outcome.Pass, $"{profile} 风扇脚本转速检查通过，阶段1={a:0}，阶段2={b:0} RPM；自动模式恢复命令已执行，硬件回读仍以目标机验收为准");
    }

    public static async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, CancellationToken ct)
    {
        string profile = item.FanProfile;
        var validation = Validate(run.Options.ReferenceRoot, profile);
        if (validation.Count > 0) return new(Outcome.Fail, string.Join("；", validation));
        if (profile == "N") return new(Outcome.Pass, "未启用风扇检查");
        string id = Guid.NewGuid().ToString("N");
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GW.AgingTool", "fan-work", id);
        string output = Path.Combine(run.LogDirectory, $"{run.CurrentIndex + 1}_Fan_{profile}_{run.Items[run.CurrentIndex].CompletedCycles + 1}_{id[..8]}");
        Directory.CreateDirectory(work); Directory.CreateDirectory(output);
        TaskResult result = new(Outcome.Uncertain, "风扇脚本未执行");
        bool attempted = false, cancelled = false;
        var restorationErrors = new List<string>();
        var runner = new OwnedProcessRunner();
        try
        {
            string source = DirectoryFor(run.Options.ReferenceRoot, profile);
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (string path in Directory.EnumerateFiles(source, "*", enumeration))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(work, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
            }
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(work, "CPUFUN.cmd"), ct);
            await File.WriteAllTextAsync(Path.Combine(work, "CPUFUN.runtime.cmd"), CreateRuntimeScript(profile, original), Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(output, "script-origin.txt"), $"Profile={profile}\r\nSource={source}\r\nSHA256={Convert.ToHexString(SHA256.HashData(original))}\r\nRuntimeChanges=bounded Start-Sleep replacements; WB/Ed IF grouping and missing SET correction only\r\n", ct);
            ct.ThrowIfCancellationRequested();
            attempted = true;
            var execution = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/s", "/c", "CPUFUN.runtime.cmd"], work, Path.Combine(output, "test"), TimeSpan.FromMinutes(2), ct);
            var samples = Enumerable.Range(1, 4).Select(n => Path.Combine(work, $"fanr{n}.txt")).ToArray();
            result = Evaluate(profile, samples.All(File.Exists) ? samples.Select(File.ReadAllText).ToArray() : [], File.Exists(Path.Combine(work, "Error.txt")), execution.ExitCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        catch (Exception e) { result = new(Outcome.Fail, profile + " 风扇执行异常：" + e.Message); }
        finally
        {
            if (attempted)
            {
                // Cancellation never cancels restoration. Every command has an independent bounded lifetime.
                int i = 0;
                foreach (var command in RestoreCommands(profile))
                {
                    try
                    {
                        string prefix = Path.Combine(output, "restore-" + ++i);
                        var restored = await runner.RunAsync(Path.Combine(work, command.Executable), command.Arguments, work, prefix, TimeSpan.FromSeconds(8), CancellationToken.None);
                        string text = File.ReadAllText(restored.StdoutPath) + "\n" + File.ReadAllText(restored.StderrPath);
                        if (restored.ExitCode != 0 || Regex.IsMatch(text, @"(?i)\b(error|failed|denied|cannot)\b")) restorationErrors.Add($"恢复步骤 {i} 未确认成功，退出码 {restored.ExitCode}");
                        if (command.DelayAfterSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(command.DelayAfterSeconds), CancellationToken.None);
                    }
                    catch (Exception e) { restorationErrors.Add($"恢复步骤 {i} 异常：{e.Message}"); }
                }
            }
            foreach (string file in Directory.EnumerateFiles(work, "*", SearchOption.TopDirectoryOnly))
                if (new[] { ".txt", ".cmd", ".log" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
            run.Items[run.CurrentIndex].Reports.Add("风扇原始输出与恢复记录：" + output);
            foreach (string error in restorationErrors) run.Items[run.CurrentIndex].Reports.Add("风扇自动模式恢复失败，须人工核查：" + error);
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { run.Items[run.CurrentIndex].Reports.Add("本轮风扇临时目录未能清理：" + work); }
        }
        if (cancelled) ct.ThrowIfCancellationRequested();
        return restorationErrors.Count > 0 ? new(Outcome.Fail, profile + " 风扇自动模式恢复失败，禁止判定通过：" + string.Join("；", restorationErrors)) : result;
    }
}
