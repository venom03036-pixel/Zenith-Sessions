using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroSuite.Core.Licensing;

/// <summary>
/// Signed license envelope (schema v1). Grants come only from the explicit entitlement map.
/// </summary>
public sealed class SignedLicensePayload
{
    public int SchemaVersion { get; init; } = SignedLicenseVerifier.CurrentSchemaVersion;
    public string LicenseId { get; init; } = string.Empty;
    public string Edition { get; init; } = "Free";
    public string[] Entitlements { get; init; } = [];
    public DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset ExpirationUtc { get; init; }
    public string? CustomerId { get; init; }
    public string? MachineBinding { get; init; }
}

/// <summary>
/// Ed25519-style ECDSA P-256 signatures over canonical JSON (Release 4).
/// Production embeds only the public key. Tests supply an ephemeral key pair.
/// </summary>
public static class SignedLicenseVerifier
{
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> s_knownEditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Free", "Pro", "Enterprise", "Administrator"
    };

    /// <summary>
    /// Embedded production public key (base64 SubjectPublicKeyInfo). Empty means no production signed licenses yet.
    /// </summary>
    public static string EmbeddedPublicKeyBase64 { get; } = string.Empty;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static (string PublicKeyBase64, string PrivateKeyBase64) CreateTestKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    public static string Sign(SignedLicensePayload payload, string privateKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] canonical = CanonicalBytes(payload);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        byte[] signature = ecdsa.SignData(canonical, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public static LicenseInfo AttachSignature(SignedLicensePayload payload, string signatureBase64)
    {
        var type = ParseEdition(payload.Edition);
        var features = ParseEntitlements(payload.Entitlements);
        return new LicenseInfo
        {
            LicenseKey = payload.LicenseId,
            CustomerId = payload.CustomerId ?? "signed",
            Type = type,
            IssuedUtc = payload.IssuedUtc,
            ExpirationUtc = payload.ExpirationUtc,
            LicensedFeatures = features,
            SchemaVersion = payload.SchemaVersion,
            Signature = signatureBase64,
            MachineBinding = payload.MachineBinding,
            SignedPayloadJson = CanonicalJson(payload)
        };
    }

    public static bool TryVerify(LicenseInfo info, out string? error)
        => TryVerify(info, EmbeddedPublicKeyBase64, expectedMachine: null, out error);

    public static bool TryVerify(LicenseInfo info, string publicKeyBase64, string? expectedMachine, out string? error)
    {
        error = null;
        if (info is null)
        {
            error = "License is missing.";
            return false;
        }

        if (info.IsExpired)
        {
            error = "License is expired.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.Signature) || string.IsNullOrWhiteSpace(info.SignedPayloadJson))
        {
            error = "License is not a signed envelope.";
            return false;
        }

        if (info.SchemaVersion != CurrentSchemaVersion)
        {
            error = $"Unknown license schema version '{info.SchemaVersion}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            error = "No license public key is configured.";
            return false;
        }

        SignedLicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SignedLicensePayload>(info.SignedPayloadJson, JsonOptions);
        }
        catch (Exception ex)
        {
            error = $"License payload is malformed: {ex.Message}";
            return false;
        }

        if (payload is null)
        {
            error = "License payload is empty.";
            return false;
        }

        if (payload.SchemaVersion != CurrentSchemaVersion)
        {
            error = $"Unknown license schema version '{payload.SchemaVersion}'.";
            return false;
        }

        if (!s_knownEditions.Contains(payload.Edition))
        {
            error = $"Unknown license edition '{payload.Edition}'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.MachineBinding)
            && !string.IsNullOrWhiteSpace(expectedMachine)
            && !payload.MachineBinding.Equals(expectedMachine, StringComparison.OrdinalIgnoreCase))
        {
            error = "License is bound to a different machine.";
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            byte[] canonical = Encoding.UTF8.GetBytes(info.SignedPayloadJson);
            byte[] signature = Convert.FromBase64String(info.Signature);
            if (!ecdsa.VerifyData(canonical, signature, HashAlgorithmName.SHA256))
            {
                error = "License signature is invalid.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"License signature could not be verified: {ex.Message}";
            return false;
        }

        var expectedFeatures = ParseEntitlements(payload.Entitlements);
        if (!expectedFeatures.SetEquals(info.LicensedFeatures))
        {
            error = "License entitlement map does not match the signed payload.";
            return false;
        }

        return true;
    }

    private static LicenseType ParseEdition(string edition)
        => edition.ToLowerInvariant() switch
        {
            "pro" => LicenseType.Pro,
            "enterprise" => LicenseType.Enterprise,
            "administrator" => LicenseType.Administrator,
            _ => LicenseType.Free
        };

    private static HashSet<FeatureId> ParseEntitlements(IReadOnlyList<string> names)
    {
        var set = new HashSet<FeatureId>();
        foreach (string name in names)
        {
            if (Enum.TryParse(name, ignoreCase: true, out FeatureId id))
                set.Add(id);
        }

        return set;
    }

    private static string CanonicalJson(SignedLicensePayload payload)
        => JsonSerializer.Serialize(payload, JsonOptions);

    private static byte[] CanonicalBytes(SignedLicensePayload payload)
        => Encoding.UTF8.GetBytes(CanonicalJson(payload));
}
