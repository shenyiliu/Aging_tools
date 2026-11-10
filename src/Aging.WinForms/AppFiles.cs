using Aging.Core;
using System.Text.Json;

namespace Aging.WinForms;

public sealed class AppFiles
{
    public string Root { get; }
    public string ConfigPath => Path.Combine(Root, "Other", "Config.ini");
    public string OptionsPath => Path.Combine(Root, "options.json");
    public string CheckpointPath => Path.Combine(Root, "active-run.json");
    public string LogsRoot => Path.Combine(Root, "LOG");
    public AppOptions Options { get; private set; } = new();
    public List<AgingItem> Queue { get; private set; } = [];
    public AppFiles(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        if (!File.Exists(ConfigPath)) Initialize();
        Reload();
    }
    private void Initialize()
    {
        var doc = IniDocument.Empty();
        string reference = FindReference();
        var config = Path.Combine(reference, "Other", "Config.ini");
        List<AgingItem> items;
        if (File.Exists(config))
        {
            var original = IniDocument.Load(config);
            items = LegacyConfig.ReadQueue(original);
            Options = LegacyConfig.ReadOptions(original, reference);
            Options.AfterAgingScript = ""; // Legacy factory cleanup is outside this product. Explicitly set a new completion action.
        }
        else
        {
            items = [AgingItem.Parse("Reboot|3|30|N|N|N|N"), AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("Video")];
            var found = reference.Length > 0 ? DiscoverTools(reference) : (Power: "", Video: "", Sft: "");
            Options = new AppOptions { ReferenceRoot = reference, PwrTestPath = found.Power, SftPath = found.Sft, VideoPath = found.Video, SwitchReboot = false };
        }
        LegacyConfig.WriteQueue(doc, items); doc.Save(ConfigPath);
        SaveOptions();
    }
    private static string FindReference()
    {
        // Prefer this application's owned copy, including development checkouts.
        string bundled = FindBundledTools();
        if (bundled.Length > 0) return bundled;
        // Installed machines must never inherit paths from a build/developer directory tree.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.json")))
            return Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Tools")) ? Path.Combine(AppContext.BaseDirectory, "Tools") : "";
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(origin);
            for (int i = 0; dir != null && i < 9; i++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "GW_TEST"))) return Path.Combine(dir.FullName, "GW_TEST");
        }
        return "";
    }
    public static string FindBundledTools()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        bool installed = File.Exists(Path.Combine(dir.FullName, "installed.json"));
        for (int i = 0; dir != null && i < (installed ? 1 : 9); i++, dir = dir.Parent)
        {
            string tools = Path.Combine(dir.FullName, "Tools");
            if (File.Exists(Path.Combine(tools, "reuse-manifest.json"))) return tools;
        }
        return "";
    }
    public static (string Power, string Video, string Sft) DiscoverTools(string root) =>
        (Existing(root, "Sleep", "pwrtest_x64.exe"), Existing(root, "SFT", "TestVideo.mp4"), Existing(root, "SFT", "SFTClassic.exe"));
    private static string Existing(string root, params string[] parts)
    { string path = Path.Combine([root, .. parts]); return File.Exists(path) ? Path.GetFullPath(path) : ""; }
    public static string DiscoverBurnIn(string root) => Existing(root, "BurnInTest", "BurnInTest", "bit.exe");
    public void Reload()
    {
        Options = File.Exists(OptionsPath) ? JsonSerializer.Deserialize<AppOptions>(File.ReadAllBytes(OptionsPath), JsonData.Options) ?? throw new InvalidDataException("设置为空") : new();
        string bundled = FindBundledTools();
        if (bundled.Length > 0 && (Options.ReferenceRoot.Length == 0 || !Directory.Exists(Options.ReferenceRoot)))
        {
            Options.ReferenceRoot = bundled;
            var found = DiscoverTools(bundled);
            Options.PwrTestPath = found.Power; Options.VideoPath = found.Video; Options.SftPath = found.Sft;
            Options.BurnInPath = DiscoverBurnIn(bundled);
        }
        else if (bundled.Length > 0 && !Path.GetFullPath(Options.ReferenceRoot).Equals(Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase))
        {
            // Adopt the shipped copy when it is byte-identical to the reference engine.
            // Keep unrelated explicit tool overrides, while moving paths rooted in that suite.
            string oldRoot = Options.ReferenceRoot;
            string oldEngine = DiscoverBurnIn(oldRoot), ownEngine = DiscoverBurnIn(bundled);
            if (oldEngine.Length > 0 && ownEngine.Length > 0 &&
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(oldEngine)).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownEngine))))
            {
                string MovePath(string path)
                {
                    if (path.Length == 0 || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(oldRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
                    string candidate = Path.Combine(bundled, Path.GetRelativePath(oldRoot, path));
                    return File.Exists(candidate) ? candidate : path;
                }
                Options.PwrTestPath = MovePath(Options.PwrTestPath); Options.BurnInPath = MovePath(Options.BurnInPath);
                Options.VideoPath = MovePath(Options.VideoPath); Options.SftPath = MovePath(Options.SftPath);
                Options.ReferenceRoot = bundled;
            }
        }
        if (Options.BurnInPath.Length == 0 && Options.ReferenceRoot.Length > 0) Options.BurnInPath = DiscoverBurnIn(Options.ReferenceRoot);
        Queue = LegacyConfig.ReadQueue(IniDocument.Load(ConfigPath));
    }
    public void Save(List<AgingItem> queue, AppOptions options)
    {
        foreach (var item in queue) item.Validate();
        if (queue.Count == 0) throw new FormatException("队列不能为空");
        if (options.ShutdownAfterAging && queue.Any(x => x.Kind == TaskKind.Video)) throw new FormatException("视频与完成后关机不能同时启用");
        var doc = IniDocument.Load(ConfigPath);
        LegacyConfig.WriteQueue(doc, queue);
        doc.Set("Main", "AgingFailedStop", options.StopOnFail ? "Y" : "N");
        doc.Set("Main", "ShutdownAfterAging", options.ShutdownAfterAging ? "Y" : "N");
        doc.Set("Main", "AgingInNextBoot", options.AgingInNextBoot ? "Y" : "N");
        doc.Set("Main", "TestWithMaxVolume", options.TestWithMaxVolume ? "Y" : "N");
        doc.Set("Video", "VideoPath", options.VideoPath);
        doc.Save(ConfigPath);
        Queue = queue; Options = options; SaveOptions();
    }
    public void SaveOptions() => AtomicFile.Write(OptionsPath, JsonSerializer.SerializeToUtf8Bytes(Options, JsonData.Options));
    public void Import(string path)
    {
        if (Path.GetFullPath(path).Equals(ConfigPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择原版配置文件");
        var doc = IniDocument.Load(path);
        var queue = LegacyConfig.ReadQueue(doc); // Validate before changing the working copy.
        string root = Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName ?? Path.GetDirectoryName(path)!;
        var options = LegacyConfig.ReadOptions(doc, root);
        options.AfterAgingScript = "";
        AtomicFile.Write(ConfigPath, File.ReadAllBytes(path)); // Unknown keys/comments/encoding retained in private user data.
        Options = options; Queue = queue; SaveOptions();
    }
}
