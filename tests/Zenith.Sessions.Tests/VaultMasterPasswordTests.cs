using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Owned temporary stores, synthetic sessions, real CurrentUser DPAPI; never a real password or account.
public sealed class VaultMasterPasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-master-password-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Password = "synthetic vault password 2"u8.ToArray();
    private static readonly byte[] Wrong = "synthetic wrong password"u8.ToArray();
    private string VaultRoot => Path.Combine(_root, "accounts.vault");
    private string ControlPath => Path.Combine(VaultRoot, ProtectedCredentialVault.PasswordControlName);
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    private static AuthorizationRecord Session(Guid id, long user = 123, long generation = 1) => new(id,
        RobloxSessionIdentity.Provider, user, generation, "", "", DateTimeOffset.MaxValue, "Synthetic_Account",
        Issuer: RobloxSessionIdentity.Issuer, Subject: user.ToString(System.Globalization.CultureInfo.InvariantCulture), SessionCookie: "synthetic-master-session-123");
    private JsonObject Envelope(Guid id) => JsonNode.Parse(File.ReadAllBytes(RecordPath(id)).AsSpan("ZENITH-VAULT\n"u8.Length))!.AsObject();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task DefaultRemainsV2AndEnablingMigratesWithoutChangingIdentityOrGeneration()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); var snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        Assert.Equal(2, Envelope(record.ProfileId)["Version"]!.GetValue<int>());
        Assert.False(File.Exists(ControlPath)); Assert.Equal(VaultPasswordMode.Disabled, (await vault.GetPasswordStatusAsync(default)).Mode);
        await vault.EnableMasterPasswordAsync(Password, default);
        Assert.Equal(3, Envelope(record.ProfileId)["Version"]!.GetValue<int>());
        Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(VaultPasswordMode.Unlocked, (await vault.GetPasswordStatusAsync(default)).Mode);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(snapshot, record with { Generation = 2 }, Guid.NewGuid(), default)).Status);
        Assert.Empty(Directory.EnumerateFiles(VaultRoot, "*.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AValidLegacyV1FileNeedsExplicitReauthenticationWithoutInventingVerifiedIdentity()
    {
        var id = Guid.NewGuid(); Directory.CreateDirectory(VaultRoot);
        var old = new AuthorizationRecord(id, RobloxOAuthIdentity.Provider, 123, 1, "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1));
        byte[] clear = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(old);
        byte[] entropy = Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v1/" + id.ToString("N"));
        byte[] cipher;
        try { cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(entropy); }
        File.WriteAllBytes(RecordPath(id), cipher);
        using var vault = new ProtectedCredentialVault(_root);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.ReadAsync(id, default))!.Health);
        await Assert.ThrowsAsync<VaultPasswordLegacyMigrationException>(() => vault.EnableMasterPasswordAsync(Password, default));
        Assert.False(File.Exists(ControlPath)); Assert.Equal(cipher, File.ReadAllBytes(RecordPath(id)));
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.ReadAsync(id, default))!.Health);
    }

    [Fact]
    public async Task LockWhileDispatchWaitsPreventsTheNativeCallbackAndSuccessfulUnlockNeedsANewRequest()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        using var held = new FileStream(Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        bool called = false;
        var dispatch = vault.TryDispatchCurrentAsync(snapshot, () => { called = true; return null!; }, default);
        vault.LockMasterPassword(); held.Dispose();
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => dispatch); Assert.False(called);
        await vault.UnlockMasterPasswordAsync(Password, default);
        Assert.False(called); Assert.Equal(VaultPasswordMode.Unlocked, (await vault.GetPasswordStatusAsync(default)).Mode);
    }

    [Fact]
    public async Task AcceptedDispatchDrainsAfterLockWithoutBlockingItOrDiscardingTheActualReceipt()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var receipt = new LaunchProcessReceipt(700001, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow);
        var dispatch = vault.TryDispatchCurrentAsync(snapshot, () =>
        {
            entered.TrySetResult(); release.Wait();
            return new(NativeHandoffDispatchOutcome.StartedUnverified, receipt);
        }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((true, false), ReadDispatchState(vault));
            await Task.Run(vault.LockMasterPassword).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(dispatch.IsCompleted); Assert.Equal((true, true), ReadDispatchState(vault));
        }
        finally { release.Set(); await dispatch; }
        var result = await dispatch;
        Assert.Same(receipt, result.Dispatch!.ProcessReceipt); Assert.Equal((false, false), ReadDispatchState(vault));
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
    }
    private static (bool, bool) ReadDispatchState(ProtectedCredentialVault vault) =>
        typeof(ProtectedCredentialVault).GetProperty("PasswordDispatchState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vault) is ValueTuple<bool, bool> state ? state : default;

    [Fact]
    public async Task LockBlocksSecretsButKeepsMetadataAndWrongPasswordDoesNotUnlock()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var key = (byte[])typeof(ProtectedCredentialVault).GetField("_dataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vault)!;
        vault.LockMasterPassword(); Assert.All(key, value => Assert.Equal(0, value));
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
        Assert.Equal(record.VerifiedUserId, (await vault.GetSummaryAsync(record.ProfileId, default))!.VerifiedUserId);
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.ReadAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.SaveAsync(record with { Generation = 2 }, default));
        await Assert.ThrowsAsync<CryptographicException>(() => vault.UnlockMasterPasswordAsync(Wrong, default));
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
        await vault.UnlockMasterPasswordAsync(Password, default); Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task ReopenStartsLockedAndControlContainsOnlyThePasswordWrappedKey()
    {
        var record = Session(Guid.NewGuid()); byte[] key;
        using (var vault = new ProtectedCredentialVault(_root))
        {
            await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
            key = ((byte[])typeof(ProtectedCredentialVault).GetField("_dataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vault)!).ToArray();
        }
        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(ControlPath), "ZenithSessions/Personal/vault-key/v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try
        {
            var control = JsonNode.Parse(clear)!;
            Assert.True(control["PasswordRequired"]!.GetValue<bool>()); Assert.Equal(600000, control["Iterations"]!.GetValue<int>());
            Assert.NotEqual(key, Convert.FromBase64String(control["WrappedKey"]!.GetValue<string>()));
            Assert.DoesNotContain(Convert.ToBase64String(key), Encoding.UTF8.GetString(clear));
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key); }
        using var reopened = new ProtectedCredentialVault(_root);
        Assert.Equal(VaultPasswordMode.Locked, (await reopened.GetPasswordStatusAsync(default)).Mode);
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => reopened.ReadAsync(record.ProfileId, default));
        await reopened.UnlockMasterPasswordAsync(Password, default); Assert.Equal(record, await reopened.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task ReplayingAnOldV2RecordCannotBypassPasswordModeEvenWhenUnlocked()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); byte[] old = File.ReadAllBytes(RecordPath(record.ProfileId));
        await vault.EnableMasterPasswordAsync(Password, default); File.WriteAllBytes(RecordPath(record.ProfileId), old);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(old, File.ReadAllBytes(RecordPath(record.ProfileId)));
    }

    [Fact]
    public async Task DisableRequiresThePasswordAndRewrapsOnlyTheKey()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        byte[] recordBytes = File.ReadAllBytes(RecordPath(record.ProfileId));
        await Assert.ThrowsAsync<CryptographicException>(() => vault.DisableMasterPasswordAsync(Wrong, default));
        await vault.DisableMasterPasswordAsync(Password, default);
        Assert.Equal(recordBytes, File.ReadAllBytes(RecordPath(record.ProfileId)));
        vault.LockMasterPassword(); Assert.Equal(VaultPasswordMode.Disabled, (await vault.GetPasswordStatusAsync(default)).Mode);
        Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
        using var reopened = new ProtectedCredentialVault(_root); Assert.Equal(record, await reopened.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task LockWhileUnlockWaitsForTheProcessLeasePreventsLateKeyInstallation()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default); vault.LockMasterPassword();
        using var held = new FileStream(Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var unlock = vault.UnlockMasterPasswordAsync(Password, default);
        vault.LockMasterPassword(); held.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlock);
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task RemovingOrCorruptingTheControlNeverFallsBackToAReadableV2Grant()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        byte[] original = File.ReadAllBytes(ControlPath); File.WriteAllBytes(ControlPath, new byte[100]);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
        File.Delete(ControlPath);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
        using (var reopened = new ProtectedCredentialVault(_root))
        {
            await Assert.ThrowsAsync<CryptographicException>(() => reopened.GetPasswordStatusAsync(default));
            await Assert.ThrowsAsync<CryptographicException>(() => reopened.ReadAsync(record.ProfileId, default));
        }
        File.WriteAllBytes(ControlPath, original);
    }

    [Fact]
    public async Task AChangedGcmTagCannotReleaseCredentialMaterial()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var envelope = Envelope(record.ProfileId); envelope["Tag"] = Convert.ToBase64String(new byte[16]);
        File.WriteAllText(RecordPath(record.ProfileId), "ZENITH-VAULT\n" + envelope.ToJsonString());
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task InterruptedMigrationBlocksBothV2AndV3UntilExplicitSamePasswordResume()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var first = Session(Guid.Parse("00000000-0000-0000-0000-000000000001"), 456);
        var record = Session(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await vault.SaveAsync(first, default); await vault.SaveAsync(record, default);
        using var blocked = new FileStream(RecordPath(record.ProfileId), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new CancellationTokenSource();
        var migration = vault.EnableMasterPasswordAsync(Password, stop.Token);
        bool firstConverted = false;
        try
        {
            for (int i = 0; i < 500 && !firstConverted; i++)
            {
                try { firstConverted = Envelope(first.ProfileId)["Version"]!.GetValue<int>() == 3; }
                catch (IOException) { } // Atomic replacement can briefly refuse this independent fixture reader.
                if (!firstConverted) await Task.Delay(10);
            }
            Assert.True(firstConverted); Assert.Equal(2, Envelope(record.ProfileId)["Version"]!.GetValue<int>());
            Assert.True(File.Exists(ControlPath)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
        }
        finally
        {
            stop.Cancel(); blocked.Dispose();
            try { await migration; } catch (Exception) { } // Join the owner even if a fixture assertion fails.
        }
        blocked.Dispose(); vault.LockMasterPassword();
        Assert.Equal(VaultPasswordMode.MigrationRequired, (await vault.GetPasswordStatusAsync(default)).Mode);
        await Assert.ThrowsAsync<VaultPasswordMigrationException>(() => vault.ReadAsync(record.ProfileId, default));
        using (var reopened = new ProtectedCredentialVault(_root))
        {
            Assert.Equal(VaultPasswordMode.MigrationRequired, (await reopened.GetPasswordStatusAsync(default)).Mode);
            await Assert.ThrowsAsync<VaultPasswordMigrationException>(() => reopened.ReadAsync(first.ProfileId, default));
            await Assert.ThrowsAsync<VaultPasswordMigrationException>(() => reopened.ReadAsync(record.ProfileId, default));
        }
        await Assert.ThrowsAsync<VaultPasswordMigrationException>(() => vault.UnlockMasterPasswordAsync(Password, default));
        await Assert.ThrowsAsync<CryptographicException>(() => vault.EnableMasterPasswordAsync(Wrong, default));
        await vault.EnableMasterPasswordAsync(Password, default);
        Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default)); Assert.Equal(first, await vault.ReadAsync(first.ProfileId, default));
    }

    [Fact]
    public async Task ForgottenPasswordRecoveryInvalidatesOldSnapshotsAndAllowsFreshSignIns()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        File.WriteAllBytes(RecordPath(record.ProfileId) + ".bak", File.ReadAllBytes(RecordPath(record.ProfileId)));
        vault.LockMasterPassword(); await vault.ForgetMasterProtectedSignInsAsync(default);
        Assert.False(File.Exists(ControlPath)); Assert.False(File.Exists(RecordPath(record.ProfileId)));
        Assert.Null(await vault.ReadAsync(record.ProfileId, default));
        var fresh = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        Assert.True(fresh.HighestAllocated > snapshot.HighestAllocated);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(snapshot, record with { Generation = snapshot.HighestAllocated + 1 }, Guid.NewGuid(), default)).Status);
        Assert.Equal(VaultPasswordMode.Disabled, (await vault.GetPasswordStatusAsync(default)).Mode);
        await vault.SaveAsync(record with { Generation = fresh.HighestAllocated + 1 }, default);
        Assert.NotNull(await vault.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task UnknownCredentialCopyKeepsRecoveryFailClosedAndRetainsControl()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        string unknown = RecordPath(record.ProfileId) + ".unexpected";
        File.WriteAllBytes(unknown, File.ReadAllBytes(RecordPath(record.ProfileId)));
        await Assert.ThrowsAsync<VaultPasswordMigrationException>(() => vault.ForgetMasterProtectedSignInsAsync(default));
        Assert.True(File.Exists(ControlPath)); Assert.True(File.Exists(unknown));
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
        File.Delete(unknown); await vault.ForgetMasterProtectedSignInsAsync(default);
        Assert.False(File.Exists(ControlPath));
    }

    [Fact]
    public async Task CorruptedControlRecoveryDoesNotRequireDecryptingTheLostKey()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        File.WriteAllBytes(ControlPath, new byte[20]);
        await vault.ForgetMasterProtectedSignInsAsync(default);
        Assert.Equal(VaultPasswordMode.Disabled, (await vault.GetPasswordStatusAsync(default)).Mode);
        Assert.Null(await vault.ReadAsync(record.ProfileId, default));
    }

    [Fact]
    public async Task LockBlocksDispatchAndAnEmptySnapshotCommitWithoutInvokingTheConsumer()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        var current = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        var empty = await vault.ReadWriteSnapshotAsync(Guid.NewGuid(), default);
        vault.LockMasterPassword(); bool called = false;
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.TryDispatchCurrentAsync(current, () => { called = true; return null!; }, default));
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.TryCommitSnapshotAsync(empty, Session(empty.ProfileId, 456), Guid.NewGuid(), default));
        Assert.False(called);
    }

    [Fact]
    public async Task AnotherStoreChangingTheControlCannotLeaveAnOldUnlockedKeyUsable()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default);
        using var second = new ProtectedCredentialVault(_root);
        await second.DisableMasterPasswordAsync(Password, default);
        await second.EnableMasterPasswordAsync(Wrong, default);
        Assert.Equal(VaultPasswordMode.Locked, (await vault.GetPasswordStatusAsync(default)).Mode);
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.ReadAsync(record.ProfileId, default));
        await vault.UnlockMasterPasswordAsync(Wrong, default); Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
    }

    [Theory]
    [InlineData("Iterations")]
    [InlineData("KeyId")]
    [InlineData("Mode")]
    public async Task AuthenticatedControlCannotBeChangedIntoAKeyRecoveryBypass(string field)
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default); await vault.EnableMasterPasswordAsync(Password, default); vault.LockMasterPassword();
        // Same-user DPAPI access still cannot decrypt the password wrapper. Even a syntactically
        // valid forged disabled control contains random wrapper bytes, not the data key.
        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(ControlPath), "ZenithSessions/Personal/vault-key/v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try
        {
            var control = JsonNode.Parse(clear)!;
            if (field == "Iterations") control["Iterations"] = int.MaxValue;
            else if (field == "KeyId") control["KeyId"] = Guid.NewGuid();
            else
            {
                control["PasswordRequired"] = false; control["Iterations"] = 0;
                control["Salt"] = ""; control["Nonce"] = ""; control["Tag"] = "";
            }
            byte[] forged = Encoding.UTF8.GetBytes(control.ToJsonString());
            try { File.WriteAllBytes(ControlPath, ProtectedData.Protect(forged, "ZenithSessions/Personal/vault-key/v1"u8.ToArray(), DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(forged); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        if (field != "Mode") await Assert.ThrowsAsync<CryptographicException>(() => vault.UnlockMasterPasswordAsync(Password, default));
        if (field == "KeyId") await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => vault.ReadAsync(record.ProfileId, default));
        else await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
    }
}
