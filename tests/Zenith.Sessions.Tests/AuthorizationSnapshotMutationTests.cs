using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Only new owned Temp fixtures and synthetic CurrentUser-DPAPI records. No account or client use.
public sealed class AuthorizationSnapshotMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-snapshot-vault-" + Guid.NewGuid().ToString("N"));
    private const string Original = "synthetic-snapshot-original", Replacement = "synthetic-snapshot-replacement";
    private string VaultRoot => Path.Combine(_root, "accounts.vault");
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    private string GenerationPath(Guid id) => Path.Combine(VaultRoot, "generations", id.ToString("N") + ".json");
    private static AuthorizationRecord Session(Guid id, long generation = 1, string cookie = Original, long user = 987654)
        => new(id, RobloxSessionIdentity.Provider, user, generation, "", "", DateTimeOffset.MaxValue,
            "Synthetic_Snapshot", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer,
            user.ToString(CultureInfo.InvariantCulture), cookie);

    [Fact]
    public async Task CompetingFirstSignInsHaveOneWinnerAcrossVaultInstances()
    {
        using var first = new ProtectedCredentialVault(_root); using var second = new ProtectedCredentialVault(_root);
        Guid id = Guid.NewGuid();
        var one = await first.ReadWriteSnapshotAsync(id, default); var two = await second.ReadWriteSnapshotAsync(id, default);
        Assert.Null(one.Record); Assert.Null(one.RecordSha256); Assert.Equal(1L, one.NextGeneration);
        var results = await Task.WhenAll(first.TryCommitSnapshotAsync(one, Session(id), Guid.NewGuid(), default),
            second.TryCommitSnapshotAsync(two, Session(id, cookie: Replacement, user: 987655), Guid.NewGuid(), default));
        Assert.Single(results, result => result.Status == AuthorizationMutationStatus.Applied && result.CommittedGeneration == 1);
        Assert.Single(results, result => result.Status == AuthorizationMutationStatus.Conflict && result.CommittedGeneration is null);
        var current = await first.ReadWriteSnapshotAsync(id, default);
        Assert.NotNull(current.Record); Assert.Equal(1L, current.LastCommitted); Assert.Equal(1L, current.HighestAllocated);
        Assert.Contains(current.Record!.SessionCookie, new[] { Original, Replacement });
    }

    [Fact]
    public async Task AbsentSnapshotPinsTheDeletionTombstoneAndCannotWinAnAbaRace()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        var oldAbsent = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(1L, (await vault.TryDeleteSnapshotAsync(oldAbsent, default)).CommittedGeneration);
        var newAbsent = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Null(newAbsent.Record); Assert.Equal(1L, newAbsent.HighestAllocated); Assert.Equal(0L, newAbsent.LastCommitted);
        var before = Files();
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(oldAbsent, Session(id), Guid.NewGuid(), default)).Status);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(oldAbsent, default)).Status);
        Unchanged(before);
        Assert.Equal(2L, (await vault.TryCommitSnapshotAsync(newAbsent, Session(id, 2), Guid.NewGuid(), default)).CommittedGeneration);
    }

    [Fact]
    public async Task FencedRecordSnapshotHasOriginalHashAndCanCommitFreshSameIdentity()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default);
        string originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(RecordPath(id))));
        await vault.AllocateNextGenerationAsync(id, 1, default);
        var fenced = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(originalHash, fenced.RecordSha256); Assert.Equal(3L, fenced.NextGeneration);
        Assert.Equal(AuthorizationHealth.SignInRequired, fenced.Record!.Health);
        Assert.Empty(fenced.Record.SessionCookie); Assert.Empty(fenced.Record.RefreshToken); Assert.Empty(fenced.Record.AccessToken);
        Guid owner = Guid.NewGuid();
        Assert.Equal(3L, (await vault.TryCommitSnapshotAsync(fenced, Session(id, 3, Replacement), owner, default)).CommittedGeneration);
        var current = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(Session(id, 3, Replacement), current.Record); Assert.Equal(owner, current.HighestMutationId);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("user")]
    [InlineData("provider")]
    [InlineData("generation")]
    [InlineData("mutation")]
    public async Task InvalidReplacementNeverReservesOrChangesFiles(string kind)
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        var replacement = kind switch
        {
            "profile" => Session(Guid.NewGuid(), 2),
            "user" => Session(id, 2, user: 987655),
            "provider" => new AuthorizationRecord(id, RobloxOAuthIdentity.Provider, 987654, 2, "", "synthetic-refresh",
                DateTimeOffset.MaxValue, "Synthetic_Snapshot", Issuer: RobloxOAuthIdentity.Issuer, Subject: "opaque-synthetic-subject"),
            "generation" => Session(id, 1),
            _ => Session(id, 2)
        };
        var before = Files();
        await Assert.ThrowsAsync<ArgumentException>(() => vault.TryCommitSnapshotAsync(snapshot, replacement,
            kind == "mutation" ? Guid.Empty : Guid.NewGuid(), default));
        Unchanged(before);
    }

    [Theory]
    [InlineData("HighestAllocated")]
    [InlineData("LastCommitted")]
    [InlineData("HighestMutationId")]
    public async Task EveryAuthoritativeStateFieldIsComparedWithoutWrites(string field)
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        var state = State(id);
        if (field == "HighestAllocated") state[field] = 2L;
        else if (field == "LastCommitted") state[field] = 0L;
        else state[field] = Guid.NewGuid();
        await File.WriteAllTextAsync(GenerationPath(id), state.ToJsonString()); var before = Files();
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(snapshot, Session(id, 2), Guid.NewGuid(), default)).Status);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(snapshot, default)).Status);
        Unchanged(before);
    }

    [Fact]
    public async Task HashOnlyChangeConflictsEvenWhenDecryptedRecordAndGenerationAreIdentical()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        await File.AppendAllTextAsync(RecordPath(id), "\n"); // valid envelope, different exact encrypted-file bytes
        var current = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(snapshot.Record, current.Record); Assert.NotEqual(snapshot.RecordSha256, current.RecordSha256);
        var before = Files();
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(snapshot, Session(id, 2), Guid.NewGuid(), default)).Status);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(snapshot, default)).Status);
        Unchanged(before);
    }

    [Theory]
    [InlineData("cookie")]
    [InlineData("username")]
    [InlineData("health")]
    [InlineData("expiry")]
    public async Task EntireReconstructedReadPolicyRecordMustMatch(string field)
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        var altered = snapshot.Record!;
        altered = field switch
        {
            "cookie" => altered with { SessionCookie = Replacement },
            "username" => altered with { VerifiedUsername = "Different_Synthetic" },
            "health" => altered with { Health = AuthorizationHealth.SignInRequired },
            _ => altered with { ExpiresUtc = DateTimeOffset.MaxValue.AddMinutes(-1) }
        };
        var before = Files(); var incorrect = snapshot with { Record = altered };
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(incorrect, Session(id, 2), Guid.NewGuid(), default)).Status);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(incorrect, default)).Status);
        Unchanged(before);
    }

    [Fact]
    public async Task StaleDeletePreservesANewerCommittedGrantAndDeletionFencesOnlyOnce()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var old = await vault.ReadWriteSnapshotAsync(id, default);
        await vault.TryCommitSnapshotAsync(old, Session(id, 2, Replacement), Guid.NewGuid(), default);
        var before = Files(); Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(old, default)).Status); Unchanged(before);
        var current = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(Replacement, current.Record!.SessionCookie);
        Assert.Equal(3L, (await vault.TryDeleteSnapshotAsync(current, default)).CommittedGeneration);
        var absent = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Null(absent.Record); Assert.Equal(3L, absent.HighestAllocated); Assert.Equal(2L, absent.LastCommitted); Assert.Equal(Guid.Empty, absent.HighestMutationId);
    }

    [Fact]
    public async Task AnotherProfilesKnownIdentityClaimIsRejectedBeforeAnyReservation()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await vault.SaveAsync(Session(first), default); var absent = await vault.ReadWriteSnapshotAsync(second, default); var before = Files();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.TryCommitSnapshotAsync(absent, Session(second), Guid.NewGuid(), default));
        Unchanged(before); Assert.False(File.Exists(GenerationPath(second)));
    }

    [Fact]
    public async Task SnapshotReadsOnlyTheSelectedRecordAndDoesNotClearRetainedCopies()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid selected = Guid.NewGuid(), unrelated = Guid.NewGuid();
        await vault.SaveAsync(Session(selected), default); await vault.SaveAsync(Session(unrelated, user: 987655), default);
        await File.WriteAllTextAsync(RecordPath(unrelated), "synthetic-corrupt-unselected-record");
        File.Copy(RecordPath(selected), RecordPath(selected) + ".bak");
        var before = Files(); var snapshot = await vault.ReadWriteSnapshotAsync(selected, default);
        Assert.Equal(Session(selected), snapshot.Record); Unchanged(before);
        Assert.True(File.Exists(RecordPath(selected) + ".bak"));
    }

    [Fact]
    public async Task MissingCurrentWithObsoleteBackupIsNotAFreshSlot()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        var absent = await vault.ReadWriteSnapshotAsync(id, default);
        await File.WriteAllTextAsync(RecordPath(id) + ".bak", "synthetic-retained-original"); var before = Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadWriteSnapshotAsync(id, default));
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(absent, Session(id), Guid.NewGuid(), default)).Status);
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(absent, default)).Status); Unchanged(before);
    }

    [Fact]
    public async Task PartialFirstCommitRetainsOwnerAndCanBeFencedThenFreshlyRecovered()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid(), owner = Guid.NewGuid();
        var absent = await vault.ReadWriteSnapshotAsync(id, default);
        string blocker = RecordPath(id) + ".identity"; Directory.CreateDirectory(blocker);
        var error = await Record.ExceptionAsync(() => vault.TryCommitSnapshotAsync(absent, Session(id), owner, default));
        Assert.True(error is IOException or UnauthorizedAccessException); Assert.False(File.Exists(RecordPath(id)));
        Assert.Equal(1L, State(id)["HighestAllocated"]!.GetValue<long>()); Assert.Equal(owner, State(id)["HighestMutationId"]!.GetValue<Guid>());
        Assert.False(await vault.TryFenceOwnedMutationAsync(id, Guid.NewGuid(), default));
        Assert.True(await vault.TryFenceOwnedMutationAsync(id, owner, default));
        Directory.Delete(blocker); // the fixture's own empty obstruction, never automatic vault recovery
        var recovery = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Null(recovery.Record); Assert.Equal(3L, recovery.NextGeneration); Assert.Equal(0L, recovery.LastCommitted);
        Assert.Equal(3L, (await vault.TryCommitSnapshotAsync(recovery, Session(id, 3, Replacement), Guid.NewGuid(), default)).CommittedGeneration);
    }

    [Fact]
    public async Task InterruptedReplacementRetainsOriginalCiphertextButNoReusableAuthority()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid(), owner = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(id));
        using var cancel = new CancellationTokenSource();
        using (var reader = new FileStream(RecordPath(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var attempt = vault.TryCommitSnapshotAsync(snapshot, Session(id, 2, Replacement), owner, cancel.Token);
            try
            {
                await UntilAsync(() => State(id)["HighestAllocated"]!.GetValue<long>() == 2);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt);
            }
            finally
            {
                cancel.Cancel();
                try { await attempt; } catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(id)));
        var interrupted = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(owner, interrupted.HighestMutationId); Assert.Equal(1L, interrupted.LastCommitted);
        Assert.Equal(AuthorizationHealth.SignInRequired, interrupted.Record!.Health); Assert.Empty(interrupted.Record.SessionCookie);
        Assert.True(await vault.TryFenceOwnedMutationAsync(id, owner, default));
        var recovery = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(4L, recovery.NextGeneration);
        await vault.TryCommitSnapshotAsync(recovery, Session(id, 4, Replacement), Guid.NewGuid(), default);
        Assert.Equal(Session(id, 4, Replacement), (await vault.ReadWriteSnapshotAsync(id, default)).Record);
        var before = Files(); Assert.False(await vault.TryFenceOwnedMutationAsync(id, owner, default)); Unchanged(before);
    }

    [Fact]
    public async Task FailedDeletionFencesOnceAndAnOldRetryCannotFenceAgain()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        using (var reader = new FileStream(RecordPath(id), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => vault.TryDeleteSnapshotAsync(snapshot, default));
        var current = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(2L, current.HighestAllocated); Assert.Equal(1L, current.LastCommitted); Assert.Empty(current.Record!.SessionCookie);
        var before = Files(); Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryDeleteSnapshotAsync(snapshot, default)).Status); Unchanged(before);
        Assert.Equal(3L, (await vault.TryDeleteSnapshotAsync(current, default)).CommittedGeneration);
    }

    [Fact]
    public async Task ActualCoordinationFileLeaseProtectsEverySnapshotOperationAndCancellation()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default); var before = Files();
        using (var held = new FileStream(Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await CancelledAsync(ct => vault.ReadWriteSnapshotAsync(id, ct));
            await CancelledAsync(ct => vault.TryCommitSnapshotAsync(snapshot, Session(id, 2), Guid.NewGuid(), ct));
            await CancelledAsync(ct => vault.TryDeleteSnapshotAsync(snapshot, ct));
        }
        Unchanged(before);
    }

    [Fact]
    public async Task CorruptGenerationOrIdentityStateIsPreservedAndRejected()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var state = State(id); state["LastCommitted"] = 5L;
        await File.WriteAllTextAsync(GenerationPath(id), state.ToJsonString()); var before = Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadWriteSnapshotAsync(id, default)); Unchanged(before);
        state["LastCommitted"] = 1L; await File.WriteAllTextAsync(GenerationPath(id), state.ToJsonString());
        await File.WriteAllTextAsync(RecordPath(id) + ".identity", "synthetic-corrupt-index"); before = Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadWriteSnapshotAsync(id, default)); Unchanged(before);
    }

    [Fact]
    public async Task MissingCommittedRecordWithRetainedIdentityIsAmbiguousRatherThanFresh()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default); var snapshot = await vault.ReadWriteSnapshotAsync(id, default);
        File.Delete(RecordPath(id)); var before = Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadWriteSnapshotAsync(id, default));
        Assert.Equal(AuthorizationMutationStatus.Conflict, (await vault.TryCommitSnapshotAsync(snapshot, Session(id, 2), Guid.NewGuid(), default)).Status);
        Unchanged(before);
    }

    [Fact]
    public async Task MissingCommittedRecordAndPointerCannotBeReportedAsAFreshAbsentSlot()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid();
        await vault.SaveAsync(Session(id), default);
        var committed = await vault.ReadWriteSnapshotAsync(id, default);
        Assert.True(committed.HighestAllocated > 0); Assert.Equal(committed.HighestAllocated, committed.LastCommitted);
        // Remove only this fixture's current credential and pointer. Its committed marker and
        // identity claims still describe an interrupted/incomplete store, not an explicit Delete.
        File.Delete(RecordPath(id)); File.Delete(RecordPath(id) + ".identity");
        Assert.True(File.Exists(GenerationPath(id)));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(VaultRoot, "identity-index"), "*.json").Length);
        var before = Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadWriteSnapshotAsync(id, default));
        Unchanged(before);
    }

    [Fact]
    public async Task SnapshotSerializationAndDisplayContainNoSelectedRecordOrSecret()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid(); await vault.SaveAsync(Session(id), default);
        var snapshot = await vault.ReadWriteSnapshotAsync(id, default); string display = snapshot.ToString(), json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(Original, display); Assert.DoesNotContain(Original, json);
        Assert.DoesNotContain("Synthetic_Snapshot", display); Assert.DoesNotContain("Synthetic_Snapshot", json);
        Assert.DoesNotContain(snapshot.RecordSha256!, display); Assert.DoesNotContain(_root, display); Assert.DoesNotContain(_root, json);
        using var parsed = JsonDocument.Parse(json); Assert.False(parsed.RootElement.TryGetProperty("Record", out _));
        Assert.False(parsed.RootElement.TryGetProperty("NextGeneration", out _));
        Assert.Equal(8L, (snapshot with { HighestAllocated = 7 }).NextGeneration);
        Assert.Equal(6L, (snapshot with { Record = snapshot.Record! with { Generation = 5 }, HighestAllocated = 2 }).NextGeneration);
        Assert.Throws<OverflowException>(() => { _ = (snapshot with { HighestAllocated = long.MaxValue }).NextGeneration; });
    }

    [Fact]
    public async Task PreCancelledSnapshotOperationsCreateNoStorage()
    {
        using var vault = new ProtectedCredentialVault(_root); Guid id = Guid.NewGuid(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var absent = new AuthorizationWriteSnapshot(id, null, 0, 0, Guid.Empty, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.ReadWriteSnapshotAsync(id, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryCommitSnapshotAsync(absent, Session(id), Guid.NewGuid(), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryDeleteSnapshotAsync(absent, cancel.Token));
        Assert.False(Directory.Exists(_root));
    }

    private JsonObject State(Guid id) => JsonNode.Parse(File.ReadAllText(GenerationPath(id)))!.AsObject();
    private Dictionary<string, byte[]> Files() => Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(VaultRoot, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    private void Unchanged(Dictionary<string, byte[]> expected)
    {
        var actual = Files(); Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var entry in expected) Assert.Equal(entry.Value, actual[entry.Key]);
    }
    private static async Task CancelledAsync(Func<CancellationToken, Task> operation)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(cancel.Token));
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { if (condition()) return; }
            // The fixture observes a sidecar outside the vault lease while its owner replaces
            // it. Only this bounded observation tolerates the destination's rename gap.
            catch (FileNotFoundException) { }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { }
            await Task.Delay(10, deadline.Token);
        }
    }
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root), temp = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("zenith-snapshot-vault-", StringComparison.Ordinal))
            throw new InvalidOperationException("The synthetic vault fixture path changed.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
