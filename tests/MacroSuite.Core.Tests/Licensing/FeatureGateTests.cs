using FluentAssertions;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.Core.Tests.Licensing;

public sealed class FeatureGateTests
{
    private sealed class MockLicenseService : ILicenseService
    {
        private LicenseInfo _license;

        public event EventHandler<LicenseChangedEventArgs>? LicenseChanged;

        public MockLicenseService(LicenseInfo license)
        {
            _license = license;
        }

        public LicenseInfo GetCurrentLicense() => _license;

        public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(_license.IsValid ? LicenseValidationResult.Success(_license) : LicenseValidationResult.Failure("Expired"));

        public bool IsFeatureLicensed(FeatureId feature) => _license.IsFeatureLicensed(feature);

        public void SetLicense(LicenseInfo newLicense)
        {
            var old = _license;
            _license = newLicense;
            LicenseChanged?.Invoke(this, new LicenseChangedEventArgs(old, newLicense));
        }
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_ForLicensedFeature()
    {
        var license = LicenseInfo.CreateFree();
        var service = new MockLicenseService(license);
        var gate = new FeatureGate(service);

        gate.IsAvailable(FeatureId.BasicMacroRecording).Should().BeTrue();
        gate.IsAvailable(FeatureId.BasicPlayback).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_ForUnlicensedFeature()
    {
        var license = LicenseInfo.CreateFree();
        var service = new MockLicenseService(license);
        var gate = new FeatureGate(service);

        gate.IsAvailable(FeatureId.AIVision).Should().BeFalse();
        gate.IsAvailable(FeatureId.PluginExecution).Should().BeFalse();
        gate.IsAvailable(FeatureId.ImageRecognition).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenLicenseExpired()
    {
        var expiredLicense = new LicenseInfo
        {
            LicenseKey = "EXPIRED-KEY",
            Type = LicenseType.Enterprise,
            ExpirationUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LicensedFeatures = new HashSet<FeatureId>(Enum.GetValues<FeatureId>())
        };

        var service = new MockLicenseService(expiredLicense);
        var gate = new FeatureGate(service);

        gate.IsAvailable(FeatureId.BasicMacroRecording).Should().BeFalse();
        gate.IsAvailable(FeatureId.AIVision).Should().BeFalse();
    }

    [Fact]
    public void Require_DoesNotThrow_WhenFeatureIsLicensed()
    {
        var license = LicenseInfo.CreateEnterprise("ENT-123", "Acme Corp");
        var service = new MockLicenseService(license);
        var gate = new FeatureGate(service);

        var act = () => gate.Require(FeatureId.AIVision);
        act.Should().NotThrow();
    }

    [Fact]
    public void Require_ThrowsFeatureNotLicensedException_WhenFeatureIsNotLicensed()
    {
        var license = LicenseInfo.CreateFree();
        var service = new MockLicenseService(license);
        var gate = new FeatureGate(service);

        var act = () => gate.Require(FeatureId.AIVision);
        act.Should().Throw<FeatureNotLicensedException>()
            .Which.Feature.Should().Be(FeatureId.AIVision);
    }

    [Fact]
    public void PredefinedTiers_GrantExpectedFeatures()
    {
        var free = LicenseInfo.CreateFree();
        free.IsFeatureLicensed(FeatureId.BasicMacroRecording).Should().BeTrue();
        free.IsFeatureLicensed(FeatureId.BasicPlayback).Should().BeTrue();
        free.IsFeatureLicensed(FeatureId.ColorDetection).Should().BeFalse();

        var pro = LicenseInfo.CreatePro("PRO-1", "Power User");
        pro.IsFeatureLicensed(FeatureId.ColorDetection).Should().BeTrue();
        pro.IsFeatureLicensed(FeatureId.ImageRecognition).Should().BeTrue();
        pro.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();

        var ent = LicenseInfo.CreateEnterprise("ENT-1", "Enterprise User");
        ent.IsFeatureLicensed(FeatureId.AIVision).Should().BeTrue();
        ent.IsFeatureLicensed(FeatureId.PluginExecution).Should().BeTrue();
        ent.IsFeatureLicensed(FeatureId.AuditLog).Should().BeTrue();
    }
}
