using System.Collections.ObjectModel;
using MacroSuite.App.Common;
using MacroSuite.Core.Licensing;

namespace MacroSuite.App.ViewModels;

public sealed class FeatureStatusItem
{
    public string FeatureName { get; }
    public bool IsLicensed { get; }
    public string DisplayName { get; }
    public string AvailabilityText => IsLicensed ? "Available" : "Not included";

    public FeatureStatusItem(FeatureId feature, bool isLicensed)
    {
        FeatureName = feature.ToString();
        IsLicensed = isLicensed;
        DisplayName = feature switch
        {
            FeatureId.BasicMacroRecording => "Record macros",
            FeatureId.BasicPlayback => "Play macros",
            FeatureId.ColorDetection => "Color detection",
            FeatureId.ImageRecognition => "Image recognition",
            FeatureId.AIVision => "AI vision",
            FeatureId.AutonomousMode => "Autonomous AI actions",
            FeatureId.AdvancedConditions => "Conditions and loops",
            FeatureId.PluginExecution => "Plugins",
            FeatureId.AuditLog => "Activity log",
            FeatureId.UsageTracking => "AI usage tracking",
            _ => feature.ToString()
        };
    }
}

/// <summary>
/// View model displaying license tier, feature entitlements, and key installation.
/// </summary>
public sealed class LicensingViewModel : ObservableObject
{
    private readonly ILicenseService _licenseService;

    private string _licenseTier = "FREE";
    private string _customerId = "Default User";
    private string _licensedEmail = "local-user@host";
    private string _issuedDateText = DateTime.UtcNow.ToString("yyyy-MM-dd");
    private string _expirationDateText = "Never";
    private string _expirationText = "Permanent License";
    private bool _isRootAuthority;
    private bool _isActive = true;
    private string _unlockedCountText = "2/10 unlocked";
    private string _emailInput = string.Empty;
    private string _licenseKeyInput = string.Empty;
    private string _statusMessage = string.Empty;

    public ObservableCollection<FeatureStatusItem> Features { get; } = new();

    public string LicenseTier
    {
        get => _licenseTier;
        private set => SetProperty(ref _licenseTier, value);
    }

    public string CustomerId
    {
        get => _customerId;
        private set => SetProperty(ref _customerId, value);
    }

    public string LicensedEmail
    {
        get => _licensedEmail;
        private set => SetProperty(ref _licensedEmail, value);
    }

    public string IssuedDateText
    {
        get => _issuedDateText;
        private set => SetProperty(ref _issuedDateText, value);
    }

    public string ExpirationDateText
    {
        get => _expirationDateText;
        private set => SetProperty(ref _expirationDateText, value);
    }

    public string ExpirationText
    {
        get => _expirationText;
        private set => SetProperty(ref _expirationText, value);
    }

