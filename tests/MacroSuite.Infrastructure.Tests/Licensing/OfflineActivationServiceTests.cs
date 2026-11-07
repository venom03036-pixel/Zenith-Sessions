using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MacroSuite.Core.Licensing;
using MacroSuite.Infrastructure.Licensing;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Licensing;

public sealed class OfflineActivationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Zenith-activation-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task DeviceBoundLicensePersistsButInvalidImportPreservesIt()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(issuer); using var device = new FakeDevice();
        using (var activation = new OfflineActivationService(trust, _directory, device))
        {
            Assert.False((await activation.RefreshAsync()).Success); Assert.Throws<ActivationRequiredException>(activation.RequireActive);
            var request = await activation.CreateRequestAsync(); Assert.True(request.Success, request.Message);
            Assert.True((await activation.ImportAsync(Sign(issuer, trust, device))).Success); activation.RequireActive();
            using var adapter = new PublicActivationLicenseService(activation);
            Assert.True(adapter.IsFeatureLicensed(FeatureId.ImageRecognition)); Assert.False(adapter.IsFeatureLicensed(FeatureId.AIVision));
            Assert.False((await activation.ImportAsync("{invalid}")).Success); Assert.True(activation.IsActive);
            string stored = await File.ReadAllTextAsync(Path.Combine(_directory, "activation-v2.bin")); Assert.DoesNotContain("TEST-CUSTOMER", stored);
        }
        using var reloaded = new OfflineActivationService(trust, _directory, device); Assert.True((await reloaded.RefreshAsync()).Success);
        device.PossessesKey = false; Assert.False((await reloaded.RefreshAsync()).Success); Assert.False(reloaded.IsActive);
    }
    [Fact]
    public async Task CopiedGrantAndCorruptSavedStateNeverUnlockAnotherDevice()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(issuer); using var device = new FakeDevice { PossessesKey = false };
        using var activation = new OfflineActivationService(trust, _directory, device);
        Assert.False((await activation.ImportAsync(Sign(issuer, trust, device))).Success); Assert.False(activation.IsActive);
        Directory.CreateDirectory(_directory); await File.WriteAllBytesAsync(Path.Combine(_directory, "activation-v2.bin"), new byte[] { 1, 2, 3 });
        Assert.False((await activation.RefreshAsync()).Success); Assert.Throws<ActivationRequiredException>(activation.RequireActive);
    }
    [Fact]
    public async Task SaveFailureDoesNotClearValidInMemoryGrant()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var trust = Trust(issuer); using var device = new FakeDevice();
        using var activation = new OfflineActivationService(trust, _directory, device);
        Assert.True((await activation.ImportAsync(Sign(issuer, trust, device))).Success);
        using var locked = new FileStream(Path.Combine(_directory, "activation-v2.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False((await activation.ImportAsync(Sign(issuer, trust, device))).Success); Assert.True(activation.IsActive);
    }
    private static ActivationTrustManifest Trust(ECDsa key)
    { byte[] spki = key.ExportSubjectPublicKeyInfo(); string id = ActivationWireFormat.KeyId(spki); return new(id, new[] { new ActivationTrustKey(id, Convert.ToBase64String(spki)) }); }
    private static string Sign(ECDsa issuer, ActivationTrustManifest trust, FakeDevice device)
    {
        var payload = new ActivationLicensePayload
        {
            LicenseId = "TEST-LICENSE", CustomerId = "TEST-CUSTOMER", ActivationId = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"),
            DeviceKeyId = device.Identity.KeyId, DeviceProtection = device.Identity.Protection, IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ActivationWireFormat.JsonOptions);
        return JsonSerializer.Serialize(new { formatVersion = 2, keyId = trust.ActiveKeyId, payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(issuer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) });
    }
    private sealed class FakeDevice : IDeviceBindingService, IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public DeviceBindingIdentity Identity { get; }
        public bool PossessesKey { get; set; } = true;
        public FakeDevice()
        { byte[] spki = _key.ExportSubjectPublicKeyInfo(); Identity = new(ActivationWireFormat.KeyId(spki), Convert.ToBase64String(spki), ActivationProduct.TpmProtection); }
        public Task<DeviceBindingIdentity> GetOrCreateAsync(bool allowSoftwareFallback, CancellationToken ct = default) => Task.FromResult(Identity);
        public Task<byte[]> SignAsync(string protection, byte[] challenge, CancellationToken ct = default) => Task.FromResult(_key.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        public Task<bool> ProvePossessionAsync(VerifiedLicense license, CancellationToken ct = default) => Task.FromResult(PossessesKey && license.DeviceKeyId == Identity.KeyId);
        public void Dispose() => _key.Dispose();
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
