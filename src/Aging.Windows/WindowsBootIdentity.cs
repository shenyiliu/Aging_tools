using Aging.Core;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Aging.Windows;

public static class WindowsBootIdentity
{
    public static string ReadEvent()
    {
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent() ?? throw new IOException("缺少内核启动事件，无法确认本次开机；禁止自动增加重启周期");
        return record.ToXml();
    }
    public static string Read() => FromEvent(ReadEvent());
    public static string FromEvent(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var root = XDocument.Load(reader).Root;
        var system = root?.Element(ns + "System");
        string? start = root?.Element(ns + "EventData")?.Elements(ns + "Data").FirstOrDefault(x => x.Attribute("Name")?.Value == "StartTime")?.Value;
        if (system?.Element(ns + "Provider")?.Attribute("Name")?.Value != "Microsoft-Windows-Kernel-General" ||
            system.Element(ns + "EventID")?.Value != "12" ||
            !long.TryParse(system.Element(ns + "EventRecordID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long record) || record <= 0 ||
            !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var boot))
            throw new IOException("内核启动事件缺少有效的来源、记录号或启动时间；禁止自动增加重启周期");
        // Persisted event fields remain stable when the wall clock changes. Missing
        // evidence (for example after clearing System) fails closed rather than using a stale fallback.
        return BootSession.EventPrefix + record.ToString(CultureInfo.InvariantCulture) + "|" + boot.UtcTicks.ToString(CultureInfo.InvariantCulture);
    }
}
