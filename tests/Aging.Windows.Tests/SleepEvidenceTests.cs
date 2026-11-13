using Aging.Core;
using Aging.Windows;
using Xunit;

namespace Aging.Windows.Tests;

public class SleepEvidenceTests
{
    private readonly DateTimeOffset started = DateTimeOffset.Parse("2026-11-13T02:20:40Z");
    private static string Entry(string state = "5") => $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>42</EventID><TimeCreated SystemTime='2026-11-13T02:20:44Z'/></System><EventData><Data Name='TargetState'>{state}</Data><Data Name='EffectiveState'>{state}</Data></EventData></Event>";
    private static string Wake(string state = "5", string wake = "02:21:52") => $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Power-Troubleshooter'/><EventID>1</EventID></System><EventData><Data Name='TargetState'>{state}</Data><Data Name='EffectiveState'>{state}</Data><Data Name='SleepTime'>2026-11-13T02:20:42Z</Data><Data Name='WakeTime'>2026-11-13T{wake}Z</Data></EventData></Event>";
    [Fact]
    public void ActualStateAndPairedCurrentEventsAreRequired()
    {
        Assert.Equal(Outcome.Pass, SleepEvidence.Evaluate([Entry(), Wake()], TaskKind.S4, started, 60).Outcome);
        Assert.Equal(Outcome.Uncertain, SleepEvidence.Evaluate([Entry("4"), Wake("4")], TaskKind.S4, started, 60).Outcome);
        Assert.Equal(Outcome.Uncertain, SleepEvidence.Evaluate([Wake()], TaskKind.S4, started, 60).Outcome);
        Assert.Equal(Outcome.Uncertain, SleepEvidence.Evaluate([Entry(), Wake()], TaskKind.S4, started.AddHours(1), 60).Outcome);
        Assert.Equal(Outcome.Fail, SleepEvidence.Evaluate([Entry(), Wake(wake: "02:20:50")], TaskKind.S4, started, 60).Outcome);
        Assert.Equal(Outcome.Uncertain, SleepEvidence.Evaluate([Entry(), Wake().Replace("Microsoft-Windows-Power-Troubleshooter", "Unrelated")], TaskKind.S4, started, 60).Outcome);
    }
}
