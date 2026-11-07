using System.Security.Cryptography;
using System.Text.Json;

namespace MacroSuite.Core.Licensing;

public sealed class ActivationRequestPayload
{
    public int SchemaVersion { get; init; } = 2;
    public string ProductId { get; init; } = ActivationProduct.ProductId;
    public int MajorVersion { get; init; } = ActivationProduct.MajorVersion;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("D");
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string DeviceKeyId { get; init; } = string.Empty;
    public string DevicePublicKeyBase64 { get; init; } = string.Empty;
    public string DeviceProtection { get; init; } = string.Empty;
    public string ProofAlgorithm { get; init; } = ActivationProduct.DeviceAlgorithm;
    public string Nonce { get; init; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class VerifiedActivationRequest
{
    public string RequestId { get; }
    public string DeviceKeyId { get; }
    public string DevicePublicKeyBase64 { get; }
    public string DeviceProtection { get; }
    public DateTimeOffset CreatedUtc { get; }
    internal VerifiedActivationRequest(ActivationRequestPayload payload)
    {
        RequestId = payload.RequestId; DeviceKeyId = payload.DeviceKeyId;
        DevicePublicKeyBase64 = payload.DevicePublicKeyBase64; DeviceProtection = payload.DeviceProtection;
        CreatedUtc = payload.CreatedUtc;
    }
}

public static class ActivationRequestCodec
{
    public static bool TryVerify(string text, out VerifiedActivationRequest? request, out string? error)
    {
        request = null; error = null;
        try
        {
            using var outer = ActivationWireFormat.Parse(text); var e = outer.RootElement;
            ActivationWireFormat.ExactObject(e, "formatVersion", "payload", "proof");
            if (e.GetProperty("formatVersion").GetInt32() != 2) throw new FormatException("Unsupported activation request.");
            byte[] bytes = ActivationWireFormat.DecodeBase64(ActivationWireFormat.Text(e, "payload", ActivationProduct.MaximumFileBytes));
            byte[] proof = ActivationWireFormat.DecodeBase64(ActivationWireFormat.Text(e, "proof", 512), 256);
            using var payload = ActivationWireFormat.Parse(bytes); var p = payload.RootElement;
            ActivationWireFormat.ExactObject(p, "schemaVersion", "productId", "majorVersion", "requestId", "createdUtc", "deviceKeyId", "devicePublicKeyBase64", "deviceProtection", "proofAlgorithm", "nonce");
            if (p.GetProperty("schemaVersion").GetInt32() != 2 || p.GetProperty("productId").GetString() != ActivationProduct.ProductId || p.GetProperty("majorVersion").GetInt32() != ActivationProduct.MajorVersion || p.GetProperty("proofAlgorithm").GetString() != ActivationProduct.DeviceAlgorithm)
                throw new FormatException("Request is not for this product and version.");
            string publicKey = ActivationWireFormat.Text(p, "devicePublicKeyBase64", 2048);
            byte[] spki = ActivationWireFormat.DecodeBase64(publicKey, 512);
            string id = ActivationWireFormat.Text(p, "deviceKeyId"); string protection = ActivationWireFormat.Text(p, "deviceProtection");
            if (id != ActivationWireFormat.KeyId(spki) || protection is not (ActivationProduct.TpmProtection or ActivationProduct.SoftwareProtection) || ActivationWireFormat.DecodeBase64(ActivationWireFormat.Text(p, "nonce"), 32).Length != 32)
                throw new FormatException("Request device identity is invalid.");
            using var device = RSA.Create(); device.ImportSubjectPublicKeyInfo(spki, out int read);
            if (read != spki.Length || device.KeySize != 2048 || proof.Length != 256 || !device.VerifyData(bytes, proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new FormatException("Request proof is invalid.");
            var result = new ActivationRequestPayload
            {
                RequestId = ActivationWireFormat.Text(p, "requestId"), DeviceKeyId = id, DeviceProtection = protection,
                DevicePublicKeyBase64 = publicKey, CreatedUtc = p.GetProperty("createdUtc").GetDateTimeOffset()
            };
            if (!Guid.TryParseExact(result.RequestId, "D", out _) || result.CreatedUtc.Offset != TimeSpan.Zero || result.CreatedUtc < DateTimeOffset.UnixEpoch || result.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new FormatException("Request identity or creation date is invalid.");
            request = new VerifiedActivationRequest(result); return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException)
        { error = ex is FormatException ? ex.Message : "Activation request is malformed or cannot be verified."; return false; }
    }
}
