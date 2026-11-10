using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aging.Core;

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { f.Write(bytes); f.Flush(true); }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
                    else File.Move(temp, path);
                    break;
                }
                catch (IOException e) when (attempt < 8 && File.Exists(temp) && IsTransientFileLock(e))
                {
                    // Antivirus/indexers and old viewers may briefly open the destination/backup without FILE_SHARE_DELETE.
                    // Never fall back to truncate-in-place; preserve the last committed checkpoint on failure.
                    Thread.Sleep(Math.Min(250, 25 * (attempt + 1)));
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool IsTransientFileLock(IOException error) => (error.HResult & 0xffff) is 5 or 32 or 33 or 1175 or 1176 or 1177 or 1224;
}

public sealed class JsonRunStore(string path) : IRunStore
{
    private sealed record Envelope(int Version, long Revision, string Sha256, string Payload);
    public bool RecoveredFromBackup { get; private set; }
    public void Save(RunSnapshot snapshot)
    {
        snapshot.Revision++;
        string payload = JsonSerializer.Serialize(snapshot, JsonData.Options);
        var envelope = new Envelope(1, snapshot.Revision, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), payload);
        // A corrupt primary must not replace the last valid backup.
        bool preserveBackup = File.Exists(path) && !IsValid(path);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonData.Options), !preserveBackup);
    }
    private static bool IsValid(string candidate) { try { _ = Read(candidate); return true; } catch (Exception e) when (e is IOException or JsonException or InvalidDataException or FormatException) { return false; } }
    private static RunSnapshot Read(string candidate)
    {
        var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(candidate), JsonData.Options) ?? throw new InvalidDataException("空检查点");
        if (e.Version != 1 || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Payload))).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("检查点版本或校验和无效");
        var run = JsonSerializer.Deserialize<RunSnapshot>(e.Payload, JsonData.Options) ?? throw new InvalidDataException("空状态");
        if (run.SchemaVersion != 1 || run.Revision != e.Revision || run.Items.Count == 0 || run.CurrentIndex < 0 || run.CurrentIndex > run.Items.Count)
            throw new InvalidDataException("检查点内容无效");
        foreach (var item in run.Items) item.Item.Validate();
        return run;
    }
    public RunSnapshot? Load()
    {
        RecoveredFromBackup = false;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
        try { return Read(path); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or FormatException)
        {
            try { var run = Read(path + ".bak"); RecoveredFromBackup = true; return run; }
            catch (Exception backup) when (backup is IOException or JsonException or InvalidDataException or FormatException)
            { throw new InvalidDataException("主检查点及备份均损坏。保留文件供诊断，禁止自动开始新任务。", e); }
        }
    }
}

