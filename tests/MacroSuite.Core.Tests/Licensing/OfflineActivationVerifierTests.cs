using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.Core.Tests.Licensing;

public sealed class OfflineActivationVerifierTests
{
    [Fact]
    public void ValidOriginalBytesGrantExactlyStandardLocalFeatures()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(key);
        string license = Sign(key, trust.ActiveKeyId, Payload());
        var verifier = new ActivationLicenseVerifier(trust);
        Assert.True(verifier.TryVerify(license, out var grant, out var error), error);
        Assert.Equal(7, grant!.Features.Count); Assert.DoesNotContain(FeatureId.AIVision, grant.Features);
        Assert.DoesNotContain(FeatureId.AutonomousMode, grant.Features); Assert.Equal(2, grant.LicensedMajorVersion);
        Assert.True(verifier.TryVerify(ActivationWireFormat.AsLicenseCode(license), out _, out error), error);
    }

    [Theory]
    [InlineData("edition", "Administrator")]
    [InlineData("edition", "Enterprise")]
    [InlineData("productId", "another-product")]
    [InlineData("deviceProtection", "Unknown")]
    [InlineData("deviceKeyId", "not-a-device")]
    [InlineData("expiresUtc", "not-a-date")]
    public void EvenValidSignaturesCannotGrantUnsupportedOrMalformedFields(string field, string value)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(key);
        JsonObject payload = JsonSerializer.SerializeToNode(Payload(), ActivationWireFormat.JsonOptions)!.AsObject(); payload[field] = value;
        Assert.False(new ActivationLicenseVerifier(trust).TryVerify(SignBytes(key, trust.ActiveKeyId, Encoding.UTF8.GetBytes(payload.ToJsonString())), out _, out _));
    }

    [Fact]
    public void UnsignedChangesAndDifferentIssuerAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key); var verifier = new ActivationLicenseVerifier(trust);
        JsonObject envelope = JsonNode.Parse(Sign(key, trust.ActiveKeyId, Payload()))!.AsObject();
        JsonObject payload = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject(); payload["customerId"] = "another customer";
        envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        Assert.False(verifier.TryVerify(envelope.ToJsonString(), out _, out _));
        Assert.False(verifier.TryVerify(Sign(other, trust.ActiveKeyId, Payload()), out _, out _));
    }

    [Fact]
    public void DuplicateAndUnknownFieldsAreRejectedAtBothLevels()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(key); var verifier = new ActivationLicenseVerifier(trust);
        string valid = Sign(key, trust.ActiveKeyId, Payload());
        Assert.False(verifier.TryVerify(valid.Insert(1, "\"formatVersion\":2,"), out _, out _));
        Assert.False(verifier.TryVerify(valid.Insert(1, "\"administrator\":true,"), out _, out _));
        string payload = JsonSerializer.Serialize(Payload(), ActivationWireFormat.JsonOptions);
        Assert.False(verifier.TryVerify(SignBytes(key, trust.ActiveKeyId, Encoding.UTF8.GetBytes(payload.Insert(1, "\"edition\":\"Standard\","))), out _, out _));
        Assert.False(verifier.TryVerify(SignBytes(key, trust.ActiveKeyId, Encoding.UTF8.GetBytes(payload.Insert(1, "\"entitlements\":[\"AIVision\"],"))), out _, out _));
    }

    [Fact]
    public void SoftwareBindingNeedsSignedExplicitApprovalAndDatesFailClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(key); var verifier = new ActivationLicenseVerifier(trust);
        JsonObject payload = JsonSerializer.SerializeToNode(Payload(), ActivationWireFormat.JsonOptions)!.AsObject();
        payload["deviceProtection"] = "Software"; payload["softwareFallbackApproved"] = false;
        Assert.False(VerifyNode(payload));
        payload["softwareFallbackApproved"] = true; Assert.True(VerifyNode(payload));
        payload["expiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1); Assert.False(VerifyNode(payload));
        payload["expiresUtc"] = null; payload["issuedUtc"] = DateTimeOffset.UtcNow.AddHours(1); Assert.False(VerifyNode(payload));
        payload["issuedUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5); payload["licensedMajorVersion"] = 3; Assert.False(VerifyNode(payload));
        bool VerifyNode(JsonObject data) => verifier.TryVerify(SignBytes(key, trust.ActiveKeyId, Encoding.UTF8.GetBytes(data.ToJsonString())), out _, out _);
    }

    [Fact]
    public void LimitsAndTrustedKeyIdentityAreEnforced()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(key);
        Assert.False(new ActivationLicenseVerifier(trust).TryVerify(new string(' ', 65_537), out _, out _));
        Assert.Throws<FormatException>(() => new ActivationTrustManifest("wrong", trust.Keys));
        Assert.Throws<FormatException>(() => ActivationTrustManifest.Parse(trust.ToJson().Insert(1, "\"schemaVersion\":1,")));
        Assert.True(trust.TryGetPublicKey(trust.ActiveKeyId, out byte[] copy)); copy[0] ^= 1;
        Assert.True(new ActivationLicenseVerifier(trust).TryVerify(Sign(key, trust.ActiveKeyId, Payload()), out _, out _));
    }

    [Fact]
    public void DeviceRequestRequiresValidProofAndExactFields()
    {
        using var rsa = RSA.Create(2048); byte[] spki = rsa.ExportSubjectPublicKeyInfo();
        var payload = new ActivationRequestPayload
        {
            DeviceKeyId = ActivationWireFormat.KeyId(spki), DevicePublicKeyBase64 = Convert.ToBase64String(spki), DeviceProtection = ActivationProduct.TpmProtection
        };
        string request = Request(rsa, JsonSerializer.SerializeToUtf8Bytes(payload, ActivationWireFormat.JsonOptions));
        Assert.True(ActivationRequestCodec.TryVerify(request, out var verified, out var error), error); Assert.Equal(payload.DeviceKeyId, verified!.DeviceKeyId);
        JsonObject altered = JsonNode.Parse(request)!.AsObject(); byte[] proof = Convert.FromBase64String(altered["proof"]!.GetValue<string>()); proof[2] ^= 1; altered["proof"] = Convert.ToBase64String(proof);
        Assert.False(ActivationRequestCodec.TryVerify(altered.ToJsonString(), out _, out _));
        Assert.False(ActivationRequestCodec.TryVerify(request.Insert(1, "\"formatVersion\":2,"), out _, out _));
        string unknown = JsonSerializer.Serialize(payload, ActivationWireFormat.JsonOptions).Insert(1, "\"machineCount\":99,");
        Assert.False(ActivationRequestCodec.TryVerify(Request(rsa, Encoding.UTF8.GetBytes(unknown)), out _, out _));
    }

    private static ActivationTrustManifest Trust(ECDsa key)
    { byte[] spki = key.ExportSubjectPublicKeyInfo(); string id = ActivationWireFormat.KeyId(spki); return new(id, new[] { new ActivationTrustKey(id, Convert.ToBase64String(spki)) }); }
    private static ActivationLicensePayload Payload() => new()
    {
        LicenseId = "TEST-LICENSE", CustomerId = "TEST-CUSTOMER", ActivationId = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"),
        DeviceKeyId = new string('a', 64), DeviceProtection = ActivationProduct.TpmProtection, IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-5), ExpiresUtc = null
    };
    private static string Sign(ECDsa key, string id, ActivationLicensePayload payload) => SignBytes(key, id, JsonSerializer.SerializeToUtf8Bytes(payload, ActivationWireFormat.JsonOptions));
    private static string SignBytes(ECDsa key, string id, byte[] bytes) => JsonSerializer.Serialize(new
    {
        formatVersion = 2, keyId = id, payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });
    private static string Request(RSA key, byte[] bytes) => JsonSerializer.Serialize(new
    {
        formatVersion = 2, payload = Convert.ToBase64String(bytes), proof = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
    });
}
