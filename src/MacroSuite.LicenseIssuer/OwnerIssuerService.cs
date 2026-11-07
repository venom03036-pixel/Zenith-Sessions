using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Licensing;

namespace MacroSuite.LicenseIssuer;

public sealed record OwnerIdentityResult(bool Success, string Message, string? TrustManifestJson = null, string? Fingerprint = null);
public sealed record OwnerIssueResult(bool Success, string Message, string? LicenseText = null, string? LicenseId = null);
public sealed record OwnerTestLicense(int Number, string CustomerId, string LicenseId, string LicenseText)
{
    public string DisplayName => $"Test license {Number} · {CustomerId}";
}
public sealed record OwnerTestIssueResult(bool Success, string Message, IReadOnlyList<OwnerTestLicense>? Licenses = null);
public sealed record OwnerLedgerItem(string LicenseId, string CustomerId, string ActivationId, string RequestId,
    string DeviceKeyId, string Protection, DateTimeOffset IssuedUtc, string State, string? ReplacementReason, string LicenseText);

/// <summary>Owner-only signing and issuance ledger. Private material never belongs in either app package.</summary>
public sealed class OwnerIssuerService
{
    private const int MaximumLedgerBytes = 16 * 1024 * 1024;
    private static readonly byte[] s_identityEntropy = "ZenithMacros.Owner.Issuer.Identity.v2"u8.ToArray();
    private static readonly byte[] s_ledgerEntropy = "ZenithMacros.Owner.Issuer.Ledger.v2"u8.ToArray();
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string IdentityPath => Path.Combine(_root, "issuer-identity-v2.bin");
    private string LedgerPath => Path.Combine(_root, "issuance-ledger-v2.bin");
    public string OwnerDataDirectory => _root;
    public OwnerIssuerService(string? ownerDataDirectory = null)
        => _root = ownerDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenithMacrosOwner", "Issuer");

    /// <summary>Explicit one-time provisioning; an established but unreadable identity is never regenerated.</summary>
    public Task<OwnerIdentityResult> ProvisionAsync(CancellationToken ct = default) => LockedAsync(() =>
    {
        using var processLock = OpenProcessLock(); ct.ThrowIfCancellationRequested();
        bool created = !File.Exists(IdentityPath);
        if (created)
        {
            // A surviving ledger/public identity is evidence of prior provisioning: require restore.
            if (File.Exists(LedgerPath) || File.Exists(Path.Combine(_root, "issuer-trust.json")))
                return new OwnerIdentityResult(false, "An established issuer identity is missing. Restore the encrypted owner backup; a new identity was not generated.");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] privateKey = key.ExportPkcs8PrivateKey();
            try { WriteProtectedCreateOnly(IdentityPath, privateKey, s_identityEntropy); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
        }
        using var identity = LoadIdentity(); var trust = Manifest(identity); string json = trust.ToJson();
        AtomicWrite(Path.Combine(_root, "issuer-trust.json"), Encoding.UTF8.GetBytes(json));
        SelfTest(identity, trust);
        return new OwnerIdentityResult(true, created ? "Owner signing identity created. Make an encrypted backup before distribution." : "Existing owner signing identity verified.", json, trust.ActiveKeyId);
    }, ct);

    public Task<OwnerIdentityResult> GetIdentityAsync(CancellationToken ct = default) => LockedAsync(() =>
    {
        using var processLock = OpenProcessLock(); using var identity = LoadIdentity(); var trust = Manifest(identity); SelfTest(identity, trust);
        return new OwnerIdentityResult(true, "Owner signing identity is ready.", trust.ToJson(), trust.ActiveKeyId);
    }, ct);

    public async Task ExportTrustAsync(string path, CancellationToken ct = default)
    {
        OwnerIdentityResult identity = await GetIdentityAsync(ct).ConfigureAwait(false);
        if (!identity.Success) throw new InvalidOperationException(identity.Message);
        AtomicWrite(path, Encoding.UTF8.GetBytes(identity.TrustManifestJson!));
    }

