namespace MacroSuite.Core.Licensing;

/// <summary>
/// Service responsible for managing, inspecting, and validating the application license state.
/// </summary>
public interface ILicenseService
{
    /// <summary>Returns current license metadata without blocking.</summary>
    LicenseInfo GetCurrentLicense();

    /// <summary>Validates the license file, expiration, and cryptographic signature.</summary>
    Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default);

    /// <summary>Returns true if the specified feature is licensed and active.</summary>
    bool IsFeatureLicensed(FeatureId feature);

    /// <summary>Raised whenever the active license is changed, installed, or revoked.</summary>
    event EventHandler<LicenseChangedEventArgs>? LicenseChanged;
}
