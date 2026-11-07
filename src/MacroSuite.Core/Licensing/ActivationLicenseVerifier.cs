using System.Security.Cryptography;
using System.Text.Json;

namespace MacroSuite.Core.Licensing;

public sealed class ActivationLicenseVerifier
{
    private readonly ActivationTrustManifest _trust;
    private readonly TimeProvider _time;
    public ActivationLicenseVerifier(ActivationTrustManifest trust, TimeProvider? time = null)
    { _trust = trust ?? throw new ArgumentNullException(nameof(trust)); _time = time ?? TimeProvider.System; }

    public bool TryVerify(string text, out VerifiedLicense? license, out string? error)
    {
        license = null; error = null;
        try
        {
            using var envelope = ActivationWireFormat.Parse(ActivationWireFormat.UnwrapLicenseCode(text)); var e = envelope.RootElement;
            ActivationWireFormat.ExactObject(e, "formatVersion", "keyId", "payload", "signature");
            if (e.GetProperty("formatVersion").GetInt32() != 2) throw new FormatException("Unsupported license format.");
            string keyId = ActivationWireFormat.Text(e, "keyId");
            if (!_trust.TryGetPublicKey(keyId, out byte[] spki)) throw new FormatException("License issuer is not trusted. This license and Public release use different owner identities. Use the matching updated Public package, then import the license again.");
            byte[] payload = ActivationWireFormat.DecodeBase64(ActivationWireFormat.Text(e, "payload", ActivationProduct.MaximumFileBytes));
            byte[] signature = ActivationWireFormat.DecodeBase64(ActivationWireFormat.Text(e, "signature", 128), 64);
            using var verifier = ECDsa.Create(); verifier.ImportSubjectPublicKeyInfo(spki, out _);
            if (signature.Length != 64 || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new FormatException("License signature is invalid.");
            using var content = ActivationWireFormat.Parse(payload); var p = content.RootElement;
            ActivationWireFormat.ExactObject(p, "schemaVersion", "productId", "edition", "licensedMajorVersion", "licenseId", "customerId", "activationId", "requestId", "issuedUtc", "expiresUtc", "deviceKeyId", "deviceProtection", "softwareFallbackApproved");
            if (p.GetProperty("schemaVersion").GetInt32() != 2 || p.GetProperty("productId").GetString() != ActivationProduct.ProductId || p.GetProperty("edition").GetString() != "Standard" || p.GetProperty("licensedMajorVersion").GetInt32() != ActivationProduct.MajorVersion)
                throw new FormatException("This license is not for this product and version.");
            string deviceId = ActivationWireFormat.Text(p, "deviceKeyId"); string protection = ActivationWireFormat.Text(p, "deviceProtection");
            bool softwareApproved = p.GetProperty("softwareFallbackApproved").GetBoolean();
            if (!ActivationWireFormat.IsKeyId(deviceId) || protection is not (ActivationProduct.TpmProtection or ActivationProduct.SoftwareProtection) || (protection == ActivationProduct.SoftwareProtection && !softwareApproved))
                throw new FormatException("License device binding is invalid or was not approved.");
            DateTimeOffset issued = p.GetProperty("issuedUtc").GetDateTimeOffset();
            DateTimeOffset? expiry = p.GetProperty("expiresUtc").ValueKind == JsonValueKind.Null ? null : p.GetProperty("expiresUtc").GetDateTimeOffset();
            DateTimeOffset now = _time.GetUtcNow();
            if (issued.Offset != TimeSpan.Zero || issued < DateTimeOffset.UnixEpoch || issued > now.AddMinutes(5) || (expiry is { } exp && (exp.Offset != TimeSpan.Zero || exp <= issued || now >= exp)))
                throw new FormatException("License dates are invalid or the license has expired.");
            var data = new ActivationLicensePayload
            {
                LicenseId = ActivationWireFormat.Text(p, "licenseId"), CustomerId = ActivationWireFormat.Text(p, "customerId"),
                ActivationId = ActivationWireFormat.Text(p, "activationId"), RequestId = ActivationWireFormat.Text(p, "requestId"),
                DeviceKeyId = deviceId, DeviceProtection = protection, SoftwareFallbackApproved = softwareApproved,
                IssuedUtc = issued, ExpiresUtc = expiry
            };
            if (!Guid.TryParseExact(data.ActivationId, "D", out _) || !Guid.TryParseExact(data.RequestId, "D", out _)) throw new FormatException("License activation identifier is invalid.");
            license = new VerifiedLicense(data, keyId); return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException)
        { error = ex is FormatException ? ex.Message : "License data is malformed or cannot be verified."; return false; }
    }
}