    public Task<OwnerIssueResult> IssueAsync(string requestText, string customerId, bool approveSoftwareFallback = false,
        bool replaceExistingDevice = false, string? replacementReason = null, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(customerId) || customerId.Length > 128 || customerId.Any(char.IsControl))
                return new OwnerIssueResult(false, "Enter a customer reference of 1–128 characters.");
            if (!ActivationRequestCodec.TryVerify(requestText, out var request, out string? requestError)) return new(false, requestError ?? "Request is invalid.");
            if (request!.DeviceProtection == ActivationProduct.SoftwareProtection && !approveSoftwareFallback)
                return new(false, "This request uses weaker software protection. Explicit owner approval is required.");
            if (replaceExistingDevice && (string.IsNullOrWhiteSpace(replacementReason) || replacementReason.Length < 3 || replacementReason.Length > 256 || replacementReason.Any(char.IsControl)))
                return new(false, "Record a replacement reason (3–256 characters).");
            using var processLock = OpenProcessLock(); using var identity = LoadIdentity(); var ledger = ReadLedger();
            var existingRequest = ledger.FirstOrDefault(x => x.RequestId == request.RequestId);
            if (existingRequest is not null)
            {
                if (existingRequest.CustomerId != customerId.Trim() || existingRequest.DeviceKeyId != request.DeviceKeyId)
                    return new(false, "This request is already associated with another issuance.");
                if (existingRequest.State != "Active") return new(false, "This request belongs to a replaced activation. Export a new request.");
                return new(true, "The existing license for this request was returned.", existingRequest.LicenseText, existingRequest.LicenseId);
            }
            var active = ledger.Where(x => x.CustomerId.Equals(customerId.Trim(), StringComparison.OrdinalIgnoreCase) && x.State == "Active").ToArray();
            bool differentDevice = active.Any(x => x.DeviceKeyId != request.DeviceKeyId);
            if (differentDevice && !replaceExistingDevice)
                return new(false, "This customer already has an approved device. Select replacement and record the reason to approve a new device.");
            if (ledger.Count >= 5000) return new(false, "The owner ledger reached its supported entry limit. Archive it before issuing more licenses.");
            string licenseId = active.FirstOrDefault()?.LicenseId ?? "ZENITH-" + Guid.NewGuid().ToString("N");
            var payload = new ActivationLicensePayload
            {
                LicenseId = licenseId, CustomerId = customerId.Trim(), ActivationId = Guid.NewGuid().ToString("D"), RequestId = request.RequestId,
                IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = null, DeviceKeyId = request.DeviceKeyId, DeviceProtection = request.DeviceProtection,
                SoftwareFallbackApproved = request.DeviceProtection == ActivationProduct.SoftwareProtection && approveSoftwareFallback
            };
            ActivationTrustManifest trust = Manifest(identity); string envelope = Sign(identity, trust.ActiveKeyId, payload);
            if (!new ActivationLicenseVerifier(trust).TryVerify(envelope, out _, out string? error)) throw new CryptographicException("Issuer self-verification failed: " + error);
            // Reissuing on the same device supersedes earlier requests in the owner ledger, too.
            for (int i = 0; i < ledger.Count; i++)
                if (active.Contains(ledger[i])) ledger[i] = ledger[i] with { State = "Replaced", ReplacementReason = differentDevice ? replacementReason : "Reissued for the same device" };
            ledger.Add(new(licenseId, payload.CustomerId, payload.ActivationId, request.RequestId, request.DeviceKeyId, request.DeviceProtection, payload.IssuedUtc, "Active", replacementReason, envelope));
            ct.ThrowIfCancellationRequested(); WriteLedger(ledger);
            return new(true, differentDevice ? "Replacement license issued. Previous offline copies cannot be remotely revoked." : "Device-bound Standard license issued.", envelope, licenseId);
        }, ct);

    public Task<IReadOnlyList<OwnerLedgerItem>> GetLedgerAsync(CancellationToken ct = default) => LockedAsync<IReadOnlyList<OwnerLedgerItem>>(() =>
    { using var processLock = OpenProcessLock(); return ReadLedger().AsReadOnly(); }, ct);

    /// <summary>Creates a reusable, bounded test set for the PC in a verified request. These are ordinary
    /// signed Standard licenses, not universal unlock codes. Regular customer issuance is unchanged.</summary>
    public Task<OwnerTestIssueResult> IssueThreeTestLicensesAsync(string requestText, string customerReference,
        bool approveSoftwareFallback = false, CancellationToken ct = default) => LockedAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!ActivationRequestCodec.TryVerify(requestText, out var request, out string? error))
            return new OwnerTestIssueResult(false, error ?? "Import a valid activation request first.");
        if (request!.DeviceProtection == ActivationProduct.SoftwareProtection && !approveSoftwareFallback)
            return new(false, "This request uses weaker software protection. Explicit owner approval is required.");
        string reference = string.IsNullOrWhiteSpace(customerReference) ? "Local test" : customerReference.Trim();
        if (reference.Length > 128 || reference.Any(char.IsControl))
            return new(false, "Enter a test reference of at most 128 characters without control characters.");

        using var processLock = OpenProcessLock(); using var identity = LoadIdentity(); var ledger = ReadLedger();
        var existing = new OwnerLedgerItem?[3]; int existingCount = 0;
        for (int number = 1; number <= 3; number++)
        {
            existing[number - 1] = ledger.FirstOrDefault(x => x.RequestId == TestRequestId(request.RequestId, number));
            if (existing[number - 1] is not null) existingCount++;
        }
        if (existingCount != 0)
        {
            if (existingCount != 3 || existing.Any(x => x!.State != "Active" || x.DeviceKeyId != request.DeviceKeyId || x.Protection != request.DeviceProtection))
                return new(false, "This request's test set is incomplete or was replaced. Export a new activation request to create a new test set.");
            var previous = existing.Select((entry, index) => new OwnerTestLicense(index + 1, entry!.CustomerId, entry.LicenseId, entry.LicenseText)).ToArray();
            return new(true, "The same three test licenses were returned. Each activates only the PC in this request; no extra ledger entries were created.", Array.AsReadOnly(previous));
        }
        if (ledger.Count > 5000 - 3)
            return new(false, "The owner ledger does not have room for three test licenses. Archive it before issuing more licenses.");
        var trust = Manifest(identity); var verifier = new ActivationLicenseVerifier(trust);
        var licenses = new OwnerTestLicense[3];
        int referenceLength = Math.Min(reference.Length, 116);
        if (referenceLength < reference.Length && char.IsHighSurrogate(reference[referenceLength - 1])) referenceLength--;
        string testReference = reference[..referenceLength];
        for (int number = 1; number <= 3; number++)
        {
            ct.ThrowIfCancellationRequested();
            // Separate deterministic test issuance IDs keep the existing unique-request ledger contract.
            // The verified source request still supplies the device binding and protection unchanged.
            string customer = $"TEST {number} · {testReference}";
            var payload = new ActivationLicensePayload
            {
                LicenseId = "ZENITH-TEST-" + Guid.NewGuid().ToString("N"), CustomerId = customer,
                ActivationId = Guid.NewGuid().ToString("D"), RequestId = TestRequestId(request.RequestId, number),
                IssuedUtc = DateTimeOffset.UtcNow, DeviceKeyId = request.DeviceKeyId, DeviceProtection = request.DeviceProtection,
                SoftwareFallbackApproved = request.DeviceProtection == ActivationProduct.SoftwareProtection && approveSoftwareFallback
            };
            string envelope = Sign(identity, trust.ActiveKeyId, payload);
            if (!verifier.TryVerify(envelope, out _, out string? verifyError)) throw new CryptographicException("Test license self-verification failed: " + verifyError);
            licenses[number - 1] = new(number, customer, payload.LicenseId, envelope);
            ledger.Add(new(payload.LicenseId, customer, payload.ActivationId, payload.RequestId, request.DeviceKeyId,
                request.DeviceProtection, payload.IssuedUtc, "Active", "Device-bound test set", envelope));
        }
        ct.ThrowIfCancellationRequested(); WriteLedger(ledger);
        return new(true, "Three test licenses are ready. Each activates only the PC in this request. Select one to copy or save all three.", Array.AsReadOnly(licenses));
    }, ct);

    private static string TestRequestId(string requestId, int number)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ZenithMacros.TestLicense.v1/{requestId}/{number}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    public Task ExportBackupAsync(string path, string password, CancellationToken ct = default) => LockedAsync(() =>
    {
        ValidatePassword(password); using var processLock = OpenProcessLock(); using var identity = LoadIdentity();
        byte[] privateKey = identity.ExportPkcs8PrivateKey(); byte[] plain;
        try { plain = JsonSerializer.SerializeToUtf8Bytes(new OwnerBackupData(1, Convert.ToBase64String(privateKey), ReadLedger()), ActivationWireFormat.JsonOptions); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        try { AtomicWrite(path, OwnerBackupEncryption.Encrypt(plain, password)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return true;
    }, ct);

    public Task<OwnerIdentityResult> RestoreBackupAsync(string path, string password, string expectedTrustManifestJson, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            ValidatePassword(password); var expected = ActivationTrustManifest.Parse(expectedTrustManifestJson);
            byte[] backup = ReadBounded(path, MaximumLedgerBytes + 8192); byte[] plain = OwnerBackupEncryption.Decrypt(backup, password);
            byte[] privateKey = [];
            try
            {
                var data = JsonSerializer.Deserialize<OwnerBackupData>(plain, ActivationWireFormat.JsonOptions) ?? throw new FormatException("Owner backup is empty.");
                if (data.SchemaVersion != 1 || data.Ledger is null || data.Ledger.Count > 5000) throw new FormatException("Unsupported owner backup.");
                privateKey = Convert.FromBase64String(data.PrivateKeyBase64); using var restored = ECDsa.Create(); restored.ImportPkcs8PrivateKey(privateKey, out int read);
                if (read != privateKey.Length || Manifest(restored).ActiveKeyId != expected.ActiveKeyId) throw new CryptographicException("Backup identity does not match the trusted public fingerprint.");
                SelfTest(restored, expected); ValidateLedger(data.Ledger, expected);
                using var processLock = OpenProcessLock();
                if (File.Exists(IdentityPath))
                {
                    try
                    {
                        using var current = LoadIdentity();
                        if (Manifest(current).ActiveKeyId != expected.ActiveKeyId) return new OwnerIdentityResult(false, "A different valid owner identity already exists. It was not replaced.");
                    }
                    catch (CryptographicException) { /* Explicit restore repairs an unreadable identity using the pinned fingerprint. */ }
                    // Preserve the previous protected bytes before an explicit restore.
                    AtomicWrite(IdentityPath + ".before-restore", ReadBounded(IdentityPath, ActivationProduct.MaximumFileBytes));
                }
                AtomicWrite(IdentityPath, ProtectedData.Protect(privateKey, s_identityEntropy, DataProtectionScope.CurrentUser));
                WriteLedger(data.Ledger); AtomicWrite(Path.Combine(_root, "issuer-trust.json"), Encoding.UTF8.GetBytes(expected.ToJson()));
                return new OwnerIdentityResult(true, "Owner identity and ledger restored and verified.", expected.ToJson(), expected.ActiveKeyId);
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(privateKey); }
        }, ct);

    private ECDsa LoadIdentity()
    {
        if (!File.Exists(IdentityPath)) throw new InvalidOperationException("Owner identity is missing. Initialize it once, or restore its encrypted backup if previously provisioned.");
        byte[] plain = ProtectedData.Unprotect(ReadBounded(IdentityPath, ActivationProduct.MaximumFileBytes), s_identityEntropy, DataProtectionScope.CurrentUser);
        var identity = ECDsa.Create();
        try
        {
            identity.ImportPkcs8PrivateKey(plain, out int read);
            if (read != plain.Length || identity.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("Owner identity is not ECDSA P-256.");
            string trustPath = Path.Combine(_root, "issuer-trust.json");
            if (File.Exists(trustPath) && ActivationTrustManifest.Parse(Encoding.UTF8.GetString(ReadBounded(trustPath, ActivationProduct.MaximumFileBytes))).ActiveKeyId != Manifest(identity).ActiveKeyId)
                throw new CryptographicException("Owner identity does not match its established public fingerprint.");
            return identity;
        }
        catch { identity.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static ActivationTrustManifest Manifest(ECDsa identity)
    {
        byte[] publicKey = identity.ExportSubjectPublicKeyInfo(); string id = ActivationWireFormat.KeyId(publicKey);
        return new(id, new[] { new ActivationTrustKey(id, Convert.ToBase64String(publicKey)) });
    }
    private static string Sign(ECDsa identity, string id, ActivationLicensePayload payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ActivationWireFormat.JsonOptions);
        byte[] signature = identity.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new { formatVersion = 2, keyId = id, payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(signature) }, ActivationWireFormat.JsonOptions);
    }
    private static void SelfTest(ECDsa identity, ActivationTrustManifest trust)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] signature = identity.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!trust.TryGetPublicKey(trust.ActiveKeyId, out byte[] publicKey)) throw new CryptographicException("Issuer trust identity is missing.");
        using var verifier = ECDsa.Create(); verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (!verifier.VerifyData(challenge, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException("Owner identity self-test failed.");
        // Exercise the same strict grant verifier used in Public, without issuing a ledger entry.
        var payload = new ActivationLicensePayload
        {
            LicenseId = "LOCAL-SELF-TEST", CustomerId = "Local verifier self-test", ActivationId = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"),
            DeviceKeyId = ActivationWireFormat.KeyId(challenge), DeviceProtection = ActivationProduct.TpmProtection, IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        if (!new ActivationLicenseVerifier(trust).TryVerify(Sign(identity, trust.ActiveKeyId, payload), out _, out _))
            throw new CryptographicException("Owner-to-public license self-test failed.");
    }
    private List<OwnerLedgerItem> ReadLedger()
    {
        if (!File.Exists(LedgerPath)) return new();
        byte[] plain = ProtectedData.Unprotect(ReadBounded(LedgerPath, MaximumLedgerBytes + 4096), s_ledgerEntropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > MaximumLedgerBytes) throw new FormatException("Owner ledger is too large.");
            var ledger = JsonSerializer.Deserialize<List<OwnerLedgerItem>>(plain, ActivationWireFormat.JsonOptions) ?? throw new FormatException("Owner ledger is empty.");
            using var identity = LoadIdentity(); ValidateLedger(ledger, Manifest(identity)); return ledger;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void ValidateLedger(List<OwnerLedgerItem> ledger, ActivationTrustManifest trust)
    {
        if (ledger.Count > 5000) throw new FormatException("Owner ledger entry limit exceeded.");
        var verifier = new ActivationLicenseVerifier(trust); var requests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ledger)
            if (!requests.Add(entry.RequestId) || entry.State is not ("Active" or "Replaced") || !verifier.TryVerify(entry.LicenseText, out var grant, out _) || grant!.LicenseId != entry.LicenseId || grant.ActivationId != entry.ActivationId || grant.RequestId != entry.RequestId || grant.CustomerId != entry.CustomerId || grant.DeviceKeyId != entry.DeviceKeyId || grant.DeviceProtection != entry.Protection || grant.IssuedUtc != entry.IssuedUtc)
                throw new FormatException("Owner ledger failed integrity verification.");
    }
    private void WriteLedger(List<OwnerLedgerItem> ledger)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(ledger, ActivationWireFormat.JsonOptions);
        try
        {
            if (plain.Length > MaximumLedgerBytes) throw new FormatException("Owner ledger is too large.");
            AtomicWrite(LedgerPath, ProtectedData.Protect(plain, s_ledgerEntropy, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void WriteProtectedCreateOnly(string path, byte[] data, byte[] entropy)
    {
        byte[] encrypted = ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(encrypted); stream.Flush(true);
    }
    private FileStream OpenProcessLock()
    {
        Directory.CreateDirectory(_root);
        return new FileStream(Path.Combine(_root, "issuer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new FormatException("Owner file exceeds its supported size.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void ValidatePassword(string password)
    { if (password.Length < 12 || password.Length > 1024) throw new ArgumentException("Use a backup password of 12–1024 characters.", nameof(password)); }
    private async Task<T> LockedAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(operation, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private sealed record OwnerBackupData(int SchemaVersion, string PrivateKeyBase64, List<OwnerLedgerItem> Ledger);
}
