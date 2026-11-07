using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Real CurrentUser DPAPI, but only new owned temporary synthetic records. No account/network/browser.
public sealed class AuthorizationConditionalMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-conditional-vault-" + Guid.NewGuid().ToString("N"));
    private const string OriginalCookie = "synthetic-conditional-original-cookie";
    private const string RotatedCookie = "synthetic-conditional-rotated-cookie";
    private string VaultRoot => Path.Combine(_root, "accounts.vault");
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    private string GenerationPath(Guid id) => Path.Combine(VaultRoot, "generations", id.ToString("N") + ".json");
    private string LockPath => Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName);
    private static AuthorizationRecord Session(Guid id) => new(id, RobloxSessionIdentity.Provider, 123, 1, "", "",
        DateTimeOffset.MaxValue, "Synthetic_Account", Issuer: RobloxSessionIdentity.Issuer, Subject: "123", SessionCookie: OriginalCookie);

    [Fact]
    public async Task AppliedReceiptUsesVaultAllocatedGenerationAndDoesNotExposeCredentials()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var expected = Session(Guid.NewGuid()); await vault.SaveAsync(expected, default);
        Guid mutation = Guid.NewGuid();
        var result = await vault.TryReplaceNextAsync(expected, expected with { SessionCookie = RotatedCookie }, mutation, default);
        Assert.Equal(AuthorizationMutationStatus.Applied, result.Status); Assert.Equal(2L, result.CommittedGeneration);
        Assert.Equal(expected with { Generation = 2, SessionCookie = RotatedCookie }, await vault.ReadAsync(expected.ProfileId, default));
        AssertState(expected.ProfileId, 2, 2, mutation);
        Assert.DoesNotContain(OriginalCookie, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedCookie, result.ToString(), StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories))
        {
            string stored = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain(OriginalCookie, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(RotatedCookie, stored, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("cookie")]
    [InlineData("health")]
    [InlineData("username")]
    [InlineData("expiry")]
    [InlineData("user")]
    public async Task AnyChangedExpectedRecordConflictsWithoutChangingStoredFiles(string difference)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        var expected = difference switch
        {
            "generation" => current with { Generation = 2 },
            "cookie" => current with { SessionCookie = RotatedCookie },
            "health" => current with { Health = AuthorizationHealth.SignInRequired },
            "username" => current with { VerifiedUsername = "Changed_Account" },
            "expiry" => current with { ExpiresUtc = DateTimeOffset.MaxValue.AddSeconds(-1) },
            "user" => current with { VerifiedUserId = 456, Subject = "456" },
            _ => throw new InvalidOperationException()
        };
        var before = SnapshotFiles();
        var result = await vault.TryReplaceNextAsync(expected, expected with { Health = AuthorizationHealth.SignInRequired }, Guid.NewGuid(), default);
        Assert.Equal(AuthorizationMutationStatus.Conflict, result.Status); Assert.Null(result.CommittedGeneration);
        AssertFilesUnchanged(before);
        Assert.Equal(current, await vault.ReadAsync(current.ProfileId, default));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("user")]
    [InlineData("provider")]
    [InlineData("empty-mutation")]
    public async Task InvalidReplacementTemplatesWriteNothing(string difference)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        var replacement = difference switch
        {
            "profile" => current with { ProfileId = Guid.NewGuid() },
            "generation" => current with { Generation = 2 },
            "user" => current with { VerifiedUserId = 456, Subject = "456" },
            "provider" => current with { Provider = RobloxOAuthIdentity.Provider, Issuer = RobloxOAuthIdentity.Issuer,
                Subject = "opaque-oauth-subject", SessionCookie = "", RefreshToken = "synthetic-refresh" },
            _ => current
        };
        var before = SnapshotFiles();
        await Assert.ThrowsAsync<ArgumentException>(() => vault.TryReplaceNextAsync(current, replacement,
            difference == "empty-mutation" ? Guid.Empty : Guid.NewGuid(), default));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task OAuthExpectedComparisonIncludesAccessMaterialButReplacementNeverPersistsIt()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var oauth = new AuthorizationRecord(Guid.NewGuid(), RobloxOAuthIdentity.Provider, 123, 1, "synthetic-access",
            "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), "Synthetic_Account", Issuer: RobloxOAuthIdentity.Issuer, Subject: "opaque-subject");
        await vault.SaveAsync(oauth, default);
        var current = (await vault.ReadAsync(oauth.ProfileId, default))!;
        Assert.Empty(current.AccessToken); Assert.Empty(current.SessionCookie);
        var before = SnapshotFiles();
        var conflict = await vault.TryReplaceNextAsync(oauth, oauth, Guid.NewGuid(), default);
        Assert.Equal(AuthorizationMutationStatus.Conflict, conflict.Status); AssertFilesUnchanged(before);
        var applied = await vault.TryReplaceNextAsync(current, current with { AccessToken = "new-operational-access", RefreshToken = "rotated-refresh" }, Guid.NewGuid(), default);
        Assert.Equal(2L, applied.CommittedGeneration);
        var saved = (await vault.ReadAsync(oauth.ProfileId, default))!;
        Assert.Empty(saved.AccessToken); Assert.Empty(saved.SessionCookie); Assert.Equal("rotated-refresh", saved.RefreshToken);
    }

    [Fact]
    public async Task SameExpectedGenerationAcrossInstancesHasExactlyOneWinner()
    {
        using var first = new ProtectedCredentialVault(_root); using var second = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await first.SaveAsync(current, default);
        var results = await Task.WhenAll(first.TryReplaceNextAsync(current, current with { SessionCookie = RotatedCookie }, Guid.NewGuid(), default),
            second.TryReplaceNextAsync(current, current with { SessionCookie = "other-synthetic-cookie" }, Guid.NewGuid(), default));
        Assert.Single(results, item => item.Status == AuthorizationMutationStatus.Applied && item.CommittedGeneration == 2);
        Assert.Single(results, item => item.Status == AuthorizationMutationStatus.Conflict && item.CommittedGeneration is null);
        Assert.Equal(2, (await first.ReadAsync(current.ProfileId, default))!.Generation);
    }

    [Fact]
    public async Task OwnedFenceInvalidatesOnlyItsGenerationWithoutEditingProtectedBytesOrClaims()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); Guid mutation = Guid.NewGuid();
        await vault.TryReplaceNextAsync(current, current with { SessionCookie = RotatedCookie }, mutation, default);
        var before = SnapshotFiles();
        Assert.True(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default));
        AssertState(current.ProfileId, 3, 2, Guid.Empty);
        AssertFilesUnchanged(before, GenerationPath(current.ProfileId));
        var fenced = (await vault.ReadAsync(current.ProfileId, default))!;
        Assert.Equal(AuthorizationHealth.SignInRequired, fenced.Health); Assert.Empty(fenced.SessionCookie);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(current.ProfileId, default))!.Health);
        before = SnapshotFiles();
        Assert.False(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default));
        Assert.False(await vault.TryFenceOwnedMutationAsync(current.ProfileId, Guid.NewGuid(), default));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task ANewerForeignConditionalGrantCannotBeFencedByTheOlderMutation()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        Guid first = Guid.NewGuid(), foreign = Guid.NewGuid();
        await vault.TryReplaceNextAsync(current, current with { SessionCookie = RotatedCookie }, first, default);
        var next = (await vault.ReadAsync(current.ProfileId, default))!;
        await vault.TryReplaceNextAsync(next, next with { SessionCookie = "foreign-synthetic-cookie" }, foreign, default);
        var before = SnapshotFiles();
        Assert.False(await vault.TryFenceOwnedMutationAsync(current.ProfileId, first, default)); AssertFilesUnchanged(before);
        AssertState(current.ProfileId, 3, 3, foreign);
        Assert.Equal("foreign-synthetic-cookie", (await vault.ReadAsync(current.ProfileId, default))!.SessionCookie);
        await Assert.ThrowsAsync<ArgumentException>(() => vault.TryReplaceNextAsync(
            next with { Generation = 3, SessionCookie = "foreign-synthetic-cookie" }, next with { Generation = 3 }, foreign, default));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData("allocate")]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task OrdinaryAdvancesClearMutationOwnership(string operation)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); Guid mutation = Guid.NewGuid();
        await vault.TryReplaceNextAsync(current, current, mutation, default);
        switch (operation)
        {
            case "allocate": Assert.Equal(3, await vault.AllocateNextGenerationAsync(current.ProfileId, 2, default)); break;
            case "save": await vault.SaveAsync(current with { Generation = 3, SessionCookie = RotatedCookie }, default); break;
            case "delete": await vault.DeleteAsync(current.ProfileId, default); break;
        }
        AssertState(current.ProfileId, 3, operation == "save" ? 3 : 2, Guid.Empty);
        var before = SnapshotFiles(); Assert.False(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default)); AssertFilesUnchanged(before);
        if (operation == "delete") Assert.Null(await vault.ReadAsync(current.ProfileId, default));
    }

    [Fact]
    public async Task InterruptedCredentialReplacementKeepsOldBytesButItsReservationIsFencedAndCannotBeAdopted()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); Guid mutation = Guid.NewGuid();
        byte[] original = await File.ReadAllBytesAsync(RecordPath(current.ProfileId));
        using var stop = new CancellationTokenSource();
        using (var heldOriginal = new FileStream(RecordPath(current.ProfileId), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Task<AuthorizationMutationResult> pending = vault.TryReplaceNextAsync(current, current with { SessionCookie = RotatedCookie }, mutation, stop.Token);
            Exception? error = null;
            try { await WaitUntilAsync(() => State(current.ProfileId)["HighestMutationId"]!.GetValue<Guid>() == mutation); }
            finally { stop.Cancel(); error = await Record.ExceptionAsync(async () => await pending); }
            Assert.True(error is OperationCanceledException or IOException, error?.GetType().Name ?? "Unexpected success");
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(current.ProfileId)));
        AssertState(current.ProfileId, 2, 1, mutation);
        Assert.Empty((await vault.ReadAsync(current.ProfileId, default))!.SessionCookie);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(current with { Generation = 2, SessionCookie = RotatedCookie }, default));
        Assert.True(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default));
        AssertState(current.ProfileId, 3, 1, Guid.Empty);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(current.ProfileId)));
    }

    [Fact]
    public async Task FailedOwnershipReservationCannotFenceAnUnchangedGrant()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); Guid mutation = Guid.NewGuid();
        var before = SnapshotFiles();
        using (var heldGeneration = new FileStream(GenerationPath(current.ProfileId), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryReplaceNextAsync(current, current, mutation, stop.Token));
        AssertFilesUnchanged(before);
        Assert.False(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default));
        Assert.Equal(current, await vault.ReadAsync(current.ProfileId, default)); AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task ACompletePayloadWithoutItsFinalMarkerStillFailsClosedAndCanBeFencedByItsOwner()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); Guid mutation = Guid.NewGuid();
        await vault.TryReplaceNextAsync(current, current with { SessionCookie = RotatedCookie }, mutation, default);
        // Only synthetic non-secret state is changed to model interruption before final commit-marker publication.
        JsonObject state = State(current.ProfileId); state["LastCommitted"] = 1; await WriteStateAsync(current.ProfileId, state);
        var before = SnapshotFiles();
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.ReadAsync(current.ProfileId, default))!.Health);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(current.ProfileId, default))!.Health);
        AssertFilesUnchanged(before);
        Assert.True(await vault.TryFenceOwnedMutationAsync(current.ProfileId, mutation, default));
        AssertFilesUnchanged(before, GenerationPath(current.ProfileId)); AssertState(current.ProfileId, 3, 1, Guid.Empty);
    }

    [Fact]
    public async Task VersionOneGenerationStateIsReadWithoutRewriteAndExplicitMutationPublishesVersionTwo()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        JsonObject state = State(current.ProfileId); state["Version"] = 1; state.Remove("HighestMutationId");
        await WriteStateAsync(current.ProfileId, state); var before = SnapshotFiles();
        Assert.Equal(current, await vault.ReadAsync(current.ProfileId, default));
        Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(current.ProfileId, default))!.Health); AssertFilesUnchanged(before);
        Guid mutation = Guid.NewGuid();
        Assert.Equal(AuthorizationMutationStatus.Applied, (await vault.TryReplaceNextAsync(current, current, mutation, default)).Status);
        AssertState(current.ProfileId, 2, 2, mutation);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("missing-owner")]
    [InlineData("invalid-owner")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("inconsistent")]
    public async Task UnknownOrMalformedGenerationStatePreservesOriginalAndNeverFallsBack(string corruption)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        JsonObject state = State(current.ProfileId);
        switch (corruption)
        {
            case "future": state["Version"] = 3; break;
            case "missing-owner": state.Remove("HighestMutationId"); break;
            case "invalid-owner": state["HighestMutationId"] = "not-a-guid"; break;
            case "unknown": state["UnknownAuthority"] = true; break;
            case "inconsistent": state["LastCommitted"] = 2; break;
        }
        string json = state.ToJsonString();
        if (corruption == "duplicate") json = json.Insert(json.Length - 1, ",\"HighestAllocated\":1");
        await File.WriteAllTextAsync(GenerationPath(current.ProfileId), json);
        var before = SnapshotFiles();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(current.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetSummaryAsync(current.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.TryReplaceNextAsync(current, current, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.AllocateNextGenerationAsync(current.ProfileId, 1, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.SaveAsync(current with { Generation = 2 }, default));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task AnExclusiveProcessLeaseBlocksReadersAndWritersAndCancellationDoesNotTouchGrantData()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default); var before = SnapshotFiles();
        using (var held = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await CancelledAsync(ct => vault.ReadAsync(current.ProfileId, ct));
            await CancelledAsync(ct => vault.GetSummaryAsync(current.ProfileId, ct));
            await CancelledAsync(ct => vault.SaveAsync(current with { Generation = 2 }, ct));
            await CancelledAsync(ct => vault.AllocateNextGenerationAsync(current.ProfileId, 1, ct));
            await CancelledAsync(ct => vault.TryReplaceNextAsync(current, current, Guid.NewGuid(), ct));
            await CancelledAsync(ct => vault.TryFenceOwnedMutationAsync(current.ProfileId, Guid.NewGuid(), ct));
            await CancelledAsync(ct => vault.DeleteAsync(current.ProfileId, ct));
        }
        AssertFilesUnchanged(before); Assert.Equal(current, await vault.ReadAsync(current.ProfileId, default));
    }

    [Fact]
    public async Task AdmissionIsBoundedAcrossInstancesAndDisposalNeverAbandonsItsActualOwner()
    {
        using var first = new ProtectedCredentialVault(_root); using var second = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await first.SaveAsync(current, default);
        using var stop = new CancellationTokenSource();
        using (var held = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Task[] pending = Enumerable.Range(0, ProtectedCredentialVault.MaximumPendingOperations)
                .Select(index => (index % 2 == 0 ? first : second).GetSummaryAsync(current.ProfileId, stop.Token)).ToArray();
            try
            {
                await Assert.ThrowsAsync<AuthorizationVaultBusyException>(() => first.GetSummaryAsync(current.ProfileId, default));
                Assert.Throws<InvalidOperationException>(() => first.Dispose());
                Assert.Throws<InvalidOperationException>(() => second.Dispose());
            }
            finally
            {
                stop.Cancel();
                await Record.ExceptionAsync(async () => await Task.WhenAll(pending));
                Assert.All(pending, task => Assert.True(task.IsCanceled));
            }
        }
        first.Dispose(); first.Dispose(); second.Dispose(); second.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.GetSummaryAsync(current.ProfileId, default));
    }

    [Fact]
    public async Task LockFileIsEmptyPrivateAndPersistsAcrossVaultLifetimes()
    {
        Guid id = Guid.NewGuid();
        using (var vault = new ProtectedCredentialVault(_root)) await vault.SaveAsync(Session(id), default);
        Assert.Equal(0, new FileInfo(LockPath).Length);
        FileSecurity acl = new FileInfo(LockPath).GetAccessControl(AccessControlSections.Access);
        Assert.True(acl.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.True(rule.IdentityReference.Equals(identity.User) || rule.IdentityReference.Equals(system));
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.False(rule.IsInherited);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        });
        using var reopened = new ProtectedCredentialVault(_root);
        Assert.Equal(Session(id), await reopened.ReadAsync(id, default)); Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public async Task NonemptyCoordinationFileIsRejectedPreservedAndNotInterpretedAsCredentialData()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var current = Session(Guid.NewGuid()); await vault.SaveAsync(current, default);
        await File.WriteAllTextAsync(LockPath, "synthetic-invalid-coordination-state"); var before = SnapshotFiles();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(current.ProfileId, default));
        Assert.DoesNotContain(OriginalCookie, error.ToString(), StringComparison.Ordinal); Assert.Null(error.InnerException);
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task PreCancelledOperationsCreateNoStorage()
    {
        using var vault = new ProtectedCredentialVault(_root); using var stop = new CancellationTokenSource(); stop.Cancel();
        var current = Session(Guid.NewGuid());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryReplaceNextAsync(current, current, Guid.NewGuid(), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.SaveAsync(current, stop.Token));
        Assert.False(Directory.Exists(_root));
    }

    private static async Task CancelledAsync(Func<CancellationToken, Task> operation)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(stop.Token));
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true)
        {
            try{if(condition())return;}
            // This test deliberately observes the generation file during atomic replacement.
            // Absence during a destination rename or a sharing denial is an unfinished
            // observation, never a passing condition; final store assertions remain strict.
            catch(FileNotFoundException){}
            catch(IOException error) when((error.HResult&0xffff) is 32 or 33){}
            await Task.Delay(10,timeout.Token);
        }
    }
    private JsonObject State(Guid id) => JsonNode.Parse(File.ReadAllText(GenerationPath(id)))!.AsObject();
    private Task WriteStateAsync(Guid id, JsonObject state) => File.WriteAllTextAsync(GenerationPath(id), state.ToJsonString());
    private void AssertState(Guid id, long highest, long committed, Guid owner)
    {
        JsonObject state = State(id);
        Assert.Equal(2, state["Version"]!.GetValue<int>()); Assert.Equal(id, state["ProfileId"]!.GetValue<Guid>());
        Assert.Equal(highest, state["HighestAllocated"]!.GetValue<long>()); Assert.Equal(committed, state["LastCommitted"]!.GetValue<long>());
        Assert.Equal(owner, state["HighestMutationId"]!.GetValue<Guid>());
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    private void AssertFilesUnchanged(Dictionary<string, byte[]> before, string? except = null)
    {
        var after = SnapshotFiles();
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), after.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var entry in before)
            if (!string.Equals(entry.Key, except, StringComparison.OrdinalIgnoreCase)) Assert.Equal(entry.Value, after[entry.Key]);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
