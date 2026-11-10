using System.Text;
using System.Text.RegularExpressions;

namespace Aging.Core;

/// <summary>Line-preserving legacy INI editor. Only touched keys/sections are rewritten.</summary>
public sealed class IniDocument
{
    private readonly List<string> lines;
    public Encoding Encoding { get; }
    private readonly string newline;
    private IniDocument(string text, Encoding encoding)
    {
        Encoding = encoding;
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
    }
    public static IniDocument Empty() => new("", new UnicodeEncoding(false, true, true));
    public static IniDocument Parse(string text) => new(text, new UnicodeEncoding(false, true, true));
    public static IniDocument Load(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding;
        int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(true, true); skip = 3; }
        else
        {
            encoding = new UTF8Encoding(false, true);
            try { _ = encoding.GetString(bytes); }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936); }
        }
        return new IniDocument(encoding.GetString(bytes, skip, bytes.Length - skip), encoding);
    }
    private static string? Section(string line)
    {
        var m = Regex.Match(line, @"^\s*\[([^\]]+)\]\s*(?:[;#].*)?$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
    private static (string Key, string Value)? Pair(string line)
    {
        var trim = line.TrimStart();
        if (trim.StartsWith(';') || trim.StartsWith('#')) return null;
        int eq = line.IndexOf('=');
        return eq > 0 ? (line[..eq].Trim(), line[(eq + 1)..].Trim()) : null;
    }
    public IReadOnlyList<KeyValuePair<string, string>> GetSection(string section)
    {
        List<KeyValuePair<string, string>> result = [];
        string current = "";
        foreach (string line in lines)
        {
            if (Section(line) is { } name) { current = name; continue; }
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && Pair(line) is { } pair)
                result.Add(new(pair.Key, pair.Value));
        }
        return result;
    }
    public string Get(string section, string key, string fallback = "") =>
        GetSection(section).LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? fallback;
    public void Set(string section, string key, string value)
    {
        Guard(value); Guard(key);
        string current = "";
        int insert = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (Section(lines[i]) is { } name)
            {
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && insert < 0) insert = i;
                current = name;
                if (name.Equals(section, StringComparison.OrdinalIgnoreCase)) insert = i + 1;
                continue;
            }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            insert = i + 1;
            if (Pair(lines[i]) is { } pair && pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = lines[i][..(lines[i].IndexOf('=') + 1)] + " " + value;
                return;
            }
        }
        if (insert < 0) { lines.Add($"[{section}]"); lines.Add($"{key} = {value}"); }
        else lines.Insert(insert, $"{key} = {value}");
    }
    public void ReplaceSection(string section, IEnumerable<KeyValuePair<string, string>> values)
    {
        string current = "";
        int insert = -1;
        for (int i = 0; i < lines.Count;)
        {
            if (Section(lines[i]) is { } name)
            {
                current = name;
                if (name.Equals(section, StringComparison.OrdinalIgnoreCase)) insert = i + 1;
                i++; continue;
            }
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && Pair(lines[i]) != null)
            { lines.RemoveAt(i); continue; }
            i++;
        }
        if (insert < 0) { lines.Add($"[{section}]"); insert = lines.Count; }
        foreach (var value in values) { Guard(value.Value); lines.Insert(insert++, $"{value.Key} = {value.Value}"); }
    }
    public void Save(string path, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var preamble = Encoding.GetPreamble();
        var body = Encoding.GetBytes(string.Join(newline, lines) + newline);
        AtomicFile.Write(path, [.. preamble, .. body], backup);
    }
    private static void Guard(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new FormatException("INI 字段不能含换行");
    }
}

public static class LegacyConfig
{
    public static List<AgingItem> ReadQueue(IniDocument doc)
    {
        var pairs = doc.GetSection("AgingSwitch");
        if (pairs.Count == 0) throw new FormatException("AgingSwitch 没有配置项目");
        return pairs.Select(x => AgingItem.Parse(x.Value)).ToList();
    }
    public static void WriteQueue(IniDocument doc, IEnumerable<AgingItem> items) => doc.ReplaceSection("AgingSwitch",
        items.Select((x, i) => new KeyValuePair<string, string>(i.ToString(), x.Raw.Length > 0 && AgingItem.Parse(x.Raw) == x ? x.Raw : x.Serialize())));
    public static string Resolve(string root, string legacyPath)
    {
        if (string.IsNullOrWhiteSpace(legacyPath)) return "";
        return Path.GetFullPath(Path.IsPathFullyQualified(legacyPath) ? legacyPath : Path.Combine(root, legacyPath.TrimStart('\\', '/')));
    }
    public static AppOptions ReadOptions(IniDocument doc, string root)
    {
        bool Flag(string key, bool fallback) => doc.Get("Main", key) is { Length: > 0 } value ? AgingItem.LegacyBool(value) : fallback;
        return new AppOptions
        {
            ReferenceRoot = root,
            StopOnFail = Flag("AgingFailedStop", false), ShutdownAfterAging = Flag("ShutdownAfterAging", false),
            AgingInNextBoot = Flag("AgingInNextBoot", false), TestWithMaxVolume = Flag("TestWithMaxVolume", false),
            VideoPath = Resolve(root, doc.Get("Video", "VideoPath")),
            SftPath = Path.Combine(root, "SFT", "SFTClassic.exe"),
            PwrTestPath = Path.Combine(root, "Sleep", "pwrtest_x64.exe"),
            AfterAgingScript = Resolve(root, doc.Get("Main", "AfterAgingScript"))
        };
    }
}
