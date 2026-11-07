using FluentAssertions;
using MacroSuite.Core.Input;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class ForegroundProcessPolicyTests
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("cmd")]
    [InlineData("wt")]
    [InlineData("WindowsTerminal")]
    [InlineData("rundll32")]
    public void BlockedProcessNames_AreRejected(string name)
        => ForegroundProcessPolicy.IsBlockedProcessName(name).Should().BeTrue();

    [Theory]
    [InlineData("RobloxPlayerBeta")]
    [InlineData("chrome")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("explorer")]
    public void GameAndDesktopProcessNames_AreAllowed(string? name)
        => ForegroundProcessPolicy.IsBlockedProcessName(name).Should().BeFalse();

    [Fact]
    public void HigherIntegrity_IsDetectedAgainstMediumSelf()
        => ForegroundProcessPolicy.IsHigherIntegrity(0x3000, ForegroundProcessPolicy.MediumIntegrityRid).Should().BeTrue();

    [Fact]
    public void SameIntegrity_IsAllowed()
        => ForegroundProcessPolicy.IsHigherIntegrity(0x2000, ForegroundProcessPolicy.MediumIntegrityRid).Should().BeFalse();
}
