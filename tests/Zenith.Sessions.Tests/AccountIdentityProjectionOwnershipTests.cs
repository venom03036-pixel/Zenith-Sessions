using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Owned synthetic Temp metadata/DPAPI only. No account, browser or client operations.
public sealed class AccountIdentityProjectionOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-projection-ownership-" + Guid.NewGuid().ToString("N"));
    private string Journal(Guid id) => Path.Combine(_root, "account-identity-pending", id.ToString("N") + ".json");
    private static AuthorizationRecord Session(Guid id, long generation) => new(id, RobloxSessionIdentity.Provider, 123456,
        generation, "", "", DateTimeOffset.MaxValue, "synthetic_player", AuthorizationHealth.Linked,
        RobloxSessionIdentity.Issuer, "123456", "synthetic-projection-session");
    private static AuthorizationMetadata Summary(AuthorizationRecord record) => new(record.ProfileId, record.ProfileId.ToString("N"),
        record.Provider, record.Issuer, record.Subject, record.VerifiedUserId, record.VerifiedUsername, record.Generation,
        record.Health, record.ExpiresUtc, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedGrantCleanupMustPreserveANewerPreparedIntentBeforeItsVaultCommit(bool conservativeDefault)
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var vault = new ProtectedCredentialVault(_root);
        using var deleting = new AccountIdentityProjection(_root, repository, vault);
        using var signingIn = new AccountIdentityProjection(_root, repository, vault);
        await vault.SaveAsync(Session(profile.Id, 1), default);
        var original = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        var deletion = await vault.TryDeleteSnapshotAsync(original, default);
        Assert.Equal(2L, deletion.CommittedGeneration);

        // B has verified a fresh sign-in and prepared g+1, but has not committed its grant yet.
        var absent = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        var fresh = Session(profile.Id, absent.NextGeneration);
        await signingIn.PrepareAsync(Summary(fresh), default);
        byte[] newerIntent = await File.ReadAllBytesAsync(Journal(profile.Id));

        if (conservativeDefault) await deleting.DiscardAsync(profile.Id, default);
        else await deleting.DiscardAsync(profile.Id, deletion.CommittedGeneration!.Value, default);
        Assert.True(File.Exists(Journal(profile.Id)), "Disconnect cleanup deleted another operation's newer prepared identity intent.");
        Assert.Equal(newerIntent, await File.ReadAllBytesAsync(Journal(profile.Id)));
        Assert.Equal(AuthorizationMutationStatus.Applied,
            (await vault.TryCommitSnapshotAsync(absent, fresh, Guid.NewGuid(), default)).Status);
        await signingIn.CompleteAsync(profile.Id, fresh.Generation, default);
        Assert.Equal("synthetic_player", (await repository.FindAsync(profile.Id, default))!.Username);
    }

    [Fact]
    public async Task OtherProjectionInstanceCannotPrepareWhileDiscardOwnsItsReadCompareDelete()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        var metadata = new BlockedAbsentMetadata();
        var mayWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = new TrackedRepository(repository, metadata, mayWrite.Task);
        using var first = new AccountIdentityProjection(_root, repository, metadata);
        using var second = new AccountIdentityProjection(_root, tracked, metadata);
        await first.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        var discarding = first.DiscardAsync(profile.Id, 1, default); Task? preparing = null;
        try
        {
            await metadata.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            preparing = second.PrepareAsync(Summary(Session(profile.Id, 2)), default);
            // Before the fix, its uncontended local semaphore permits the repository callback
            // synchronously, even while the other instance is paused inside discard.
            Assert.False(tracked.EnteredDuringDiscard, "A second projection writer entered while another instance owned journal cleanup.");
        }
        finally
        {
            metadata.Release.TrySetResult();
            try { await discarding; }
            finally { mayWrite.TrySetResult(); if (preparing is not null) await preparing; }
        }
        Assert.True(File.Exists(Journal(profile.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAndReplayAlsoExcludeOtherInstancePreparation(bool replay)
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        var metadata = new BlockedAbsentMetadata { Summary = Summary(Session(profile.Id, 1)) };
        var mayWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = new TrackedRepository(repository, metadata, mayWrite.Task);
        using var first = new AccountIdentityProjection(_root, repository, metadata);
        using var second = new AccountIdentityProjection(_root, tracked, metadata);
        await first.PrepareAsync(metadata.Summary!, default);
        Task projecting = replay ? first.ReplayAsync(default) : first.CompleteAsync(profile.Id, 1, default);
        Task? preparing = null;
        try
        {
            await metadata.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            preparing = second.PrepareAsync(Summary(Session(profile.Id, 2)), default);
            Assert.False(tracked.EnteredDuringDiscard);
        }
        finally
        {
            metadata.Release.TrySetResult();
            try { await projecting; }
            finally { mayWrite.TrySetResult(); if (preparing is not null) await preparing; }
        }
        Assert.True(File.Exists(Journal(profile.Id)));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Journal(profile.Id)));
        Assert.Equal(2L, json.RootElement.GetProperty("Expected").GetProperty("Generation").GetInt64());
    }

    [Fact]
    public async Task CompatibilityDiscardRequiresSnapshotSupportAndPreservesMetadataOnlyJournal()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        var metadata = new MetadataOnly(); using var projection = new AccountIdentityProjection(_root, repository, metadata);
        await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        byte[] original = await File.ReadAllBytesAsync(Journal(profile.Id));
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.DiscardAsync(profile.Id, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(Journal(profile.Id))); Assert.Equal(0, metadata.Reads);
        await projection.DiscardAsync(profile.Id, 1, default); Assert.False(File.Exists(Journal(profile.Id)));
    }

    [Fact]
    public async Task CompatibilityDiscardWithNoCommittedGenerationPreservesIntentUntilExplicitFence()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var vault = new ProtectedCredentialVault(_root); using var projection = new AccountIdentityProjection(_root, repository, vault);
        var absent = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        var receipt = await vault.TryDeleteSnapshotAsync(absent, default);
        Assert.Equal(1L, receipt.CommittedGeneration);
        await projection.DiscardAsync(profile.Id, default); Assert.True(File.Exists(Journal(profile.Id)));
        await projection.DiscardAsync(profile.Id, receipt.CommittedGeneration!.Value, default);
        Assert.False(File.Exists(Journal(profile.Id)));
    }

    [Fact]
    public async Task OwnedBoundDeletesOldIntentButNeverDiscardsAStillSavedGrant()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var vault = new ProtectedCredentialVault(_root); using var projection = new AccountIdentityProjection(_root, repository, vault);
        await vault.SaveAsync(Session(profile.Id, 1), default); await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.DiscardAsync(profile.Id, 2, default));
        Assert.True(File.Exists(Journal(profile.Id)));
        var snapshot = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        var receipt = await vault.TryDeleteSnapshotAsync(snapshot, default);
        await projection.DiscardAsync(profile.Id, receipt.CommittedGeneration!.Value, default);
        Assert.False(File.Exists(Journal(profile.Id))); Assert.True(File.Exists(LockPath));
        Assert.Equal(0L, new FileInfo(LockPath).Length);
    }

    [Fact]
    public async Task MismatchedProfileOrContextPreservesJournal()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default); byte[] original = await File.ReadAllBytesAsync(Journal(profile.Id));
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(Summary(Session(profile.Id, 2)) with
        { VerifiedUserId = 999, Subject = "999" }, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(Journal(profile.Id)));
        var json = JsonNode.Parse(original)!.AsObject(); Guid foreign = Guid.NewGuid();
        json["Expected"]!["ProfileId"] = foreign; json["Expected"]!["CredentialRef"] = foreign.ToString("N");
        await File.WriteAllTextAsync(Journal(profile.Id), json.ToJsonString()); original = await File.ReadAllBytesAsync(Journal(profile.Id));
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.DiscardAsync(profile.Id, 2, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(Journal(profile.Id)));
    }

    [Fact]
    public async Task ActualProcessFileLeaseBlocksAllOperationsAndHonorsCancellation()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        var expected = Summary(Session(profile.Id, 1)); await projection.PrepareAsync(expected, default);
        var before = Files();
        using (var held = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Cancelled(ct => projection.PrepareAsync(expected with { Generation = 2 }, ct));
            await Cancelled(ct => projection.CompleteAsync(profile.Id, 1, ct));
            await Cancelled(ct => projection.DiscardAsync(profile.Id, 1, ct));
            await Cancelled(ct => projection.ReplayAsync(ct));
        }
        Unchanged(before);
        await projection.DiscardAsync(profile.Id, 1, default); Assert.False(File.Exists(Journal(profile.Id)));
    }

    [Fact]
    public async Task AdmissionIsBoundedAcrossInstancesAndWaitingOwnersCannotBeDisposed()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        var metadata = new BlockedAbsentMetadata(); using var first = new AccountIdentityProjection(_root, repository, metadata);
        using var second = new AccountIdentityProjection(_root, repository, metadata);
        var expected = Summary(Session(profile.Id, 1)); await first.PrepareAsync(expected, default);
        var active = first.DiscardAsync(profile.Id, 1, default); using var stop = new CancellationTokenSource();
        Task[] waiting = [];
        try
        {
            await metadata.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            waiting = [second.PrepareAsync(expected with { Generation = 2 }, stop.Token), second.ReplayAsync(stop.Token), second.DiscardAsync(profile.Id, 1, stop.Token)];
            await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => first.PrepareAsync(expected, default));
            Assert.Throws<InvalidOperationException>(() => second.Dispose());
        }
        finally
        {
            stop.Cancel();
            try { foreach (Task task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task); }
            finally { metadata.Release.TrySetResult(); await active; }
        }
        Assert.False(File.Exists(Journal(profile.Id)));
    }

    [Fact]
    public async Task ExternalLeaseAdmissionDeadlineFailsWithoutChangingJournal()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        var expected = Summary(Session(profile.Id, 1)); await projection.PrepareAsync(expected, default); var before = Files();
        using (var held = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(expected with { Generation = 2 }, default));
        Unchanged(before);
    }

    [Fact]
    public async Task OnlyCoordinationFileIsExcludedFromTheJournalBound()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        for (int i = 0; i < AccountIdentityProjection.MaximumPending - 1; i++)
            await File.WriteAllTextAsync(Path.Combine(JournalDirectory, "unknown-" + i + ".state"), "synthetic-unknown");
        var result = await projection.ReplayAsync(default);
        Assert.Equal(64, result.Pending); Assert.Equal(64, result.Problems.Count); Assert.Equal(0, result.Projected);
        await File.WriteAllTextAsync(Path.Combine(JournalDirectory, "unknown-extra.state"), "synthetic-unknown"); var before = Files();
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.ReplayAsync(default)); Unchanged(before);
    }

    [Fact]
    public async Task InvalidCoordinationContentsArePreservedAndNeverIncludedInErrors()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        var expected = Summary(Session(profile.Id, 1)); await projection.PrepareAsync(expected, default);
        const string marker = "synthetic-invalid-coordination-content";
        await File.WriteAllTextAsync(LockPath, marker); var before = Files();
        var error = await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(expected with { Generation = 2 }, default));
        Assert.DoesNotContain(marker, error.ToString()); Unchanged(before);
    }

    [Fact]
    public async Task JournalIsStillVersionOneNonsecretMetadataAndPrecancellationCreatesNoDirectory()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic alias"), default);
        using var projection = new AccountIdentityProjection(_root, repository, new MetadataOnly());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => projection.PrepareAsync(Summary(Session(profile.Id, 1)), stop.Token));
        Assert.False(Directory.Exists(JournalDirectory));
        await projection.PrepareAsync(Summary(Session(profile.Id, 1)), default);
        string text = await File.ReadAllTextAsync(Journal(profile.Id)); using var json = JsonDocument.Parse(text);
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.DoesNotContain("synthetic-projection-session", text); Assert.DoesNotContain("SessionCookie", text);
        Assert.DoesNotContain("AccessToken", text); Assert.DoesNotContain("RefreshToken", text);
        Assert.Equal(0L, new FileInfo(LockPath).Length);
    }

    private string JournalDirectory => Path.Combine(_root, "account-identity-pending");
    private string LockPath => Path.Combine(JournalDirectory, AccountIdentityProjection.WriterLockFileName);
    private Dictionary<string, byte[]> Files() => Directory.GetFiles(JournalDirectory)
        .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);
    private void Unchanged(Dictionary<string, byte[]> expected)
    {
        var actual = Files(); Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
    private static async Task Cancelled(Func<CancellationToken, Task> operation)
    { using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(stop.Token)); }

    private sealed class MetadataOnly : IAuthorizationMetadataReader
    {
        public int Reads;
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Reads++; return Task.FromResult<AuthorizationMetadata?>(null); }
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
            => throw new InvalidOperationException("Inventory reads are forbidden in this fixture.");
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class BlockedAbsentMetadata : IAuthorizationMetadataReader
    {
        public AuthorizationMetadata? Summary { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return Summary; }
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
            => throw new InvalidOperationException("Inventory reads are forbidden in this fixture.");
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class TrackedRepository(IAccountRepository inner, BlockedAbsentMetadata metadata, Task mayWrite) : IAccountRepository
    {
        private int _enteredDuringDiscard;
        public bool EnteredDuringDiscard => Volatile.Read(ref _enteredDuringDiscard) != 0;
        public async Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
        {
            if (!metadata.Release.Task.IsCompleted) Interlocked.Exchange(ref _enteredDuringDiscard, 1);
            await mayWrite.WaitAsync(ct); return await inner.FindAsync(id, ct);
        }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct) => inner.ReadAsync(query, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) => inner.SaveAsync(profile, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }

    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root), temp = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("zenith-projection-ownership-", StringComparison.Ordinal))
            throw new InvalidOperationException("The synthetic projection fixture path changed.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
