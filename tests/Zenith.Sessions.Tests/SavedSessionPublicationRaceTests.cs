using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class SavedSessionPublicationRaceTests : IDisposable
{
    private readonly string _name = "zenith-saved-session-publication-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private const string Cookie = "synthetic-post-commit-owned-session";
    private const string SuccessEvent = "Roblox sign-in identity verified; selected account session stored with Windows protection.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LockOrPanicDuringCommittedSignInReloadCannotPublishLateSuccess(bool checkSaved, bool panic) => OnDispatcherAsync(async () =>
    {
        // Everything is a synthetic local fixture. The real vault proves this barrier is after
        // durable authorization commit, rather than merely after the fake sign-in returns.
        using var storage = new AccountRepository(Root);
        await storage.InitializeAsync(default);
        var profile = await storage.SaveAsync(AccountProfile.Create("Kept local alias") with { Notes = "Kept local notes" }, default);
        using var vault = new ProtectedCredentialVault(Root);
        var accounts = new CommittedReloadGate(storage, vault, profile.Id);
        var signIn = new SyntheticSignIn(); var client = new SyntheticClient();
        await using var shell = new ShellViewModel(Root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(),
            accounts: accounts, signIn: signIn, sessionClient: client);
        await shell.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(shell.IsReady, shell.Notice);
        shell.SelectedAccount = Assert.Single(shell.Accounts);

        if (checkSaved)
        {
            shell.LinkAccount.Execute(null);
            await shell.LinkAccount.Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Saved sign-in verified", shell.Notice);
        }
        long previousGeneration = (await vault.GetSummaryAsync(profile.Id, default))?.Generation ?? 0;
        int priorSuccessEvents = shell.Events.Count(item => item.Contains(SuccessEvent, StringComparison.Ordinal));
        shell.RefreshDiagnostics.Execute(null);
        int priorLinkedDiagnostics = shell.DiagnosticRows.Count(item => item.Event.Kind == ManagerEventKind.AuthorizationLinked);
        var command = checkSaved ? shell.RefreshAuthorization : shell.LinkAccount;
        string? stopNotice = null;
        accounts.Arm(previousGeneration);
        command.Execute(null);
        Task execution = command.Execution;
        try
        {
            var committed = await accounts.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(profile.Id, committed.ProfileId);
            Assert.Equal(RobloxSessionIdentity.Provider, committed.Provider);
            Assert.Equal(AuthorizationHealth.Linked, committed.Health);
            Assert.True(committed.Generation > previousGeneration);
            Assert.True(shell.IsAuthorizing);
            Assert.False(execution.IsCompleted);
            var protectedRecord = (await vault.ReadAsync(profile.Id, default))!;
            Assert.Equal(committed.Generation, protectedRecord.Generation);
            Assert.Equal(Cookie, protectedRecord.SessionCookie);
            Assert.Equal(456, protectedRecord.VerifiedUserId);
            Assert.NotNull(signIn.Returned);
            Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue());

            if (panic) shell.Panic.Execute(null); else shell.Lock.Execute(null);
            stopNotice = shell.Notice;
            Assert.Contains(panic ? "Panic stop" : "Controls locked", stopNotice);
            Assert.Equal(!panic, shell.IsLocked);
            // Lock/Panic cancels authorization, not this already-started library read. Its late
            // continuation is the exact boundary that must recheck the authorization fence.
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            accounts.Release();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, accounts.GatedReads);
        Assert.False(shell.IsAuthorizing);
        Assert.Equal(stopNotice, shell.Notice);
        Assert.Equal(priorSuccessEvents, shell.Events.Count(item => item.Contains(SuccessEvent, StringComparison.Ordinal)));
        Assert.DoesNotContain(Cookie, shell.Notice);
        Assert.DoesNotContain(shell.Events, item => item.Contains(Cookie, StringComparison.Ordinal));
        var row = Assert.Single(shell.Accounts);
        Assert.Equal(profile.Id, row.Profile.Id);
        Assert.Equal(456, row.KnownUserId);
        Assert.Equal("Kept local alias", row.Alias);
        Assert.Equal("Kept local notes", row.Profile.Notes);
        Assert.Equal(profile.Id, shell.SelectedAccount?.Profile.Id);
        Assert.Equal(1, signIn.Calls);
        Assert.Equal(checkSaved ? 2 : 1, client.Calls);

        // The commit preceded the stop: cancellation must fence presentation, not erase the saved
        // profile or falsely pretend that the accepted durable write never happened.
        var retained = (await vault.ReadAsync(profile.Id, default))!;
        Assert.Equal(AuthorizationHealth.Linked, retained.Health);
        Assert.Equal(Cookie, retained.SessionCookie);

        if (shell.IsLocked) shell.Unlock.Execute(null);
        shell.RefreshDiagnostics.Execute(null);
        Assert.Equal(priorLinkedDiagnostics, shell.DiagnosticRows.Count(item => item.Event.Kind == ManagerEventKind.AuthorizationLinked));
        Assert.Equal(1, signIn.Calls); // Unlock does not silently restart the cancelled sign-in.
        Assert.Equal(checkSaved ? 2 : 1, client.Calls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateNetworkFailureAfterLockOrPanicCannotReplaceTheStopNotice(bool panic) => OnDispatcherAsync(async () =>
    {
        using var storage = new AccountRepository(Root);
        await storage.InitializeAsync(default);
        var profile = await storage.SaveAsync(AccountProfile.Create("Unchanged local alias") with { Notes = "Unchanged local notes" }, default);
        using var vault = new ProtectedCredentialVault(Root);
        var accounts = new CommittedReloadGate(storage, vault, profile.Id);
        var signIn = new SyntheticSignIn(); var client = new GatedFailingClient();
        await using var shell = new ShellViewModel(Root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(),
            accounts: accounts, signIn: signIn, sessionClient: client);
        await shell.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(shell.IsReady, shell.Notice);
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        string? stopNotice = null;
        shell.LinkAccount.Execute(null);
        Task execution = shell.LinkAccount.Execution;
        try
        {
            await client.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(shell.IsAuthorizing);
            Assert.False(execution.IsCompleted);
            Assert.Null(await vault.GetSummaryAsync(profile.Id, default));
            Assert.Null(await vault.ReadAsync(profile.Id, default));
            if (panic) shell.Panic.Execute(null); else shell.Lock.Execute(null);
            stopNotice = shell.Notice;
            Assert.Contains(panic ? "Panic stop" : "Controls locked", stopNotice);
            Assert.False(execution.IsCompleted); // The deliberately uncooperative owner is still joined.
        }
        finally
        {
            client.Release();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(client.CancellationObservedAtFailure);
        Assert.False(shell.IsAuthorizing);
        Assert.Equal(!panic, shell.IsLocked);
        Assert.Equal(stopNotice, shell.Notice);
        Assert.DoesNotContain(shell.Events, item => item.Contains(SuccessEvent, StringComparison.Ordinal));
        Assert.DoesNotContain(Cookie, shell.Notice);
        Assert.NotNull(signIn.Returned);
        Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue());
        Assert.Equal(1, signIn.Calls); Assert.Equal(1, client.Calls);
        Assert.Null(await vault.GetSummaryAsync(profile.Id, default));
        Assert.Null(await vault.ReadAsync(profile.Id, default));
        var row = Assert.Single(shell.Accounts);
        Assert.Equal(profile.Id, row.Profile.Id);
        Assert.Null(row.KnownUserId);
        Assert.Equal("Unchanged local alias", row.Profile.Label);
        Assert.Equal("Unchanged local notes", row.Profile.Notes);
        Assert.Equal(profile.Id, shell.SelectedAccount?.Profile.Id);
        if (shell.IsLocked) shell.Unlock.Execute(null);
        shell.RefreshDiagnostics.Execute(null);
        Assert.DoesNotContain(shell.DiagnosticRows, item => item.Event.Kind == ManagerEventKind.AuthorizationLinked);
        Assert.Equal(1, signIn.Calls); Assert.Equal(1, client.Calls);
    });

    private sealed class CommittedReloadGate(IAccountRepository inner, IAuthorizationMetadataReader metadata, Guid profileId) : IAccountRepository
    {
        private readonly TaskCompletionSource<AuthorizationMetadata> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _minimumGeneration;
        private int _armed, _gatedReads;
        public Task<AuthorizationMetadata> Entered => _entered.Task;
        public int GatedReads => Volatile.Read(ref _gatedReads);
        public void Arm(long minimumGeneration) { Interlocked.Exchange(ref _minimumGeneration, minimumGeneration); Volatile.Write(ref _armed, 1); }
        public void Release() => _release.TrySetResult();
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            AccountPage page = await inner.ReadAsync(query, ct).ConfigureAwait(false);
            if (Volatile.Read(ref _armed) != 0)
            {
                var committed = await metadata.GetSummaryAsync(profileId, ct).ConfigureAwait(false);
                if (committed is { Provider: RobloxSessionIdentity.Provider, Health: AuthorizationHealth.Linked }
                    && committed.Generation > Interlocked.Read(ref _minimumGeneration)
                    && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
                {
                    Interlocked.Increment(ref _gatedReads);
                    _entered.TrySetResult(committed);
                    await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            return page;
        }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) => inner.SaveAsync(profile, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }

    private sealed class SyntheticSignIn : IRobloxSessionSignIn
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public RobloxSessionCredential? Returned { get; private set; }
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls);
            return Task.FromResult(Returned = new RobloxSessionCredential(Cookie));
        }
    }

    private sealed class SyntheticClient : IRobloxSessionClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls);
            Assert.Equal(Cookie, credential.CopyValue());
            return Task.FromResult(new RobloxSessionValidation(new(456, "verified_player", RobloxSessionIdentity.Issuer, "456")));
        }
    }

    private sealed class GatedFailingClient : IRobloxSessionClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls, _cancelledAtFailure;
        public Task Entered => _entered.Task;
        public int Calls => Volatile.Read(ref _calls);
        public bool CancellationObservedAtFailure => Volatile.Read(ref _cancelledAtFailure) != 0;
        public void Release() => _release.TrySetResult();
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls);
            Assert.Equal(Cookie, credential.CopyValue());
            _entered.TrySetResult();
            // Model a native/network owner that ignores cancellation until it actually completes,
            // then faults instead of returning a successful result through the normal Ensure fence.
            await _release.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested) Volatile.Write(ref _cancelledAtFailure, 1);
            throw new RobloxSessionException(RobloxSessionFailure.Network, "The synthetic session validation network request failed.");
        }
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB,
                20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), []));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected synthetic fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
