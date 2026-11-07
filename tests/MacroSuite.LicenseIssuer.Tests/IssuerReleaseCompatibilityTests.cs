using System.Security.Cryptography;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.LicenseIssuer.Tests;

public sealed class IssuerReleaseCompatibilityTests
{
    [Fact]
    public void MatchingOwnerIsAcceptedAndUnrelatedOwnerIsRejected()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ownerTrust = Trust(owner); var otherTrust = Trust(other);
        var identity = new OwnerIdentityResult(true, "Ready", ownerTrust.ToJson(), ownerTrust.ActiveKeyId);
        Assert.True(IssuerReleaseCompatibility.Evaluate(ownerTrust.ToJson(), identity).Compatible);
        var mismatch = IssuerReleaseCompatibility.Evaluate(otherTrust.ToJson(), identity);
        Assert.False(mismatch.Compatible);
        Assert.Contains("does not match", mismatch.Message);
        Assert.False(IssuerReleaseCompatibility.Evaluate(null, identity).Compatible);
        Assert.False(IssuerReleaseCompatibility.Evaluate("{}", identity).Compatible);
    }

    [Fact]
    public void EstablishedPreviousIssuerRemainsCompatibleWhenExplicitlyRetained()
    {
        using var previous = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldTrust = Trust(previous); var newTrust = Trust(current);
        var combined = new ActivationTrustManifest(newTrust.ActiveKeyId, newTrust.Keys.Concat(oldTrust.Keys));
        Assert.True(IssuerReleaseCompatibility.Evaluate(combined.ToJson(),
            new(true, "Ready", oldTrust.ToJson(), oldTrust.ActiveKeyId)).Compatible);
        Assert.True(IssuerReleaseCompatibility.Evaluate(combined.ToJson(),
            new(true, "Ready", newTrust.ToJson(), newTrust.ActiveKeyId)).Compatible);
    }

    private static ActivationTrustManifest Trust(ECDsa key)
    {
        byte[] spki = key.ExportSubjectPublicKeyInfo(); string id = ActivationWireFormat.KeyId(spki);
        return new(id, new[] { new ActivationTrustKey(id, Convert.ToBase64String(spki)) });
    }
}
