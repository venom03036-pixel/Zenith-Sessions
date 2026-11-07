using FluentAssertions;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.Core.Tests.Licensing;

public sealed class LicenseInstallPolicyTests
{
    [Fact]
    public void ProductionBuild_RejectsAnyKey()
    {
        var decision = LicenseInstallPolicy.FromUserKey("ENT-WHATEVER", "a@b.c", isDeveloperBuild: false);
        decision.Accepted.Should().BeFalse();
        decision.Message.Should().Be(LicenseInstallPolicy.VerificationUnavailableMessage);
        decision.License.Should().BeNull();
    }

    [Theory]
    [InlineData("PRO-ABC")]
    [InlineData("ENT-XYZ")]
    [InlineData("garbage")]
    public void DeveloperBuild_UnverifiedKeys_GrantNothing(string key)
    {
        var decision = LicenseInstallPolicy.FromUserKey(key, "dev@host", isDeveloperBuild: true);
        decision.Accepted.Should().BeFalse();
        decision.License.Should().BeNull();
    }

    [Fact]
    public void DeveloperBuild_AdminKey_CreatesAdministrator()
    {
        var decision = LicenseInstallPolicy.FromUserKey("ADMIN-MASTER", "dev@host", isDeveloperBuild: true);
        decision.Accepted.Should().BeTrue();
        decision.License!.Type.Should().Be(LicenseType.Administrator);
        decision.License.IsFeatureLicensed(FeatureId.AIVision).Should().BeTrue();
    }
}
