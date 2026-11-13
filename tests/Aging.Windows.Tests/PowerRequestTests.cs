using Aging.Core;
using Aging.Windows;
using System.Diagnostics;
using Xunit;

namespace Aging.Windows.Tests;

public class PowerRequestTests
{
    private static string Requests()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "/requests" } })!;
        string text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        Assert.Equal(0, process.ExitCode); return text;
    }
    [Fact]
    public void LoadRequestsExistOnlyForTheirSessionAndNeverForPowerTests()
    {
        string reason = "GW-Aging-Scoped-Test-" + Guid.NewGuid().ToString("N");
        using (var request = PowerRequestSession.Start(AgingItem.Parse("BurnIn|0.1|3"), reason))
        {
            Assert.NotNull(request);
            if (WindowsPlatform.IsAdministrator) Assert.Contains(reason, Requests());
        }
        if (WindowsPlatform.IsAdministrator) Assert.DoesNotContain(reason, Requests());
        foreach (string raw in new[] { "Reboot|1|30|N|N|N|N", "S3|1|30|60|N|N|N", "S4|1|30|60|N|N|N", "CS|1|30|60|N|N|N" })
            Assert.Null(PowerRequestSession.Start(AgingItem.Parse(raw)));
    }
}
