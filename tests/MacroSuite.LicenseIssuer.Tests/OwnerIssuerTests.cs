using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using MacroSuite.Core.Licensing;
using Xunit;

namespace MacroSuite.LicenseIssuer.Tests;

public sealed class OwnerIssuerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Zenith-owner-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task ProvisionOnceAndIssueVerifiedGrantWithoutPrivateMaterialInTrust()
    {
        var issuer = new OwnerIssuerService(_directory); var first = await issuer.ProvisionAsync(); var second = await issuer.ProvisionAsync();
        Assert.True(first.Success); Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain("private", first.TrustManifestJson!, StringComparison.OrdinalIgnoreCase);
        using var device = RSA.Create(2048); string request = Request(device, "TPM");
        var result = await issuer.IssueAsync(request, "customer-one"); Assert.True(result.Success, result.Message);
        Assert.True(new ActivationLicenseVerifier(ActivationTrustManifest.Parse(first.TrustManifestJson!)).TryVerify(result.LicenseText!, out var grant, out var error), error);
        Assert.Equal("customer-one", grant!.CustomerId); Assert.Equal(7, grant.Features.Count);
        var same = await issuer.IssueAsync(request, "customer-one"); Assert.Equal(result.LicenseText, same.LicenseText);
        Assert.Single(await issuer.GetLedgerAsync());
        Assert.DoesNotContain("customer-one", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_directory, "issuance-ledger-v2.bin"))));
    }
    [Fact]
    public async Task SoftwareRequiresSeparateApprovalAndSecondDeviceRequiresRecordedReplacement()
    {
        var issuer = new OwnerIssuerService(_directory); await issuer.ProvisionAsync();
        using var one = RSA.Create(2048); using var two = RSA.Create(2048); string first = Request(one, "Software"); string next = Request(two, "TPM");
        Assert.False((await issuer.IssueAsync(first, "customer")).Success);
        Assert.True((await issuer.IssueAsync(first, "customer", approveSoftwareFallback: true)).Success);
        Assert.False((await issuer.IssueAsync(next, "customer")).Success);
        Assert.False((await issuer.IssueAsync(next, "customer", replaceExistingDevice: true)).Success);
        Assert.True((await issuer.IssueAsync(next, "customer", replaceExistingDevice: true, replacementReason: "Replaced the damaged computer")).Success);
        var ledger = await issuer.GetLedgerAsync(); Assert.Equal(2, ledger.Count); Assert.Single(ledger.Where(x => x.State == "Active")); Assert.Single(ledger.Where(x => x.State == "Replaced"));
        Assert.False((await issuer.IssueAsync(first, "customer", approveSoftwareFallback: true)).Success);
    }
    [Fact]
    public async Task EncryptedBackupRestoresExactIdentityAndLedgerButRejectsWrongPasswordAndFingerprint()
    {
        var issuer = new OwnerIssuerService(_directory); var identity = await issuer.ProvisionAsync(); using var device = RSA.Create(2048);
        await issuer.IssueAsync(Request(device, "TPM"), "backup-customer"); string backupPath = Path.Combine(_directory, "owner.zenithissuer");
        await issuer.ExportBackupAsync(backupPath, "a long test backup password");
        var restored = new OwnerIssuerService(Path.Combine(_directory, "restored"));
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => restored.RestoreBackupAsync(backupPath, "the wrong long test password", identity.TrustManifestJson!));
        Assert.False(File.Exists(Path.Combine(_directory, "restored", "issuer-identity-v2.bin")));
        var result = await restored.RestoreBackupAsync(backupPath, "a long test backup password", identity.TrustManifestJson!);
        Assert.True(result.Success); Assert.Equal(identity.Fingerprint, result.Fingerprint); Assert.Single(await restored.GetLedgerAsync());
        var other = new OwnerIssuerService(Path.Combine(_directory, "other")); var otherIdentity = await other.ProvisionAsync();
        await Assert.ThrowsAsync<CryptographicException>(() => restored.RestoreBackupAsync(backupPath, "a long test backup password", otherIdentity.TrustManifestJson!));
        Assert.Equal(identity.Fingerprint, (await restored.GetIdentityAsync()).Fingerprint);
    }
    [Fact]
    public async Task MissingOrUnreadableEstablishedIdentityDoesNotSilentlyRegenerate()
    {
        var issuer = new OwnerIssuerService(_directory); await issuer.ProvisionAsync(); string identityPath = Path.Combine(_directory, "issuer-identity-v2.bin");
        await File.WriteAllBytesAsync(identityPath, new byte[] { 1, 2, 3 });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => issuer.ProvisionAsync());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(identityPath));
        File.Delete(identityPath); Assert.False((await issuer.ProvisionAsync()).Success); Assert.False(File.Exists(identityPath));
    }
    [Fact]
    public async Task ThreeTestLicensesVerifyForRequestedDeviceAndRerunsPreserveLedgerAndRegularIssuance()
    {
        var issuer = new OwnerIssuerService(_directory); var identity = await issuer.ProvisionAsync();
        using var device = RSA.Create(2048); string request = Request(device, "TPM");
        var normal = await issuer.IssueAsync(request, "real-customer"); Assert.True(normal.Success, normal.Message);
        byte[] originalIdentity = await File.ReadAllBytesAsync(Path.Combine(_directory, "issuer-identity-v2.bin"));
        var batch = await issuer.IssueThreeTestLicensesAsync(request, "My local tests");
        Assert.True(batch.Success, batch.Message); Assert.Equal(3, batch.Licenses!.Count);
        Assert.Equal(3, batch.Licenses.Select(x => x.LicenseId).Distinct().Count());
        var verifier = new ActivationLicenseVerifier(ActivationTrustManifest.Parse(identity.TrustManifestJson!));
        var activationIds = new HashSet<string>(); var requestIds = new HashSet<string>();
        foreach (var item in batch.Licenses)
        {
            Assert.True(verifier.TryVerify(item.LicenseText, out var grant, out string? error), error);
            Assert.Equal(ActivationWireFormat.KeyId(device.ExportSubjectPublicKeyInfo()), grant!.DeviceKeyId);
            Assert.Equal("TPM", grant.DeviceProtection); Assert.Null(grant.ExpiresUtc); Assert.Equal(7, grant.Features.Count);
            Assert.StartsWith("TEST ", grant.CustomerId); Assert.Equal(item.CustomerId, grant.CustomerId);
            Assert.True(activationIds.Add(grant.ActivationId)); Assert.True(requestIds.Add(grant.RequestId));
        }
        byte[] firstLedger = await File.ReadAllBytesAsync(Path.Combine(_directory, "issuance-ledger-v2.bin"));
        var repeated = await issuer.IssueThreeTestLicensesAsync(request, "changed label");
        Assert.True(repeated.Success, repeated.Message); Assert.Equal(batch.Licenses, repeated.Licenses);
        Assert.Equal(firstLedger, await File.ReadAllBytesAsync(Path.Combine(_directory, "issuance-ledger-v2.bin")));
        Assert.Equal(originalIdentity, await File.ReadAllBytesAsync(Path.Combine(_directory, "issuer-identity-v2.bin")));
        var normalRepeated = await issuer.IssueAsync(request, "real-customer"); Assert.Equal(normal.LicenseText, normalRepeated.LicenseText);
        var ledger = await issuer.GetLedgerAsync(); Assert.Equal(4, ledger.Count); Assert.All(ledger, x => Assert.Equal("Active", x.State));
    }
    [Fact]
    public async Task InvalidAndUnapprovedSoftwareTestRequestsDoNotWriteLedger()
    {
        var issuer = new OwnerIssuerService(_directory); await issuer.ProvisionAsync();
        using var device = RSA.Create(2048); string software = Request(device, "Software");
        Assert.False((await issuer.IssueThreeTestLicensesAsync("{}", "test")).Success);
        Assert.False((await issuer.IssueThreeTestLicensesAsync(software, "test")).Success);
        Assert.False(File.Exists(Path.Combine(_directory, "issuance-ledger-v2.bin")));
        var approved = await issuer.IssueThreeTestLicensesAsync(software, "test", approveSoftwareFallback: true);
        Assert.True(approved.Success, approved.Message); Assert.Equal(3, (await issuer.GetLedgerAsync()).Count);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(_directory, "issuance-ledger-v2.bin"));
        Assert.False((await issuer.IssueThreeTestLicensesAsync(software, "test")).Success);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_directory, "issuance-ledger-v2.bin")));
    }
    [Fact]
    public async Task TestBundleContainsThreeIndividualLicensesAndNeverOverwritesExistingFile()
    {
        var issuer = new OwnerIssuerService(_directory); await issuer.ProvisionAsync(); using var device = RSA.Create(2048);
        var batch = await issuer.IssueThreeTestLicensesAsync(Request(device, "TPM"), "archive test"); Assert.True(batch.Success, batch.Message);
        string path = Path.Combine(_directory, "test-licenses.zip");
        await OwnerTestLicenseExport.SaveAsync(path, batch.Licenses!);
        byte[] before = await File.ReadAllBytesAsync(path);
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(4, archive.Entries.Count);
            foreach (var item in batch.Licenses!)
            {
                using var reader = new StreamReader(archive.GetEntry($"Zenith-test-{item.Number}.zlicense")!.Open());
                Assert.Equal(item.LicenseText, await reader.ReadToEndAsync());
            }
        }
        await Assert.ThrowsAsync<IOException>(() => OwnerTestLicenseExport.SaveAsync(path, batch.Licenses!));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task CancelledTestGenerationDoesNotWriteAnIssuance()
    {
        var issuer = new OwnerIssuerService(_directory); await issuer.ProvisionAsync(); using var device = RSA.Create(2048);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issuer.IssueThreeTestLicensesAsync(Request(device, "TPM"), "cancel", ct: cancellation.Token));
        Assert.Empty(await issuer.GetLedgerAsync());
    }
    [Fact]
    public async Task TestReferenceWithEmojiAtTruncationBoundarySurvivesLedgerReload()
    {
        var issuer = new OwnerIssuerService(_directory); var identity = await issuer.ProvisionAsync();
        using var device = RSA.Create(2048);
        var result = await issuer.IssueThreeTestLicensesAsync(Request(device, "TPM"), new string('x', 115) + "😀 suffix");
        Assert.True(result.Success, result.Message);
        var verifier = new ActivationLicenseVerifier(ActivationTrustManifest.Parse(identity.TrustManifestJson!));
        foreach (var item in result.Licenses!)
        {
            Assert.True(verifier.TryVerify(item.LicenseText, out var grant, out string? error), error);
            Assert.Equal(item.CustomerId, grant!.CustomerId);
            Assert.DoesNotContain('\uFFFD', item.CustomerId);
        }
        Assert.Equal(3, (await issuer.GetLedgerAsync()).Count);
    }
    private static string Request(RSA key, string protection)
    {
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ActivationRequestPayload { DeviceKeyId = ActivationWireFormat.KeyId(spki), DevicePublicKeyBase64 = Convert.ToBase64String(spki), DeviceProtection = protection }, ActivationWireFormat.JsonOptions);
        return JsonSerializer.Serialize(new { formatVersion = 2, payload = Convert.ToBase64String(bytes), proof = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
