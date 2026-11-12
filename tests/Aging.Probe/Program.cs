using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Contains("--launcher"))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--finite-child", args[1] }, UseShellExecute = false })!;
}
else if (args.Contains("--launcher-tree"))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false })!;
    File.WriteAllText(args[1], child.Id.ToString());
}
else if (args.Contains("--finite-child"))
{
    await Task.Delay(1200);
    Console.WriteLine("finite child completed"); Console.Error.WriteLine("finite child diagnostic");
    File.WriteAllText(args[1], "real child completed");
}
else if (args.Contains("--echo-args")) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(args.Skip(1)));
else if (args.Contains("--tree"))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false })!;
    File.WriteAllText(args[1], child.Id.ToString());
    await Task.Delay(Timeout.Infinite);
}
else if (args.Contains("--child")) await Task.Delay(Timeout.Infinite);
else if (args.Contains("--wait")) await Task.Delay(Timeout.Infinite);
else { Console.WriteLine("harmless probe 中文"); Console.Error.WriteLine("diagnostic only"); return args.Contains("--failure") ? 7 : 0; }
return 0;
