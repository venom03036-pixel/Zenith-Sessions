using FluentAssertions;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.Core.Tests.Licensing;

public sealed class SignedLicenseVerifierTests
{
    [Fact]
    public void SignAndVerify_RoundTripsExplicitEntitlements()
    {
        var (pub, priv) = SignedLicenseVerifier.CreateTestKeyPair();
        var payload = new SignedLicensePayload
        {
            SchemaVersion = SignedLicenseVerifier.CurrentSchemaVersion,
            LicenseId = "LIC-1",
            Edition = "Enterprise",
            Entitlements = ["AIVision", "AutonomousMode"],
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpirationUtc = DateTimeOffset.UtcNow.AddDays(1),
            CustomerId = "corp"
        };
        string signature = SignedLicenseVerifier.Sign(payload, priv);
        var info = SignedLicenseVerifier.AttachSignature(payload, signature);

        SignedLicenseVerifier.TryVerify(info, pub, expectedMachine: null, out string? error).Should().BeTrue(error);
        info.IsFeatureLicensed(FeatureId.AIVision).Should().BeTrue();
        info.IsFeatureLicensed(FeatureId.PluginExecution).Should().BeFalse();
    }

    [Fact]
    public void TamperedEntitlementMap_FailsVerify()
    {
        var (pub, priv) = SignedLicenseVerifier.CreateTestKeyPair();
        var payload = new SignedLicensePayload
        {
            SchemaVersion = 1,
            LicenseId = "LIC-2",
            Edition = "Pro",
            Entitlements = ["ColorDetection"],
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpirationUtc = DateTimeOffset.UtcNow.AddDays(1)
        };
        var info = SignedLicenseVerifier.AttachSignature(payload, SignedLicenseVerifier.Sign(payload, priv));
        info.LicensedFeatures.Add(FeatureId.AIVision);

        SignedLicenseVerifier.TryVerify(info, pub, null, out _).Should().BeFalse();
    }
}
