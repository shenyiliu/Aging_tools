using Aging.Core;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class BootIdentityTests
{
    private static string Event(int record, string time) => $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-General'/><EventID>12</EventID><EventRecordID>{record}</EventRecordID></System><EventData><Data Name='StartTime'>{time}</Data></EventData></Event>";
    [Fact]
    public void PersistedKernelStartSeparatesBootsAndRejectsLegacyOrMalformedEvidence()
    {
        string before = WindowsBootIdentity.FromEvent(Event(100, "2026-11-13T01:03:28Z"));
        string after = WindowsBootIdentity.FromEvent(Event(200, "2026-11-13T03:01:59Z"));
        Assert.True(BootSession.HasChanged(before, after));
        Assert.False(BootSession.HasChanged(after, after));
        Assert.False(BootSession.HasChanged("old-guid", after));
        Assert.False(BootSession.HasChanged("", after));
        Assert.False(BootSession.HasChanged(BootSession.EventPrefix + "broken", after));
        Assert.Throws<IOException>(() => WindowsBootIdentity.FromEvent(Event(0, "2026-11-13T03:01:59Z")));
        Assert.Throws<IOException>(() => WindowsBootIdentity.FromEvent(Event(100, "invalid")));
        Assert.Throws<IOException>(() => WindowsBootIdentity.FromEvent(Event(100, "2026-11-13T03:01:59Z").Replace("Kernel-General", "Other")));
    }
}
