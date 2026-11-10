using Aging.Core;
using Microsoft.Win32;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aging.Windows;

public static class MachineCompatibility
{
    public static IReadOnlyList<string> ApplicationErrors() => PlatformPolicy.Evaluate(Environment.OSVersion.Version, RuntimeInformation.OSArchitecture.ToString(), IsWindowsClient());
    private static bool IsWindowsClient()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return string.Equals(Convert.ToString(key?.GetValue("InstallationType")), "Client", StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<MachineCompatibilityReport> CaptureAsync(AppOptions options, CancellationToken ct = default)
    {
        var errors = ApplicationErrors();
        var report = new MachineCompatibilityReport
        {
            CanRunApplication = errors.Count == 0, OperatingSystem = RuntimeInformation.OSDescription,
            WindowsVersion = Environment.OSVersion.Version.ToString(), Architecture = RuntimeInformation.OSArchitecture.ToString()
        };
        report.Items.Add(new("操作系统 / 架构", errors.Count == 0 ? "支持" : "不支持", errors.Count == 0 ? "Windows 10 22H2 / Windows 11 原生 x64；Intel/AMD 使用同一安装包" : string.Join("；", errors)));
        report.Items.Add(new(".NET 运行环境", "已包含", RuntimeInformation.FrameworkDescription + "；安装包自包含，不需要目标电脑联网下载 .NET"));
        var caps = new byte[128];
        if (GetPwrCapabilities(caps))
        {
            report.Items.Add(new("S3", caps[5] != 0 ? "硬件声明支持" : "不支持", "由本机 BIOS、固件和 Windows 电源策略决定；需实测进入/唤醒"));
            report.Items.Add(new("S4", caps[6] != 0 && caps[8] != 0 ? "硬件声明支持" : "不支持或休眠文件关闭", "需要 S4 能力和休眠文件；程序不擅自修改休眠配置"));
            report.Items.Add(new("Modern Standby / CS", caps[20] != 0 ? "硬件声明支持" : "不支持", "不把 CS 与 S3 相互替代"));
        }
        else report.Items.Add(new("系统电源能力", "未获取", "无法读取 SYSTEM_POWER_CAPABILITIES"));
        report.Items.Add(new("Windows Media Player", Type.GetTypeFromProgID("WMPlayer.OCX") != null ? "组件存在" : "组件缺失", "组件存在不代表所有视频解码器可用；N/KN 或精简镜像可能需要安装媒体功能"));
        report.Items.Add(new("真实电源操作权限", WindowsPlatform.IsAdministrator ? "已提权" : "需管理员", "模拟模式可使用普通账户；真实重启/睡眠及压力引擎需要提权"));
        report.Items.Add(new("PwrTest x64", File.Exists(options.PwrTestPath) ? "文件存在" : "未配置或缺失", options.PwrTestPath));
        report.Items.Add(new("视频文件", File.Exists(options.VideoPath) ? "文件存在" : "未配置或缺失", options.VideoPath));
        using (var framework = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            bool frameworkReady = Convert.ToInt32(framework?.GetValue("Release", 0) ?? 0) >= 378389;
            report.Items.Add(new("SFT / .NET Framework", File.Exists(options.SftPath) && frameworkReady ? "文件与运行环境存在" : "未配置或依赖缺失", "原 SFT 使用 .NET Framework 4.5；自包含 .NET 10 不替代该旧运行环境"));
        }
        foreach (var kind in new[] { "BurnIn", "FurMark", "3DMark" })
        {
            if (kind == "BurnIn" && File.Exists(options.BurnInPath))
            {
                var issues = BurnInAdapter.Validate(options.BurnInPath, AgingItem.Parse("BurnIn|0.1|0"));
                report.Items.Add(new("BurnIn", issues.Count == 0 ? "内置适配可启动；本型号需验收" : "未就绪", issues.Count == 0 ? options.BurnInPath : string.Join("；", issues)));
                continue;
            }
            var profiles = options.Engines.Where(x => x.Key == kind || x.Key.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase)).ToList();
            bool ready = profiles.Any(x => x.Value.Verified && File.Exists(x.Value.Executable));
            report.Items.Add(new(kind, ready ? "存在适配，仍需逐项预检" : "未配置已验证引擎", "运行前还会核对版本、SHA256、目标时长、参数和报告规则"));
        }
        var sensor = CoreTempReader.Read();
        report.Items.Add(new("Core Temp 温度", sensor != null ? "正在提供真实数据" : CoreTempSession.FindExecutable(options.ReferenceRoot) != null ? "组件已包含，测试时采集" : "组件缺失", sensor != null ? $"最高核心温度 {sensor.TemperatureCelsius:0.0}°C；{sensor.CpuName}" : "未读取到有效共享内存时显示未获取；旧组件对新 CPU 的支持须实测"));
        foreach (string scheme in new[] { "IP3", "WB", "Ed" })
        {
            var validation = FanTestAdapter.Validate(options.ReferenceRoot, scheme);
            report.Items.Add(new("风扇 " + scheme, validation.Count == 0 ? "组件可调用；仅适配对应主板" : "未就绪", validation.Count == 0 ? "仅用户选择该方案时运行；实际转速与自动模式恢复需在对应主板验收" : string.Join("；", validation)));
        }
        await Task.Run(() =>
        {
            try
            {
                using var firmware = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                report.Manufacturer = Convert.ToString(firmware?.GetValue("SystemManufacturer")) ?? "未获取";
                report.Model = Convert.ToString(firmware?.GetValue("SystemProductName")) ?? "未获取";
                report.FirmwareVersion = string.Join(" / ", (firmware?.GetValue("BIOSVersion") as string[]) ?? [Convert.ToString(firmware?.GetValue("BIOSVersion")) ?? "未获取"]);
                using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                report.Cpu = Convert.ToString(cpu?.GetValue("ProcessorNameString"))?.Trim() ?? "未获取";
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery("SELECT Name,DriverVersion FROM Win32_VideoController"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false });
                using var results = searcher.Get();
                foreach (ManagementObject adapter in results)
                {
                    using (adapter) { ct.ThrowIfCancellationRequested(); report.DisplayAdapters.Add($"{adapter["Name"]} / {adapter["DriverVersion"]}"); }
                }
                if (report.DisplayAdapters.Count == 0) report.Items.Add(new("显示适配器信息", "未获取", "WMI 返回空列表；不据此推断 GPU 压力测试兼容"));
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { report.Items.Add(new("硬件详细信息", "未获取", e.Message)); }
        }, ct);
        return report;
    }
    public static void Save(MachineCompatibilityReport report, string path) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(report, JsonData.Options));
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool GetPwrCapabilities(byte[] capabilities);
}
