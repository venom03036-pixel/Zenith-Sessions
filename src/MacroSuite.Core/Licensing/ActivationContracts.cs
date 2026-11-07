using System.Collections.Frozen;

namespace MacroSuite.Core.Licensing;

public static class ActivationProduct
{
    public const string ProductId = "zenith-macros-public";
    public const int MajorVersion = 2;
    public const int SchemaVersion = 2;
    public const int MaximumFileBytes = 65_536;
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256-P1363";
    public const string DeviceAlgorithm = "RSA2048-SHA256-PKCS1";
    public const string TpmProtection = "TPM";
    public const string SoftwareProtection = "Software";
    public static IReadOnlySet<FeatureId> IncludedFeatures { get; } = new[]
    {
        FeatureId.BasicMacroRecording, FeatureId.BasicPlayback, FeatureId.ColorDetection,
        FeatureId.ImageRecognition, FeatureId.AdvancedConditions, FeatureId.PluginExecution, FeatureId.AuditLog
    }.ToFrozenSet();
}

/// <summary>Created only by signature validation; all authorization data comes from signed bytes.</summary>
public sealed class VerifiedLicense
{
    public string LicenseId { get; }
    public string CustomerId { get; }
    public string ActivationId { get; }
    public string RequestId { get; }
    public string IssuerKeyId { get; }
    public string DeviceKeyId { get; }
    public string DeviceProtection { get; }
    public DateTimeOffset IssuedUtc { get; }
    public DateTimeOffset? ExpiresUtc { get; }
    public int LicensedMajorVersion => ActivationProduct.MajorVersion;
    public IReadOnlySet<FeatureId> Features => ActivationProduct.IncludedFeatures;
    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expiry && now >= expiry;

    internal VerifiedLicense(ActivationLicensePayload payload, string issuerKeyId)
    {
        LicenseId = payload.LicenseId; CustomerId = payload.CustomerId;
        ActivationId = payload.ActivationId; RequestId = payload.RequestId;
        DeviceKeyId = payload.DeviceKeyId; DeviceProtection = payload.DeviceProtection;
        IssuedUtc = payload.IssuedUtc; ExpiresUtc = payload.ExpiresUtc; IssuerKeyId = issuerKeyId;
    }
}

public sealed class ActivationLicensePayload
{
    public int SchemaVersion { get; init; } = ActivationProduct.SchemaVersion;
    public string ProductId { get; init; } = ActivationProduct.ProductId;
    public string Edition { get; init; } = "Standard";
    public int LicensedMajorVersion { get; init; } = ActivationProduct.MajorVersion;
    public string LicenseId { get; init; } = string.Empty;
    public string CustomerId { get; init; } = string.Empty;
    public string ActivationId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string DeviceKeyId { get; init; } = string.Empty;
    public string DeviceProtection { get; init; } = string.Empty;
    public bool SoftwareFallbackApproved { get; init; }
}

public sealed record ActivationOperationResult(bool Success, string Message);
public sealed record ActivationRequestResult(bool Success, string Message, string? RequestText = null, string? DeviceProtection = null);

public interface IActivationService
{
    bool IsActive { get; }
    string StatusMessage { get; }
    VerifiedLicense? CurrentLicense { get; }
    event EventHandler? StateChanged;
    Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default);
    Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default);
    Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default);
    void RequireActive();
}

public sealed record DeviceBindingIdentity(string KeyId, string PublicKeyBase64, string Protection);
public interface IDeviceBindingService
{
    Task<DeviceBindingIdentity> GetOrCreateAsync(bool allowSoftwareFallback, CancellationToken ct = default);
    Task<byte[]> SignAsync(string protection, byte[] challenge, CancellationToken ct = default);
    Task<bool> ProvePossessionAsync(VerifiedLicense license, CancellationToken ct = default);
}

public sealed class ActivationRequiredException : InvalidOperationException
{
    public ActivationRequiredException(string message) : base(message) { }
}
