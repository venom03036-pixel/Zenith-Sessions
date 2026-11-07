namespace MacroSuite.Core.Licensing;

/// <summary>
/// The tier or category of license grant.
/// </summary>
public enum LicenseType
{
    /// <summary>Unregistered or complimentary tier with basic capabilities.</summary>
    Free,

    /// <summary>Time-limited evaluation tier with full feature access.</summary>
    Trial,

    /// <summary>Professional standalone license tier.</summary>
    Pro,

    /// <summary>Enterprise license tier with advanced security and audit logging.</summary>
    Enterprise,

    /// <summary>Master administrative tier with root authority and full feature access.</summary>
    Administrator,

    Standard,
}

/// <summary>
/// Encapsulates details of a software license entitlement.
/// </summary>
public sealed class LicenseInfo
{
    /// <summary>The unique license key or identifier string.</summary>
    public string LicenseKey { get; init; } = string.Empty;

    /// <summary>Customer, company, or user identifier associated with the license.</summary>
    public string CustomerId { get; init; } = "Default User";

    /// <summary>Licensed email or owner account.</summary>
    public string LicensedEmail { get; init; } = string.Empty;

    /// <summary>The license tier.</summary>
    public LicenseType Type { get; init; } = LicenseType.Free;

    /// <summary>UTC issuance timestamp.</summary>
    public DateTimeOffset IssuedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC expiration timestamp.</summary>
    public DateTimeOffset ExpirationUtc { get; init; } = DateTimeOffset.MaxValue;

    /// <summary>The set of features licensed for use. Must match a signed entitlement map when <see cref="Signature"/> is set.</summary>
    public HashSet<FeatureId> LicensedFeatures { get; init; } = new();

    /// <summary>Signed-envelope schema version. Unsigned stub payloads use 0.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Base64 ECDSA signature over <see cref="SignedPayloadJson"/>.</summary>
    public string? Signature { get; init; }

    /// <summary>Canonical JSON of <see cref="SignedLicensePayload"/> that was signed.</summary>
    public string? SignedPayloadJson { get; init; }

    /// <summary>Optional machine binding copied from the signed payload.</summary>
    public string? MachineBinding { get; init; }

    /// <summary>True if current UTC time exceeds expiration timestamp.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow > ExpirationUtc;

    /// <summary>True if license is non-null and not expired.</summary>
    public bool IsValid => !IsExpired;

    /// <summary>
    /// Checks whether a specific feature is granted and active.
    /// </summary>
    public bool IsFeatureLicensed(FeatureId feature)
    {
        if (IsExpired)
        {
            return false;
        }

        if (Type == LicenseType.Administrator)
        {
            return true;
        }

        return LicensedFeatures.Contains(feature);
    }

    /// <summary>
    /// Creates a default Free tier license granting only basic input recording and playback.
    /// </summary>
    public static LicenseInfo CreateFree()
    {
        return new LicenseInfo
        {
            LicenseKey = "FREE-COMMUNITY",
            CustomerId = "Community User",
            Type = LicenseType.Free,
            ExpirationUtc = DateTimeOffset.MaxValue,
            LicensedFeatures = FeatureCatalog.ToSet(FeatureCatalog.Free)
        };
    }

    /// <summary>
    /// Creates a Pro tier license granting local automation features.
    /// </summary>
    public static LicenseInfo CreatePro(string licenseKey, string customerId, DateTimeOffset? expiration = null)
    {
        return new LicenseInfo
        {
            LicenseKey = licenseKey,
            CustomerId = customerId,
            Type = LicenseType.Pro,
            ExpirationUtc = expiration ?? DateTimeOffset.MaxValue,
            LicensedFeatures = FeatureCatalog.ToSet(FeatureCatalog.Pro)
        };
    }

    /// <summary>
    /// Creates an Enterprise tier license granting all capabilities including AI Vision and Plugins.
    /// </summary>
    public static LicenseInfo CreateEnterprise(string licenseKey, string customerId, DateTimeOffset? expiration = null)
    {
        return new LicenseInfo
        {
            LicenseKey = licenseKey,
            CustomerId = customerId,
            Type = LicenseType.Enterprise,
            ExpirationUtc = expiration ?? DateTimeOffset.MaxValue,
            LicensedFeatures = FeatureCatalog.ToSet(FeatureCatalog.Enterprise)
        };
    }

    /// <summary>
    /// Creates a Master Administrator license tier granting root authority and all capabilities.
    /// </summary>
    public static LicenseInfo CreateAdministrator(
        string licenseKey,
        string customerId,
        string? email = null,
        DateTimeOffset? issued = null,
        DateTimeOffset? expiration = null)
    {
        return new LicenseInfo
        {
            LicenseKey = licenseKey,
            CustomerId = customerId,
            LicensedEmail = string.IsNullOrWhiteSpace(email) ? customerId : email,
            Type = LicenseType.Administrator,
            IssuedUtc = issued ?? DateTimeOffset.UtcNow,
            ExpirationUtc = expiration ?? DateTimeOffset.MaxValue,
            LicensedFeatures = FeatureCatalog.ToSet(FeatureCatalog.Enterprise)
        };
    }
}

/// <summary>
/// Result of validating a license grant or signature.
/// </summary>
public sealed class LicenseValidationResult
{
    public bool IsValid { get; }
    public string? Message { get; }
    public LicenseInfo? License { get; }

    private LicenseValidationResult(bool isValid, string? message, LicenseInfo? license)
    {
        IsValid = isValid;
        Message = message;
        License = license;
    }

    public static LicenseValidationResult Success(LicenseInfo license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return new LicenseValidationResult(true, null, license);
    }

    public static LicenseValidationResult Failure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new LicenseValidationResult(false, message, null);
    }
}

/// <summary>
/// Event arguments raised when the active license changes.
/// </summary>
public sealed class LicenseChangedEventArgs : EventArgs
{
    public LicenseInfo? PreviousLicense { get; }
    public LicenseInfo NewLicense { get; }

    public LicenseChangedEventArgs(LicenseInfo? previousLicense, LicenseInfo newLicense)
    {
        PreviousLicense = previousLicense;
        NewLicense = newLicense ?? throw new ArgumentNullException(nameof(newLicense));
    }
}
