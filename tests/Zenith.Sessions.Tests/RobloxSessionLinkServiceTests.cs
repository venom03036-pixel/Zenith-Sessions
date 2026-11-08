using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RobloxSessionLinkServiceTests : IDisposable
{
    private const string Secret = "synthetic-owned-session-only";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-session-link-" + Guid.NewGuid().ToString("N"));
    private static LinkedIdentity Identity(long id = 456) => new(id, "verified_player", RobloxSessionIdentity.Issuer, id.ToString(CultureInfo.InvariantCulture));
    private static AuthorizationRecord Saved(Guid id, long user = 456) => new(id, RobloxSessionIdentity.Provider, user, 1,
        "", "", DateTimeOffset.MaxValue, "verified_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer,
        user.ToString(CultureInfo.InvariantCulture), SessionCookie: Secret);
    private async Task<AccountRepository> Repository()
    { var repository = new AccountRepository(_root); await repository.InitializeAsync(default); return repository; }
    private static async Task<AccountProfile> Profile(AccountRepository repository) => await repository.SaveAsync(AccountProfile.Create("Manual alias") with { Notes = "Keep notes", Group = "Keep group", IsFavorite = true }, default);
    private sealed class Browser : IRobloxSessionSignIn
    {
        public int Calls; public RobloxSessionCredential? Issued;
        public Func<CancellationToken, Task<RobloxSessionCredential>>? Wait;
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
        { Calls++; return Wait?.Invoke(ct) ?? Task.FromResult(Issued = new RobloxSessionCredential(Secret)); }
    }
    private sealed class Client : IRobloxSessionClient
    {
        public int Calls; public readonly List<string> Seen = [];
        public Func<int, CancellationToken, Task<RobloxSessionValidation>>? Reply;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { Seen.Add(credential.CopyValue()); Calls++; return Reply?.Invoke(Calls, ct) ?? Task.FromResult(new RobloxSessionValidation(Identity())); }
    }
    private sealed class Vault : IAccountCredentialVault, IAuthorizationGenerationStore, IAuthorizationMetadataReader, IRobloxAuthorizationIdentityReader,ISnapshotAuthorizationVault
    {
        public readonly Dictionary<Guid, AuthorizationRecord> Records = [];
        private readonly Dictionary<Guid, long> _generations = [];
        private readonly Dictionary<Guid,long> _committed=[];
        private readonly Dictionary<Guid,Guid> _owners=[];
        public int Reads, Saves, FenceAttempts; public bool FailSave;
        public Func<AuthorizationRecord, Task>? BeforeSave;
        /// <summary>When set, fencing an owned write fails with this exception instead of running.</summary>
        public Exception? FenceFailure;
        public Task<AuthorizationRecord?> ReadAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Records.GetValueOrDefault(id)); }
        public async Task SaveAsync(AuthorizationRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); record.Validated(); if (BeforeSave is not null) await BeforeSave(record);
            if (FailSave) throw new IOException("Synthetic protected save failure"); Saves++; Records[record.ProfileId] = record;
        }
        public Task DeleteAsync(Guid id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Records.Remove(id); return Task.CompletedTask; }
        public Task<long> AllocateNextGenerationAsync(Guid id, long minimumExclusive, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); long next = Math.Max(_generations.GetValueOrDefault(id), minimumExclusive) + 1; _generations[id] = next;_owners[id]=Guid.Empty; return Task.FromResult(next); }
        private AuthorizationWriteSnapshot Snapshot(Guid id)
        {
            var raw=Records.GetValueOrDefault(id);long highest=_generations.GetValueOrDefault(id,raw?.Generation??0);
            long committed=_committed.GetValueOrDefault(id,raw?.Generation??0);var readable=raw;
            if(raw is not null&&(raw.Generation!=highest||raw.Generation!=committed))
                readable=raw with{Health=AuthorizationHealth.SignInRequired,SessionCookie="",AccessToken="",RefreshToken=""};
            return new(id,readable,highest,committed,_owners.GetValueOrDefault(id),
                raw is null?null:Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(raw))));
        }
        public Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid id,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Reads++;return Task.FromResult(Snapshot(id));}
        public async Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();if(Snapshot(expected.ProfileId)!=expected)return new(AuthorizationMutationStatus.Conflict);
            Assert.Equal(expected.NextGeneration,replacement.Generation);_generations[expected.ProfileId]=replacement.Generation;_owners[expected.ProfileId]=mutationId;
            // Preserve the existing controllable uncooperative Save boundary for service tests;
            // real cross-process/partial-file semantics are covered by the actual vault fixtures.
            await SaveAsync(replacement,ct);_committed[expected.ProfileId]=replacement.Generation;
            return new(AuthorizationMutationStatus.Applied,replacement.Generation);
        }
        public async Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();if(Snapshot(expected.ProfileId)!=expected)return new(AuthorizationMutationStatus.Conflict);
            _committed[expected.ProfileId]=expected.LastCommitted;_generations[expected.ProfileId]=expected.NextGeneration;_owners[expected.ProfileId]=Guid.Empty;
            await DeleteAsync(expected.ProfileId,ct);return new(AuthorizationMutationStatus.Applied,expected.NextGeneration);
        }
        public Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)
        {
            var snapshot=Snapshot(expected.ProfileId);if(snapshot.Record!=expected)return Task.FromResult(new AuthorizationMutationResult(AuthorizationMutationStatus.Conflict));
            return TryCommitSnapshotAsync(snapshot,replacement with{Generation=snapshot.NextGeneration},mutationId,ct);
        }
        public Task<bool> TryFenceOwnedMutationAsync(Guid id,Guid mutationId,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();FenceAttempts++;if(FenceFailure is not null)return Task.FromException<bool>(FenceFailure);
            if(_owners.GetValueOrDefault(id)!=mutationId)return Task.FromResult(false);
            _generations[id]=Snapshot(id).NextGeneration;_owners[id]=Guid.Empty;return Task.FromResult(true);
        }
        private static AuthorizationMetadata Summary(AuthorizationRecord r) => new(r.ProfileId, r.ProfileId.ToString("N"), r.Provider, r.Issuer, r.Subject,
            r.VerifiedUserId, r.VerifiedUsername, r.Generation, r.Health, r.ExpiresUtc, DateTimeOffset.UtcNow);
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct) => Task.FromResult(Records.TryGetValue(id, out var r) ? Summary(r) : null);
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuthorizationMetadata>>(ids.Where(Records.ContainsKey).Select(id => Summary(Records[id])).ToArray());
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) =>
            Task.FromResult(Records.Values.Where(r => r.Provider == provider && r.Issuer == issuer && r.Subject == subject).Select(Summary).SingleOrDefault());
        public Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long userId, CancellationToken ct) =>
            Task.FromResult(Records.Values.Where(r => r.VerifiedUserId == userId).Select(Summary).SingleOrDefault());
    }

    [Fact]
    public async Task ExplicitSignInSavesSelectedSessionAndProjectsSearchableNameWithoutChangingPreferences()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var browser = new Browser(); var client = new Client();
        using var projection = new AccountIdentityProjection(_root, repository, vault);
        await using var service = new RobloxSessionLinkService(repository, vault, client, browser, projection);
        var identity = await service.LinkAsync(profile.Id, default); var saved = Assert.Single(vault.Records).Value;
        Assert.Equal(456, identity.UserId); Assert.Equal(RobloxSessionIdentity.Provider, saved.Provider); Assert.Equal(Secret, saved.SessionCookie);
        Assert.Empty(saved.RefreshToken); Assert.Empty(saved.AccessToken); Assert.Equal(DateTimeOffset.MaxValue, saved.ExpiresUtc);
        Assert.Equal(1, vault.Reads); Assert.Equal(1, browser.Calls); Assert.Equal(1, client.Calls);
        Assert.Throws<ObjectDisposedException>(() => browser.Issued!.CopyValue()); Assert.DoesNotContain(Secret, saved.ToString());
        var updated = (await repository.FindAsync(profile.Id, default))!;
        Assert.Equal("Manual alias", updated.Label); Assert.Equal("Keep notes", updated.Notes); Assert.Equal("Keep group", updated.Group); Assert.True(updated.IsFavorite);
        Assert.Equal(profile.Id, Assert.Single((await repository.ReadAsync(new("verified_player"), default)).Items).Id);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "account-identity-pending"), "*.json"));
    }

    [Fact]
    public async Task ShareLinkResolutionUsesTheSavedSessionOnceAndDisposesIt()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = new AuthorizationRecord(profile.Id, RobloxSessionIdentity.Provider, 456, 1, "", "", DateTimeOffset.MaxValue, "verified_player",
            Issuer: RobloxSessionIdentity.Issuer, Subject: "456", SessionCookie: Secret);
        var resolver = new ShareLinks(); var client = new Client(); var browser = new Browser();
        await using var service = new RobloxSessionLinkService(repository, vault, client, browser, shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.ExperienceDetails);
        using var resolution = await service.ResolveShareLinkAsync(profile.Id, link, default);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(5, resolution.PlaceId);
        Assert.Equal(1, resolver.Calls); Assert.Same(link, resolver.SeenLink); Assert.Equal(Secret, resolver.SeenCookie); Assert.True(resolver.CredentialDisposedAfterCall);
        Assert.Equal(0, vault.Saves); Assert.Equal(0, client.Calls); Assert.Equal(0, browser.Calls); Assert.Same(vault.Records[profile.Id], vault.Records[profile.Id]);
    }

    // A kept anti-forgery token belongs to the profile and the account verified for it, and sign-out forgets it.
    [Fact]
    public async Task ShareLinkTokensAreScopedToTheVerifiedAccountAndForgottenOnSignOut()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = Saved(profile.Id, 789);
        var resolver = new ShareLinks();
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.ExperienceDetails);
        using (await service.ResolveShareLinkAsync(profile.Id, link, default)) { }
        Assert.Equal(new RobloxShareLinkTokenScope(profile.Id, 789), resolver.SeenScope);
        Assert.Empty(resolver.Forgotten);
        await service.DisconnectAsync(profile.Id, default);
        Assert.Equal(profile.Id, Assert.Single(resolver.Forgotten)); Assert.Equal(0, resolver.ForgotAll); Assert.Empty(vault.Records);
    }

    [Fact]
    public async Task ShareLinkResolutionWithoutASavedSessionIsSignInRequiredWithoutARequest()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var resolver = new ShareLinks();
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        vault.Records[profile.Id] = new AuthorizationRecord(profile.Id, RobloxSessionIdentity.Provider, 456, 1, "", "", DateTimeOffset.MaxValue, "verified_player",
            Health: AuthorizationHealth.SignInRequired, Issuer: RobloxSessionIdentity.Issuer, Subject: "456");
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Equal(RobloxShareLinkOutcome.Invalid, (await service.ResolveShareLinkAsync(Guid.NewGuid(), link, default)).Outcome); // Unknown profile.
        Assert.Equal(0, resolver.Calls);
        await using var unwired = new RobloxSessionLinkService(repository, vault, new Client(), new Browser());
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, (await unwired.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
    }

    // O-C1-3: a check on an account that is being removed is refused before its saved sign-in is read (N-B1a-1), and the
    // outcome says the account is being removed, not that another account operation is running.
    [Fact]
    public async Task ShareLinkResolutionOnAProfileBeingRemovedSaysTheProfileIsRemovedWithoutARequest()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var resolver = new ShareLinks();
        vault.Records[profile.Id] = Saved(profile.Id, 456);
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.ExperienceDetails);
        using (await service.BeginProfileRemovalAsync([profile.Id], default))
        {
            Assert.Equal(RobloxShareLinkOutcome.ProfileRemoved, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
            Assert.Equal(0, resolver.Calls); Assert.Equal(0, vault.Reads); Assert.Equal(0, vault.Saves);
        }
        Assert.Equal(RobloxShareLinkOutcome.Experience, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome); Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task ShareLinkUnauthorizedFencesTheSavedSessionToSignInRequired()
    {
        // Roblox answered 401 with this saved session: the account row and launch eligibility must stop calling it linked.
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = Saved(profile.Id);
        var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.SignInRequired) }; var client = new Client(); var browser = new Browser();
        await using var service = new RobloxSessionLinkService(repository, vault, client, browser, shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        var fenced = vault.Records[profile.Id];
        Assert.Equal(AuthorizationHealth.SignInRequired, fenced.Health); Assert.True(fenced.Generation > 1); Assert.Equal(1, vault.Saves);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(profile.Id, default))!.Health); // What the account row reads.
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Equal(1, resolver.Calls); Assert.Equal(0, client.Calls); Assert.Equal(0, browser.Calls); Assert.Equal(1, vault.Saves);
    }

    [Fact]
    public async Task ShareLinkUnauthorizedLeavesANewerGrantFromAnotherOperationUnchanged()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = Saved(profile.Id);
        var newer = Saved(profile.Id) with { Generation = 2, SessionCookie = "synthetic-rotation" };
        // Another process signs in again while Roblox is answering the old session's request.
        var resolver = new ShareLinks { Reply = () => { vault.Records[profile.Id] = newer; return new(RobloxShareLinkOutcome.SignInRequired); } };
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Same(newer, vault.Records[profile.Id]); Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task ShareLinkFailuresOtherThanUnauthorizedLeaveTheSavedSessionLinked()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var saved = Saved(profile.Id); vault.Records[profile.Id] = saved;
        foreach (var outcome in new[] { RobloxShareLinkOutcome.Unavailable, RobloxShareLinkOutcome.Expired, RobloxShareLinkOutcome.Invalid })
        {
            var resolver = new ShareLinks { Reply = () => new(outcome) };
            await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
            using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
            Assert.Equal(outcome, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        }
        Assert.Same(saved, vault.Records[profile.Id]); Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task ShareLinkRotatedSessionIsSavedAtTheNextGenerationOnlyAfterItVerifiesAsTheSamePlayer()
    {
        // R-accounts-3: Roblox replaced the saved session while answering the share link.
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = Saved(profile.Id);
        var expiry = DateTimeOffset.UtcNow.AddHours(1); RobloxSessionCredential? rotation = null;
        var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: rotation = new("synthetic-share-rotation", expiry)) };
        var client = new Client(); var browser = new Browser();
        await using var service = new RobloxSessionLinkService(repository, vault, client, browser, shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.ExperienceDetails);
        using var resolution = await service.ResolveShareLinkAsync(profile.Id, link, default);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(5, resolution.PlaceId);
        Assert.Null(resolution.TakeRotatedCredential()); // The replacement never leaves the service.
        Assert.Throws<ObjectDisposedException>(() => rotation!.CopyValue());
        var kept = vault.Records[profile.Id];
        Assert.Equal("synthetic-share-rotation", kept.SessionCookie); Assert.Equal(2, kept.Generation); Assert.Equal(AuthorizationHealth.Linked, kept.Health);
        Assert.Equal(expiry, kept.ExpiresUtc); Assert.Equal(456, kept.VerifiedUserId); Assert.Equal(1, vault.Saves);
        Assert.Equal(new[] { "synthetic-share-rotation" }, client.Seen); Assert.Equal(0, browser.Calls); // Verified with the replacement itself.
        Assert.DoesNotContain("synthetic-share-rotation", resolution.ToString(), StringComparison.Ordinal);

        // A 401 that came with a verified replacement keeps the replacement instead of marking the row as needing sign-in.
        var refusedResolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.SignInRequired, rotatedCredential: new("synthetic-share-rotation-2")) };
        await using var refusedService = new RobloxSessionLinkService(repository, vault, client, browser, shareLinks: refusedResolver);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await refusedService.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Equal(1, refusedResolver.Calls); Assert.Equal("synthetic-share-rotation", refusedResolver.SeenCookie);
        var replaced = vault.Records[profile.Id];
        Assert.Equal("synthetic-share-rotation-2", replaced.SessionCookie); Assert.Equal(3, replaced.Generation); Assert.Equal(AuthorizationHealth.Linked, replaced.Health);
    }

    [Fact]
    public async Task ShareLinkRotatedSessionThatFailsVerificationOrMeetsANewerRecordLeavesTheVaultUnchanged()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var saved = Saved(profile.Id); vault.Records[profile.Id] = saved;
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        foreach (var reply in new Func<int, CancellationToken, Task<RobloxSessionValidation>>[]
        {
            (_, _) => Task.FromResult(new RobloxSessionValidation(Identity(789))), // Another player.
            (_, _) => throw new RobloxSessionException(RobloxSessionFailure.InvalidSession, "synthetic refusal"),
            (_, _) => throw new RobloxSessionException(RobloxSessionFailure.Network, "synthetic network failure")
        })
        {
            var client = new Client { Reply = reply }; RobloxSessionCredential? rotation = null;
            var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: rotation = new("synthetic-share-rotation")) };
            await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser(), shareLinks: resolver);
            using var resolution = await service.ResolveShareLinkAsync(profile.Id, link, default);
            Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(5, resolution.PlaceId); // The answer stands.
            Assert.Equal(1, client.Calls); Assert.Throws<ObjectDisposedException>(() => rotation!.CopyValue());
            Assert.Same(saved, vault.Records[profile.Id]); Assert.Equal(0, vault.Saves);
        }

        // Another operation saved a newer sign-in while Roblox was answering: the replacement is not even checked.
        var newer = Saved(profile.Id) with { Generation = 2, SessionCookie = "synthetic-newer-sign-in" };
        var racing = new ShareLinks { Reply = () => { vault.Records[profile.Id] = newer; return new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: new("synthetic-share-rotation")); } };
        var unused = new Client();
        await using var racingService = new RobloxSessionLinkService(repository, vault, unused, new Browser(), shareLinks: racing);
        Assert.Equal(RobloxShareLinkOutcome.Experience, (await racingService.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Same(newer, vault.Records[profile.Id]); Assert.Equal(0, vault.Saves); Assert.Equal(0, unused.Calls);
    }

    [Fact]
    public async Task ShareLinkRotationKeepDefectIsSurfacedInsteadOfSwallowed()
    {
        // M-L1a-4: a programming error while keeping the replacement is not an expected outcome, so it must not pass for one.
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var saved = Saved(profile.Id); vault.Records[profile.Id] = saved;
        var client = new Client { Reply = (_, _) => throw new InvalidOperationException("synthetic defect") };
        RobloxSessionCredential? rotation = null; RobloxShareLinkResolution? answer = null;
        var resolver = new ShareLinks { Reply = () => answer = new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: rotation = new("synthetic-share-rotation")) };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveShareLinkAsync(profile.Id, link, default));
        Assert.Throws<ObjectDisposedException>(() => rotation!.CopyValue()); Assert.Null(answer!.TakeRotatedCredential());
        Assert.Same(saved, vault.Records[profile.Id]); Assert.Equal(0, vault.Saves);
        // The owned operation still ended, so the next resolve is not refused as busy.
        resolver.Reply = null;
        Assert.Equal(RobloxShareLinkOutcome.Experience, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
    }

    [Fact]
    public async Task ShareLinkRejectedSessionFenceDefectIsSurfacedInsteadOfSwallowed()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        vault.Records[profile.Id] = Saved(profile.Id);
        vault.BeforeSave = _ => throw new InvalidOperationException("synthetic defect");
        var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.SignInRequired) };
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver);
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveShareLinkAsync(profile.Id, link, default));
        Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task ShareLinkExpectedKeepAndFenceFailuresKeepTheAnswerAndReportOnlyTheirType()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var saved = Saved(profile.Id); vault.Records[profile.Id] = saved; var failures = new List<Type>();
        using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        foreach (var (reply, expected) in new (Func<int, CancellationToken, Task<RobloxSessionValidation>>, Type)[]
        {
            ((_, _) => throw new RobloxSessionException(RobloxSessionFailure.Network, "synthetic network failure"), typeof(RobloxSessionException)),
            ((_, _) => Task.FromResult(new RobloxSessionValidation(Identity(789))), typeof(AccountLinkException)) // Another player.
        })
        {
            failures.Clear();
            var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: new("synthetic-share-rotation")) };
            await using var service = new RobloxSessionLinkService(repository, vault, new Client { Reply = reply }, new Browser(), shareLinks: resolver, failed: failures.Add);
            Assert.Equal(RobloxShareLinkOutcome.Experience, (await service.ResolveShareLinkAsync(profile.Id, link, default)).Outcome); // The answer stands.
            Assert.Equal(new[] { expected }, failures); Assert.Same(saved, vault.Records[profile.Id]);
        }

        // A storage failure while marking the refused session keeps the SignInRequired answer and reports only its type.
        failures.Clear(); vault.FailSave = true;
        var refused = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.SignInRequired) };
        await using (var fencing = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: refused, failed: failures.Add))
            Assert.Equal(RobloxShareLinkOutcome.SignInRequired, (await fencing.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Equal(new[] { typeof(IOException) }, failures);

        // A kept replacement reports nothing.
        failures.Clear(); var clean = new Vault(); clean.Records[profile.Id] = Saved(profile.Id);
        var rotating = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: new("synthetic-share-rotation")) };
        await using (var keeping = new RobloxSessionLinkService(repository, clean, new Client(), new Browser(), shareLinks: rotating, failed: failures.Add))
            Assert.Equal(RobloxShareLinkOutcome.Experience, (await keeping.ResolveShareLinkAsync(profile.Id, link, default)).Outcome);
        Assert.Empty(failures); Assert.Equal("synthetic-share-rotation", clean.Records[profile.Id].SessionCookie);
    }

    [Fact]
    public async Task ShareLinkKeepWhoseCleanupCannotFenceItsWriteReportsTheCleanupFailureTypeToo()
    {
        // N-B1a-6: the replacement's commit reached the store and then failed, so the keep fences that write. When the fence
        // itself fails, that failure is reported by type as well, after the keep's own; the share-link answer still stands.
        using var repository = await Repository(); var profile = await Profile(repository); using var link = new RobloxShareLink(new ProtectedShareCode("abc123"), RobloxShareLinkType.Server);
        foreach (var (fenceFailure, expected) in new (Exception?, Type[])[]
        {
            (null, [typeof(IOException)]), // The fence ran: only the keep failure is reported.
            (new UnauthorizedAccessException("Synthetic fence denied"), [typeof(IOException), typeof(UnauthorizedAccessException)])
        })
        {
            var vault = new Vault { FailSave = true, FenceFailure = fenceFailure }; var saved = Saved(profile.Id); vault.Records[profile.Id] = saved;
            var failures = new List<Type>();
            var resolver = new ShareLinks { Reply = () => new(RobloxShareLinkOutcome.Experience, 5, 6, rotatedCredential: new("synthetic-share-rotation")) };
            await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), shareLinks: resolver, failed: failures.Add);
            using (var answer = await service.ResolveShareLinkAsync(profile.Id, link, default))
                Assert.Equal(RobloxShareLinkOutcome.Experience, answer.Outcome);
            Assert.Equal(expected, failures); Assert.Equal(1, vault.FenceAttempts); Assert.Same(saved, vault.Records[profile.Id]);
        }
    }

    private sealed class ShareLinks : IRobloxShareLinkClient
    {
        public int Calls; public string? SeenCookie; public RobloxShareLink? SeenLink; public RobloxShareLinkTokenScope? SeenScope; private RobloxSessionCredential? _credential;
        public readonly List<Guid> Forgotten = []; public int ForgotAll;
        public Func<RobloxShareLinkResolution>? Reply;
        public bool CredentialDisposedAfterCall { get { try { _credential?.CopyValue(); return false; } catch (ObjectDisposedException) { return true; } } }
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        { Calls++; SeenLink = link; SeenCookie = credential.CopyValue(); SeenScope = scope; _credential = credential; return Task.FromResult(Reply?.Invoke() ?? new RobloxShareLinkResolution(RobloxShareLinkOutcome.Experience, 5, 6)); }
        public void ForgetTokens(Guid profileId) => Forgotten.Add(profileId);
        public void ForgetAllTokens() => ForgotAll++;
    }

    [Fact]
    public async Task OldOAuthRecordIsPreservedUntilExplicitLocalDisconnectWithoutNetwork()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var old = new AuthorizationRecord(profile.Id, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), "old_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "456");
        vault.Records[profile.Id] = old; var client = new Client(); var browser = new Browser();
        await using var service = new RobloxSessionLinkService(repository, vault, client, browser);
        await Assert.ThrowsAsync<AccountLinkException>(() => service.LinkAsync(profile.Id, default));
        await Assert.ThrowsAsync<AccountLinkException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.Same(old, vault.Records[profile.Id]); Assert.Equal(0, browser.Calls); Assert.Equal(0, client.Calls); Assert.Equal(0, vault.Saves);
        await service.DisconnectAsync(profile.Id, default); Assert.Empty(vault.Records);
        Assert.Equal(0, client.Calls); Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task DifferentUserCannotReplaceExistingProfileBinding()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var old = Saved(profile.Id, 111); vault.Records[profile.Id] = old;
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser());
        await Assert.ThrowsAsync<AccountLinkException>(() => service.LinkAsync(profile.Id, default)); Assert.Same(old, vault.Records[profile.Id]);
    }

    [Fact]
    public async Task DuplicateRobloxUserAcrossProvidersCannotBeSavedOnAnotherProfile()
    {
        using var repository = await Repository(); var selected = await Profile(repository); var other = await repository.SaveAsync(AccountProfile.Create("Other"), default);
        var vault = new Vault(); vault.Records[other.Id] = new(other.Id, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1), Issuer: RobloxOAuthIdentity.Issuer, Subject: "opaque-subject");
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser());
        await Assert.ThrowsAsync<AccountLinkException>(() => service.LinkAsync(selected.Id, default)); Assert.Single(vault.Records); Assert.False(vault.Records.ContainsKey(selected.Id));
    }

    [Fact]
    public async Task RotatedSessionIsIndependentlyVerifiedBeforeItIsCommitted()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var client = new Client
        { Reply = (call, _) => Task.FromResult(new RobloxSessionValidation(Identity(), call == 1 ? new("synthetic-rotation") : null)) };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        await service.LinkAsync(profile.Id, default);
        Assert.Equal(new[] { Secret, "synthetic-rotation" }, client.Seen); Assert.Equal("synthetic-rotation", vault.Records[profile.Id].SessionCookie); Assert.Equal(1, vault.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongOrRepeatedRotationNeverCommitsAnUnverifiedCookie(bool wrongIdentity)
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); var client = new Client
        { Reply = (call, _) => Task.FromResult(call == 1 ? new RobloxSessionValidation(Identity(), new("synthetic-rotation"))
            : new RobloxSessionValidation(Identity(wrongIdentity ? 999 : 456), wrongIdentity ? null : new("synthetic-third"))) };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        await Assert.ThrowsAsync<AccountLinkException>(() => service.LinkAsync(profile.Id, default)); Assert.Empty(vault.Records); Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task SavedCheckFencesOldGenerationBeforeContactThenCommitsFreshVerifiedGeneration()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var client = new Client { Reply = (_, _) => { Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); return Task.FromResult(new RobloxSessionValidation(Identity())); } };
        var browser = new Browser(); await using var service = new RobloxSessionLinkService(repository, vault, client, browser);
        await service.CheckSavedAsync(profile.Id, default); Assert.Equal(3, vault.Records[profile.Id].Generation);
        Assert.Equal(AuthorizationHealth.Linked, vault.Records[profile.Id].Health); Assert.Equal(0, browser.Calls); Assert.Equal(2, vault.Reads);
    }

    [Theory]
    [InlineData(RobloxSessionFailure.InvalidSession)]
    [InlineData(RobloxSessionFailure.Challenge)]
    [InlineData(RobloxSessionFailure.Network)]
    [InlineData(RobloxSessionFailure.TimedOut)]
    public async Task FailedSavedCheckCannotSilentlyReuseAnObsoleteSession(RobloxSessionFailure failure)
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var client = new Client { Reply = (_, _) => throw new RobloxSessionException(failure, "Synthetic fixed failure") };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        await Assert.ThrowsAsync<RobloxSessionException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); Assert.Equal(Secret, vault.Records[profile.Id].SessionCookie);
        await Assert.ThrowsAsync<AccountLinkException>(() => service.CheckSavedAsync(profile.Id, default)); Assert.Equal(1, client.Calls);
    }

    // R-accounts-1: a check Roblox provably never processed restores the unchanged saved session.
    [Fact]
    public async Task SavedCheckThatRobloxNeverProcessedRestoresTheUnchangedSessionAtAFreshGeneration()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var client = new Client { Reply = (call, _) =>
        {
            Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); // Still fenced before every contact.
            return call == 1 ? throw new RobloxSessionException(RobloxSessionFailure.NotProcessed, "Synthetic refusal " + Secret)
                : Task.FromResult(new RobloxSessionValidation(Identity()));
        } };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.Equal(RobloxSessionFailure.NotProcessed, error.Failure);
        Assert.DoesNotContain(Secret, error.Message); Assert.DoesNotContain("Synthetic", error.Message); Assert.Contains("unchanged", error.Message);
        Assert.Equal(Saved(profile.Id) with { Generation = 3 }, vault.Records[profile.Id]);
        // The restored record is an ordinary saved sign-in: the next check fences, contacts Roblox and verifies it.
        await service.CheckSavedAsync(profile.Id, default);
        Assert.Equal(2, client.Calls); Assert.Equal(5, vault.Records[profile.Id].Generation); Assert.Equal(AuthorizationHealth.Linked, vault.Records[profile.Id].Health);
    }

    [Fact]
    public async Task ARefusedCheckOfARotatedReplacementNeverRestoresTheOldSession()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var client = new Client { Reply = (call, _) => call == 1 ? Task.FromResult(new RobloxSessionValidation(Identity(), new("synthetic-rotation")))
            : throw new RobloxSessionException(RobloxSessionFailure.NotProcessed, "Synthetic refusal") };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.NotEqual(RobloxSessionFailure.NotProcessed, error.Failure); Assert.Contains("needing sign-in", error.Message);
        Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health); Assert.Equal(2, vault.Records[profile.Id].Generation);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ARefusedCheckNeverOverwritesAnotherOperationsNewerRecord()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id);
        var client = new Client { Reply = (_, _) =>
        {
            vault.Records[profile.Id] = vault.Records[profile.Id] with { VerifiedUsername = "newer_player" };
            throw new RobloxSessionException(RobloxSessionFailure.NotProcessed, "Synthetic refusal");
        } };
        await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        // M-L1a-5: the refused restore still reports that Roblox never processed the check, so a batch stops on it
        // instead of reading a generic "changed" refusal as a sign-in answer and continuing into the outage.
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.Equal(RobloxSessionFailure.NotProcessed, error.Failure); Assert.Contains("newer sign-in was kept", error.Message);
        Assert.DoesNotContain("Synthetic", error.Message);
        Assert.Equal("newer_player", vault.Records[profile.Id].VerifiedUsername); Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health);
    }

    [Fact]
    public async Task ExpiredSavedSessionIsFencedWithoutAnyRemoteCall()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault(); vault.Records[profile.Id] = Saved(profile.Id) with { ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var client = new Client(); await using var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        var expired = await Assert.ThrowsAsync<AccountLinkException>(() => service.CheckSavedAsync(profile.Id, default)); Assert.Equal(0, client.Calls);
        Assert.False(expired.OperationInProgress); // A real sign-in answer, not a refusal by a busy service.
        Assert.Equal(AuthorizationHealth.SignInRequired, vault.Records[profile.Id].Health);
    }

    [Fact]
    public async Task CancelAndDisconnectAwaitDelayedBrowserAndCannotPublishItsLateCredential()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var release = new TaskCompletionSource<RobloxSessionCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser { Wait = _ => { entered.TrySetResult(); return release.Task; } };
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), browser);
        var linking = service.LinkAsync(profile.Id, default); await entered.Task;
        Assert.True((await Assert.ThrowsAsync<AccountLinkException>(() => service.LinkAsync(profile.Id, default))).OperationInProgress);
        // M-L1a-5: a check refused by the busy service says so, so a batch can stop instead of counting it as needing sign-in.
        Assert.True((await Assert.ThrowsAsync<AccountLinkException>(() => service.CheckSavedAsync(profile.Id, default))).OperationInProgress);
        var disconnecting = service.DisconnectAsync(profile.Id, default); Assert.False(disconnecting.IsCompleted);
        var late = new RobloxSessionCredential(Secret); release.SetResult(late);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => linking); await disconnecting;
        Assert.Empty(vault.Records); Assert.Throws<ObjectDisposedException>(() => late.CopyValue());
    }

    [Fact]
    public async Task DisconnectWaitsForAlreadyIssuedUncooperativeSaveThenDeletesIt()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vault.BeforeSave = _ => { entered.TrySetResult(); return release.Task; };
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser());
        var linking = service.LinkAsync(profile.Id, default); await entered.Task;
        var disconnecting = service.DisconnectAsync(profile.Id, default); Assert.False(disconnecting.IsCompleted);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => linking); await disconnecting;
        Assert.Empty(vault.Records); Assert.Equal(1, vault.Saves);
    }

    [Fact]
    public async Task DisposeOwnsDelayedValidationToCompletionAndRejectsFurtherOperations()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<RobloxSessionValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Reply = (_, _) => { entered.TrySetResult(); return release.Task; } };
        var service = new RobloxSessionLinkService(repository, vault, client, new Browser());
        var linking = service.LinkAsync(profile.Id, default); await entered.Task; var disposal = service.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        release.SetResult(new(Identity())); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => linking); await disposal;
        Assert.Empty(vault.Records); await Assert.ThrowsAsync<ObjectDisposedException>(() => service.LinkAsync(profile.Id, default)); await service.DisposeAsync();
    }

    [Fact]
    public async Task FailedSavePreservesNonsecretProjectionJournalForVerifiedRetry()
    {
        using var repository = await Repository(); var profile = await Profile(repository); var vault = new Vault { FailSave = true };
        using var projection = new AccountIdentityProjection(_root, repository, vault);
        await using var service = new RobloxSessionLinkService(repository, vault, new Client(), new Browser(), projection);
        await Assert.ThrowsAsync<IOException>(() => service.LinkAsync(profile.Id, default));
        var pending = Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "account-identity-pending"), "*.json"));
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(pending)); Assert.Empty(vault.Records);
        vault.FailSave = false; await service.LinkAsync(profile.Id, default); Assert.Equal(3, vault.Records[profile.Id].Generation);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "account-identity-pending"), "*.json"));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
