using System.Reflection;

namespace Aging.Core;

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.1";
    public static string Product => "GW Aging " + Version;
}

public static class PlatformPolicy
{
    public static IReadOnlyList<string> Evaluate(Version windows, string nativeArchitecture, bool isClient)
    {
        var errors = new List<string>();
        if (!nativeArchitecture.Equals("X64", StringComparison.OrdinalIgnoreCase))
            errors.Add("此安装包支持 Intel/AMD 原生 x64；不支持 32 位 Windows 或 ARM64 仿真运行。");
        if (windows.Major != 10 || windows.Build < 19045)
            errors.Add("需要 Windows 10 22H2（19045）或 Windows 11；不支持 Windows 7/8 和旧版 Windows 10。");
        if (!isClient) errors.Add("此版本面向 Windows 10/11 客户端，Windows Server 尚未验收。");
        return errors;
    }
}

public sealed record CompatibilityItem(string Name, string Status, string Detail);
public sealed class MachineCompatibilityReport
{
    public string ToolVersion { get; init; } = BuildInfo.Version;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public bool CanRunApplication { get; init; }
    public string OperatingSystem { get; init; } = "";
    public string WindowsVersion { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Manufacturer { get; set; } = "未获取";
    public string Model { get; set; } = "未获取";
    public string FirmwareVersion { get; set; } = "未获取";
    public string Cpu { get; set; } = "未获取";
    public List<string> DisplayAdapters { get; set; } = [];
    public List<CompatibilityItem> Items { get; init; } = [];
    public string Note { get; init; } = "本报告只做能力和依赖检测，不执行重启、睡眠、压力测试或驱动加载。可安装不等于所有老化项目均已通过实机验证。";
}
