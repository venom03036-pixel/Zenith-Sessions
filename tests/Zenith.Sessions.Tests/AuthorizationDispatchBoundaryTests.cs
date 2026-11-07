using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Real CurrentUser-DPAPI only in marked, newly owned Temp fixtures. Every dispatch callback is
// synthetic: no Process.Start, browser, account endpoint, real HWND or native Roblox operation.
public sealed class AuthorizationDispatchBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-dispatch-boundary-" + Guid.NewGuid().ToString("N"));
    private const string Marker = ".zenith-dispatch-fixture", Owner = "Zenith synthetic dispatch fixture v1";
    private const string Cookie = "synthetic-dispatch-owned-cookie", Rotated = "synthetic-dispatch-new-cookie";
    private string VaultRoot => Path.Combine(_root, "accounts.vault");
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    public AuthorizationDispatchBoundaryTests() { Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, Marker), Owner); }
    private static AuthorizationRecord Session(Guid id) => new(id, RobloxSessionIdentity.Provider, 123, 1, "", "",
        DateTimeOffset.MaxValue, "Synthetic_Dispatch", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "123", Cookie);
    private static LaunchProcessReceipt Receipt() => new(700001, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExactCurrentSavedSessionInvokesOnceAndPreservesReceiptWithoutWritingGrantData()
    {
        using var vault = new ProtectedCredentialVault(_root); var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        var snapshot = await vault.ReadWriteSnapshotAsync(current.ProfileId, default); var before = Files();
        var receipt = Receipt(); int calls = 0; bool actualFileLeaseHeld = false;
        var result = await vault.TryDispatchCurrentAsync(snapshot, () =>
        {
            calls++;
            try { using var conflicting = new FileStream(Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { actualFileLeaseHeld = true; }
            return new(NativeHandoffDispatchOutcome.StartedUnverified, receipt);
        }, default);
        Assert.Equal(1, calls); Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, result.Status);
        Assert.True(actualFileLeaseHeld);
        Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, result.Dispatch!.Outcome); Assert.Same(receipt, result.Dispatch.ProcessReceipt);
        Unchanged(before); Assert.Equal(current, await vault.ReadAsync(current.ProfileId, default));
        AssertSecretFree(result);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("expired")]
    [InlineData("needs-sign-in")]
    [InlineData("fenced-current")]
    [InlineData("oauth")]
    [InlineData("oauth-pending")]
    public async Task AnExactlyMatchingButIneligibleSnapshotNeverInvokesDispatch(string condition)
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        if (condition != "absent")
        {
            var record = Session(id);
            if (condition == "expired") record = record with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            if (condition == "needs-sign-in") record = record with { Health = AuthorizationHealth.SignInRequired };
            if (condition.StartsWith("oauth", StringComparison.Ordinal)) record = record with { Provider = RobloxOAuthIdentity.Provider,
                Issuer = RobloxOAuthIdentity.Issuer, Subject = "opaque-synthetic-subject", SessionCookie = "", RefreshToken = "synthetic-refresh",
                Health = condition == "oauth-pending" ? AuthorizationHealth.RefreshPending : AuthorizationHealth.Linked };
            await vault.SaveAsync(record, default);
            if (condition == "fenced-current") await vault.AllocateNextGenerationAsync(id, 1, default);
        }
        var snapshot = await vault.ReadWriteSnapshotAsync(id, default); var before = Files(); int calls = 0;
        var result = await vault.TryDispatchCurrentAsync(snapshot, () => { calls++; return new(NativeHandoffDispatchOutcome.NotStarted); }, default);
        AssertDenied(result, calls); Unchanged(before);
    }

    [Fact]
    public async Task LegacySelectedRecordNeverBecomesDispatchAuthority()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.ReadWriteSnapshotAsync(id, default);
        var legacy = new AuthorizationRecord(id, RobloxOAuthIdentity.Provider, 123, 1, "synthetic-legacy-access", "synthetic-legacy-refresh", DateTimeOffset.MaxValue);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(legacy), entropy = Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v1/" + id.ToString("N"));
        byte[] cipher = [];
        try { cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser); await File.WriteAllBytesAsync(RecordPath(id), cipher); }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(entropy); CryptographicOperations.ZeroMemory(cipher); }
        var snapshot = await vault.ReadWriteSnapshotAsync(id, default); var before = Files(); int calls = 0;
        var result = await vault.TryDispatchCurrentAsync(snapshot, () => { calls++; return new(NativeHandoffDispatchOutcome.NotStarted); }, default);
        AssertDenied(result, calls); Unchanged(before);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("deleted")]
    [InlineData("fenced")]
    [InlineData("hash-only")]
    [InlineData("username")]
    [InlineData("identity")]
    [InlineData("health")]
    [InlineData("owner")]
    [InlineData("hash")]
    public async Task AnyStaleOrAlteredComparisonIsDeniedWithoutCallbackOrStorageWrites(string change)
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        switch (change)
        {
            case "replaced": await vault.TryCommitSnapshotAsync(expected, record with { Generation = 2, SessionCookie = Rotated }, Guid.NewGuid(), default); break;
            case "deleted": await vault.TryDeleteSnapshotAsync(expected, default); break;
            case "fenced": await vault.AllocateNextGenerationAsync(record.ProfileId, 1, default); break;
            case "hash-only": await File.AppendAllTextAsync(RecordPath(record.ProfileId), "\n"); break;
            case "username": expected = expected with { Record = record with { VerifiedUsername = "Changed_User" } }; break;
            case "identity": expected = expected with { Record = record with { VerifiedUserId = 456, Subject = "456" } }; break;
            case "health": expected = expected with { Record = record with { Health = AuthorizationHealth.SignInRequired } }; break;
            case "owner": expected = expected with { HighestMutationId = Guid.NewGuid() }; break;
            case "hash": expected = expected with { RecordSha256 = new string('A', 64) }; break;
        }
        var before = Files(); int calls = 0;
        var result = await vault.TryDispatchCurrentAsync(expected, () => { calls++; return new(NativeHandoffDispatchOutcome.StartedUnverified, Receipt()); }, default);
        AssertDenied(result, calls); Unchanged(before);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("missing-hash")]
    [InlineData("negative-generation")]
    [InlineData("invalid-context")]
    public async Task InvalidComparisonArgumentsFailBeforeCallbackAndPreserveTheStore(string change)
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        expected = change switch
        {
            "profile" => expected with { ProfileId = Guid.Empty },
            "missing-hash" => expected with { RecordSha256 = null },
            "negative-generation" => expected with { HighestAllocated = -1 },
            _ => expected with { Record = record with { Subject = "456" } }
        };
        var before = Files(); int calls = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => vault.TryDispatchCurrentAsync(expected, () => { calls++; return new(NativeHandoffDispatchOutcome.NotStarted); }, default));
        Assert.Equal(0, calls); Unchanged(before);
    }

    [Fact]
    public async Task CancelBeforeEntryCreatesNoVaultAndCannotInvokeCallback()
    {
        using var vault = new ProtectedCredentialVault(_root); using var stop = new CancellationTokenSource(); stop.Cancel(); int calls = 0;
        var expected = new AuthorizationWriteSnapshot(Guid.NewGuid(), null, 0, 0, Guid.Empty, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryDispatchCurrentAsync(expected,
            () => { calls++; return new(NativeHandoffDispatchOutcome.NotStarted); }, stop.Token));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(VaultRoot));
    }

    [Fact]
    public async Task CancellationDuringSynchronousCallbackPreservesReceiptAndOwnsTheWriterUntilItReturns()
    {
        using var vault = new ProtectedCredentialVault(_root); using var other = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default); var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        using var release = new ManualResetEventSlim(false); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(); var receipt = Receipt(); int calls = 0;
        Task<AuthorizationDispatchResult> dispatch = vault.TryDispatchCurrentAsync(expected, () =>
        {
            calls++; entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic callback gate did not release.");
            return new(NativeHandoffDispatchOutcome.StartedUnverified, receipt);
        }, stop.Token);
        Task<AuthorizationMutationResult>? replacement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            replacement = other.TryCommitSnapshotAsync(expected, record with { Generation = 2, SessionCookie = Rotated }, Guid.NewGuid(), default);
            Assert.False(replacement.IsCompleted); stop.Cancel(); Assert.False(dispatch.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => vault.Dispose());
            using (var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.ReadWriteSnapshotAsync(record.ProfileId, waiting.Token));
            release.Set(); var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, calls); Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, result.Status);
            Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, result.Dispatch!.Outcome); Assert.Same(receipt, result.Dispatch.ProcessReceipt);
            Assert.Equal(2L, (await replacement.WaitAsync(TimeSpan.FromSeconds(3))).CommittedGeneration);
        }
        finally { release.Set(); await dispatch; if (replacement is not null) await replacement; }
        Assert.Equal(Rotated, (await vault.ReadAsync(record.ProfileId, default))!.SessionCookie);
        AssertDenied(await vault.TryDispatchCurrentAsync(expected, () => throw new InvalidOperationException("Stale callback must not run."), default), 0);
    }

    [Fact]
    public async Task QueuedCancellationNeverInvokesASecondCallbackAndDoesNotAbandonTheFirstOwner()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        using var release = new ManualResetEventSlim(false); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AuthorizationDispatchResult> first = vault.TryDispatchCurrentAsync(expected, () =>
        {
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic owner gate timed out.");
            return new(NativeHandoffDispatchOutcome.NotStarted);
        }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); using var stop = new CancellationTokenSource(); int secondCalls = 0;
            var second = vault.TryDispatchCurrentAsync(expected, () => { secondCalls++; return new(NativeHandoffDispatchOutcome.NotStarted); }, stop.Token);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, secondCalls); Assert.False(first.IsCompleted);
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public async Task CallbackExceptionIsUncertainAndRawNativeErrorNeverEscapesOrReachesAResult()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default); var before = Files(); int calls = 0;
        var result = await vault.TryDispatchCurrentAsync(expected, () =>
        { calls++; throw new InvalidOperationException("roblox-player:1+gameinfo:" + Cookie, new Exception(Rotated)); }, default);
        Assert.Equal(1, calls); Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, result.Status);
        Assert.Equal(NativeHandoffDispatchOutcome.Uncertain, result.Dispatch!.Outcome); Assert.Null(result.Dispatch.ProcessReceipt);
        AssertSecretFree(result); Unchanged(before);
        Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, (await vault.TryDispatchCurrentAsync(expected, () => new(NativeHandoffDispatchOutcome.NotStarted), default)).Status);
    }

    [Fact]
    public async Task CancellationThrownInsideAnInvokedCallbackCannotPretendThatStartupWasPrevented()
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default); using var stop = new CancellationTokenSource();
        var result = await vault.TryDispatchCurrentAsync(expected, () =>
        {
            stop.Cancel();
            throw new OperationCanceledException("roblox-player:1+gameinfo:" + Cookie, new Exception(Rotated), stop.Token);
        }, stop.Token);
        Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, result.Status);
        Assert.Equal(NativeHandoffDispatchOutcome.Uncertain, result.Dispatch!.Outcome); Assert.Null(result.Dispatch.ProcessReceipt);
        AssertSecretFree(result); Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("unknown-with-receipt")]
    [InlineData("started-without-receipt")]
    [InlineData("zero-pid")]
    [InlineData("negative-pid")]
    [InlineData("zero-start")]
    [InlineData("future-start")]
    [InlineData("missing-recorded-time")]
    [InlineData("not-started-with-receipt")]
    public async Task InvalidCallbackFactsBecomeUncertainWithoutInventedOrLostValidReceipt(string condition)
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default); var valid = Receipt();
        NativeHandoffDispatchResult? returned = condition switch
        {
            "null" => null,
            "unknown" => new((NativeHandoffDispatchOutcome)999),
            "unknown-with-receipt" => new((NativeHandoffDispatchOutcome)999, valid),
            "started-without-receipt" => new(NativeHandoffDispatchOutcome.StartedUnverified),
            "zero-pid" => new(NativeHandoffDispatchOutcome.StartedUnverified, valid with { ProcessId = 0 }),
            "negative-pid" => new(NativeHandoffDispatchOutcome.StartedUnverified, valid with { ProcessId = -1 }),
            "zero-start" => new(NativeHandoffDispatchOutcome.StartedUnverified, valid with { ProcessStartUtcTicks = 0 }),
            "future-start" => new(NativeHandoffDispatchOutcome.StartedUnverified, valid with { ProcessStartUtcTicks = valid.RecordedUtc.AddMinutes(1).UtcTicks }),
            "missing-recorded-time" => new(NativeHandoffDispatchOutcome.StartedUnverified, valid with { RecordedUtc = default }),
            _ => new(NativeHandoffDispatchOutcome.NotStarted, valid)
        };
        var result = await vault.TryDispatchCurrentAsync(expected, () => returned!, default);
        Assert.Equal(AuthorizationDispatchStatus.CallbackInvoked, result.Status); Assert.Equal(NativeHandoffDispatchOutcome.Uncertain, result.Dispatch!.Outcome);
        if (condition is "unknown-with-receipt" or "not-started-with-receipt") Assert.Same(valid, result.Dispatch.ProcessReceipt);
        else Assert.Null(result.Dispatch.ProcessReceipt);
        AssertSecretFree(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HonestNotStartedAndUncertainOutcomesAreRetained(bool uncertain)
    {
        using var vault = new ProtectedCredentialVault(_root); var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        var expected = await vault.ReadWriteSnapshotAsync(record.ProfileId, default); var receipt = uncertain ? Receipt() : null;
        var outcome = uncertain ? NativeHandoffDispatchOutcome.Uncertain : NativeHandoffDispatchOutcome.NotStarted;
        var result = await vault.TryDispatchCurrentAsync(expected, () => new(outcome, receipt), default);
        Assert.Equal(outcome, result.Dispatch!.Outcome); Assert.Same(receipt, result.Dispatch.ProcessReceipt);
    }

    private static void AssertDenied(AuthorizationDispatchResult result, int callbackCalls)
    { Assert.Equal(0, callbackCalls); Assert.Equal(AuthorizationDispatchStatus.NotAuthorized, result.Status); Assert.Null(result.Dispatch); }
    private void AssertSecretFree(AuthorizationDispatchResult result)
    {
        string text = result + JsonSerializer.Serialize(result);
        foreach (string forbidden in new[] { Cookie, Rotated, "roblox-player:", "gameinfo", "Synthetic_Dispatch", _root }) Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        Assert.Equal(new[] { "Outcome", "ProcessReceipt" }, typeof(NativeHandoffDispatchResult).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Dispatch", "Status" }, typeof(AuthorizationDispatchResult).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
    private Dictionary<string, byte[]> Files() => Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(VaultRoot, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    private void Unchanged(Dictionary<string, byte[]> before)
    {
        var after = Files(); Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), after.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("zenith-dispatch-boundary-", StringComparison.Ordinal)
            || File.ReadAllText(Path.Combine(full, Marker)) != Owner) throw new InvalidOperationException("The owned synthetic dispatch fixture changed.");
        Directory.Delete(full, true);
    }
}
