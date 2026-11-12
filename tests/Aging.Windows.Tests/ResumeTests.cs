using Aging.Core;
using Aging.Windows;
using System.Security.Principal;
using System.Xml.Linq;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class ResumeTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private static ResumeTaskIdentity Identity(Guid? runId = null) => ResumeTaskIdentity.Create(runId ?? Guid.NewGuid(), @"C:\Program Files\GW Tool\Test.exe", @"C:\Users\tester\AppData\Local\GW & Tool", Sid);

    private static RunSnapshot PendingRun()
    {
        var run = AgingCoordinator.Create([AgingItem.Parse("Reboot|2|30|N|N|N|N")], new AppOptions { SwitchReboot = false }, false, @"C:\Logs");
        run.Phase = RunPhase.AwaitingReboot; run.PendingOperation = "cycle"; run.ExpectedBootId = "previous-boot";
        run.OperationId = Guid.NewGuid(); run.OperationStartedAt = DateTimeOffset.Now;
        Identity(run.RunId).BindTo(run); return run;
    }

    private static ResumeDecision Evaluate(RunSnapshot run, string bootId = "new-boot", Guid? runId = null, int version = 1,
        string executable = @"C:\Program Files\GW Tool\Test.exe", string data = @"C:\Users\tester\AppData\Local\GW & Tool", string sid = Sid)
        => ResumePolicy.EvaluateAutomaticResume(run, runId ?? run.RunId, version, executable, data, sid, bootId);

    [Fact]
    public void LoginTaskContainsBoundRunAndCurrentUserWithoutAPassword()
    {
        var identity = Identity(); var xml = XDocument.Parse(LogonResume.BuildTaskXml(identity));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(Sid, xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("HighestAvailable", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal(identity.Executable, xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal(identity.Arguments, xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Contains("--resume-run " + identity.RunId.ToString("D"), identity.Arguments);
        Assert.Contains("--resume-version 1", identity.Arguments);
        Assert.Equal("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.DoesNotContain(xml.Descendants(), x => x.Name.LocalName.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaskNameSeparatesUserInstallationDataAndRun()
    {
        Guid runId = Guid.NewGuid(); var identity = Identity(runId);
        Assert.Equal(identity.TaskName, Identity(runId).TaskName);
        Assert.NotEqual(identity.TaskName, Identity().TaskName);
        Assert.NotEqual(identity.TaskName, ResumeTaskIdentity.Create(runId, identity.Executable, @"C:\OtherData", Sid).TaskName);
        Assert.NotEqual(identity.TaskName, ResumeTaskIdentity.Create(runId, identity.Executable, identity.DataRoot, "S-1-5-21-100-200-300-1002").TaskName);
    }

    [Fact]
    public void OnlyANewBootCanAutomaticallyResumeABoundRun()
    {
        var run = PendingRun();
        Assert.True(Evaluate(run).CanResume);
        Assert.False(Evaluate(run, "previous-boot").CanResume);
        Assert.Equal(0, run.Items[0].CompletedCycles);
        Assert.False(Evaluate(run, "").CanResume);
        run.Phase = RunPhase.Running;
        Assert.False(Evaluate(run).CanResume);
    }

    [Fact]
    public void OldOrMismatchedCheckpointsRequireManualRecovery()
    {
        var run = PendingRun();
        Assert.False(Evaluate(run, runId: Guid.NewGuid()).CanResume);
        Assert.False(Evaluate(run, version: 2).CanResume);
        Assert.False(Evaluate(run, executable: @"C:\Other\Test.exe").CanResume);
        Assert.False(Evaluate(run, data: @"C:\OtherData").CanResume);
        Assert.False(Evaluate(run, sid: "S-1-5-21-100-200-300-1002").CanResume);
        run.ResumeVersion = 0;
        Assert.False(Evaluate(run).CanResume);
    }

    [Fact]
    public void MissingOperationAndTerminalStateCannotBeAutoResumed()
    {
        var run = PendingRun(); run.OperationId = Guid.Empty;
        Assert.False(Evaluate(run).CanResume);
        run = PendingRun(); run.OperationStartedAt = null;
        Assert.False(Evaluate(run).CanResume);
        run = PendingRun(); run.Phase = RunPhase.Completed;
        Assert.False(Evaluate(run).CanResume);
        run = PendingRun(); run.PendingOperation = "unknown";
        Assert.False(Evaluate(run).CanResume);
    }

    [Fact]
    public void ContinuationIsOnlyRegisteredForRealQueuesThatMayReboot()
    {
        var run = PendingRun(); Assert.True(ResumePolicy.NeedsRegistration(run));
        run.Simulation = true; Assert.False(ResumePolicy.NeedsRegistration(run));
        run.Simulation = false; run.Phase = RunPhase.Aborted; Assert.False(ResumePolicy.NeedsRegistration(run));
        run = AgingCoordinator.Create([AgingItem.Parse("BurnIn|0.1|0")], new AppOptions { SwitchReboot = false }, false, @"C:\Logs");
        Assert.False(ResumePolicy.NeedsRegistration(run));
        run.Options.AgingInNextBoot = true; Assert.True(ResumePolicy.NeedsRegistration(run));
        run.Options.AgingInNextBoot = false; run.Options.SwitchReboot = true; run.Items.Add(new ItemRun { Item = AgingItem.Parse("Video") });
        Assert.True(ResumePolicy.NeedsRegistration(run));
    }

    [Fact]
    public void IdentityRejectsRelativePathsAndEmptyRunIds()
    {
        Assert.Throws<ArgumentException>(() => ResumeTaskIdentity.Create(Guid.Empty, @"C:\Test.exe", @"C:\Data", Sid));
        Assert.Throws<ArgumentException>(() => ResumeTaskIdentity.Create(Guid.NewGuid(), "Test.exe", @"C:\Data", Sid));
        Assert.Throws<ArgumentException>(() => ResumeTaskIdentity.Create(Guid.NewGuid(), @"C:\Test.exe", "Data", Sid));
        Assert.Throws<ArgumentException>(() => ResumeTaskIdentity.Create(Guid.NewGuid(), @"C:\Test.exe", @"C:\Data", "user"));
    }

    [LogonTaskIntegrationFact]
    public void CurrentUserTaskCanBeRegisteredVerifiedAndRemovedWithoutStartingIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "gw-aging-resume", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var user = WindowsIdentity.GetCurrent();
        string probe = Path.Combine(AppContext.BaseDirectory, "probe", "Aging.Probe.exe");
        var identity = ResumeTaskIdentity.Create(Guid.NewGuid(), probe, root, user.User!.Value);
        try
        {
            Assert.False(LogonResume.Verify(identity));
            LogonResume.Register(identity); Assert.True(LogonResume.Verify(identity));
            LogonResume.Register(identity); Assert.True(LogonResume.Verify(identity));
            LogonResume.Remove(identity); Assert.False(LogonResume.Verify(identity));
            LogonResume.Remove(identity);
        }
        finally { LogonResume.Remove(identity); Directory.Delete(root, true); }
    }
}

public sealed class LogonTaskIntegrationFactAttribute : FactAttribute
{
    public LogonTaskIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GW_AGING_TEST_LOGON_TASK") != "1") Skip = "Set GW_AGING_TEST_LOGON_TASK=1 to register and remove an isolated current-user task without running it.";
    }
}
