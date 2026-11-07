using System.Security.Cryptography;
using MacroSuite.Core.Licensing;

namespace MacroSuite.LicenseIssuer;

/// <summary>Checks the established owner identity against the verification keys shipped in this release.</summary>
public static class IssuerReleaseCompatibility
{
    public static (bool Compatible, string Message) Evaluate(string? expectedTrustManifestJson, OwnerIdentityResult identity)
    {
        if (!identity.Success || identity.TrustManifestJson is null || identity.Fingerprint is null)
            return (false, identity.Message);
        if (string.IsNullOrWhiteSpace(expectedTrustManifestJson))
            return (false, "This Personal release has no Public verification identity. Use the matching Personal/Public release pair before generating licenses.");
        try
        {
            var release = ActivationTrustManifest.Parse(expectedTrustManifestJson);
            var owner = ActivationTrustManifest.Parse(identity.TrustManifestJson);
            if (owner.ActiveKeyId == identity.Fingerprint
                && owner.TryGetPublicKey(identity.Fingerprint, out byte[] ownerKey)
                && release.TryGetPublicKey(identity.Fingerprint, out byte[] releaseKey)
                && CryptographicOperations.FixedTimeEquals(ownerKey, releaseKey))
                return (true, "Owner identity matches this release. Generated licenses work with the matching Public package on their approved PC.");
            return (false, $"Owner identity {identity.Fingerprint[..12]} does not match this release ({release.ActiveKeyId[..12]}). Licenses from this identity will be rejected by that Public package. Use a Public package built for this owner identity, or restore the original owner backup. Do not initialize another identity.");
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return (false, "The release verification identity could not be checked. Use the matching Personal/Public release pair; your owner identity was not changed.");
        }
    }
}
