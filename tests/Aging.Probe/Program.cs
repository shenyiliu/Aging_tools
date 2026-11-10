using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Contains("--tree"))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false })!;
    File.WriteAllText(args[1], child.Id.ToString());
    await Task.Delay(Timeout.Infinite);
}
else if (args.Contains("--child")) await Task.Delay(Timeout.Infinite);
else if (args.Contains("--wait")) await Task.Delay(Timeout.Infinite);
else { Console.WriteLine("harmless probe 中文"); Console.Error.WriteLine("diagnostic only"); return args.Contains("--failure") ? 7 : 0; }
return 0;
