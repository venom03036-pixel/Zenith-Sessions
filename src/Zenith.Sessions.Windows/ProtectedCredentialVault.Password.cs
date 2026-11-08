using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class ProtectedCredentialVault
{
    internal const string PasswordControlName = "vault-key.dpapi-control";
    internal const int PasswordIterations = 600000, MaximumMigrationRecords = 10000;
    private static readonly byte[] KeyEntropy = "ZenithSessions/Personal/vault-key/v1"u8.ToArray();
    private readonly object _keyGate = new();
    private byte[]? _dataKey;
    private string? _passwordControlDigest;
    private Guid _dataKeyId;
    private bool _passwordRequired, _passwordMigrating;
    private bool _checkedLegacyInventory;
    private long _passwordEpoch;
    private AcceptedPasswordDispatch? _acceptedPasswordDispatch;
    private string PasswordControlPath => Path.Combine(_directory, PasswordControlName);

    /// <summary>Atomically fences key installation and dispatch acceptance; never waits for a native callback.</summary>
    public void LockMasterPassword()
    {
        lock (_keyGate)
        {
            Interlocked.Increment(ref _passwordEpoch); ClearDataKey();
            if (_acceptedPasswordDispatch is { } accepted) accepted.LockRequested = true;
        }
    }

    // Non-secret, operation-local acceptance state. Once accepted, the existing native owner owns
    // cancellation/drain and any real receipt. Lock can mark it but cannot pretend it never started.
    internal (bool InFlight, bool LockRequested) PasswordDispatchState
    { get { lock (_keyGate) return (_acceptedPasswordDispatch is not null, _acceptedPasswordDispatch?.LockRequested == true); } }
    private AcceptedPasswordDispatch AcceptPasswordDispatch(long epoch, CancellationToken ct)
    {
        lock (_keyGate)
        {
            EnsureSecretAccess(); CheckPasswordEpoch(epoch, ct);
            if (_acceptedPasswordDispatch is not null) throw new InvalidOperationException("A dispatch already owns this vault boundary.");
            return _acceptedPasswordDispatch = new(this);
        }
    }
    private sealed class AcceptedPasswordDispatch(ProtectedCredentialVault owner) : IDisposable
    {
        internal bool LockRequested;
        public void Dispose()
        {
            lock (owner._keyGate)
                if (ReferenceEquals(owner._acceptedPasswordDispatch, this)) owner._acceptedPasswordDispatch = null;
        }
    }

    public async Task<VaultPasswordStatus> GetPasswordStatusAsync(CancellationToken ct)
    {
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        lock (_keyGate) return new(_passwordMigrating ? VaultPasswordMode.MigrationRequired : !_passwordRequired
            ? VaultPasswordMode.Disabled : _dataKey is null ? VaultPasswordMode.Locked : VaultPasswordMode.Unlocked);
    }

    public async Task UnlockMasterPasswordAsync(ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        long epoch = Volatile.Read(ref _passwordEpoch);
        byte[] ownedPassword = CopyPassword(password, creating: false), key = [];
        try
        {
            using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            using var control = ReadPasswordControlOwned() ?? throw PasswordFailure();
            if (!control.PasswordRequired) return;
            if (control.Migrating) throw new VaultPasswordMigrationException();
            key = UnwrapDataKey(control, ownedPassword);
            CheckPasswordEpoch(epoch, ct);
            InstallDataKey(key, epoch, ct);
        }
        finally { CryptographicOperations.ZeroMemory(ownedPassword); CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Enable, or explicitly resume an interrupted first conversion with the same password.</summary>
    public async Task EnableMasterPasswordAsync(ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        long epoch = Volatile.Read(ref _passwordEpoch);
        byte[] ownedPassword = CopyPassword(password, creating: true), key = [];
        PasswordControl? pending = null;
        try
        {
            using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            using var previous = ReadPasswordControlOwned();
            var paths = MigrationPaths();
            if (previous is { PasswordRequired: true, Migrating: false })
                throw new InvalidOperationException("The master password is already enabled. Disable it explicitly before choosing a replacement.");
            if (previous is { PasswordRequired: true })
            {
                key = UnwrapDataKey(previous, ownedPassword);
                InstallDataKey(key, epoch, ct);
                pending = previous.Copy();
            }
            else
            {
                // Validate every current record before publishing the fail-closed transition marker.
                foreach (string path in paths)
                {
                    CheckPasswordEpoch(epoch, ct);
                    byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
                    try
                    {
                        Guid id = ProfileFromPath(path);
                        _ = ReadProtectedBytes(bytes, id, out bool legacy);
                        if (legacy) throw new VaultPasswordLegacyMigrationException();
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                key = previous is null ? RandomNumberGenerator.GetBytes(32) : UnwrapDataKey(previous, ownedPassword);
                Guid keyId = previous?.KeyId ?? Guid.NewGuid();
                pending = WrapDataKey(keyId, key, ownedPassword, migrating: true);
                CheckPasswordEpoch(epoch, ct);
                await WritePasswordControlOwnedAsync(pending, ct).ConfigureAwait(false);
                InstallDataKey(key, epoch, ct);
            }

            foreach (string path in paths)
            {
                CheckPasswordEpoch(epoch, ct);
                byte[] original = ReadPrivateBytes(path, MaximumFileBytes), replacement = [];
                try
                {
                    Guid id = ProfileFromPath(path);
                    _ = ReadProtectedBytes(original, id, out _, allowMigration: true);
                    var envelope = ParseEnvelope(original, id);
                    try
                    {
                        if (envelope.Version == Version)
                        {
                            var wrapped = SealPayloadOwned(envelope.Metadata, envelope.ProtectedPayload, allowMigration: true);
                            try { replacement = EnvelopeBytes(wrapped); }
                            finally { ClearEnvelopeCipher(wrapped); }
                            CheckPasswordEpoch(epoch, ct);
                            await AtomicWriteAsync(path, replacement, ct).ConfigureAwait(false);
                        }
                        DeleteObsoleteCopies(path);
                    }
                    finally { ClearEnvelopeCipher(envelope); }
                }
                finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(replacement); }
            }
            CheckPasswordEpoch(epoch, ct);
            VerifyMigrationComplete();
            using var ready = pending.Copy(migrating: false);
            await WritePasswordControlOwnedAsync(ready, ct).ConfigureAwait(false);
            InstallDataKey(key, epoch, ct);
        }
        finally { pending?.Dispose(); CryptographicOperations.ZeroMemory(ownedPassword); CryptographicOperations.ZeroMemory(key); }
    }

    public async Task DisableMasterPasswordAsync(ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        long epoch = Volatile.Read(ref _passwordEpoch);
        byte[] ownedPassword = CopyPassword(password, creating: false), key = [];
        try
        {
            using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            using var previous = ReadPasswordControlOwned() ?? throw PasswordFailure();
            if (previous.Migrating) throw new VaultPasswordMigrationException();
            if (!previous.PasswordRequired) return;
            key = UnwrapDataKey(previous, ownedPassword);
            using var disabled = new PasswordControl(1, previous.KeyId, false, false, 0, [], [], [], key.ToArray());
            CheckPasswordEpoch(epoch, ct);
            await WritePasswordControlOwnedAsync(disabled, ct).ConfigureAwait(false);
            CheckPasswordEpoch(epoch, ct);
        }
        finally { CryptographicOperations.ZeroMemory(ownedPassword); CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Explicit destructive recovery: retain metadata/tombstones, invalidate and delete sign-ins before removing the key.</summary>
    public async Task ForgetMasterProtectedSignInsAsync(CancellationToken ct)
    {
        LockMasterPassword();
        using var lease = await EnterAsync(ct, skipPasswordControl: true).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var profiles = new HashSet<Guid>(); int inspected = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.dpapi*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (++inspected > MaximumMigrationRecords * 4) throw new VaultPasswordMigrationException();
            string name = Path.GetFileName(path);
            if (name == PasswordControlName || IsAtomicTemporaryOf(name, PasswordControlName)) continue;
            if (name.Length < 38 || !Guid.TryParseExact(name[..32], "N", out var id) || id == Guid.Empty || !name[32..].StartsWith(".dpapi", StringComparison.Ordinal))
                throw new VaultPasswordMigrationException();
            profiles.Add(id);
            if (profiles.Count > MaximumMigrationRecords) throw new VaultPasswordMigrationException();
        }
        foreach (Guid id in profiles) { ct.ThrowIfCancellationRequested(); await DeleteOwnedAsync(id, ct).ConfigureAwait(false); }
        DeleteOrphanedTemporaries(PasswordControlPath, PasswordControlPath);
        // Unknown/undeleted copies keep the password control in place; never leave a mixed library silently downgraded.
        if (Directory.EnumerateFiles(_directory, "*.dpapi*", SearchOption.TopDirectoryOnly).Any(path => Path.GetFileName(path) != PasswordControlName))
            throw new VaultPasswordMigrationException();
        ct.ThrowIfCancellationRequested(); DeletePrivateFile(PasswordControlPath);
        lock (_keyGate)
        {
            ClearDataKey(); _passwordControlDigest = null; _dataKeyId = Guid.Empty; _passwordRequired = false; _passwordMigrating = false;
        }
    }

    private static byte[] CopyPassword(ReadOnlyMemory<byte> password, bool creating)
    {
        if (password.Length < (creating ? 12 : 1) || password.Length > 1024)
            throw new ArgumentException(creating ? "Use a master password of at least 12 UTF-8 bytes and at most 1,024 bytes." : "The password length is invalid.");
        return password.ToArray();
    }

    private void RefreshPasswordControlOwned()
    {
        PasswordControl? read;
        try { read = ReadPasswordControlOwned(); }
        catch { lock (_keyGate) ClearDataKey(); throw; }
        using var control = read;
        if (control is null)
        {
            lock (_keyGate)
            {
                if (_passwordControlDigest is not null) { ClearDataKey(); throw PasswordFailure(); }
                _dataKeyId = Guid.Empty; _passwordRequired = false; _passwordMigrating = false;
            }
            // On a new store instance, missing key control must not expose the v2 half of
            // an orphaned mixed migration. This is a bounded startup check, not an attacker-proof rollback detector.
            if (!_checkedLegacyInventory)
            {
                foreach (string path in MigrationPaths())
                {
                    byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
                    try
                    {
                        if (!HasMagic(bytes)) continue;
                        var envelope = ParseEnvelope(bytes, ProfileFromPath(path));
                        try { if (envelope.Version == 3) throw PasswordFailure(); }
                        finally { ClearEnvelopeCipher(envelope); }
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                _checkedLegacyInventory = true;
            }
            return;
        }
        string digest = control.Digest;
        lock (_keyGate)
        {
            if (_passwordControlDigest != digest) ClearDataKey();
            _passwordControlDigest = digest; _dataKeyId = control.KeyId;
            _passwordRequired = control.PasswordRequired; _passwordMigrating = control.Migrating;
            if (!control.PasswordRequired && _dataKey is null) _dataKey = control.WrappedKey.ToArray();
        }
    }

    private PasswordControl? ReadPasswordControlOwned()
    {
        string path = PasswordControlPath;
        if (!File.Exists(path)) return null;
        byte[] bytes = ReadPrivateBytes(path, 8192), clear = [];
        PasswordControl? control = null;
        try
        {
            clear = ProtectedData.Unprotect(bytes, KeyEntropy, DataProtectionScope.CurrentUser);
            control = JsonSerializer.Deserialize<PasswordControl>(clear, JsonOptions) ?? throw PasswordFailure();
            if (control.Version != 1 || control.KeyId == Guid.Empty || control.WrappedKey is not { Length: 32 }
                || control.Salt is null || control.Nonce is null || control.Tag is null
                || control.PasswordRequired && (control.Iterations != PasswordIterations || control.Salt.Length != 32 || control.Nonce.Length != 12 || control.Tag.Length != 16)
                || !control.PasswordRequired && (control.Migrating || control.Iterations != 0 || control.Salt.Length != 0 || control.Nonce.Length != 0 || control.Tag.Length != 0))
                throw PasswordFailure();
            control.Digest = Digest(bytes);
            var result = control; control = null; return result;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        { throw PasswordFailure(); }
        finally { control?.Dispose(); CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(clear); }
    }

    private async Task WritePasswordControlOwnedAsync(PasswordControl control, CancellationToken ct)
    {
        byte[] clear = [], encrypted = [];
        try
        {
            clear = JsonSerializer.SerializeToUtf8Bytes(control, JsonOptions);
            encrypted = ProtectedData.Protect(clear, KeyEntropy, DataProtectionScope.CurrentUser);
            await AtomicWriteAsync(PasswordControlPath, encrypted, ct).ConfigureAwait(false);
            RefreshPasswordControlOwned();
            DeleteOrphanedTemporaries(PasswordControlPath, PasswordControlPath);
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(encrypted); }
    }

    private static PasswordControl WrapDataKey(Guid keyId, byte[] key, byte[] password, bool migrating)
    {
        var control = new PasswordControl(1, keyId, true, migrating, PasswordIterations,
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(12), new byte[16], new byte[32]);
        byte[] derived = [], aad = KeyAad(control);
        try
        {
            derived = Rfc2898DeriveBytes.Pbkdf2(password, control.Salt, control.Iterations, HashAlgorithmName.SHA512, 32);
            using var aes = new AesGcm(derived, 16);
            aes.Encrypt(control.Nonce, key, control.WrappedKey, control.Tag, aad);
            return control;
        }
        catch { control.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(aad); }
    }
    private static byte[] UnwrapDataKey(PasswordControl control, byte[] password)
    {
        if (!control.PasswordRequired) return control.WrappedKey.ToArray();
        byte[] derived = [], aad = KeyAad(control), key = new byte[32];
        try
        {
            derived = Rfc2898DeriveBytes.Pbkdf2(password, control.Salt, control.Iterations, HashAlgorithmName.SHA512, 32);
            using var aes = new AesGcm(derived, 16);
            aes.Decrypt(control.Nonce, control.WrappedKey, control.Tag, key, aad);
            return key;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(key); throw PasswordFailure(); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(aad); }
    }
    private static byte[] KeyAad(PasswordControl control) => Encoding.UTF8.GetBytes(
        $"ZenithSessions/vault-key/v{control.Version}/{control.KeyId:N}/password/{control.Iterations}/{Convert.ToHexString(control.Salt)}");
    private static byte[] RecordAad(Guid profileId, Guid keyId) => Encoding.UTF8.GetBytes($"ZenithSessions/vault-record/v3/{keyId:N}/{profileId:N}");

    private void CheckPasswordEpoch(long epoch, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (epoch != Volatile.Read(ref _passwordEpoch)) throw new OperationCanceledException("The vault was locked during this operation."); }
    private void InstallDataKey(byte[] key, long epoch, CancellationToken ct)
    {
        lock (_keyGate)
        {
            CheckPasswordEpoch(epoch, ct);
            ClearDataKey(); _dataKey = key.ToArray();
        }
    }
    private void ClearDataKey() { if (_dataKey is not null) CryptographicOperations.ZeroMemory(_dataKey); _dataKey = null; }
    private void EnsureSecretAccess(bool allowMigration = false)
    {
        lock (_keyGate)
        {
            if (_passwordMigrating && !allowMigration) throw new VaultPasswordMigrationException();
            if (_passwordControlDigest is not null && _dataKey is null) throw new VaultPasswordRequiredException();
        }
    }

    private VaultEnvelope SealPayloadOwned(AuthorizationMetadata metadata, byte[] dpapi, bool allowMigration = false)
    {
        lock (_keyGate)
        {
            EnsureSecretAccess(allowMigration);
            if (_passwordControlDigest is null) return new(Version, metadata, Digest(dpapi), dpapi.ToArray());
            byte[] cipher = new byte[dpapi.Length], nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], aad = RecordAad(metadata.ProfileId, _dataKeyId);
            try
            {
                using var aes = new AesGcm(_dataKey!, 16);
                aes.Encrypt(nonce, dpapi, cipher, tag, aad);
                return new(3, metadata, Digest(cipher), cipher, _dataKeyId, nonce, tag);
            }
            catch { CryptographicOperations.ZeroMemory(cipher); throw; }
            finally { CryptographicOperations.ZeroMemory(aad); }
        }
    }
    private byte[] UnsealPayloadOwned(VaultEnvelope envelope, bool allowMigration)
    {
        lock (_keyGate)
        {
            EnsureSecretAccess(allowMigration);
            if (envelope.Version == Version)
            {
                if (_passwordRequired && !allowMigration) throw PasswordFailure();
                return envelope.ProtectedPayload.ToArray();
            }
            if (_passwordControlDigest is null || envelope.KeyId != _dataKeyId || _dataKey is null) throw PasswordFailure();
            byte[] dpapi = new byte[envelope.ProtectedPayload.Length], aad = RecordAad(envelope.Metadata.ProfileId, envelope.KeyId);
            try { using var aes = new AesGcm(_dataKey, 16); aes.Decrypt(envelope.Nonce!, envelope.ProtectedPayload, envelope.Tag!, dpapi, aad); return dpapi; }
            catch (CryptographicException) { CryptographicOperations.ZeroMemory(dpapi); throw PasswordFailure(); }
            finally { CryptographicOperations.ZeroMemory(aad); }
        }
    }

    private List<string> MigrationPaths()
    {
        var paths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*.dpapi", SearchOption.TopDirectoryOnly))
        {
            if (paths.Count == MaximumMigrationRecords) throw new InvalidDataException("Migrate at most 10,000 protected records per library.");
            _ = ProfileFromPath(path); RejectReparse(path); paths.Add(path);
        }
        paths.Sort(StringComparer.Ordinal); return paths;
    }
    private static Guid ProfileFromPath(string path) => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && id != Guid.Empty ? id : throw PasswordFailure();
    private void VerifyMigrationComplete()
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.dpapi*", SearchOption.TopDirectoryOnly))
        {
            if (++count > MaximumMigrationRecords * 3) throw PasswordFailure();
            string name = Path.GetFileName(path);
            if (name == PasswordControlName || name.EndsWith(".dpapi.identity", StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".dpapi", StringComparison.Ordinal)) throw new VaultPasswordMigrationException();
            byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
            try { var envelope = ParseEnvelope(bytes, ProfileFromPath(path)); try { if (envelope.Version != 3 || envelope.KeyId != _dataKeyId) throw new VaultPasswordMigrationException(); } finally { ClearEnvelopeCipher(envelope); } }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    private static byte[] EnvelopeBytes(VaultEnvelope envelope)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        try
        {
            if (json.Length + Magic.Length > MaximumFileBytes) throw InvalidRecord();
            byte[] result = new byte[Magic.Length + json.Length]; Magic.CopyTo(result, 0); json.CopyTo(result, Magic.Length); return result;
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }
    private static void ClearEnvelopeCipher(VaultEnvelope envelope)
    { CryptographicOperations.ZeroMemory(envelope.ProtectedPayload); if (envelope.Nonce is not null) CryptographicOperations.ZeroMemory(envelope.Nonce); if (envelope.Tag is not null) CryptographicOperations.ZeroMemory(envelope.Tag); }
    private static CryptographicException PasswordFailure() => new("The master password or protected vault control is invalid. Originals are preserved; no alternative key or older record was used.");

    private sealed record PasswordControl(int Version, Guid KeyId, bool PasswordRequired, bool Migrating, int Iterations,
        byte[] Salt, byte[] Nonce, byte[] Tag, byte[] WrappedKey) : IDisposable
    {
        [System.Text.Json.Serialization.JsonIgnore] public string Digest { get; set; } = "";
        public PasswordControl Copy(bool? migrating = null) => new(Version, KeyId, PasswordRequired, migrating ?? Migrating, Iterations,
            Salt.ToArray(), Nonce.ToArray(), Tag.ToArray(), WrappedKey.ToArray());
        public void Dispose() { if (Salt is not null) CryptographicOperations.ZeroMemory(Salt); if (Nonce is not null) CryptographicOperations.ZeroMemory(Nonce); if (Tag is not null) CryptographicOperations.ZeroMemory(Tag); if (WrappedKey is not null) CryptographicOperations.ZeroMemory(WrappedKey); }
    }
}
