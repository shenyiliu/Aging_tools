using System.Globalization;

namespace Aging.Core;

public sealed record LegacyRequest(AgingItem Item, string LogPath, int ItemIndex, bool StopOnFail, string EnginePath = "");
public static class LegacyCommandLine
{
    public static LegacyRequest Parse(string module, string commandLine)
    {
        string raw = commandLine.Trim().Trim('"');
        var p = raw.Split('/').Skip(1).Select(x => x.Trim().Trim('"')).ToArray();
        module = module.ToLowerInvariant();
        int count = module switch { "reboot" => 10, "sleep" => 13, "burnintest" => 7, "furmarktest" => 8, "3dmarktest" => 9, _ => throw new FormatException("未知兼容入口") };
        if (!raw.StartsWith('/') || p.Length != count) throw new FormatException($"{module} 需要 {count} 个斜杠字段，路径应使用 Windows 反斜杠");
        int Int(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
        string B(int i) => AgingItem.LegacyBool(p[i]) ? "Y" : "N";
        LegacyRequest request = module switch
        {
            "reboot" => new(AgingItem.Parse($"Reboot|{p[0]}|{p[1]}|{B(6)}|{B(7)}|{B(5)}|{p[3]}"), p[2], Int(9), AgingItem.LegacyBool(p[8])),
            "sleep" => new(AgingItem.Parse($"{p[0]}|{p[1]}|{p[2]}|{Math.Max(30, Int(4))}|{B(9)}|{B(10)}|{B(8)}"), p[3], Int(12), AgingItem.LegacyBool(p[11])),
            "burnintest" => new(AgingItem.Parse($"BurnIn|{p[1]}|{p[3]}"), p[2], Int(6), true, p[0]),
            "furmarktest" => new(AgingItem.Parse($"FurMark|{p[0]}|{p[1]}|{p[2]}|{B(3)}"), p[4], Int(7), true),
            "3dmarktest" when p[0].Equals("RUN", StringComparison.OrdinalIgnoreCase) => new(AgingItem.Parse($"3DMark|{p[2]}|{p[1]}|{p[4]}|{B(3)}"), p[5], Int(8), true),
            _ => throw new FormatException("无效兼容参数")
        };
        if (request.ItemIndex < 1 || !Path.IsPathFullyQualified(request.LogPath)) throw new FormatException("旧日志必须为绝对路径，项目序号从 1 开始");
        return request;
    }
}
