using Aging.Core;
using Xunit;

namespace Aging.Tests;

public sealed class SimulationReportTests
{
    [Fact]
    public void SuccessfulSimulationIsNeverReportedAsHardwarePass()
    {
        string root = Path.Combine(Path.GetTempPath(), "gw-simulation-report", Guid.NewGuid().ToString("N"));
        try
        {
            var run = AgingCoordinator.Create([AgingItem.Parse("Video")], new AppOptions(), true, root);
            run.Outcome = Outcome.Pass; run.Phase = RunPhase.Completed;
            run.Items[0].Outcome = Outcome.Pass; run.Items[0].Phase = RunPhase.Completed;
            new LegacyLogWriter().Write(run);
            var doc = IniDocument.Load(LegacyLogWriter.LogPath(run));
            Assert.Equal("Simulated", doc.Get("MAIN", "Outcome"));
            Assert.Equal("Simulated", doc.Get("1_Video_Info", "Outcome"));
            Assert.Equal("Y", doc.Get("MAIN", "Simulation"));
            Assert.False(run.HardwarePassed);
            Assert.Equal("N", doc.Get("MAIN", "HardwarePassed"));
            run.Simulation = false; new LegacyLogWriter().Write(run);
            Assert.Equal("Pass", IniDocument.Load(LegacyLogWriter.LogPath(run)).Get("MAIN", "Outcome"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
