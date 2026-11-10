using System.Runtime.InteropServices;
using System.Xml;

namespace Aging.Windows;

public static class LogonResume
{
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
