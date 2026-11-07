using FluentAssertions;
using MacroSuite.Core.Licensing;
using MacroSuite.Infrastructure.Licensing;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Licensing;

public sealed class LocalLicenseServiceTests : IDisposable
{
    private readonly string _testLicensePath;

    public LocalLicenseServiceTests()
    {
        _testLicensePath = Path.Combine(Path.GetTempPath(), $"ZenithMacros_License_Test_{Guid.NewGuid():N}.bin");
    }

    [Fact]
    public void InitialState_WithoutFile_ReturnsFreeTierLicense()
    {
        var service = new LocalLicenseService(_testLicensePath);
        var license = service.GetCurrentLicense();

        license.Type.Should().Be(LicenseType.Free);
        service.IsFeatureLicensed(FeatureId.BasicPlayback).Should().BeTrue();
        service.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();
    }

    [Fact]
    public void InstallLicense_SavesEncryptedFile_And_FiresLicenseChangedEvent()
    {
        var service = new LocalLicenseService(_testLicensePath);
        bool eventFired = false;
        service.LicenseChanged += (s, e) =>
        {
            eventFired = true;
            e.NewLicense.Type.Should().Be(LicenseType.Enterprise);
        };

        var entLicense = LicenseInfo.CreateEnterprise("ENT-XYZ", "Customer 123");
        service.InstallLicense(entLicense);

        eventFired.Should().BeTrue();
        File.Exists(_testLicensePath).Should().BeTrue();

        service.IsFeatureLicensed(FeatureId.AIVision).Should().BeTrue();
        service.IsFeatureLicensed(FeatureId.PluginExecution).Should().BeTrue();
    }

    [Fact]
    public void ReloadFromDisk_DecryptsWithDpapi_And_RestoresEntitlements()
    {
        // First instance installs pro license
        var service1 = new LocalLicenseService(_testLicensePath);
        var proLicense = LicenseInfo.CreatePro("PRO-ABC", "Professional User");
        service1.InstallLicense(proLicense);

        // Second instance points to same file and reloads
        var service2 = new LocalLicenseService(_testLicensePath);
        var loaded = service2.GetCurrentLicense();

        loaded.LicenseKey.Should().Be("PRO-ABC");
        loaded.Type.Should().Be(LicenseType.Pro);
        service2.IsFeatureLicensed(FeatureId.ColorDetection).Should().BeTrue();
        service2.IsFeatureLicensed(FeatureId.ImageRecognition).Should().BeTrue();
        service2.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();
    }

    [Fact]
    public void ClearLicense_RemovesFile_And_RevertsToFreeTier()
    {
        var service = new LocalLicenseService(_testLicensePath);
        service.InstallLicense(LicenseInfo.CreateEnterprise("ENT-CLEAR", "Corp"));

        File.Exists(_testLicensePath).Should().BeTrue();

        service.ClearLicense();

        File.Exists(_testLicensePath).Should().BeFalse();
        service.GetCurrentLicense().Type.Should().Be(LicenseType.Free);
        service.IsFeatureLicensed(FeatureId.AIVision).Should().BeFalse();
    }

    [Fact]
    public void CorruptedFile_SafelyDegradesToFreeTier()
    {
        // Write garbage bytes
        File.WriteAllBytes(_testLicensePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

        var service = new LocalLicenseService(_testLicensePath);
        var license = service.GetCurrentLicense();

        license.Type.Should().Be(LicenseType.Free);
        service.IsFeatureLicensed(FeatureId.BasicMacroRecording).Should().BeTrue();
    }

    [Fact]
    public void InstallLicense_AdministratorTier_EncryptsAndRestoresAllMetadata()
    {
        var service1 = new LocalLicenseService(_testLicensePath);
        var issueDate = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var adminLicense = LicenseInfo.CreateAdministrator(
            licenseKey: "ZENITH-ADMIN-MASTER-TEST",
            customerId: "Administrator",
            email: "owner@zenithmacros.dev",
            issued: issueDate,
            expiration: DateTimeOffset.MaxValue);

        service1.InstallLicense(adminLicense);

        var service2 = new LocalLicenseService(_testLicensePath);
        var reloaded = service2.GetCurrentLicense();

        reloaded.Type.Should().Be(LicenseType.Administrator);
        reloaded.CustomerId.Should().Be("Administrator");
        reloaded.LicensedEmail.Should().Be("owner@zenithmacros.dev");
        reloaded.IssuedUtc.Should().Be(issueDate);
        reloaded.ExpirationUtc.Should().Be(DateTimeOffset.MaxValue);
        reloaded.IsExpired.Should().BeFalse();
        reloaded.IsValid.Should().BeTrue();

        // Every single feature must be licensed
        foreach (var feature in Enum.GetValues<FeatureId>())
        {
            service2.IsFeatureLicensed(feature).Should().BeTrue();
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testLicensePath))
            {
                File.Delete(_testLicensePath);
            }
        }
        catch
        {
            // Best effort test cleanup
        }
    }
}
