using System.Globalization;
using System.Text.Json.Serialization;

namespace Aging.Core;

[JsonConverter(typeof(JsonStringEnumConverter<TaskKind>))]
public enum TaskKind { Reboot, S3, S4, CS, BurnIn, FurMark, Mark3D, Video }
[JsonConverter(typeof(JsonStringEnumConverter<RunPhase>))]
public enum RunPhase { Idle, Preflight, Countdown, Running, Paused, AwaitingReboot, Completed, Failed, Aborted, Skipped }
[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome { None, Pass, Fail, Aborted, Uncertain }

public sealed record AgingItem
{
    public TaskKind Kind { get; init; }
    public string Raw { get; init; } = "";
    public int Cycles { get; init; } = 1;
    public int DelaySeconds { get; init; } = 30;
    public int SleepSeconds { get; init; } = 60;
    public double Hours { get; init; } = 0.1;
    public bool HardwareCheck { get; init; }
    public bool TimeCheck { get; init; }
    public bool WifiCheck { get; init; }
    public string FanProfile { get; init; } = "N";
    public int BurnInMode { get; init; }
    public string Msaa { get; init; } = "8X";
    public string Resolution { get; init; } = "1920x1080";
    public bool WindowMode { get; init; } = true;
    public string MarkVersion { get; init; } = "3DMarkV2_AllItem";
    public string Name => Kind == TaskKind.Mark3D ? "3DMark" : Kind.ToString();
    public static readonly string[] BurnInModeNames = ["综合", "综合(DX9)", "硬盘", "DDR Test（内存）"];
    public bool IsPower => Kind is TaskKind.Reboot or TaskKind.S3 or TaskKind.S4 or TaskKind.CS;
    public bool IsStress => Kind is TaskKind.BurnIn or TaskKind.FurMark or TaskKind.Mark3D;

    public static bool LegacyBool(string value) => value.Trim().ToUpperInvariant() switch
    { "Y" or "1" => true, "N" or "4" => false, _ => throw new FormatException($"无效检查开关：{value}") };

    public static AgingItem Parse(string raw)
    {
        var p = raw.Split('|', StringSplitOptions.TrimEntries);
        if (p.Length == 0) throw new FormatException("空老化项目");
        var kind = p[0].ToUpperInvariant() switch
        {
            "REBOOT" => TaskKind.Reboot, "S3" => TaskKind.S3, "S4" => TaskKind.S4, "CS" => TaskKind.CS,
            "BURNIN" => TaskKind.BurnIn, "FURMARK" => TaskKind.FurMark, "3DMARK" => TaskKind.Mark3D,
            "VIDEO" => TaskKind.Video, _ => throw new FormatException($"未知项目：{p[0]}")
        };
        int n = kind switch { TaskKind.Reboot or TaskKind.S3 or TaskKind.S4 or TaskKind.CS => 7,
            TaskKind.BurnIn => 3, TaskKind.FurMark or TaskKind.Mark3D => 5, _ => 1 };
        if (p.Length != n) throw new FormatException($"{p[0]} 应有 {n} 个字段，实际 {p.Length}");
        int Int(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
        double Hours() => double.Parse(p[1], CultureInfo.InvariantCulture);
        var item = new AgingItem { Kind = kind, Raw = raw };
        item = kind switch
        {
            TaskKind.Reboot => item with { Cycles = Int(1), DelaySeconds = Int(2), HardwareCheck = LegacyBool(p[3]), TimeCheck = LegacyBool(p[4]), WifiCheck = LegacyBool(p[5]), FanProfile = p[6] },
            TaskKind.S3 or TaskKind.S4 or TaskKind.CS => item with { Cycles = Int(1), DelaySeconds = Int(2), SleepSeconds = Int(3), HardwareCheck = LegacyBool(p[4]), TimeCheck = LegacyBool(p[5]), WifiCheck = LegacyBool(p[6]) },
            TaskKind.BurnIn => item with { Hours = Hours(), BurnInMode = Int(2) },
            TaskKind.FurMark => item with { Hours = Hours(), Msaa = p[2], Resolution = p[3], WindowMode = LegacyBool(p[4]) },
            TaskKind.Mark3D => item with { Hours = Hours(), MarkVersion = p[2], Resolution = p[3], WindowMode = LegacyBool(p[4]) }, _ => item
        };
        item.Validate();
        return item;
    }

    public void Validate()
    {
        if (IsPower && (Cycles < 1 || Cycles > 100000 || DelaySeconds < 30 || DelaySeconds > 86400))
            throw new FormatException("次数必须为 1–100000，倒计时为 30–86400 秒");
        if (Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS && (SleepSeconds < 30 || SleepSeconds > 86400))
            throw new FormatException("睡眠时间必须为 30–86400 秒（新建项目至少 60 秒）");
        if (IsStress && (!double.IsFinite(Hours) || Hours < 0.1 || Hours > 10000))
            throw new FormatException("压力测试时长必须为 0.1–10000 小时");
        if (Kind == TaskKind.BurnIn && BurnInMode is < 0 or > 3) throw new FormatException("BurnIn 模式必须为 0/1/2/3（DDR 内存）");
        if (FanProfile is not ("N" or "IP3" or "WB" or "Ed")) throw new FormatException("未知风扇方案");
        if (IsStress && !System.Text.RegularExpressions.Regex.IsMatch(Resolution, @"^\d{3,5}x\d{3,5}$"))
            throw new FormatException("分辨率格式应为 1920x1080");
    }

    public string Serialize() => Kind switch
    {
        TaskKind.Reboot => $"Reboot|{Cycles}|{DelaySeconds}|{B(HardwareCheck)}|{B(TimeCheck)}|{B(WifiCheck)}|{FanProfile}",
        TaskKind.S3 or TaskKind.S4 or TaskKind.CS => $"{Name}|{Cycles}|{DelaySeconds}|{SleepSeconds}|{B(HardwareCheck)}|{B(TimeCheck)}|{B(WifiCheck)}",
        TaskKind.BurnIn => $"BurnIn|{Hours.ToString(CultureInfo.InvariantCulture)}|{BurnInMode}",
        TaskKind.FurMark => $"FurMark|{Hours.ToString(CultureInfo.InvariantCulture)}|{Msaa}|{Resolution}|{B(WindowMode)}",
        TaskKind.Mark3D => $"3DMark|{Hours.ToString(CultureInfo.InvariantCulture)}|{MarkVersion}|{Resolution}|{B(WindowMode)}", _ => "Video"
    };
    private static string B(bool b) => b ? "Y" : "N";
}

public sealed class AppOptions
{
    public string ReferenceRoot { get; set; } = "";
    public bool StopOnFail { get; set; } = true;
    public bool SwitchReboot { get; set; } = true;
    public bool ShutdownAfterAging { get; set; }
    public bool AgingInNextBoot { get; set; }
    public bool TestWithMaxVolume { get; set; }
    public string AfterAgingScript { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string SftPath { get; set; } = "";
    public string SnKeyPath { get; set; } = "";
    public string PwrTestPath { get; set; } = "";
    public string BurnInPath { get; set; } = "";
    public string FurMarkPath { get; set; } = "";
    public string Mark3DPath { get; set; } = "";
    public string Mark11Path { get; set; } = "";
    public int HybridSleep { get; set; }
    public Dictionary<string, EngineProfile> Engines { get; set; } = [];
}

// Profiles remain unverified until the dedicated machine supplies a frozen version and report contract.
public sealed class EngineProfile
{
    public string Executable { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public string VerificationNote { get; set; } = "";
    public bool Verified { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string ReportPath { get; set; } = "";
    public string PassPattern { get; set; } = "";
    public string FailPattern { get; set; } = "";
    public bool SupportsDuration { get; set; }
}

public sealed class ItemRun
{
    public AgingItem Item { get; set; } = new();
    public RunPhase Phase { get; set; } = RunPhase.Idle;
    public Outcome Outcome { get; set; }
    public int CompletedCycles { get; set; }
    public double ActiveSeconds { get; set; }
    public int RemainingSeconds { get; set; }
    public List<double> CycleSeconds { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<string> Reports { get; set; } = [];
    public EngineExecutionInfo? Execution { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int VideoPlays { get; set; }
    public List<double> VideoRunSeconds { get; set; } = [];
    public string Message { get; set; } = "等待";
}

public sealed class RunSnapshot
{
    public List<string> ExcludedItems { get; set; } = [];
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public int ResumeVersion { get; set; }
    public string ResumeUserSid { get; set; } = "";
    public string ResumeExecutable { get; set; } = "";
    public string ResumeDataRoot { get; set; } = "";
    public Guid RunId { get; set; } = Guid.NewGuid();
    public bool Simulation { get; set; } = true;
    public AppOptions Options { get; set; } = new();
    public List<ItemRun> Items { get; set; } = [];
    public int CurrentIndex { get; set; }
    public RunPhase Phase { get; set; }
    public Outcome Outcome { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; set; }
    public string LogDirectory { get; set; } = "";
    public string ExpectedBootId { get; set; } = "";
    public string PendingOperation { get; set; } = "";
    public DateTimeOffset? OperationStartedAt { get; set; }
    public Guid OperationId { get; set; }
    public HashSet<int> SwitchedItems { get; set; } = [];
    public bool InitialBootCompleted { get; set; }
    public DeviceSnapshot? DeviceBaseline { get; set; }
    public DateTimeOffset? EventBookmark { get; set; }
    public bool CompletionActionClaimed { get; set; }
    public string LegacyLogPath { get; set; } = "";
    public int LegacyItemIndex { get; set; }
    public bool IsTerminal => Phase is RunPhase.Completed or RunPhase.Failed or RunPhase.Aborted;
    public bool HardwarePassed => !Simulation && IsTerminal && Outcome == Outcome.Pass;
}

public sealed record DeviceSnapshot(Dictionary<string, int> Devices, ulong MemoryBytes);
public sealed record PreflightResult(bool CanRun, IReadOnlyList<string> Messages)
{
    public static PreflightResult Success(params string[] messages) => new(true, messages);
    public static PreflightResult Failure(params string[] messages) => new(false, messages);
}
public sealed record EngineExecutionInfo(string StartState, double ElapsedSeconds, List<string> RawReports, int? ExitCode = null);
public sealed record TaskResult(Outcome Outcome, string Message, int VideoPlays = 0, List<double>? VideoRunSeconds = null)
{
    public EngineExecutionInfo? Execution { get; init; }
}
public sealed record TelemetrySample(DateTimeOffset Time, int Item, double? Temperature, double? CpuPercent, int? BatteryPercent, string Power, string Charge);

public interface IRunStore { void Save(RunSnapshot snapshot); RunSnapshot? Load(); }
public interface ILegacyLogWriter { void Write(RunSnapshot snapshot); void Event(RunSnapshot snapshot, string level, string message); }
public interface ITaskPlatform
{
    string BootId { get; }
    Task<PreflightResult> PreflightAsync(AgingItem item, RunSnapshot run, CancellationToken ct);
    Task<IReadOnlyList<string>> PrepareCycleAsync(AgingItem item, RunSnapshot run, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    Task<DeviceSnapshot> CaptureDevicesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> CheckAsync(AgingItem item, RunSnapshot run, CancellationToken ct);
    Task RebootAsync(CancellationToken ct);
    Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct);
    Task AfterRunAsync(RunSnapshot run, CancellationToken ct);
}
