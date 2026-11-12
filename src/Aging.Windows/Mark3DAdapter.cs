using Aging.Core;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Aging.Windows;

public static class Mark3DAdapter
{
    public static IReadOnlyDictionary<string, string> Scenes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["3DMark11_Extreme"] = "custom_extreme_definition.xml", ["3DMark11_Performance"] = "custom_performance_definition.xml",
        ["3DMarkV2_AllItem"] = "custom_all.3dmdef", ["3DMarkV2_Ice_Storm"] = "custom_icestorm_extreme.3dmdef",
        ["3DMarkV2_Sky_Diver"] = "custom_skydiver.3dmdef", ["3DMarkV2_Time_Spy"] = "custom_timespy_extreme.3dmdef", ["3DMarkV2_Fire_Strike"] = "custom_firestrike_ultra.3dmdef"
    };
    public static bool IsLegacy11(AgingItem item) => item.MarkVersion.StartsWith("3DMark11_", StringComparison.Ordinal);
    public static string Definition(string root, AgingItem item) => Scenes.TryGetValue(item.MarkVersion, out string? file) ? Path.Combine(root, "3DMark", file) : "";
    public static IReadOnlyList<string> Validate(string executable, AgingItem item, string root)
    {
        var errors = new List<string>();
        if (!File.Exists(executable)) errors.Add("待补齐 3DMark 命令行引擎及用户有效 Enterprise/Professional 商业授权");
        else if (!Path.GetFileName(executable).Equals(IsLegacy11(item) ? "3DMark11Cmd.exe" : "3DMarkCmd.exe", StringComparison.OrdinalIgnoreCase)) errors.Add("请选择所选版本的 3DMark 命令行引擎");
        if (!Scenes.ContainsKey(item.MarkVersion)) errors.Add("未知 3DMark 场景：" + item.MarkVersion);
        else if (!File.Exists(Definition(root, item))) errors.Add("缺少原始 3DMark 场景定义：" + Definition(root, item));
        try { _ = GpuEngines.Resolution(item.Resolution); } catch (FormatException e) { errors.Add(e.Message); }
        return errors;
    }

    public static string RewriteDefinition(string xml, AgingItem item)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var (width, height) = GpuEngines.Resolution(item.Resolution);
        bool setWidth = false, setHeight = false, setWindow = false;
        foreach (var name in doc.Descendants().Where(e => e.Name.LocalName == "name").ToArray())
        {
            var value = name.ElementsAfterSelf().FirstOrDefault(e => e.Name.LocalName == "value");
            if (value == null) continue;
            switch (name.Value.Trim())
            {
                case "screen_width": value.Value = width.ToString(CultureInfo.InvariantCulture); setWidth = true; break;
                case "screen_height": value.Value = height.ToString(CultureInfo.InvariantCulture); setHeight = true; break;
                case "enable_window_mode": case "is_windowed": value.Value = item.WindowMode ? "1" : "0"; setWindow = true; break;
            }
        }
        if (!setWidth || !setHeight || !setWindow) throw new FormatException("3DMark 场景定义缺少分辨率或窗口模式设置");
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<string> CreateArguments(string definition, string result, string export, string log) =>
        ["--definition=" + definition, "--loop=1", "--out=" + result, "--export=" + export, "--log=" + log];

    public static TaskResult EvaluateReport(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Descendants().Any(e => (e.Name.LocalName.Contains("error", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("failure", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(e.Value) && e.Value.Trim() != "0"))
                return new(Outcome.Fail, "3DMark 导出报告包含错误");
            bool score = document.Descendants().Any(e => !e.HasElements && e.Name.LocalName.Contains("score", StringComparison.OrdinalIgnoreCase) && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && number > 0);
            // A score proves a completed workload, not a vendor hardware stability verdict.
            return new(Outcome.Uncertain, score ? "3DMark 本轮原始结果存在有效分数；硬件稳定性通过规则待授权引擎实测验收" : "3DMark 原始结果缺少有效测试分数，不能判通过");
        }
        catch (XmlException) { return new(Outcome.Uncertain, "3DMark 导出结果不是有效 XML"); }
    }

    public static async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        string executable = IsLegacy11(item) ? run.Options.Mark11Path : run.Options.Mark3DPath;
        var errors = Validate(executable, item, run.Options.ReferenceRoot);
        if (errors.Count > 0) return new(Outcome.Fail, string.Join("；", errors)) { Execution = new("DependencyMissing", 0, []) };
        string output = Path.Combine(run.LogDirectory, $"{run.CurrentIndex + 1}_3DMark", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output); var reports = new List<string>(); var watch = Stopwatch.StartNew(); int? exitCode = null; string state = "Starting";
        run.Items[run.CurrentIndex].Reports.Add("3DMark 原始报告目录：" + output);
        try
        {
            await GpuEngines.WriteIdentityAsync(executable, output, ct);
            string definition = Path.Combine(output, Path.GetFileName(Definition(run.Options.ReferenceRoot, item)));
            await File.WriteAllTextAsync(definition, RewriteDefinition(await File.ReadAllTextAsync(Definition(run.Options.ReferenceRoot, item), ct), item), ct);
            TaskResult evaluated = new(Outcome.Uncertain, "3DMark 尚无完成结果");
            for (int loop = 1; watch.Elapsed.TotalHours < item.Hours; loop++)
            {
                string prefix = Path.Combine(output, "loop-" + loop.ToString("D4"));
                string resultPath = prefix + (IsLegacy11(item) ? ".3dmark11-result" : ".3dmark-result"), exportPath = prefix + ".xml", log = prefix + ".log";
                progress($"3DMark {item.MarkVersion} 第 {loop} 轮；完成当前场景后检查目标时长");
                var result = await new OwnedProcessRunner().RunAsync(executable, CreateArguments(definition, resultPath, exportPath, log), Path.GetDirectoryName(executable)!, prefix,
                    TimeSpan.FromHours(1), ct, pid => { state = "Started"; progress($"3DMark 启动器 PID={pid}，等待实际负载及结果"); }, waitForDescendants: true);
                exitCode = result.ExitCode;
                reports.AddRange(Directory.EnumerateFiles(output, "loop-" + loop.ToString("D4") + ".*"));
                if (result.ExitCode != 0) return new(Outcome.Fail, "3DMark 退出码 " + result.ExitCode + "；请核验商业授权、SystemInfo 与所选场景 DLC") { Execution = new("Exited", watch.Elapsed.TotalSeconds, reports, exitCode) };
                if (!File.Exists(resultPath) || new FileInfo(resultPath).Length == 0 || !File.Exists(exportPath)) return new(Outcome.Uncertain, "3DMark 没有本轮原始结果及 XML 导出，不能判通过") { Execution = new("Exited", watch.Elapsed.TotalSeconds, reports, exitCode) };
                evaluated = EvaluateReport(await File.ReadAllTextAsync(exportPath, ct));
                if (evaluated.Outcome == Outcome.Fail || !evaluated.Message.Contains("有效分数", StringComparison.Ordinal)) break;
                if (result.Elapsed < TimeSpan.FromSeconds(5)) { evaluated = new(Outcome.Uncertain, "3DMark 负载执行过短，不能判通过"); break; }
            }
            return evaluated with { Execution = new("Exited", watch.Elapsed.TotalSeconds, reports, exitCode) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(Outcome.Aborted, "用户停止 3DMark，已保留本轮输出") { Execution = new("Cancelled", watch.Elapsed.TotalSeconds, Directory.EnumerateFiles(output).ToList(), exitCode) }; }
        catch (Exception e) when (e is IOException or TimeoutException or FormatException or System.ComponentModel.Win32Exception)
        { return new(Outcome.Fail, "3DMark " + e.Message) { Execution = new(state, watch.Elapsed.TotalSeconds, Directory.EnumerateFiles(output).ToList(), exitCode) }; }
    }
}
