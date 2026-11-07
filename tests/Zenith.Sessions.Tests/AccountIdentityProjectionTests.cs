using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class AccountIdentityProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-identity-projection-tests-" + Guid.NewGuid().ToString("N"));
    private static AuthorizationMetadata Summary(Guid id, long generation = 5) => new(id, id.ToString("N"), RobloxOAuthIdentity.Provider,
        RobloxOAuthIdentity.Issuer, "456", 456, "verified_player", generation, AuthorizationHealth.Linked,
        DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow);

    [Fact]
    public async Task VerifiedUsernameIsSearchableAndOtherProfileEditsArePreserved()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("My alias") with
        { Group = "Farm", Notes = "Keep this note", Priority = 71, PreferredPlaceId = 6564161046, IsFavorite = true }, default);
        var metadata = new MetadataOnly(); var expected = Summary(profile.Id);
        using var projection = new AccountIdentityProjection(_root, repository, metadata);
        await projection.PrepareAsync(expected, default);
        // An independent edit between grant preparation and projection must survive.
        await repository.SaveAsync(profile with { Label = "New alias", Notes = "New note" }, default);
        metadata.Summaries[profile.Id] = expected;
        await projection.CompleteAsync(profile.Id, expected.Generation, default);
        var found = Assert.Single((await repository.ReadAsync(new(Search: "verified_player"), default)).Items);
        Assert.Equal(profile.Id, found.Id); Assert.Equal("New alias", found.Label); Assert.Equal("New note", found.Notes);
        Assert.Equal("Farm", found.Group); Assert.Equal(71, found.Priority); Assert.Equal(6564161046, found.PreferredPlaceId);
        Assert.True(found.IsFavorite); Assert.Equal(456, found.RobloxUserId); Assert.Empty(PendingFiles());
        Assert.Equal(1, metadata.SingleReads); Assert.Equal(0, metadata.InventoryReads);
    }

    [Theory]
    [InlineData("profile")][InlineData("reference")][InlineData("provider")][InlineData("issuer")]
    [InlineData("subject")][InlineData("user")][InlineData("generation")][InlineData("username")][InlineData("health")]
    public async Task MismatchedOrStaleSavedSummaryCannotChangeProfile(string mismatch)
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias") with { Username = "entered" }, default);
        var metadata = new MetadataOnly(); var expected = Summary(profile.Id);
        using var projection = new AccountIdentityProjection(_root, repository, metadata);
        await projection.PrepareAsync(expected, default);
        metadata.Summaries[profile.Id] = mismatch switch
        {
            "profile" => expected with { ProfileId = Guid.NewGuid() },
            "reference" => expected with { CredentialRef = "different" },
            "provider" => expected with { Provider = "other" },
            "issuer" => expected with { Issuer = "https://other.invalid/" },
            "subject" => expected with { Subject = "another-subject" },
            "user" => expected with { VerifiedUserId = 999 },
            "generation" => expected with { Generation = expected.Generation + 1 },
            "username" => expected with { VerifiedUsername = "another_name" },
            _ => expected with { Health = AuthorizationHealth.SignInRequired }
        };
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.CompleteAsync(profile.Id, expected.Generation, default));
        Assert.Equal("entered", (await repository.FindAsync(profile.Id, default))!.Username);
        Assert.Null((await repository.FindAsync(profile.Id, default))!.RobloxUserId); Assert.Single(PendingFiles());
    }

    [Fact]
    public async Task InterruptedProfileSaveCanBeReplayedAfterReopeningWithoutInventoryDecryption()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Persistent alias"), default);
        var metadata = new MetadataOnly(); var expected = Summary(profile.Id);
        var failing = new RepositoryProxy(repository) { FailNextSave = true };
        using (var projection = new AccountIdentityProjection(_root, failing, metadata))
        {
            await projection.PrepareAsync(expected, default); metadata.Summaries[profile.Id] = expected;
            await Assert.ThrowsAsync<IOException>(() => projection.CompleteAsync(profile.Id, expected.Generation, default));
            Assert.Single(PendingFiles()); Assert.Empty((await repository.FindAsync(profile.Id, default))!.Username);
        }
        using var reopened = new AccountIdentityProjection(_root, repository, metadata);
        var replay = await reopened.ReplayAsync(default);
        Assert.Equal(1, replay.Projected); Assert.Equal(0, replay.Pending); Assert.Empty(replay.Problems);
        Assert.Single((await repository.ReadAsync(new(Search: "verified_player"), default)).Items);
        Assert.Empty(PendingFiles()); Assert.Equal(0, metadata.InventoryReads);
    }

    [Fact]
    public async Task CrashBeforeVaultCommitPreservesJournalAndReportsPendingInsteadOfGuessingIdentity()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly();
        using (var projection = new AccountIdentityProjection(_root, repository, metadata))
            await projection.PrepareAsync(Summary(profile.Id), default);
        using var reopened = new AccountIdentityProjection(_root, repository, metadata);
        var result = await reopened.ReplayAsync(default);
        Assert.Equal(0, result.Projected); Assert.Equal(1, result.Pending); Assert.Single(result.Problems);
        Assert.Empty((await repository.FindAsync(profile.Id, default))!.Username); Assert.Single(PendingFiles());
    }

    [Fact]
    public async Task ConcurrentProfileRevisionIsNotOverwrittenAndReplayUsesTheNewestRow()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly(); var expected = Summary(profile.Id);
        var racing = new RepositoryProxy(repository) { BeforeSave = async () =>
        {
            var latest = (await repository.FindAsync(profile.Id, default))!;
            await repository.SaveAsync(latest with { Notes = "Concurrent edit" }, default);
        } };
        using (var projection = new AccountIdentityProjection(_root, racing, metadata))
        {
            await projection.PrepareAsync(expected, default); metadata.Summaries[profile.Id] = expected;
            await Assert.ThrowsAsync<RevisionConflictException>(() => projection.CompleteAsync(profile.Id, expected.Generation, default));
        }
        Assert.Equal("Concurrent edit", (await repository.FindAsync(profile.Id, default))!.Notes);
        using var reopened = new AccountIdentityProjection(_root, repository, metadata);
        Assert.Equal(1, (await reopened.ReplayAsync(default)).Projected);
        var saved = (await repository.FindAsync(profile.Id, default))!;
        Assert.Equal("Concurrent edit", saved.Notes); Assert.Equal("verified_player", saved.Username);
    }

    [Fact]
    public async Task JournalContainsOnlyBoundedNonsecretIdentityAndRejectsSupersededCompletion()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly(); using var projection = new AccountIdentityProjection(_root, repository, metadata);
        var expected = Summary(profile.Id); await projection.PrepareAsync(expected, default);
        string json = await File.ReadAllTextAsync(Assert.Single(PendingFiles()));
        Assert.DoesNotContain("AccessToken", json); Assert.DoesNotContain("RefreshToken", json);
        Assert.DoesNotContain("synthetic-access", json); Assert.DoesNotContain("synthetic-refresh", json);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, 8192);
        await projection.PrepareAsync(expected with { Generation = expected.Generation + 1 }, default);
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.CompleteAsync(profile.Id, expected.Generation, default));
        Assert.Empty((await repository.FindAsync(profile.Id, default))!.Username);
    }

    [Fact]
    public async Task LinkServiceJournalsBeforeGrantCommitAndProjectsOnlyAfterSavedSummary()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var vault = new ProjectingVault(() => Assert.Single(PendingFiles()));
        using var projection = new AccountIdentityProjection(_root, repository, vault);
        using var http = new HttpClient(new LoginHandler());
        using var service = new AccountLinkService(repository, vault, new(http), new Browser(), projection);
        await service.LinkAsync(profile.Id, "12345", 47635, default);
        Assert.Equal(1, vault.Saves); Assert.Empty(PendingFiles());
        Assert.Single((await repository.ReadAsync(new(Search: "verified_player"), default)).Items);
        Assert.Equal("Alias", (await repository.FindAsync(profile.Id, default))!.Label);
    }

    [Fact]
    public async Task FailedCommitIntentCanBeSupersededOnlyByNewerGenerationOfTheSameContextualAccount()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly(); using var projection = new AccountIdentityProjection(_root, repository, metadata);
        var expected = Summary(profile.Id); await projection.PrepareAsync(expected, default);
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(expected with
        { Generation = expected.Generation + 1, Subject = "another-user", VerifiedUserId = 999 }, default));
        var renamed = expected with { Generation = expected.Generation + 2, VerifiedUsername = "renamed_player" };
        await projection.PrepareAsync(renamed, default); metadata.Summaries[profile.Id] = renamed;
        await projection.CompleteAsync(profile.Id, renamed.Generation, default);
        Assert.Equal("renamed_player", (await repository.FindAsync(profile.Id, default))!.Username);
        Assert.Empty(PendingFiles());
    }

    [Fact]
    public async Task ExplicitDisconnectDiscardAllowsAFreshDifferentContextButNeverDiscardsASavedGrant()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly(); using var projection = new AccountIdentityProjection(_root, repository, metadata);
        var expected = Summary(profile.Id); await projection.PrepareAsync(expected, default); metadata.Summaries[profile.Id] = expected;
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.DiscardAsync(profile.Id, expected.Generation, default));
        Assert.Single(PendingFiles());
        // Models the owned disconnect's durable vault removal; this is not an automatic startup cleanup.
        metadata.Summaries.Remove(profile.Id); await projection.DiscardAsync(profile.Id, expected.Generation, default);
        Assert.Empty(PendingFiles());
        var next = expected with { Generation = expected.Generation + 2, Subject = "999", VerifiedUserId = 999, VerifiedUsername = "different_player" };
        await projection.PrepareAsync(next, default); metadata.Summaries[profile.Id] = next;
        await projection.CompleteAsync(profile.Id, next.Generation, default);
        Assert.Equal(999, (await repository.FindAsync(profile.Id, default))!.RobloxUserId);
    }

    [Fact]
    public async Task ExplicitDiscardPreservesCorruptJournalInsteadOfSilentlyRemovingUnknownState()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Alias"), default);
        var metadata = new MetadataOnly(); using var projection = new AccountIdentityProjection(_root, repository, metadata);
        await projection.PrepareAsync(Summary(profile.Id), default);
        string path = Assert.Single(PendingFiles()); await File.WriteAllTextAsync(path, "corrupt");
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => projection.DiscardAsync(profile.Id, 5, default));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
    }

    private string[] PendingFiles() => Directory.Exists(Path.Combine(_root, "account-identity-pending"))
        ? Directory.GetFiles(Path.Combine(_root, "account-identity-pending"), "*.json") : [];
    private sealed class MetadataOnly : IAuthorizationMetadataReader
    {
        public readonly Dictionary<Guid, AuthorizationMetadata> Summaries = [];
        public int SingleReads, InventoryReads;
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); SingleReads++; return Task.FromResult(Summaries.GetValueOrDefault(id)); }
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        { InventoryReads++; throw new InvalidOperationException("Projection must never enumerate the credential inventory."); }
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class RepositoryProxy(IAccountRepository inner) : IAccountRepository
    {
        public bool FailNextSave; public Func<Task>? BeforeSave;
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);
        public async Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct)
        {
            if (FailNextSave) { FailNextSave = false; throw new IOException("Synthetic interrupted metadata projection"); }
            if (BeforeSave is not null) { var action = BeforeSave; BeforeSave = null; await action(); }
            return await inner.SaveAsync(profile, ct);
        }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct) => inner.ReadAsync(query, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }
    private sealed class ProjectingVault(Action beforeSave) : IAccountCredentialVault, IAuthorizationMetadataReader, IAuthorizationGenerationStore
    {
        private AuthorizationRecord? _record; private long _generation; public int Saves;
        public Task<AuthorizationRecord?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult(_record);
        public Task SaveAsync(AuthorizationRecord record, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); beforeSave(); Saves++; _record = record; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct) { _record = null; return Task.CompletedTask; }
        public Task<long> AllocateNextGenerationAsync(Guid id, long minimumExclusive, CancellationToken ct) => Task.FromResult(_generation = Math.Max(_generation, minimumExclusive) + 1);
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct) => Task.FromResult(_record is null ? null : Summary(id, _record.Generation));
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) => Task.FromResult<AuthorizationMetadata?>(null);
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new InvalidOperationException("No inventory read is permitted.");
    }
    private sealed class Browser : IOAuthBrowserFlow
    {
        public Task<Uri> AuthorizeAsync(OAuthAttempt attempt, CancellationToken ct)
        {
            string state = attempt.AuthorizeUri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("state=", StringComparison.Ordinal)).Split('=', 2)[1];
            return Task.FromResult(new Uri(attempt.Callback + "?state=" + state + "&code=synthetic-code"));
        }
    }
    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.RequestUri!.AbsolutePath.EndsWith("/userinfo", StringComparison.Ordinal)
                ? "{\"sub\":\"456\",\"preferred_username\":\"verified_player\"}"
                : "{\"access_token\":\"synthetic-access\",\"refresh_token\":\"synthetic-refresh\",\"token_type\":\"Bearer\",\"expires_in\":899,\"scope\":\"openid profile\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(path) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(path).StartsWith("zenith-identity-projection-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
