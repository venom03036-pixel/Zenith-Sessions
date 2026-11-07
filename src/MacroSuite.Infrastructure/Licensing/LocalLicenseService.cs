using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Licensing;

/// <summary>
/// File-based license service that uses Windows DPAPI encryption to secure license entitlements on disk (Rule 10, Rule 18).
/// Defaults to a Free tier license if no license file is installed or if the file has been tampered with.
/// </summary>
public sealed class LocalLicenseService : ILicenseService
{
    private static readonly byte[] s_entropy = "ZenithMacros_License_Salt_v1"u8.ToArray();

    private readonly string _licenseFilePath;
    private readonly LicensePersistenceTrust _trust;
    private readonly object _lock = new();
    private LicenseInfo _currentLicense;

    public event EventHandler<LicenseChangedEventArgs>? LicenseChanged;

    public LocalLicenseService(
        string? customLicenseFilePath = null,
        LicensePersistenceTrust trust = LicensePersistenceTrust.TrustInstalledPayload)
    {
        _licenseFilePath = customLicenseFilePath ?? Path.Combine(AppDataPathResolver.GetLicenseDirectory(), "license.bin");
        _trust = trust;
        _currentLicense = LoadLicenseInternal();
    }

    /// <inheritdoc/>
    public LicenseInfo GetCurrentLicense()
    {
        lock (_lock)
        {
            return _currentLicense;
        }
    }

    /// <inheritdoc/>
    public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_currentLicense.IsExpired)
            {
                return Task.FromResult(LicenseValidationResult.Failure($"License expired on {_currentLicense.ExpirationUtc:u}."));
            }

            if (!File.Exists(_licenseFilePath) && _currentLicense.Type != LicenseType.Free)
            {
                return Task.FromResult(LicenseValidationResult.Failure("License file not found on disk."));
            }

            return Task.FromResult(LicenseValidationResult.Success(_currentLicense));
        }
    }

    /// <inheritdoc/>
    public bool IsFeatureLicensed(FeatureId feature)
    {
        lock (_lock)
        {
            return _currentLicense.IsFeatureLicensed(feature);
        }
    }

    /// <summary>
    /// Installs and securely saves a new license file using DPAPI encryption.
    /// </summary>
    public void InstallLicense(LicenseInfo license)
    {
        ArgumentNullException.ThrowIfNull(license);

        LicenseInfo previous;
        lock (_lock)
        {
            previous = _currentLicense;
            string json = JsonSerializer.Serialize(license);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, s_entropy, DataProtectionScope.CurrentUser);

            string? dir = Path.GetDirectoryName(_licenseFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_licenseFilePath, encryptedBytes);
            _currentLicense = license;
        }

        LicenseChanged?.Invoke(this, new LicenseChangedEventArgs(previous, license));
    }

    /// <summary>
    /// Resets or removes any installed license, reverting back to the Free tier.
    /// </summary>
    public void ClearLicense()
    {
        LicenseInfo previous;
        var freeLicense = LicenseInfo.CreateFree();

        lock (_lock)
        {
            previous = _currentLicense;
            if (File.Exists(_licenseFilePath))
            {
                File.Delete(_licenseFilePath);
            }

            _currentLicense = freeLicense;
        }

        LicenseChanged?.Invoke(this, new LicenseChangedEventArgs(previous, freeLicense));
    }

    private LicenseInfo LoadLicenseInternal()
    {
        if (!File.Exists(_licenseFilePath))
        {
            return LicenseInfo.CreateFree();
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(_licenseFilePath);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, s_entropy, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(plainBytes);

            var info = JsonSerializer.Deserialize<LicenseInfo>(json);
            return LicenseLoadSanitizer.Sanitize(info, _trust);
        }
        catch
        {
            // If decryption fails (e.g. altered bytes or moved to a different machine/user),
            // safely degrade to Free tier rather than crashing (Rule 15, 18).
            return LicenseInfo.CreateFree();
        }
    }
}