    public bool IsRootAuthority
    {
        get => _isRootAuthority;
        private set => SetProperty(ref _isRootAuthority, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public string UnlockedCountText
    {
        get => _unlockedCountText;
        private set => SetProperty(ref _unlockedCountText, value);
    }

    public string EmailInput
    {
        get => _emailInput;
        set => SetProperty(ref _emailInput, value);
    }

    public string LicenseKeyInput
    {
        get => _licenseKeyInput;
        set => SetProperty(ref _licenseKeyInput, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsDeveloperMode => BuildEnvironment.IsDeveloperBuild;
    public string BuildProfileText => BuildEnvironment.BuildProfileName;
    public string ProductAccessText => IsRootAuthority ? "Developer access" : LicenseTier.ToLowerInvariant() switch
    {
        "free" => "Free",
        "trial" => "Trial",
        "pro" => "Pro",
        "enterprise" => "Enterprise",
        _ => LicenseTier
    };
    public string EntitlementExplanation => IsRootAuthority
        ? "Administrator permissions are enabled for development. They do not confirm a purchased product license."
        : LicenseTier == "FREE"
            ? "Basic recording and playback are available. Install a verified license to enable additional features."
            : "Access shown below comes from the installed license. This app does not store receipts, purchase price, or update entitlements.";
    public string LicenseStatusText => IsActive ? "Active" : "Expired";
    public string OwnerDisplay => LicensedEmail == "local-user@host" ? CustomerId : LicensedEmail;

    public RelayCommand InstallLicenseCommand { get; }
    public RelayCommand ClearLicenseCommand { get; }
    public RelayCommand ApplyAdminKeyCommand { get; }

    public LicensingViewModel(ILicenseService licenseService)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));

        InstallLicenseCommand = new RelayCommand(InstallLicense, () => !string.IsNullOrWhiteSpace(LicenseKeyInput));
        ClearLicenseCommand = new RelayCommand(ClearLicense);
        ApplyAdminKeyCommand = new RelayCommand(ApplyAdminKey);

        _licenseService.LicenseChanged += (s, e) => RefreshLicenseState();
        RefreshLicenseState();
    }

    private void ApplyAdminKey()
    {
        if (!BuildEnvironment.IsDeveloperBuild)
        {
            StatusMessage = "Master Admin Key activation is restricted to internal developer builds.";
            return;
        }

        string email = !string.IsNullOrWhiteSpace(EmailInput)
            ? EmailInput.Trim()
            : (!string.IsNullOrWhiteSpace(LicensedEmail) && LicensedEmail != "local-user@host" ? LicensedEmail : "developer@zenithmacros.dev");

        var license = LicenseInfo.CreateAdministrator(
            licenseKey: "ZENITH-ADMIN-MASTER-2026-UNLIMITED",
            customerId: "Administrator",
            email: email,
            issued: DateTimeOffset.UtcNow,
            expiration: DateTimeOffset.MaxValue);

        if (_licenseService is Infrastructure.Licensing.LocalLicenseService localService)
        {
            localService.InstallLicense(license);
            StatusMessage = $"Developer administrator access enabled for {email}.";
            LicenseKeyInput = string.Empty;
        }
        else
        {
            StatusMessage = $"Admin license applied for {email}.";
        }
    }

    private void RefreshLicenseState()
    {
        var license = _licenseService.GetCurrentLicense();

        LicenseTier = license.Type.ToString().ToUpperInvariant();
        CustomerId = license.Type == LicenseType.Administrator ? "Administrator" : license.CustomerId;
        LicensedEmail = !string.IsNullOrWhiteSpace(license.LicensedEmail)
            ? license.LicensedEmail
            : (!string.IsNullOrWhiteSpace(license.CustomerId) && license.CustomerId.Contains('@') ? license.CustomerId : "local-user@host");
        IssuedDateText = license.IssuedUtc.ToString("yyyy-MM-dd");
        ExpirationDateText = license.ExpirationUtc == DateTimeOffset.MaxValue
            ? "Never"
            : license.ExpirationUtc.ToString("yyyy-MM-dd");
        ExpirationText = license.ExpirationUtc == DateTimeOffset.MaxValue
            ? "Permanent License"
            : $"{license.ExpirationUtc:yyyy-MM-dd HH:mm UTC}";
        IsRootAuthority = license.Type == LicenseType.Administrator;
        IsActive = license.IsValid;
        OnPropertyChanged(nameof(ProductAccessText));
        OnPropertyChanged(nameof(EntitlementExplanation));
        OnPropertyChanged(nameof(LicenseStatusText));
        OnPropertyChanged(nameof(OwnerDisplay));

        if (string.IsNullOrWhiteSpace(EmailInput) && LicensedEmail != "local-user@host")
        {
            EmailInput = LicensedEmail;
        }

        // Feature pairs matching the mockup 2-column layout:
        // Col 1: BasicMacroRecording, ColorDetection, AIVision, AdvancedConditions, AuditLog
        // Col 2: BasicPlayback, ImageRecognition, AutonomousMode, PluginExecution, UsageTracking
        var orderedFeatures = new[]
        {
            FeatureId.BasicMacroRecording, FeatureId.BasicPlayback,
            FeatureId.ColorDetection,     FeatureId.ImageRecognition,
            FeatureId.AIVision,            FeatureId.AutonomousMode,
            FeatureId.AdvancedConditions,  FeatureId.PluginExecution,
            FeatureId.AuditLog,            FeatureId.UsageTracking
        };

        Features.Clear();
        int unlocked = 0;
        foreach (var f in orderedFeatures)
        {
            bool licensed = _licenseService.IsFeatureLicensed(f);
            if (licensed) unlocked++;
            Features.Add(new FeatureStatusItem(f, licensed));
        }

        UnlockedCountText = $"{unlocked}/{orderedFeatures.Length} unlocked";
    }

    private void InstallLicense()
    {
        if (string.IsNullOrWhiteSpace(LicenseKeyInput)) return;

        var key = LicenseKeyInput.Trim();
        var email = !string.IsNullOrWhiteSpace(EmailInput)
            ? EmailInput.Trim()
            : (!string.IsNullOrWhiteSpace(LicensedEmail) && LicensedEmail != "local-user@host" ? LicensedEmail : "local-user@host");

        var decision = LicenseInstallPolicy.FromUserKey(key, email, BuildEnvironment.IsDeveloperBuild);
        if (!decision.Accepted || decision.License is null)
        {
            StatusMessage = decision.Message;
            return;
        }

        if (_licenseService is Infrastructure.Licensing.LocalLicenseService localService)
        {
            localService.InstallLicense(decision.License);
            StatusMessage = decision.Message;
            LicenseKeyInput = string.Empty;
        }
        else
        {
            StatusMessage = decision.Message;
        }
    }

    private void ClearLicense()
    {
        if (_licenseService is Infrastructure.Licensing.LocalLicenseService localService)
        {
            localService.ClearLicense();
            StatusMessage = "Reverted to Free tier.";
        }
    }
}
