using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace Aging.Windows;

public static class LogonResume
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildTaskXml(ResumeTaskIdentity identity)
    {
        XNamespace ns = TaskNamespace;
        var document = new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Author", identity.UserSid),
                new XElement(ns + "Description", "GW Aging Tool automatic continuation; run " + identity.RunId.ToString("D") + "; version " + identity.Version),
                new XElement(ns + "URI", "\\" + identity.TaskName)),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", true),
                new XElement(ns + "UserId", identity.UserSid), new XElement(ns + "Delay", "PT5S"))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(ns + "UserId", identity.UserSid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "AllowHardTerminate", true), new XElement(ns + "StartWhenAvailable", true),
                new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                new XElement(ns + "IdleSettings", new XElement(ns + "StopOnIdleEnd", false), new XElement(ns + "RestartOnIdle", false)),
                new XElement(ns + "AllowStartOnDemand", true), new XElement(ns + "Enabled", true),
                new XElement(ns + "Hidden", false), new XElement(ns + "RunOnlyIfIdle", false),
                new XElement(ns + "WakeToRun", false), new XElement(ns + "ExecutionTimeLimit", "PT0S"), new XElement(ns + "Priority", 4)),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec",
                new XElement(ns + "Command", identity.Executable), new XElement(ns + "Arguments", identity.Arguments),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(identity.Executable)!)))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static void Register(ResumeTaskIdentity identity)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.User?.Value != identity.UserSid) throw new InvalidOperationException("只能为当前 Windows 用户注册续测任务");
        if (!File.Exists(identity.Executable)) throw new FileNotFoundException("续测程序不存在", identity.Executable);
        if (!Directory.Exists(identity.DataRoot)) throw new DirectoryNotFoundException("续测数据目录不存在：" + identity.DataRoot);
        WithFolder(folder =>
        {
            dynamic? existing = GetTask(folder, identity.TaskName);
            try
            {
                if (existing != null && !Matches((string)existing!.Xml, identity, false)) throw new InvalidOperationException("同名计划任务与当前续测绑定不匹配");
            }
            finally { Release(existing); }
            dynamic registered = folder.RegisterTask(identity.TaskName, BuildTaskXml(identity), 6, identity.UserSid, null, 3, null);
            try
            {
                if (!(bool)registered.Enabled || !Matches((string)registered.Xml, identity)) throw new IOException("Windows 未保存完整续测计划任务");
            }
            finally { Release(registered); }
            return true;
        });
        if (!Verify(identity)) throw new IOException("续测计划任务注册后验证失败");
    }

    public static bool Verify(ResumeTaskIdentity identity) => WithFolder(folder =>
    {
        dynamic? task = GetTask(folder, identity.TaskName);
        try { return task != null && (bool)task!.Enabled && Matches((string)task!.Xml, identity); }
        finally { Release(task); }
    });

    public static void Remove(ResumeTaskIdentity identity) => WithFolder(folder =>
    {
        dynamic? task = GetTask(folder, identity.TaskName);
        try
        {
            if (task == null) return false;
            if (!Matches((string)task.Xml, identity, false)) throw new IOException("拒绝删除与当前续测绑定不匹配的计划任务");
        }
        finally { Release(task); }
        folder.DeleteTask(identity.TaskName, 0); return true;
    });

    private static bool Matches(string xml, ResumeTaskIdentity identity, bool requireEnabled = true)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        XNamespace ns = TaskNamespace;
        if (root?.Name != ns + "Task") return false;
        var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
        var principals = root.Element(ns + "Principals")?.Elements().ToArray();
        var actions = root.Element(ns + "Actions")?.Elements().ToArray();
        if (triggers?.Length != 1 || triggers[0].Name != ns + "LogonTrigger" || principals?.Length != 1 || actions?.Length != 1 || actions[0].Name != ns + "Exec") return false;
        return root.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value == "\\" + identity.TaskName &&
            SameUser(triggers[0].Element(ns + "UserId")?.Value, identity.UserSid) && (!requireEnabled || IsEnabled(triggers[0], ns)) &&
            SameUser(principals[0].Element(ns + "UserId")?.Value, identity.UserSid) && principals[0].Element(ns + "LogonType")?.Value == "InteractiveToken" &&
            principals[0].Element(ns + "RunLevel")?.Value == "HighestAvailable" &&
            ResumeTaskIdentity.SamePath(actions[0].Element(ns + "Command")?.Value ?? "", identity.Executable) &&
            actions[0].Element(ns + "Arguments")?.Value == identity.Arguments &&
            ResumeTaskIdentity.SamePath(actions[0].Element(ns + "WorkingDirectory")?.Value ?? "", Path.GetDirectoryName(identity.Executable)!) &&
            (!requireEnabled || IsEnabled(root.Element(ns + "Settings"), ns)) &&
            root.Element(ns + "Settings")?.Element(ns + "MultipleInstancesPolicy")?.Value == "IgnoreNew";
    }

    // Task Scheduler serializes omitted boolean defaults and may canonicalize a trigger SID to DOMAIN\\user.
    private static bool IsEnabled(XElement? element, XNamespace ns) => element?.Element(ns + "Enabled")?.Value is null or "true" or "1";
    private static bool SameUser(string? actual, string sid)
    {
        if (actual == sid) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;
        try { return ((SecurityIdentifier)new NTAccount(actual).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static dynamic? GetTask(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (Exception exception) when (exception.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { return null; }
    }

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null;
        try { service.Connect(); folder = service.GetFolder("\\"); return action(folder); }
        finally { Release(folder); Release(service); }
    }

    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    public static void RemoveForInstallation(string directory)
    {
        string expected = Path.GetFullPath(Path.Combine(directory, "Test.exe"));
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null; dynamic? tasks = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\"); tasks = folder.GetTasks(1);
            var names = new List<string>();
            for (int i = 1; i <= (int)tasks.Count; i++)
            {
                dynamic task = tasks.Item(i);
                try
                {
                    string name = task.Name;
                    if (!name.StartsWith("GW_AgingTool_", StringComparison.Ordinal)) continue;
                    var doc = new XmlDocument(); doc.LoadXml((string)task.Xml);
                    var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", doc.DocumentElement!.NamespaceURI);
                    var commands = doc.SelectNodes("//t:Actions/t:Exec/t:Command", ns);
                    if (commands?.Cast<XmlNode>().Any(node => string.Equals(node.InnerText, expected, StringComparison.OrdinalIgnoreCase)) == true) names.Add(name);
                }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            foreach (string name in names) folder.DeleteTask(name, 0);
        }
        finally
        {
            if (tasks != null) Marshal.FinalReleaseComObject(tasks);
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }
}
