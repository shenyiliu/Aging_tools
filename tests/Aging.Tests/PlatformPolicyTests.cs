using Aging.Core;
using Xunit;

namespace Aging.Tests;

public sealed class PlatformPolicyTests
{
    [Theory]
    [InlineData(10, 19045, "X64", true, true)]
    [InlineData(10, 22000, "X64", true, true)]
    [InlineData(10, 26200, "X64", true, true)]
    [InlineData(10, 19044, "X64", true, false)]
    [InlineData(6, 7601, "X64", true, false)]
    [InlineData(10, 26200, "X86", true, false)]
    [InlineData(10, 26200, "Arm64", true, false)]
    [InlineData(10, 20348, "X64", false, false)]
    public void DeploymentRangeIsExplicit(int major, int build, string architecture, bool client, bool supported)
    { Assert.Equal(supported, PlatformPolicy.Evaluate(new Version(major, 0, build), architecture, client).Count == 0); }
}
