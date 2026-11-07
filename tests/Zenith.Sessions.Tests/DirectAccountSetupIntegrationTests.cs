using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private const string DirectCookie = "synthetic-direct-account-owned-sign-in";
    private static ShellViewModel DirectShell(string root, DirectSignIn signIn, DirectClient client, IAccountRepository? accounts = null)
        => new(root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: accounts, signIn: signIn, sessionClient: client);

    [Fact]
    public Task AddAccountNeedsNoAliasOrExistingProfileAndPublishesOnlyTheVerifiedResult() => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient { Hold = true };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.True(shell.CanAddRobloxAccount);
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(shell.IsAuthorizing); Assert.False(shell.CanAddRobloxAccount);
            Assert.Empty(shell.Accounts); Assert.NotEqual(Guid.Empty, signIn.ProfileId);
            using var before = new AccountRepository(_root); Assert.Equal(0, (await before.ReadAsync(new(), default)).Total);
            Assert.Equal(DirectCookie, signIn.Returned!.CopyValue());
        }
        finally { client.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        var added = Assert.Single(shell.Accounts); Assert.Equal(signIn.ProfileId, added.Profile.Id); Assert.Same(added, shell.SelectedAccount);
        Assert.Equal("direct_player", added.Profile.Label); Assert.Equal("direct_player", added.Profile.Username); Assert.Equal(456, added.Profile.RobloxUserId);
        Assert.Equal(456, added.KnownUserId); Assert.True(added.AuthorizationGeneration > 0); Assert.Equal("General", added.Profile.Group);
        Assert.Null(added.Profile.PreferredPlaceId); Assert.False(added.Profile.IsFavorite); Assert.Equal("", added.Profile.AdditionalGroups);
        using var vault = new ProtectedCredentialVault(_root); var summary = (await vault.GetSummaryAsync(added.Profile.Id, default))!;
        Assert.Equal(RobloxSessionIdentity.Provider, summary.Provider); Assert.Equal(AuthorizationHealth.Linked, summary.Health);
        Assert.Equal(456, summary.VerifiedUserId); Assert.Equal("direct_player", summary.VerifiedUsername);
        Assert.Equal(DirectCookie, (await vault.ReadAsync(added.Profile.Id, default))!.SessionCookie);
        Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); Assert.False(shell.IsAuthorizing);
        Assert.Contains("direct_player", shell.Notice); DirectNoSecrets(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task SelectingAnotherExistingProfileCannotRedirectThePendingAddOrItsFinalSelection() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing A") with { Notes = "Keep A", Group = "Family", AdditionalGroups = "Raid", PreferredPlaceId = 42 });
        await shell.SaveAccountAsync(AccountProfile.Create("Existing B") with { Notes = "Keep B", Priority = 37 });
        var originals = shell.Accounts.Select(row => row.Profile).ToArray(); var first = shell.Accounts.First(row => row.Alias == "Existing A");
        var second = shell.Accounts.First(row => row.Alias == "Existing B"); shell.SelectedAccount = first;
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.DoesNotContain(signIn.ProfileId, originals.Select(row => row.Id));
            shell.SelectedAccount = second; Assert.True(shell.IsAuthorizing);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(second.Profile.Id, shell.SelectedAccount?.Profile.Id); Assert.Equal(3, shell.Accounts.Count);
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        foreach (var original in originals) { Assert.Equal(original, await repository.FindAsync(original.Id, default)); Assert.Null(await vault.GetSummaryAsync(original.Id, default)); }
        Assert.Equal(456, (await vault.GetSummaryAsync(signIn.ProfileId, default))!.VerifiedUserId);
        Assert.Equal("direct_player", (await repository.FindAsync(signIn.ProfileId, default))!.Username); DirectNoSecrets(shell);
    });

    [Fact]
    public Task AddingAnAlreadySavedAccountRefreshesItsSignInKeepsItsDetailsAndSelectsItsRow() => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        var first = Assert.Single(shell.Accounts).Profile;
        await shell.SaveAccountAsync(first with { Label = "Main alt", Notes = "Keep these notes", Group = "Family", AdditionalGroups = "Raid" });
        await shell.SaveAccountAsync(AccountProfile.Create("Other row"));
        var original = shell.Accounts.First(row => row.Profile.Id == first.Id).Profile; Assert.Equal("Main alt", original.Label);
        shell.SelectedAccount = shell.Accounts.First(row => row.Alias == "Other row");
        using var vault = new ProtectedCredentialVault(_root); var before = (await vault.ReadAsync(original.Id, default))!;
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.NotEqual(original.Id, signIn.ProfileId); Assert.Equal(2, shell.Accounts.Count);
        var row = shell.Accounts.First(item => item.Profile.Id == original.Id); Assert.Equal(original, row.Profile); Assert.Same(row, shell.SelectedAccount);
        var after = (await vault.ReadAsync(original.Id, default))!;
        Assert.Equal(before with { Generation = before.Generation + 1 }, after); Assert.Equal(after.Generation, row.AuthorizationGeneration);
        Assert.Contains("Refreshed", shell.Notice); Assert.Contains("@direct_player", shell.Notice); Assert.DoesNotContain("Added", shell.Notice);
        using var repository = new AccountRepository(_root); Assert.Equal(2, (await repository.ReadAsync(new(), default)).Total);
        Assert.Null(await repository.FindAsync(signIn.ProfileId, default));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "accounts.vault"), "*.dpapi")); DirectNoSecrets(shell);
    });

    [Theory]
    [InlineData("invalid-identity")]
    [InlineData("expired")]
    public Task InvalidOrExpiredSignInCannotCreateAnAdvertisedSavedAccount(string failure) => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Expired = failure == "expired" }; var client = new DirectClient { InvalidIdentity = failure == "invalid-identity" };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.False(shell.IsAuthorizing);
        using var repository = new AccountRepository(_root); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
        Assert.False(Directory.Exists(Path.Combine(_root, "accounts.vault")) && Directory.GetFiles(Path.Combine(_root, "accounts.vault"), "*.dpapi").Length > 0);
        Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ClosingSignInIsNormalCancellationEvenWhenTheShellTokenWasNotCancelled() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { DialogCancelled = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.False(shell.IsAuthorizing); Assert.Equal(0, client.Calls);
        Assert.Contains("cancel", shell.Notice, StringComparison.OrdinalIgnoreCase); Assert.False(signIn.Token.IsCancellationRequested); DirectNoSecrets(shell);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LockOrPanicJoinsLateValidationAndCannotPublishANewAccountOrLateFailure(bool panic, bool failLate) => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient { Hold = true, FailLate = failLate };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Keep existing selection")); var original = Assert.Single(shell.Accounts).Profile;
        shell.SelectedAccount = shell.Accounts[0]; shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution; string? notice = null;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); notice = shell.Notice;
            Assert.True(client.Token.IsCancellationRequested); Assert.False(execution.IsCompleted); Assert.True(shell.IsAuthorizing);
        }
        finally { client.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(notice, shell.Notice); Assert.Equal(original, Assert.Single(shell.Accounts).Profile); Assert.Equal(original.Id, shell.SelectedAccount?.Profile.Id);
        Assert.False(shell.IsAuthorizing); Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue());
        using var repository = new AccountRepository(_root); Assert.Equal(1, (await repository.ReadAsync(new(), default)).Total);
        using var vault = new ProtectedCredentialVault(_root); Assert.Null(await vault.GetSummaryAsync(signIn.ProfileId, default));
        DirectNoSecrets(shell); Assert.Empty(shell.Launches);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StopDuringPostcommitReloadPreservesSavedDataButNeverSelectsOrAnnouncesTheLateAdd(bool panic) => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var existing = await repository.SaveAsync(AccountProfile.Create("Existing selected profile"), default);
        using var vault = new ProtectedCredentialVault(_root); var gate = new DirectReloadGate(repository, vault);
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client, gate); await shell.InitializeAsync(); shell.SelectedAccount = Assert.Single(shell.Accounts);
        gate.Armed = true; shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution; string? notice = null;
        try
        {
            var summary = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(signIn.ProfileId, summary.ProfileId);
            Assert.Equal(AuthorizationHealth.Linked, summary.Health); Assert.False(execution.IsCompleted);
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); notice = shell.Notice;
        }
        finally { gate.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(notice, shell.Notice); Assert.Equal(existing.Id, shell.SelectedAccount?.Profile.Id); Assert.False(shell.IsAuthorizing);
        Assert.NotNull(await repository.FindAsync(signIn.ProfileId, default)); Assert.Equal(456, (await vault.GetSummaryAsync(signIn.ProfileId, default))!.VerifiedUserId);
        Assert.Equal(existing, await repository.FindAsync(existing.Id, default)); Assert.DoesNotContain("added", shell.Notice, StringComparison.OrdinalIgnoreCase);
        DirectNoSecrets(shell);
    });

    [Fact]
    public Task AddOwnsTheSharedAuthorizationLaneAgainstLinkCheckAndRepeatedAddCommands() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing")); shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); string notice = shell.Notice;
            shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution; shell.RefreshAuthorization.Execute(null); await shell.RefreshAuthorization.Execution;
            shell.AddRobloxAccount.Execute(null); Assert.Same(execution, shell.AddRobloxAccount.Execution);
            Assert.Equal(1, signIn.Calls); Assert.Equal(0, client.Calls); Assert.True(shell.IsAuthorizing); Assert.False(shell.CanAddRobloxAccount); Assert.Equal(notice, shell.Notice);
            shell.CancelAuthorization.Execute(null); Assert.True(signIn.Token.IsCancellationRequested);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(shell.Accounts); Assert.False(shell.IsAuthorizing); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ExistingLinkOwnershipBlocksDirectAddWithoutReplacingItsCancellationOwner() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing")); shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.LinkAccount.Execute(null); Task execution = shell.LinkAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(shell.CanAddRobloxAccount);
            shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution; Assert.Equal(1, signIn.Calls);
            shell.CancelAuthorization.Execute(null); Assert.True(signIn.Token.IsCancellationRequested); Assert.False(execution.IsCompleted);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(shell.Accounts); Assert.Null(shell.Accounts[0].KnownUserId); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ShutdownWaitsForTheActualUncooperativeAddOwnerBeforeDisposingItsResources() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient(); var shell = DirectShell(_root, signIn, client);
        Task? disposing = null; Task execution = Task.CompletedTask;
        try
        {
            await shell.InitializeAsync(); shell.AddRobloxAccount.Execute(null); execution = shell.AddRobloxAccount.Execution;
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); disposing = shell.DisposeAsync().AsTask();
            Assert.True(signIn.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted); Assert.False(execution.IsCompleted);
            Assert.Equal(0, signIn.Disposals); Assert.False(shell.CanAddRobloxAccount);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); await (disposing ?? shell.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, signIn.Disposals); Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); Assert.Equal(0, client.Calls);
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockedOrFailedStartupCannotOpenDirectAccountSignIn(bool startupFailure) => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var gate = new DirectReloadGate(repository, vault) { FailInitialization = startupFailure };
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client, gate); await shell.InitializeAsync();
        if (!startupFailure) shell.LockDashboard(); else Assert.False(shell.IsReady);
        string notice = shell.Notice; Assert.False(shell.CanAddRobloxAccount); shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Equal(0, signIn.Calls); Assert.Equal(0, client.Calls); Assert.Equal(notice, shell.Notice); Assert.Empty(shell.Accounts);
    });

    private static void DirectNoSecrets(ShellViewModel shell)
    { Assert.DoesNotContain(DirectCookie, shell.Notice); Assert.DoesNotContain(shell.Events, item => item.Contains(DirectCookie, StringComparison.Ordinal)); }
    private sealed class DirectSignIn : IRobloxSessionSignIn, IAsyncDisposable
    {
        public bool Hold, Expired, DialogCancelled; public int Calls, Disposals; public Guid ProfileId; public CancellationToken Token;
        public RobloxSessionCredential? Returned;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; ProfileId = id; Token = ct; Entered.TrySetResult();
            if (Hold) await _release.Task.ConfigureAwait(false); // intentionally ignore cancel until actual owner completes
            if (DialogCancelled) throw new OperationCanceledException("Synthetic user closed sign-in.");
            return Returned = new(DirectCookie, Expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : null);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class DirectClient : IRobloxSessionClient
    {
        public bool Hold, InvalidIdentity, FailLate; public int Calls; public CancellationToken Token;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.Equal(DirectCookie, credential.CopyValue()); Calls++; Token = ct; Entered.TrySetResult();
            if (Hold) await _release.Task.ConfigureAwait(false);
            if (FailLate) throw new RobloxSessionException(RobloxSessionFailure.Network, DirectCookie);
            return new(new(InvalidIdentity ? 0 : 456, "direct_player", RobloxSessionIdentity.Issuer, "456"));
        }
    }
    private sealed class DirectReloadGate(IAccountRepository inner, IAuthorizationMetadataReader metadata) : IAccountRepository
    {
        public bool Armed, FailInitialization;
        public readonly TaskCompletionSource<AuthorizationMetadata> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        public void Release() => _release.TrySetResult();
        public Task InitializeAsync(CancellationToken ct) => FailInitialization ? Task.FromException(new InvalidDataException("Synthetic startup failure.")) : inner.InitializeAsync(ct);
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            var page = await inner.ReadAsync(query, ct).ConfigureAwait(false);
            if (Armed && Volatile.Read(ref _used) == 0)
                foreach (var row in page.Items)
                    if (row.RobloxUserId == 456 && await metadata.GetSummaryAsync(row.Id, ct).ConfigureAwait(false) is { Health: AuthorizationHealth.Linked } summary
                        && Interlocked.Exchange(ref _used, 1) == 0)
                    { Entered.TrySetResult(summary); await _release.Task.ConfigureAwait(false); break; }
            return page;
        }
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct) => inner.SaveAsync(account, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }
}
