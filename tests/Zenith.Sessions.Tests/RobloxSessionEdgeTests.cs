using System.Net;
using System.Net.Http;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RobloxSessionEdgeTests
{
    private const string Secret = "synthetic-edge-session";
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Transport(Func<HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response()); }

    [Fact]
    public async Task IntegerMaxAgeAtRoundedBoundaryReturnsFixedInvalidResponseInsteadOfOverflow()
    {
        const long age = 10_000_000_000;
        var now = DateTimeOffset.MaxValue.AddSeconds(-age).AddTicks(1);
        using var client = new RobloxSessionClient(new Transport(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":456,\"name\":\"verified_player\"}", Encoding.UTF8, "application/json") };
            response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com; Max-Age=10000000000");
            return response;
        }), timeProvider: new Clock(now));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(RobloxSessionFailure.InvalidResponse, error.Failure); Assert.DoesNotContain(Secret, error.ToString());
    }

    [Theory]
    [InlineData(RobloxSessionFailure.Network)]
    [InlineData(RobloxSessionFailure.TimedOut)]
    public async Task FailedSavedCheckExplainsThatItsDurableFenceRequiresSignInAgain(RobloxSessionFailure failure)
    {
        var profile = AccountProfile.Create("Synthetic saved profile") with { Revision = 1 };
        var vault = new Vault(new(profile.Id, RobloxSessionIdentity.Provider, 456, 1, "", "", DateTimeOffset.MaxValue,
            "verified_player", Issuer: RobloxSessionIdentity.Issuer, Subject: "456", SessionCookie: Secret));
        await using var service = new RobloxSessionLinkService(new Profiles(profile), vault, new FailingClient(failure), new NoBrowser());
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => service.CheckSavedAsync(profile.Id, default));
        Assert.Equal(AuthorizationHealth.SignInRequired, vault.Record.Health); Assert.Equal(Secret, vault.Record.SessionCookie);
        Assert.Equal(failure, error.Failure); Assert.Contains("sign in again", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Null(error.InnerException);
    }

    private sealed class FailingClient(RobloxSessionFailure failure) : IRobloxSessionClient
    {
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct) =>
            throw new RobloxSessionException(failure, failure == RobloxSessionFailure.Network
                ? "Roblox could not be reached. The previous saved session was preserved; its current validity is unknown."
                : "The Roblox session check timed out. Its current validity is unknown.");
    }
    private sealed class NoBrowser : IRobloxSessionSignIn
    { public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct) => throw new InvalidOperationException("Saved checks must not open sign-in implicitly."); }
    private sealed class Vault(AuthorizationRecord record) : IAccountCredentialVault, IAuthorizationGenerationStore,ISnapshotAuthorizationVault
    {
        public AuthorizationRecord Record = record;
        public Task<AuthorizationRecord?> ReadAsync(Guid profileId, CancellationToken ct) => Task.FromResult<AuthorizationRecord?>(Record);
        public Task SaveAsync(AuthorizationRecord value, CancellationToken ct) { Record = value; return Task.CompletedTask; }
        public Task DeleteAsync(Guid profileId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> AllocateNextGenerationAsync(Guid profileId, long minimumExclusive, CancellationToken ct) => Task.FromResult(minimumExclusive + 1);
        private Guid _owner;
        public Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid profileId,CancellationToken ct)=>Task.FromResult(new AuthorizationWriteSnapshot(profileId,Record,Record.Generation,Record.Generation,_owner,new string('A',64)));
        public Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)
        {Assert.Equal(Record,expected.Record);Assert.Equal(Record.Generation+1,replacement.Generation);Record=replacement;_owner=mutationId;return Task.FromResult(new AuthorizationMutationResult(AuthorizationMutationStatus.Applied,replacement.Generation));}
        public Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected,CancellationToken ct)=>throw new NotSupportedException();
        public Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<bool> TryFenceOwnedMutationAsync(Guid profileId,Guid mutationId,CancellationToken ct)=>throw new NotSupportedException();
    }
    private sealed class Profiles(AccountProfile profile) : IAccountRepository
    {
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<AccountProfile?>(id == profile.Id ? profile : null);
        public Task InitializeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => throw new NotSupportedException();
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
