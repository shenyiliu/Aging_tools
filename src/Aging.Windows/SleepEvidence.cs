using Aging.Core;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Aging.Windows;

public static class SleepEvidence
{
    public static TaskResult Evaluate(IEnumerable<string> events, TaskKind kind, DateTimeOffset started, int seconds)
    {
        if (kind is not (TaskKind.S3 or TaskKind.S4)) throw new ArgumentException("需要 S3 或 S4", nameof(kind));
        string state = kind == TaskKind.S4 ? "5" : "4";
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var entries = new List<DateTimeOffset>();
        var wakes = new List<(DateTimeOffset Sleep, DateTimeOffset Wake)>();
        foreach (string xml in events)
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root!;
            var system = root.Element(ns + "System");
            string? provider = system?.Element(ns + "Provider")?.Attribute("Name")?.Value;
            string? id = system?.Element(ns + "EventID")?.Value;
            var data = root.Element(ns + "EventData")?.Elements(ns + "Data").ToDictionary(x => x.Attribute("Name")?.Value ?? "", x => x.Value) ?? [];
            if (data.GetValueOrDefault("TargetState") != state || data.GetValueOrDefault("EffectiveState") != state) continue;
            bool Time(string? value, out DateTimeOffset time) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);
            if (provider == "Microsoft-Windows-Kernel-Power" && id == "42" && Time(system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value, out var entry) && entry >= started)
                entries.Add(entry);
            if (provider == "Microsoft-Windows-Power-Troubleshooter" && id == "1" && Time(data.GetValueOrDefault("SleepTime"), out var sleep) && Time(data.GetValueOrDefault("WakeTime"), out var wake) && sleep >= started && wake > sleep)
                wakes.Add((sleep, wake));
        }
        foreach (var wake in wakes)
        {
            if (!entries.Any(entry => Math.Abs((entry - wake.Sleep).TotalSeconds) <= 10)) continue;
            double elapsed = (wake.Wake - wake.Sleep).TotalSeconds;
            if (elapsed < seconds * .95) return new(Outcome.Fail, $"{kind} 实际睡眠仅 {elapsed:0.0} 秒，未达到目标 {seconds} 秒");
            return new(Outcome.Pass, $"系统进入及唤醒事件确认 {kind}，实际睡眠 {elapsed:0.0} 秒");
        }
        return new(Outcome.Uncertain, $"缺少本轮配对的 {kind} 进入/唤醒事件或实际电源状态不符；请检查原始日志");
    }
}
