using Aging.Core;
using System.Security.Cryptography;
using System.Text;

namespace Aging.Windows;

public sealed record ResumeTaskIdentity
{
    public const int CurrentVersion = 1;
    public int Version => CurrentVersion;
    public Guid RunId { get; }
    public string Executable { get; }
    public string DataRoot { get; }
    public string UserSid { get; }
    public string TaskName { get; }
    public string Arguments => "--resume --data " + QuoteArgument(DataRoot) + " --resume-run " + RunId.ToString("D") + " --resume-version " + Version;

    private ResumeTaskIdentity(Guid runId, string executable, string dataRoot, string userSid)
    {
        RunId = runId; Executable = executable; DataRoot = dataRoot; UserSid = userSid;
        string installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UserSid + "\n" + Executable.ToUpperInvariant() + "\n" + DataRoot.ToUpperInvariant())))[..24];
        TaskName = "GW_AgingTool_" + installation + "_" + runId.ToString("N");
    }

    public static ResumeTaskIdentity Create(Guid runId, string executable, string dataRoot, string userSid)
    {
        if (runId == Guid.Empty) throw new ArgumentException("续测任务标识不能为空", nameof(runId));
        if (string.IsNullOrWhiteSpace(userSid) || !System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-\d+(?:-\d+)+$")) throw new ArgumentException("续测用户 SID 无效", nameof(userSid));
        return new(runId, NormalizePath(executable), NormalizePath(dataRoot), userSid);
    }

    public void BindTo(RunSnapshot run)
    {
        if (run.RunId != RunId) throw new InvalidOperationException("续测标识不属于当前任务");
        run.ResumeVersion = Version; run.ResumeExecutable = Executable; run.ResumeDataRoot = DataRoot; run.ResumeUserSid = UserSid;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(c => c < 32 || c == '"') || !Path.IsPathFullyQualified(path)) throw new ArgumentException("续测路径必须为绝对路径", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static bool SamePath(string left, string right)
    {
        try { return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    // Match CommandLineToArgvW rules, including directory paths ending in a backslash.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public sealed record ResumeDecision(bool CanResume, string Reason);

public static class ResumePolicy
{
    public static bool NeedsRegistration(RunSnapshot run) => !run.Simulation && !run.IsTerminal &&
        (run.Options.AgingInNextBoot || run.Items.Any(x => x.Item.Kind == TaskKind.Reboot) ||
         (run.Options.SwitchReboot && run.Items.Count > 1));

    public static ResumeDecision EvaluateAutomaticResume(RunSnapshot run, Guid requestedRunId, int requestedVersion,
        string executable, string dataRoot, string userSid, string bootId)
    {
        if (run.Simulation || run.IsTerminal) return new(false, "模拟或已结束任务不自动续测");
        if (run.ResumeVersion != ResumeTaskIdentity.CurrentVersion || requestedVersion != run.ResumeVersion)
            return new(false, "检查点未绑定当前续测版本，请手动恢复");
        if (requestedRunId == Guid.Empty || requestedRunId != run.RunId) return new(false, "计划任务与检查点任务标识不匹配");
        if (!string.Equals(userSid, run.ResumeUserSid, StringComparison.Ordinal) ||
            !ResumeTaskIdentity.SamePath(executable, run.ResumeExecutable) || !ResumeTaskIdentity.SamePath(dataRoot, run.ResumeDataRoot))
            return new(false, "续测用户或程序、数据目录与检查点绑定不匹配");
        if (run.Phase != RunPhase.AwaitingReboot) return new(false, "任务没有等待重启；中断的负载需手动确认恢复");
        if (run.CurrentIndex < 0 || run.CurrentIndex >= run.Items.Count || run.PendingOperation is not ("cycle" or "switch" or "start") ||
            run.OperationId == Guid.Empty || run.OperationStartedAt == null) return new(false, "检查点缺少有效的待续测系统动作");
        if (run.PendingOperation == "cycle" && run.Items[run.CurrentIndex].Item.Kind != TaskKind.Reboot)
            return new(false, "检查点周期类型与待恢复动作不匹配");
        if (!BootSession.CanCompare(run.ExpectedBootId, bootId))
            return new(false, "启动标识缺失或格式已升级，旧检查点不能自动计数；请保留日志并新建测试任务");
        if (!BootSession.HasChanged(run.ExpectedBootId, bootId))
            return new(false, "仍是同次 Windows 启动，重开程序不会增加周期");
        return new(true, "登录后恢复已绑定任务，检测到新的 Windows 启动标识");
    }
}
