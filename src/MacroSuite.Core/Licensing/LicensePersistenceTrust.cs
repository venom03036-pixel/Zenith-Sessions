namespace MacroSuite.Core.Licensing;

/// <summary>
/// How on-disk license payloads are treated after DPAPI decrypt.
/// </summary>
public enum LicensePersistenceTrust
{
    /// <summary>Test/tooling: accept whatever <see cref="LicenseInfo"/> was installed via the service API.</summary>
    TrustInstalledPayload = 0,

    /// <summary>Developer app: Free and Administrator only until signed envelopes exist.</summary>
    DeveloperAdminOrFree = 1,

    /// <summary>Production: Free, or a payload that passes <see cref="SignedLicenseVerifier"/>.</summary>
    ProductionVerifiedOnly = 2
}

/// <summary>
/// Rejects unverified paid entitlements loaded from disk.
/// </summary>
public static class LicenseLoadSanitizer
{
    public static LicenseInfo Sanitize(LicenseInfo? info, LicensePersistenceTrust trust)
    {
        if (info is null || info.IsExpired)
            return LicenseInfo.CreateFree();

        return trust switch
        {
            LicensePersistenceTrust.TrustInstalledPayload => info,
            LicensePersistenceTrust.DeveloperAdminOrFree =>
                info.Type is LicenseType.Free or LicenseType.Administrator
                    ? info
                    : LicenseInfo.CreateFree(),
            LicensePersistenceTrust.ProductionVerifiedOnly =>
                info.Type == LicenseType.Free || SignedLicenseVerifier.TryVerify(info, out _)
                    ? info
                    : LicenseInfo.CreateFree(),
            _ => LicenseInfo.CreateFree()
        };
    }
}