public sealed class LegacyLogWriter : ILegacyLogWriter
{
    public static string LogPath(RunSnapshot run) => Path.Combine(run.LogDirectory, $"GWTEST_Aging_{run.StartedAt:yyyyMMddHHmmss}.log");
    public void Write(RunSnapshot run)
    {
        bool legacy = run.LegacyLogPath.Length > 0;
        string path = legacy ? run.LegacyLogPath : LogPath(run);
        bool existing = legacy && File.Exists(path);
        var doc = existing ? IniDocument.Load(path) : IniDocument.Empty();
        if (!existing) doc.Set("MAIN", "AgingState", run.IsTerminal ? "END" : "RUN");
        string metadata = existing ? $"Replica_{run.LegacyItemIndex}" : "MAIN";
        doc.Set(metadata, "Outcome", run.Simulation && run.Outcome == Outcome.Pass ? "Simulated" : run.Outcome.ToString());
        doc.Set(metadata, "Simulation", run.Simulation ? "Y" : "N");
        doc.Set(metadata, "HardwarePassed", run.HardwarePassed ? "Y" : "N");
        doc.Set(metadata, "RunId", run.RunId.ToString());
        doc.Set(metadata, "ExecutedScope", run.ExcludedItems.Count == 0 ? "ConfiguredQueue" : "SelectedItemsOnly");
        doc.Set(metadata, "ExcludedCount", run.ExcludedItems.Count.ToString());
        for (int i = 0; i < run.ExcludedItems.Count; i++) doc.Set(metadata, "Excluded" + (i + 1), run.ExcludedItems[i]);
        if (!existing) doc.Set("ToolVer", "Test", BuildInfo.Product);
        for (int i = 0; i < run.Items.Count; i++)
        {
            var item = run.Items[i]; int number = legacy ? run.LegacyItemIndex : i + 1; string prefix = $"{number}_{item.Item.Name}";
            doc.Set("AgingItem", number.ToString(), item.Item.Raw.Length > 0 ? item.Item.Raw : item.Item.Serialize());
            doc.Set("ToolVer", item.Item.Name, BuildInfo.Product);
            string state = item.Phase switch
            {
                RunPhase.Idle => "WAIT", RunPhase.Aborted or RunPhase.Skipped => "STOP",
                RunPhase.Completed or RunPhase.Failed => "END", _ => "RUN"
            };
            doc.Set(prefix + "_Info", "State", state);
            doc.Set(prefix + "_Info", "Outcome", run.Simulation && item.Outcome == Outcome.Pass ? "Simulated" : item.Outcome.ToString());
            doc.Set(prefix + "_Info", "TargetTime", (item.Item.IsPower ? item.Item.Cycles : item.Item.IsStress ? item.Item.Hours * 60 : 0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            if (item.Item.Kind == TaskKind.BurnIn) doc.Set(prefix + "_Info", "TestMode", item.Item.BurnInMode.ToString());
            doc.Set(prefix + "_Info", "Cycles", item.CompletedCycles.ToString());
            doc.Set(prefix + "_Info", "Delay", item.Item.DelaySeconds.ToString());
            doc.Set(prefix + "_Info", "FirstTime", item.StartedAt?.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            doc.Set(prefix + "_Info", "EndTime", item.EndedAt?.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            doc.Set(prefix + "_Info", "FirstTimeStamp", item.StartedAt is { } first ? LegacyTiming.Timestamp(first).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            doc.Set(prefix + "_Info", "EndTimeStamp", item.EndedAt is { } last ? LegacyTiming.Timestamp(last).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            doc.Set(prefix + "_Info", "AverageTime", item.CycleSeconds.Count == 0 ? "0" : item.CycleSeconds.Average().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            doc.Set(prefix + "_Info", "PlayNum", item.VideoPlays.ToString());
            for (int j = 0; j < item.VideoRunSeconds.Count; j++) doc.Set(prefix + "_Info", $"RunTime{j + 1}", $"第 {j + 1} 次视频播放了{item.VideoRunSeconds[j]:0}秒");
            doc.Set(prefix + "_Info", "ActiveSeconds", item.ActiveSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            doc.Set(prefix + "_Error", "Num", item.Errors.Count.ToString());
            for (int j = 0; j < item.Errors.Count; j++) doc.Set(prefix + "_Error", (j + 1).ToString(), Clean(item.Errors[j]));
            string report = item.Item.Kind == TaskKind.BurnIn ? $"{number}_BurnIn_Report" : prefix + "_Report";
            doc.Set(report, "Num", item.Reports.Count.ToString());
            for (int j = 0; j < item.Reports.Count; j++) doc.Set(report, (j + 1).ToString(), Clean(item.Reports[j]));
        }
        doc.Save(path, legacy);
        if (legacy)
        {
            // Also retain a standalone report under the new run directory.
            var copy = JsonData.Clone(run); copy.LegacyLogPath = ""; copy.LegacyItemIndex = 0;
            Write(copy);
        }
    }
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    public void Event(RunSnapshot run, string level, string message)
    {
        Directory.CreateDirectory(run.LogDirectory);
        File.AppendAllText(Path.Combine(run.LogDirectory, "GWTEST_Run.txt"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\tTest\t{level}\t{Clean(message)}\r\n", new UTF8Encoding(false));
    }
}
