using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Actual SQLite, DPAPI and conditional vault operations in a newly owned Temp fixture only.
// Sign-in and authenticated-user responses are synthetic. No browser/network/native process runs.
public sealed class RobloxSessionAccountCreationTests : IDisposable
{
    private readonly string _name = "zenith-session-add-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private const string Original = "synthetic-account-add-original", Rotated = "synthetic-account-add-rotated", Foreign = "synthetic-account-add-newer",
        Stale = "synthetic-account-add-stale";
    private static LinkedIdentity Identity(long user = 456, string username = "verified_player")
        => new(user, username, RobloxSessionIdentity.Issuer, user.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static AuthorizationRecord Record(Guid id, long generation = 1, string cookie = Original)
        => new(id, RobloxSessionIdentity.Provider, 456, generation, "", "", DateTimeOffset.MaxValue,
            "verified_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "456", SessionCookie: cookie);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task VerifiedSignInCreatesUsernameMetadataAndProtectedGrantThatReloadTogether()
    {
        await using var fixture = await Fixture.Open(Root);
        fixture.Browser.Run = async (id, _) =>
        {
            Assert.NotEqual(Guid.Empty, id); Assert.Null(await fixture.InnerProfiles.FindAsync(id, default));
            Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
            return fixture.Browser.Credential();
        };
        fixture.Client.Run = async (_, _) =>
        {
            Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
            return new(Identity());
        };
        var added = await fixture.Service.AddAsync(default);
        Assert.Equal(fixture.Browser.Ids.Single(), added.ProfileId); Assert.Equal(Identity(), added.Identity);
        using var reopenedProfiles = new AccountRepository(Root); await reopenedProfiles.InitializeAsync(default);
        using var reopenedVault = new ProtectedCredentialVault(Root);
        var profile = Assert.IsType<AccountProfile>(await reopenedProfiles.FindAsync(added.ProfileId, default));
        Assert.Equal("verified_player", profile.Label); Assert.Equal("verified_player", profile.Username);
        Assert.Equal(456, profile.RobloxUserId); Assert.Equal("General", profile.Group); Assert.Equal("", profile.AdditionalGroups);
        Assert.Null(profile.PreferredPlaceId); Assert.False(profile.IsFavorite); Assert.Equal(1, profile.Revision);
        var saved = Assert.IsType<AuthorizationRecord>(await reopenedVault.ReadAsync(added.ProfileId, default));
        Assert.Equal(Record(added.ProfileId), saved); Assert.Equal(AuthorizationHealth.Linked, saved.Health);
        Assert.Equal(1, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences); Assert.Equal(0, fixture.Vault.InventoryReads);
        Assert.Equal(0, fixture.Profiles.Upserts); Assert.Equal(1, fixture.Profiles.Creates);
        Assert.DoesNotContain(Original, JsonSerializer.Serialize(profile)); Assert.DoesNotContain(Original, added.ToString());
        byte[] protectedBytes = await File.ReadAllBytesAsync(fixture.SecretPath(added.ProfileId));
        Assert.DoesNotContain(Original, Encoding.UTF8.GetString(protectedBytes));
        Assert.False(File.Exists(fixture.JournalPath(added.ProfileId))); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Fact]
    public async Task IndexedOlderOAuthIdentityIsRejectedBeforeCreatingAnotherProfile()
    {
        // An older authorization type is never converted by Add; only a saved Roblox session is refreshed.
        await using var fixture = await Fixture.Open(Root);
        var existing = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Existing alias"), default);
        var record = new AuthorizationRecord(existing.Id, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-old-oauth-refresh", DateTimeOffset.MaxValue,
            "old_username", AuthorizationHealth.SignInRequired, RobloxOAuthIdentity.Issuer, "opaque-older-subject");
        await fixture.Inner.SaveAsync(record, default);
        byte[] before = await File.ReadAllBytesAsync(fixture.SecretPath(existing.Id));
        var error = await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
        Assert.Contains("already saved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, await fixture.InnerProfiles.FindAsync(existing.Id, default));
        Assert.Equal(1, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.Null(await fixture.InnerProfiles.FindAsync(fixture.Browser.Ids.Single(), default));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SecretPath(existing.Id)));
        Assert.Equal(record, await fixture.Inner.ReadAsync(existing.Id, default));
        Assert.Equal(0, fixture.Profiles.Creates); Assert.Equal(0, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences);
        AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Theory]
    [InlineData(AuthorizationHealth.Linked)]
    [InlineData(AuthorizationHealth.SignInRequired)]
    public async Task AddingAnAlreadySavedSessionRefreshesThatRowsSignInWithoutCreatingAProfileOrChangingItsMetadata(AuthorizationHealth health)
    {
        await using var fixture = await Fixture.Open(Root);
        var existing = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Main alt") with
        {
            Username = "verified_player", RobloxUserId = 456, Notes = "Keep these notes", Group = "Family", AdditionalGroups = "Raid",
            IsFavorite = true, Priority = 7, PreferredPlaceId = 42
        }, default);
        await fixture.Inner.SaveAsync(Record(existing.Id, cookie: health == AuthorizationHealth.Linked ? Stale : "") with { Health = health }, default);
        var added = await fixture.Service.AddAsync(default);
        Assert.Equal(existing.Id, added.ProfileId); Assert.True(added.Refreshed); Assert.Equal(Identity(), added.Identity);
        Guid signInOwner = fixture.Browser.Ids.Single(); Assert.NotEqual(existing.Id, signInOwner);
        Assert.Null(await fixture.InnerProfiles.FindAsync(signInOwner, default)); Assert.Equal(1, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.Equal(existing, await fixture.InnerProfiles.FindAsync(existing.Id, default));
        Assert.Equal(Record(existing.Id, 2), await fixture.Inner.ReadAsync(existing.Id, default));
        var unused = await fixture.Inner.ReadWriteSnapshotAsync(signInOwner, default); Assert.Null(unused.Record); Assert.Equal(0, unused.HighestAllocated);
        Assert.Equal(1, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences); Assert.Equal(0, fixture.Profiles.Creates); Assert.Equal(0, fixture.Profiles.Upserts);
        Assert.False(File.Exists(fixture.JournalPath(existing.Id))); Assert.False(File.Exists(fixture.JournalPath(signInOwner)));
        Assert.Equal(1, fixture.Client.Calls); Assert.DoesNotContain(Original, added.ToString()); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Fact]
    public async Task ASavedSessionForAnotherPlayerIsLeftAloneAndTheNewPlayerGetsItsOwnProfile()
    {
        await using var fixture = await Fixture.Open(Root);
        var other = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Other player") with { Username = "other_player", RobloxUserId = 789 }, default);
        var otherRecord = new AuthorizationRecord(other.Id, RobloxSessionIdentity.Provider, 789, 1, "", "", DateTimeOffset.MaxValue,
            "other_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "789", SessionCookie: Stale);
        await fixture.Inner.SaveAsync(otherRecord, default);
        var added = await fixture.Service.AddAsync(default);
        Assert.False(added.Refreshed); Assert.Equal(fixture.Browser.Ids.Single(), added.ProfileId); Assert.NotEqual(other.Id, added.ProfileId);
        Assert.Equal(2, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total); Assert.Equal(1, fixture.Profiles.Creates);
        Assert.Equal(other, await fixture.InnerProfiles.FindAsync(other.Id, default)); Assert.Equal(otherRecord, await fixture.Inner.ReadAsync(other.Id, default));
        Assert.Equal(Record(added.ProfileId), await fixture.Inner.ReadAsync(added.ProfileId, default));
    }

    [Fact]
    public async Task DisconnectOfTheSavedRowDuringItsRefreshCancelsAndJoinsTheRefreshBeforeRemovingIt()
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        var existing = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Main alt") with { Username = "verified_player", RobloxUserId = 456 }, default);
        await fixture.Inner.SaveAsync(Record(existing.Id, cookie: Stale), default);
        fixture.Vault.BeforeCommit = async (snapshot, _) => { if (snapshot.ProfileId == existing.Id) { entered.TrySetResult(); await release.Task; } };
        var adding = fixture.Service.AddAsync(default); Task? disconnecting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); disconnecting = fixture.Service.DisconnectAsync(existing.Id, default);
            Assert.False(disconnecting.IsCompleted); Assert.False(adding.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        await disconnecting!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await fixture.Inner.ReadAsync(existing.Id, default)); Assert.Equal(1, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.Null(await fixture.InnerProfiles.FindAsync(fixture.Browser.Ids.Single(), default)); Assert.Equal(0, fixture.Profiles.Creates);
        AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Fact]
    public async Task AnAddCannotRefreshARowWhileThatRowIsBeingRemovedAndCanAfterTheRemovalEnds()
    {
        await using var fixture = await Fixture.Open(Root);
        var existing = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Main alt") with { Username = "verified_player", RobloxUserId = 456 }, default);
        var saved = Record(existing.Id, cookie: Stale); await fixture.Inner.SaveAsync(saved, default);
        using (var removal = await fixture.Service.BeginProfileRemovalAsync([existing.Id], default))
        {
            Assert.False(removal.StoppedOperation);
            var error = await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
            Assert.Contains("being removed", error.Message); Assert.DoesNotContain(Original, error.Message);
            Assert.Equal(saved, await fixture.Inner.ReadAsync(existing.Id, default)); Assert.Equal(existing, await fixture.InnerProfiles.FindAsync(existing.Id, default));
            Assert.Equal(1, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total); Assert.Equal(0, fixture.Profiles.Creates);
            Assert.Equal(0, fixture.Vault.Commits); Assert.False(File.Exists(fixture.JournalPath(existing.Id)));
            AssertDisposed(fixture.Browser.Issued.Single());
        }
        var added = await fixture.Service.AddAsync(default);
        Assert.True(added.Refreshed); Assert.Equal(existing.Id, added.ProfileId); Assert.Equal(Record(existing.Id, 2), await fixture.Inner.ReadAsync(existing.Id, default));
    }

    [Fact]
    public async Task EditableMetadataNeverAutoMatchesOrOverwritesAnExistingProfile()
    {
        await using var fixture = await Fixture.Open(Root);
        var manual = await fixture.InnerProfiles.SaveAsync(AccountProfile.Create("Preserved manual alias") with
        { Username = "verified_player", RobloxUserId = 456, Notes = "Manual hint only", PreferredPlaceId = 42 }, default);
        var added = await fixture.Service.AddAsync(default);
        Assert.NotEqual(manual.Id, added.ProfileId); Assert.Equal(2, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.Equal(manual, await fixture.InnerProfiles.FindAsync(manual.Id, default)); Assert.Null(await fixture.Inner.ReadAsync(manual.Id, default));
        Assert.Equal(added.ProfileId, (await fixture.Inner.FindByRobloxUserIdAsync(456, default))!.ProfileId);
    }

    [Fact]
    public async Task AlreadyCancelledAddDoesNotOpenSignInOrCreateMetadata()
    {
        await using var fixture = await Fixture.Open(Root); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.AddAsync(stop.Token));
        Assert.Empty(fixture.Browser.Ids); Assert.Equal(0, fixture.Client.Calls); Assert.Equal(0, fixture.Profiles.Creates);
        Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeIdentityVerificationFinishesCreatesNoProfileAndJoinsItsOwner(bool duringValidation)
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        if (duringValidation) fixture.Client.Run = async (_, _) => { entered.TrySetResult(); await release.Task; return new(Identity()); };
        else fixture.Browser.Run = async (_, _) => { entered.TrySetResult(); await release.Task; return fixture.Browser.Credential(); };
        var adding = fixture.Service.AddAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); fixture.Service.Cancel(); Assert.False(adding.IsCompleted);
            Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
            await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, fixture.Profiles.Creates); Assert.Equal(0, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences);
        Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.All(fixture.Browser.Issued, AssertDisposed);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("username")]
    [InlineData("expired")]
    public async Task InvalidAuthenticatedIdentityOrExpiredCredentialCreatesNoProfile(string failure)
    {
        await using var fixture = await Fixture.Open(Root);
        if (failure == "expired") fixture.Browser.Expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Client.Run = (_, _) => Task.FromResult(new RobloxSessionValidation(failure switch
        {
            "user" => Identity() with { UserId = 0 }, "issuer" => Identity() with { Issuer = "https://untrusted.invalid/" },
            "subject" => Identity() with { Subject = "0456" }, "username" => Identity() with { Username = "invalid-name" }, _ => Identity()
        }));
        await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
        Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total); Assert.Equal(0, fixture.Profiles.Creates);
        Assert.Equal(0, fixture.Vault.Commits); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Fact]
    public async Task OneRotationIsReverifiedBeforeMetadataAndOnlyTheCurrentCredentialIsSaved()
    {
        await using var fixture = await Fixture.Open(Root); var replacement = new RobloxSessionCredential(Rotated, DateTimeOffset.UtcNow.AddHours(1));
        fixture.Client.Run = async (_, _) =>
        {
            Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
            return fixture.Client.Calls == 1 ? new(Identity(username: "first_name"), replacement) : new(Identity(username: "current_name"));
        };
        var added = await fixture.Service.AddAsync(default);
        var profile = Assert.IsType<AccountProfile>(await fixture.InnerProfiles.FindAsync(added.ProfileId, default));
        var saved = Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(added.ProfileId, default));
        Assert.Equal("current_name", added.Identity.Username); Assert.Equal("current_name", profile.Label); Assert.Equal("current_name", profile.Username);
        Assert.Equal(Rotated, saved.SessionCookie); Assert.Equal(replacement.ExpiresUtc, saved.ExpiresUtc);
        Assert.Equal(new[] { Original, Rotated }, fixture.Client.Seen); AssertDisposed(replacement); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("again")]
    [InlineData("expired")]
    public async Task UnverifiedOrExpiredRotationCreatesNoProfileAndDisposesEveryCredential(string failure)
    {
        await using var fixture = await Fixture.Open(Root);
        var replacement = new RobloxSessionCredential(Rotated, failure == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null);
        RobloxSessionCredential? repeated = failure == "again" ? new("synthetic-account-add-repeated") : null;
        fixture.Client.Run = (_, _) => Task.FromResult(fixture.Client.Calls == 1 ? new RobloxSessionValidation(Identity(), replacement)
            : new RobloxSessionValidation(failure switch
            {
                "user" => Identity(789), "issuer" => Identity() with { Issuer = RobloxOAuthIdentity.Issuer },
                "subject" => Identity() with { Subject = "789" }, _ => Identity()
            }, repeated));
        await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
        Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total); Assert.Equal(0, fixture.Profiles.Creates); Assert.Equal(0, fixture.Vault.Commits);
        AssertDisposed(replacement); if (repeated is not null) AssertDisposed(repeated); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFailureBeforeOrAfterItsCommitNeverReportsSuccessOrDeletesAnIdentifiableCard(bool afterCommit)
    {
        await using var fixture = await Fixture.Open(Root);
        if (afterCommit) fixture.Profiles.AfterCreate = _ => throw new IOException("Synthetic metadata acknowledgment failure");
        else fixture.Profiles.BeforeCreate = _ => throw new IOException("Synthetic metadata write failure");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.AddAsync(default));
        Guid id = fixture.Browser.Ids.Single(); var profile = await fixture.InnerProfiles.FindAsync(id, default);
        Assert.Equal(afterCommit ? 1 : 0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        if (afterCommit) { Assert.NotNull(profile); Assert.Equal("verified_player", profile.Label); Assert.Equal(456, profile.RobloxUserId); }
        Assert.Null(await fixture.Inner.ReadAsync(id, default)); Assert.Equal(0, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences);
        Assert.Equal(0, fixture.Profiles.Deletes); AssertDisposed(fixture.Browser.Issued.Single());
    }

    [Fact]
    public async Task CancellationAfterMetadataCommitPreservesTheCardWithoutIssuingAuthorization()
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        fixture.Profiles.AfterCreate = async _ => { entered.TrySetResult(); await release.Task; };
        var adding = fixture.Service.AddAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); fixture.Service.Cancel(); Assert.False(adding.IsCompleted);
            var profile = Assert.IsType<AccountProfile>(await fixture.InnerProfiles.FindAsync(fixture.Browser.Ids.Single(), default));
            Assert.Equal("verified_player", profile.Label); Assert.Equal("verified_player", profile.Username); Assert.Equal(456, profile.RobloxUserId);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        Guid id = fixture.Browser.Ids.Single(); var snapshot = await fixture.Inner.ReadWriteSnapshotAsync(id, default);
        Assert.Null(snapshot.Record); Assert.Equal(0, snapshot.HighestAllocated); Assert.False(File.Exists(fixture.SecretPath(id)));
        Assert.NotNull(await fixture.InnerProfiles.FindAsync(id, default)); Assert.Equal(0, fixture.Vault.Commits); Assert.Equal(0, fixture.Vault.Fences);
        Assert.Equal(0, fixture.Profiles.Deletes);
    }

    [Fact]
    public async Task ProjectionPreparationFailurePreservesAlreadyCreatedMetadataWithoutAUsableGrant()
    {
        await using var fixture = await Fixture.Open(Root);
        fixture.Profiles.AfterCreate = _ => { fixture.Profiles.FailFind = true; return Task.CompletedTask; };
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.AddAsync(default)); fixture.Profiles.FailFind = false;
        Guid id = fixture.Browser.Ids.Single(); Assert.NotNull(await fixture.InnerProfiles.FindAsync(id, default));
        Assert.Null(await fixture.Inner.ReadAsync(id, default)); Assert.Equal(0, fixture.Vault.Commits); Assert.Equal(0, fixture.Profiles.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFinalCommitFencesOnlyItsOwnGrantAndPreservesProtectedBytes(bool cancelled)
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal(); byte[]? committed = null;
        fixture.Vault.AfterCommit = async record =>
        {
            committed = await File.ReadAllBytesAsync(fixture.SecretPath(record.ProfileId)); entered.TrySetResult(); await release.Task;
            if (!cancelled) throw new IOException("Synthetic protected acknowledgment failure");
        };
        var adding = fixture.Service.AddAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (cancelled) fixture.Service.Cancel();
            Assert.False(adding.IsCompleted); Assert.Equal(1, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        }
        finally { release.TrySetResult(); }
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        else await Assert.ThrowsAsync<IOException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        Guid id = fixture.Browser.Ids.Single(); var snapshot = await fixture.Inner.ReadWriteSnapshotAsync(id, default);
        Assert.Equal(2, snapshot.HighestAllocated); Assert.Equal(1, snapshot.LastCommitted); Assert.Equal(Guid.Empty, snapshot.HighestMutationId);
        Assert.NotNull(snapshot.Record); Assert.Equal(AuthorizationHealth.SignInRequired, snapshot.Record.Health); Assert.Empty(snapshot.Record.SessionCookie);
        Assert.Equal(committed, await File.ReadAllBytesAsync(fixture.SecretPath(id))); Assert.True(File.Exists(fixture.JournalPath(id)));
        Assert.NotNull(await fixture.InnerProfiles.FindAsync(id, default)); Assert.Equal(1, fixture.Vault.Fences); Assert.Equal(0, fixture.Profiles.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFailureOrCancellationCannotFenceANewerForeignGrant(bool cancelled)
    {
        await using var fixture = await Fixture.Open(Root); byte[]? foreignBytes = null;
        fixture.Vault.AfterCommit = async record =>
        {
            long next = await fixture.Inner.AllocateNextGenerationAsync(record.ProfileId, record.Generation, default);
            await fixture.Inner.SaveAsync(Record(record.ProfileId, next, Foreign), default);
            foreignBytes = await File.ReadAllBytesAsync(fixture.SecretPath(record.ProfileId));
            if (cancelled) fixture.Service.Cancel(); else throw new IOException("Synthetic late acknowledgment failure");
        };
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.AddAsync(default));
        else await Assert.ThrowsAsync<IOException>(() => fixture.Service.AddAsync(default));
        Guid id = fixture.Browser.Ids.Single(); var saved = Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(id, default));
        Assert.Equal(Record(id, 2, Foreign), saved); Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(fixture.SecretPath(id)));
        Assert.NotNull(await fixture.InnerProfiles.FindAsync(id, default)); Assert.Equal(1, fixture.Vault.Fences); Assert.Equal(0, fixture.Profiles.Deletes);
    }

    [Fact]
    public async Task ChangedAbsentSnapshotPreservesTheForeignGrantAndNewlyIdentifiableProfile()
    {
        await using var fixture = await Fixture.Open(Root);
        fixture.Vault.BeforeCommit = (_, record) => fixture.Inner.SaveAsync(Record(record.ProfileId, 1, Foreign), default);
        var error = await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.AddAsync(default));
        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Guid id = fixture.Browser.Ids.Single(); Assert.Equal(Record(id, 1, Foreign), await fixture.Inner.ReadAsync(id, default));
        Assert.NotNull(await fixture.InnerProfiles.FindAsync(id, default)); Assert.Equal(1, fixture.Vault.Fences); Assert.Equal(0, fixture.Profiles.Deletes);
    }

    [Fact]
    public async Task TwoConcurrentAddsPassPreflightButAtomicVaultClaimsPermitOnlyOneSavedIdentity()
    {
        await using var first = await Fixture.Open(Root); await using var second = await Fixture.Open(Root);
        var bothReady = Signal(); var release = Signal(); int ready = 0;
        async Task BeforeCommit(AuthorizationWriteSnapshot _, AuthorizationRecord __)
        { if (Interlocked.Increment(ref ready) == 2) bothReady.TrySetResult(); await release.Task; }
        first.Vault.BeforeCommit = BeforeCommit; second.Vault.BeforeCommit = BeforeCommit;
        var one = Observe(first.Service.AddAsync(default)); var two = Observe(second.Service.AddAsync(default));
        try { await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(4));
        var winner = Assert.Single(results.Where(result => result.Added is not null)).Added!;
        Assert.Single(results.Where(result => result.Error is not null)); Assert.Equal(2, ready);
        Assert.Equal(2, (await first.InnerProfiles.ReadAsync(new(), default)).Total);
        Assert.Equal(winner.ProfileId, (await first.Inner.FindByRobloxUserIdAsync(456, default))!.ProfileId);
        Assert.Equal(Record(winner.ProfileId), await first.Inner.ReadAsync(winner.ProfileId, default));
        Guid loser = new[] { first.Browser.Ids.Single(), second.Browser.Ids.Single() }.Single(id => id != winner.ProfileId);
        Assert.Null(await first.Inner.ReadAsync(loser, default)); Assert.Equal(0, (await first.Inner.ReadWriteSnapshotAsync(loser, default)).HighestAllocated);
        Assert.NotNull(await first.InnerProfiles.FindAsync(loser, default)); Assert.Equal(0, first.Profiles.Deletes + second.Profiles.Deletes);
    }

    [Fact]
    public async Task DisposeWaitsForTheActualLateSignInAndDisposesItsCredentialWithoutTakingBorrowedDependencies()
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        fixture.Browser.Run = async (_, _) => { entered.TrySetResult(); await release.Task; return fixture.Browser.Credential(); };
        var adding = fixture.Service.AddAsync(default); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); disposing = fixture.Service.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted); Assert.False(adding.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.AddAsync(default));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        await disposing!.WaitAsync(TimeSpan.FromSeconds(3)); AssertDisposed(fixture.Browser.Issued.Single());
        Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total); Assert.False(fixture.Browser.Disposed); Assert.False(fixture.Client.Disposed);
    }

    [Fact]
    public async Task DisconnectOfThePendingNewIdentityCancelsAndJoinsBeforeWritingItsTombstone()
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        fixture.Browser.Run = async (_, _) => { entered.TrySetResult(); await release.Task; return fixture.Browser.Credential(); };
        var adding = fixture.Service.AddAsync(default); Task? disconnecting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); disconnecting = fixture.Service.DisconnectAsync(fixture.Browser.Ids.Single(), default);
            Assert.False(disconnecting.IsCompleted); Assert.False(adding.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(TimeSpan.FromSeconds(3)));
        await disconnecting!.WaitAsync(TimeSpan.FromSeconds(3));
        var snapshot = await fixture.Inner.ReadWriteSnapshotAsync(fixture.Browser.Ids.Single(), default);
        Assert.Null(snapshot.Record); Assert.Equal(1, snapshot.HighestAllocated); Assert.Equal(0, (await fixture.InnerProfiles.ReadAsync(new(), default)).Total);
        AssertDisposed(fixture.Browser.Issued.Single());
    }

    private static async Task<(AddedRobloxAccount? Added, Exception? Error)> Observe(Task<AddedRobloxAccount> work)
    { try { return (await work, null); } catch (Exception error) { return (null, error); } }
    private static void AssertDisposed(RobloxSessionCredential credential) => Assert.Throws<ObjectDisposedException>(() => credential.CopyValue());

    private sealed class Fixture : IAsyncDisposable
    {
        public required AccountRepository InnerProfiles; public required Repository Profiles;
        public required ProtectedCredentialVault Inner; public required Vault Vault; public required AccountIdentityProjection Projection;
        public required Browser Browser; public required Client Client; public required RobloxSessionLinkService Service; public required string Root;
        public string SecretPath(Guid id) => Path.Combine(Root, "accounts.vault", id.ToString("N") + ".dpapi");
        public string JournalPath(Guid id) => Path.Combine(Root, "account-identity-pending", id.ToString("N") + ".json");
        public static async Task<Fixture> Open(string root)
        {
            var innerProfiles = new AccountRepository(root); await innerProfiles.InitializeAsync(default);
            var profiles = new Repository(innerProfiles); var inner = new ProtectedCredentialVault(root); var vault = new Vault(inner);
            var browser = new Browser(); var client = new Client(); var projection = new AccountIdentityProjection(root, profiles, vault);
            return new() { Root = root, InnerProfiles = innerProfiles, Profiles = profiles, Inner = inner, Vault = vault,
                Browser = browser, Client = client, Projection = projection, Service = new(profiles, vault, client, browser, projection) };
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); Projection.Dispose(); Inner.Dispose(); InnerProfiles.Dispose(); Browser.Dispose(); Client.Dispose(); }
    }
    private sealed class Browser : IRobloxSessionSignIn, IDisposable
    {
        public readonly List<Guid> Ids = []; public readonly List<RobloxSessionCredential> Issued = [];
        public Func<Guid, CancellationToken, Task<RobloxSessionCredential>>? Run; public DateTimeOffset? Expiry; public bool Disposed;
        public RobloxSessionCredential Credential() { var credential = new RobloxSessionCredential(Original, Expiry); Issued.Add(credential); return credential; }
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { Ids.Add(id); return Run?.Invoke(id, ct) ?? Task.FromResult(Credential()); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Client : IRobloxSessionClient, IDisposable
    {
        public int Calls; public readonly List<string> Seen = []; public bool Disposed;
        public Func<RobloxSessionCredential, CancellationToken, Task<RobloxSessionValidation>>? Run;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { Calls++; Seen.Add(credential.CopyValue()); return Run?.Invoke(credential, ct) ?? Task.FromResult(new RobloxSessionValidation(Identity())); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Repository(AccountRepository inner) : IAccountRepository
    {
        public int Creates, Upserts, Deletes; public bool FailFind;
        public Func<IReadOnlyList<AccountProfile>, Task>? BeforeCreate, AfterCreate;
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
            => FailFind ? throw new IOException("Synthetic projection metadata failure") : inner.FindAsync(id, ct);
        public Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct) => inner.ReadAsync(query, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) { Upserts++; return inner.SaveAsync(profile, ct); }
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) { Deletes++; return inner.DeleteAsync(id, revision, ct); }
        public async Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct)
        { Creates++; if (BeforeCreate is not null) await BeforeCreate(profiles); await inner.CreateManyAsync(profiles, ct); if (AfterCreate is not null) await AfterCreate(profiles); }
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) { Deletes += profiles.Count; return inner.DeleteManyAsync(profiles, ct); }
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }
    private sealed class Vault(ProtectedCredentialVault inner) : IAccountCredentialVault, ISnapshotAuthorizationVault, IAuthorizationMetadataReader, IRobloxAuthorizationIdentityReader
    {
        public int Commits, Fences, InventoryReads;
        public Func<AuthorizationWriteSnapshot, AuthorizationRecord, Task>? BeforeCommit; public Func<AuthorizationRecord, Task>? AfterCommit;
        public Task<AuthorizationRecord?> ReadAsync(Guid id, CancellationToken ct) => inner.ReadAsync(id, ct);
        public Task SaveAsync(AuthorizationRecord record, CancellationToken ct) => throw new InvalidOperationException("A nonconditional save is forbidden in this fixture.");
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("A nonconditional delete is forbidden in this fixture.");
        public Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid id, CancellationToken ct) => inner.ReadWriteSnapshotAsync(id, ct);
        public async Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected, AuthorizationRecord replacement, Guid mutationId, CancellationToken ct)
        { Commits++; if (BeforeCommit is not null) await BeforeCommit(expected, replacement); var result = await inner.TryCommitSnapshotAsync(expected, replacement, mutationId, ct); if (result.Status == AuthorizationMutationStatus.Applied && AfterCommit is not null) await AfterCommit(replacement); return result; }
        public Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected, CancellationToken ct) => inner.TryDeleteSnapshotAsync(expected, ct);
        public Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected, AuthorizationRecord replacement, Guid mutationId, CancellationToken ct) => inner.TryReplaceNextAsync(expected, replacement, mutationId, ct);
        public Task<bool> TryFenceOwnedMutationAsync(Guid id, Guid mutationId, CancellationToken ct) { Fences++; return inner.TryFenceOwnedMutationAsync(id, mutationId, ct); }
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct) => inner.GetSummaryAsync(id, ct);
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        { InventoryReads++; throw new InvalidOperationException("No credential inventory reads are allowed."); }
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) => inner.FindByIdentityAsync(provider, issuer, subject, ct);
        public Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long user, CancellationToken ct) => inner.FindByRobloxUserIdAsync(user, ct);
    }
    public void Dispose()
    {
        string path = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(path) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(path) != _name)
            throw new InvalidOperationException("Unexpected synthetic account-creation cleanup path.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
