using Aging.Core;
using Xunit;

namespace Aging.Tests;

public sealed class RunSelectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gw-aging-selection-qa", Guid.NewGuid().ToString("N"));
    [Fact]
    public void SelectingLoadOnlyDoesNotInheritRebootRequirementsFromExcludedItems()
    {
        var configured = new[] { AgingItem.Parse("Reboot|50|30|N|N|N|N"), AgingItem.Parse("S3|50|30|60|N|N|N"), AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("Video") };
        var options = new AppOptions { SwitchReboot = false };
        var run = RunSelection.Create(configured, [2, 3], options, false, root);
        Assert.False(RunRequirements.RequiresManualRecovery(run.Items.Select(x => x.Item).ToList(), run.Options));
        Assert.Equal(new[] { "BurnIn", "Video" }, run.Items.Select(x => x.Item.Name));
        Assert.Equal(2, run.ExcludedItems.Count); Assert.Equal(4, configured.Length);
    }
    [Fact]
    public void RebootAnywhereInSelectedRunRequiresContinuationEvenWithoutSwitching()
    {
        var queue = new[] { AgingItem.Parse("Video"), AgingItem.Parse("Reboot|1|30|N|N|N|N") };
        Assert.True(RunRequirements.RequiresManualRecovery(queue, new AppOptions { SwitchReboot = false }));
    }
    [Fact]
    public void SwitchingAndNextBootAreIndependentReasonsForContinuation()
    {
        var queue = new[] { AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("Video") };
        Assert.True(RunRequirements.RequiresManualRecovery(queue, new AppOptions { SwitchReboot = true }));
        Assert.False(RunRequirements.RequiresManualRecovery(queue[..1], new AppOptions { SwitchReboot = true }));
        Assert.True(RunRequirements.RequiresManualRecovery(queue[..1], new AppOptions { SwitchReboot = false, AgingInNextBoot = true }));
        Assert.False(RunRequirements.RequiresAdministrator([AgingItem.Parse("Video")], new AppOptions { SwitchReboot = false }));
    }
    [Fact]
    public void SelectionPreservesOriginalOrderAndDuplicateItems()
    {
        var queue = new[] { AgingItem.Parse("BurnIn|0.1|0"), AgingItem.Parse("S3|1|30|60|N|N|N"), AgingItem.Parse("BurnIn|0.2|0") };
        var run = RunSelection.Create(queue, [2, 0, 2], new AppOptions(), true, root);
        Assert.Equal(new[] { 0.1, 0.2 }, run.Items.Select(x => x.Item.Hours));
        Assert.Single(run.ExcludedItems); Assert.Contains("S3", run.ExcludedItems[0]);
    }
    [Fact]
    public void ExclusionsSurviveCheckpointAndAreNotRecordedAsPassedTests()
    {
        var queue = new[] { AgingItem.Parse("S3|1|30|60|N|N|N"), AgingItem.Parse("Video") };
        var run = RunSelection.Create(queue, [1], new AppOptions(), false, root);
        var store = new JsonRunStore(Path.Combine(root, "checkpoint.json")); store.Save(run);
        var restored = store.Load()!;
        Assert.Equal(run.ExcludedItems, restored.ExcludedItems);
        new LegacyLogWriter().Write(restored);
        var log = IniDocument.Load(LegacyLogWriter.LogPath(restored));
        Assert.Equal("SelectedItemsOnly", log.Get("MAIN", "ExecutedScope"));
        Assert.Equal("1", log.Get("MAIN", "ExcludedCount"));
        Assert.Equal("N", log.Get("MAIN", "HardwarePassed"));
        Assert.DoesNotContain("S3_Info", File.ReadAllText(LegacyLogWriter.LogPath(restored)));
    }
    [Fact]
    public void EmptyAndOutOfRangeSelectionsCannotStart()
    {
        var queue = new[] { AgingItem.Parse("Video") };
        Assert.Throws<InvalidOperationException>(() => RunSelection.Create(queue, [], new AppOptions(), false, root));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunSelection.Create(queue, [1], new AppOptions(), false, root));
    }
    [Fact]
    public void LegacyLoginOptionsAndCheckpointRemainReadableWithoutRequiringLogin()
    {
        var options = System.Text.Json.JsonSerializer.Deserialize<AppOptions>("""{"ResumeAtLogon":true,"SwitchReboot":false,"AgingInNextBoot":true}""", JsonData.Options)!;
        var run = AgingCoordinator.Create([AgingItem.Parse("Video")], options, false, root);
        run.Phase = RunPhase.AwaitingReboot; run.PendingOperation = "start"; run.ExpectedBootId = "prior-boot";
        var payload = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(run, JsonData.Options))!;
        payload["Options"]!["ResumeAtLogon"] = true;
        string legacy = payload.ToJsonString();
        string checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy)));
        Directory.CreateDirectory(root); string path = Path.Combine(root, "legacy.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Revision = run.Revision, Sha256 = checksum, Payload = legacy }));
        var restored = new JsonRunStore(path).Load()!;
        Assert.Equal(RunPhase.AwaitingReboot, restored.Phase); Assert.Equal("start", restored.PendingOperation);
        Assert.True(restored.Options.AgingInNextBoot); Assert.False(restored.Options.SwitchReboot);
        Assert.DoesNotContain("ResumeAtLogon", System.Text.Json.JsonSerializer.Serialize(restored.Options));
        Assert.True(RunRequirements.RequiresAdministrator([AgingItem.Parse("Video")], options));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
