using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml;

namespace Aging.Windows;

public static class LogonResume
{
    private static string TaskName
    {
        get { using var user = WindowsIdentity.GetCurrent(); return "GW_AgingTool_" + user.User!.Value; }
    }
    public static void Register(string executable, string dataRoot)
    {
        using var user = WindowsIdentity.GetCurrent();
        string sid = user.User!.Value;
        var xml = new StringBuilder();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            writer.WriteStartElement("Task", ns); writer.WriteAttributeString("version", "1.2");
            writer.WriteStartElement("Triggers"); writer.WriteStartElement("LogonTrigger");
            writer.WriteElementString("Enabled", "true"); writer.WriteElementString("UserId", sid); writer.WriteElementString("Delay", "PT15S"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("Principals"); writer.WriteStartElement("Principal"); writer.WriteAttributeString("id", "Author");
            writer.WriteElementString("UserId", sid); writer.WriteElementString("LogonType", "InteractiveToken"); writer.WriteElementString("RunLevel", "HighestAvailable"); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("Settings"); writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew"); writer.WriteElementString("DisallowStartIfOnBatteries", "false");
            writer.WriteElementString("StopIfGoingOnBatteries", "false"); writer.WriteElementString("ExecutionTimeLimit", "PT0S"); writer.WriteEndElement();
            writer.WriteStartElement("Actions"); writer.WriteAttributeString("Context", "Author"); writer.WriteStartElement("Exec");
            writer.WriteElementString("Command", executable); writer.WriteElementString("Arguments", "--resume --data \"" + dataRoot + "\"");
            writer.WriteElementString("WorkingDirectory", Path.GetDirectoryName(executable));
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null; dynamic? task = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\");
            task = folder.RegisterTask(TaskName, xml.ToString(), 6, sid, null, 3, null);
        }
        finally
        {
            if (task != null) Marshal.FinalReleaseComObject(task);
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }
    public static bool IsRegisteredFor(string executable, string dataRoot)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null; dynamic? task = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\"); task = folder.GetTask(TaskName);
            var doc = new XmlDocument(); doc.LoadXml((string)task.Xml);
            var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", doc.DocumentElement!.NamespaceURI);
            return (bool)task.Enabled && doc.SelectSingleNode("//t:Command", ns)?.InnerText.Equals(executable, StringComparison.OrdinalIgnoreCase) == true
                && doc.SelectSingleNode("//t:Arguments", ns)?.InnerText == "--resume --data \"" + dataRoot + "\"";
        }
        catch (COMException) { return false; }
        finally { if (task != null) Marshal.FinalReleaseComObject(task); if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static void Remove()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null;
        try { service.Connect(); folder = service.GetFolder("\\"); try { folder.DeleteTask(TaskName, 0); } catch (COMException e) when ((uint)e.HResult == 0x80070002) { } }
        finally { if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
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
