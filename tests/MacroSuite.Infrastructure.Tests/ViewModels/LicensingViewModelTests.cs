using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Licensing;
using MacroSuite.Infrastructure.Licensing;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class LicensingViewModelTests : IDisposable
{
    private readonly string _testLicensePath;

    public LicensingViewModelTests()
    {
        _testLicensePath = Path.Combine(Path.GetTempPath(), $"Zenith_LicensingVM_{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_testLicensePath))
        {
            try { File.Delete(_testLicensePath); } catch { }
        }
    }

    [Fact]
    public void InitialState_ShowsFreeTier_AndPopulatesAllFeatures()
    {
        var service = new LocalLicenseService(_testLicensePath);
        var vm = new LicensingViewModel(service);

        vm.LicenseTier.Should().Be("FREE");
        vm.ExpirationText.Should().Be("Permanent License");
        vm.ProductAccessText.Should().Be("Free");
        vm.EntitlementExplanation.Should().Contain("verified license");
        vm.Features.Single(f => f.FeatureName == nameof(FeatureId.BasicPlayback)).DisplayName.Should().Be("Play macros");

        // Must display all 10 features from FeatureId
        var allFeatures = Enum.GetValues<FeatureId>();
        vm.Features.Should().HaveCount(allFeatures.Length);

        // BasicPlayback is free, AIVision is locked
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.BasicPlayback)).IsLicensed.Should().BeTrue();
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.AIVision)).IsLicensed.Should().BeFalse();
    }

    [Fact]
    public void InstallLicense_UnverifiedProKey_DoesNotUpgrade()
    {
        var service = new LocalLicenseService(_testLicensePath);
        var vm = new LicensingViewModel(service);

        vm.LicenseKeyInput = "PRO-KEY-TEST-123";
        vm.InstallLicenseCommand.Execute(null);

        vm.LicenseTier.Should().Be("FREE");
#if DEVELOPER_MODE
        vm.StatusMessage.Should().Be(LicenseInstallPolicy.UnverifiedKeyMessage);
#else
        vm.StatusMessage.Should().Be(LicenseInstallPolicy.VerificationUnavailableMessage);
#endif
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.AIVision)).IsLicensed.Should().BeFalse();
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.PluginExecution)).IsLicensed.Should().BeFalse();
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.AutonomousMode)).IsLicensed.Should().BeFalse();
    }

    [Theory]
    [InlineData("ENT-FAKE-ENTERPRISE")]
    [InlineData("totally-random-string")]
    [InlineData("{not-a-license}")]
    public void InstallLicense_UnverifiedKeys_GrantZeroGatedFeatures(string key)
    {
        var service = new LocalLicenseService(_testLicensePath);
        var vm = new LicensingViewModel(service);
        vm.LicenseKeyInput = key;
        vm.InstallLicenseCommand.Execute(null);

        vm.LicenseTier.Should().Be("FREE");
        service.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();
        service.IsFeatureLicensed(FeatureId.AutonomousMode).Should().BeFalse();
        service.IsFeatureLicensed(FeatureId.PluginExecution).Should().BeFalse();
    }

    [Fact]
    public void ProductionTrust_IgnoresUnsignedPaidLicenseOnDisk()
    {
        var writer = new LocalLicenseService(_testLicensePath, LicensePersistenceTrust.TrustInstalledPayload);
        writer.InstallLicense(LicenseInfo.CreateEnterprise("ENT-DISK", "Corp"));

        var production = new LocalLicenseService(_testLicensePath, LicensePersistenceTrust.ProductionVerifiedOnly);
        production.GetCurrentLicense().Type.Should().Be(LicenseType.Free);
        production.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();
    }

    [Fact]
    public void ExpiredSignedLikePayload_GrantsNothing()
    {
        var expired = LicenseInfo.CreatePro("PRO-OLD", "User", expiration: DateTimeOffset.UtcNow.AddMinutes(-1));
        LicenseLoadSanitizer.Sanitize(expired, LicensePersistenceTrust.TrustInstalledPayload)
            .Type.Should().Be(LicenseType.Free);
        expired.IsFeatureLicensed(FeatureId.ColorDetection).Should().BeFalse();
    }

    [Fact]
    public void ClearLicense_RevertsToFreeTier()
    {
        var service = new LocalLicenseService(_testLicensePath);
        var vm = new LicensingViewModel(service);

        vm.LicenseKeyInput = "PRO-KEY-TEST-123";
        vm.InstallLicenseCommand.Execute(null);
        vm.LicenseTier.Should().Be("FREE");

        // Clearing a populated store is independent of whether this build permits admin activation.
        // The fixture owns this synthetic license and file; the separate command test covers the gate.
        service.InstallLicense(LicenseInfo.CreateAdministrator("SYNTHETIC-CLEAR-FIXTURE", "Administrator",
            "fixture@example.invalid", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue));
        vm.LicenseTier.Should().Be("ADMINISTRATOR");
        vm.IsRootAuthority.Should().BeTrue();
        vm.Features.Should().OnlyContain(f => f.IsLicensed);

        vm.ClearLicenseCommand.Execute(null);

        vm.LicenseTier.Should().Be("FREE");
        vm.StatusMessage.Should().Contain("Reverted to Free tier");
        vm.Features.First(f => f.FeatureName == nameof(FeatureId.AIVision)).IsLicensed.Should().BeFalse();
        vm.IsRootAuthority.Should().BeFalse();
        File.Exists(_testLicensePath).Should().BeFalse();
    }

    [Fact]
    public void ApplyAdminKey_HonorsBuildProfile_AndReportsExactEntitlements()
    {
        var service = new LocalLicenseService(_testLicensePath);
        var vm = new LicensingViewModel(service);

        vm.EmailInput = "dev@zenithmacros.dev";
        vm.ApplyAdminKeyCommand.Execute(null);

#if DEVELOPER_MODE
        vm.IsDeveloperMode.Should().BeTrue();
        vm.LicenseTier.Should().Be("ADMINISTRATOR");
        vm.CustomerId.Should().Be("Administrator");
        vm.LicensedEmail.Should().Be("dev@zenithmacros.dev");
        vm.IsRootAuthority.Should().BeTrue();
        vm.ProductAccessText.Should().Be("Developer access");
        vm.EntitlementExplanation.Should().Contain("do not confirm a purchased product license");
        vm.ExpirationDateText.Should().Be("Never");
        vm.ExpirationText.Should().Be("Permanent License");
        vm.UnlockedCountText.Should().Be("10/10 unlocked");
        vm.Features.Should().OnlyContain(f => f.IsLicensed);
#else
        vm.IsDeveloperMode.Should().BeFalse();
        vm.BuildProfileText.Should().Be("Public Release");
        vm.LicenseTier.Should().Be("FREE");
        vm.IsRootAuthority.Should().BeFalse();
        vm.ProductAccessText.Should().Be("Free");
        vm.StatusMessage.Should().Be("Master Admin Key activation is restricted to internal developer builds.");
        vm.UnlockedCountText.Should().Be("2/10 unlocked");
        vm.Features.Where(f => f.FeatureName is not (nameof(FeatureId.BasicPlayback) or nameof(FeatureId.BasicMacroRecording)))
            .Should().OnlyContain(f => !f.IsLicensed);
        File.Exists(_testLicensePath).Should().BeFalse();
#endif
    }
}
