using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Real isolated SQLite/DPAPI with controlled application waits; no window or sign-in is opened.</summary>
[Collection(WpfProductionResourceCollection.Name)]
public sealed partial class ShellGroupLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-group-lifecycle-ui-" + Guid.NewGuid().ToString("N"));
    private const string UpdatedEvent = "Account group metadata updated.";

    [Fact]
    public Task RenameUpdatesAllPagesAndFollowsTheActiveGroupWithoutExpandingTheUiPage() => OnUi(async () =>
    {
        var repository = await Storage();
        var profiles = Enumerable.Range(0, 154).Select(index => AccountProfile.Create($"Profile {index:D3}") with
        {
            Group = index < 135 ? "Source" : "Other",
            AdditionalGroups = index is >= 135 and < 150 ? "Keep\nSource" : "Keep",
            Notes = $"Keep note {index}", Priority = index % 101, PreferredPlaceId = 42
        }).ToArray();
        await repository.Inner.CreateManyAsync(profiles, default); var before = await ReadAll(repository.Inner);
        var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        await Filter(shell, "Source"); Assert.Equal(100, shell.Accounts.Count);
        int uiThread = Environment.CurrentManagedThreadId;
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename, "Source", "Renamed"), default);
        Assert.Equal(150, preview.AffectedCount); Assert.Equal(135, preview.PrimaryCount);
        Assert.Equal(150, await shell.ApplyGroupChangeAsync(preview, default));
        Assert.Equal("Renamed", shell.Group.Value); Assert.Equal(154, shell.AccountCount); Assert.Equal(100, shell.Accounts.Count);
        Assert.All(shell.Accounts, row => Assert.True(AccountGroups.Contains(row.Profile, "Renamed")));
        Assert.DoesNotContain(shell.Groups, group => group.Value == "Source"); Assert.Contains(shell.Groups, group => group.Value == "Renamed");
        Assert.NotEqual(uiThread, repository.PreviewThread); Assert.NotEqual(uiThread, repository.ApplyThread);
        var firstPage = shell.Accounts.Select(row => row.Profile.Id).ToHashSet();
        Assert.True(shell.CanNext); shell.NextPage.Execute(null); await shell.NextPage.Execution;
        Assert.Equal(50, shell.Accounts.Count); Assert.DoesNotContain(shell.Accounts, row => firstPage.Contains(row.Profile.Id));
        Assert.All(shell.Accounts, row => Assert.True(AccountGroups.Contains(row.Profile, "Renamed")));
        var after = (await ReadAll(repository.Inner)).ToDictionary(row => row.Id);
        foreach (var original in before)
        {
            var current = after[original.Id]; AssertMetadataPreserved(original, current);
            if (AccountGroups.Contains(original, "Source")) { Assert.False(AccountGroups.Contains(current, "Source")); Assert.True(AccountGroups.Contains(current, "Renamed")); Assert.Equal(original.Revision + 1, current.Revision); }
            else Assert.Equal(original, current);
        }
        Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Fact]
    public Task PendingRenameDoesNotOverrideANewerUnrelatedFilterOrSelection() => OnUi(async () =>
    {
        var repository = await Storage();
        await repository.Inner.CreateManyAsync([AccountProfile.Create("Affected") with { Group = "Source" }, AccountProfile.Create("Unrelated") with { Group = "Other" }], default);
        var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        await Filter(shell, "Source");
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename, "Source", "Renamed"), default);
        repository.HoldApply = true; Task<int> apply = shell.ApplyGroupChangeAsync(preview, default); Guid selected = Guid.Empty;
        try
        {
            await repository.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Filter(shell, "Other"); shell.SelectedAccount = Assert.Single(shell.Accounts); selected = shell.SelectedAccount.Profile.Id;
        }
        finally { repository.ReleaseApply(); await apply.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal("Other", shell.Group.Value); Assert.Equal(selected, shell.SelectedAccount?.Profile.Id);
        Assert.Equal("Unrelated", Assert.Single(shell.Accounts).Alias);
        Assert.Equal(1, (await repository.Inner.ReadAsync(new(Group: "Renamed"), default)).Total);
        Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Fact]
    public Task SelectionChangedDuringCommittedReloadRemainsTheLatestVisibleUnrelatedProfile() => OnUi(async () =>
    {
        var repository = await Storage();
        await repository.Inner.CreateManyAsync([AccountProfile.Create("Affected") with { Group = "Source" }, AccountProfile.Create("Unrelated") with { Group = "Other" }], default);
        var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        shell.SelectedAccount = shell.Accounts.Single(row => row.Alias == "Affected");
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename, "Source", "Renamed"), default);
        repository.HoldCommittedRead = true; Task<int> apply = shell.ApplyGroupChangeAsync(preview, default); Guid selected = Guid.Empty;
        try
        {
            await repository.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            shell.SelectedAccount = shell.Accounts.Single(row => row.Alias == "Unrelated"); selected = shell.SelectedAccount.Profile.Id;
        }
        finally { repository.ReleaseRead(); await apply.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(selected, shell.SelectedAccount?.Profile.Id); Assert.Null(shell.Group.Value);
        Assert.Equal("Renamed", shell.Accounts.Single(row => row.Alias == "Affected").Profile.Group);
        Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Fact]
    public Task RemoveOnlyChangesMembershipsAndPreservesTheActualProtectedGrantAndOtherMetadata() => OnUi(async () =>
    {
        var repository = await Storage();
        await repository.Inner.CreateManyAsync([
            AccountProfile.Create("Saved identity alias") with { Group = "Source", AdditionalGroups = "Keep\nOther", Username = "fixture_player", RobloxUserId = 900000123, Notes = "Keep saved notes", Priority = 53, IsFavorite = true, PreferredPlaceId = 42 },
            AccountProfile.Create("Extra membership") with { Group = "Other", AdditionalGroups = "Before\nSource\nAfter", Notes = "Keep other notes" },
            AccountProfile.Create("Unaffected") with { Group = "Keep", AdditionalGroups = "Other" }], default);
        var before = await ReadAll(repository.Inner); var linked = before.Single(row => row.Label == "Saved identity alias");
        using var vault = new ProtectedCredentialVault(_root);
        long generation = await vault.AllocateNextGenerationAsync(linked.Id, 0, default);
        var grant = new AuthorizationRecord(linked.Id, RobloxSessionIdentity.Provider, 900000123, generation, "", "", DateTimeOffset.UtcNow.AddHours(1),
            "fixture_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "900000123", "synthetic-group-lifecycle-owned-session");
        await vault.SaveAsync(grant, default); var summary = await vault.GetSummaryAsync(linked.Id, default);
        string protectedPath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "accounts.vault"), "*.dpapi"));
        byte[] protectedHash = SHA256.HashData(await File.ReadAllBytesAsync(protectedPath));
        var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        await Filter(shell, "Source");
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove, "Source", "General"), default);
        Assert.Equal(2, preview.AffectedCount); Assert.Equal(2, await shell.ApplyGroupChangeAsync(preview, default));
        Assert.Null(shell.Group.Value); Assert.Equal(3, shell.Accounts.Count);
        var after = (await ReadAll(repository.Inner)).ToDictionary(row => row.Id);
        foreach (var original in before)
        {
            var current = after[original.Id]; AssertMetadataPreserved(original, current); Assert.False(AccountGroups.Contains(current, "Source"));
            if (!AccountGroups.Contains(original, "Source")) Assert.Equal(original, current);
        }
        Assert.Equal("General", after[linked.Id].Group); Assert.Equal("Keep\nOther", after[linked.Id].AdditionalGroups);
        Assert.Equal("Before\nAfter", after[before.Single(row => row.Label == "Extra membership").Id].AdditionalGroups);
        Assert.Equal(grant, await vault.ReadAsync(linked.Id, default)); Assert.Equal(summary, await vault.GetSummaryAsync(linked.Id, default));
        Assert.Equal(protectedHash, SHA256.HashData(await File.ReadAllBytesAsync(protectedPath)));
        Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
        Assert.DoesNotContain("synthetic-group-lifecycle-owned-session", shell.Notice);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockOrPanicCancelsAnOwnedPreapplyWaitWithoutMutationOrLateSuccess(bool panic) => OnUi(async () =>
    {
        var repository = await Storage(); await repository.Inner.CreateManyAsync([AccountProfile.Create("Unchanged") with { Group = "Source", Notes = "Keep" }], default);
        var before = Assert.Single(await ReadAll(repository.Inner)); var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename, "Source", "Renamed"), default);
        repository.HoldApply = true; var apply = shell.ApplyGroupChangeAsync(preview, default); string? stopNotice = null;
        int priorSuccess = SuccessEvents(shell);
        try
        {
            await repository.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); stopNotice = shell.Notice;
            Assert.True(repository.ApplyToken.IsCancellationRequested); Assert.False(apply.IsCompleted);
            Assert.Equal(before, await repository.Inner.FindAsync(before.Id, default));
        }
        finally { repository.ReleaseApply(); await ObserveStopped(apply); }
        Assert.Equal(before, await repository.Inner.FindAsync(before.Id, default)); Assert.Equal(stopNotice, shell.Notice);
        Assert.Equal(priorSuccess, SuccessEvents(shell)); Assert.Equal(0, repository.Commits); Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockOrPanicDuringCommittedReloadPreservesTheCommitAndStopNotice(bool panic) => OnUi(async () =>
    {
        var repository = await Storage(); await repository.Inner.CreateManyAsync([AccountProfile.Create("Saved membership") with { Group = "Source", Notes = "Preserve" }], default);
        var before = Assert.Single(await ReadAll(repository.Inner)); var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        var preview = await shell.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename, "Source", "Renamed"), default);
        repository.HoldCommittedRead = true; var apply = shell.ApplyGroupChangeAsync(preview, default); string? stopNotice = null;
        int priorSuccess = SuccessEvents(shell);
        try
        {
            await repository.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("Renamed", (await repository.Inner.FindAsync(before.Id, default))!.Group); Assert.False(apply.IsCompleted);
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); stopNotice = shell.Notice;
        }
        finally { repository.ReleaseRead(); await ObserveStopped(apply); }
        var retained = (await repository.Inner.FindAsync(before.Id, default))!;
        Assert.Equal("Renamed", retained.Group); Assert.Equal(before.Revision + 1, retained.Revision); AssertMetadataPreserved(before, retained);
        Assert.Equal(stopNotice, shell.Notice); Assert.Equal(priorSuccess, SuccessEvents(shell)); Assert.Equal(before.Id, shell.SelectedAccount?.Profile.Id);
        Assert.Equal(1, repository.Commits); Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Fact]
    public Task CallerCancellationRemovesAQueuedPreviewWithoutWaitingForOrCancellingTheActiveOwner() => OnUi(async () =>
    {
        var repository = await Storage(); await repository.Inner.CreateManyAsync([AccountProfile.Create("Source member") with { Group = "Source" }], default);
        var auth = new ForbiddenAuthorization(); await using var shell = await Ready(repository, auth);
        var change = new AccountGroupChange(AccountGroupChangeKind.Rename, "Source", "Renamed");
        var preview = await shell.PreviewGroupChangeAsync(change, default); repository.HoldApply = true;
        var apply = shell.ApplyGroupChangeAsync(preview, default);
        using var caller = new CancellationTokenSource(); Task<AccountGroupChangePreview>? queued = null;
        try
        {
            await repository.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); int previews = repository.PreviewCalls;
            queued = shell.PreviewGroupChangeAsync(change, caller.Token); Assert.False(queued.IsCompleted); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(apply.IsCompleted); Assert.False(repository.ApplyToken.IsCancellationRequested); Assert.Equal(previews, repository.PreviewCalls);
        }
        finally { caller.Cancel(); repository.ReleaseApply(); await apply.WaitAsync(TimeSpan.FromSeconds(5)); if (queued is not null) await ObserveStopped(queued); }
        Assert.Equal(1, repository.Commits); Assert.Empty(shell.Launches); Assert.Equal(0, auth.Calls);
    });

    [Fact]
    public Task DisposeCancelsQueuedCallsAndJoinsTheActualActiveOwnerBeforeDisposingStorage() => OnUi(async () =>
    {
        var repository = await Storage(); await repository.Inner.CreateManyAsync([AccountProfile.Create("Keep on shutdown") with { Group = "Source" }], default);
        var before = Assert.Single(await ReadAll(repository.Inner)); var auth = new ForbiddenAuthorization(); var shell = await Ready(repository, auth);
        Task? disposing = null; Task apply = Task.CompletedTask; Task queued = Task.CompletedTask;
        try
        {
            var change = new AccountGroupChange(AccountGroupChangeKind.Rename, "Source", "Renamed");
            var preview = await shell.PreviewGroupChangeAsync(change, default); repository.HoldApply = true; apply = shell.ApplyGroupChangeAsync(preview, default);
            await repository.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); queued = shell.PreviewGroupChangeAsync(change, default);
            disposing = shell.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(repository.ApplyToken.IsCancellationRequested); Assert.False(apply.IsCompleted); Assert.False(disposing.IsCompleted); Assert.False(repository.Disposed);
        }
        finally
        {
            repository.ReleaseApply(); await ObserveStopped(apply); await ObserveStopped(queued);
            await (disposing ?? shell.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(repository.Disposed); Assert.Equal(0, repository.Commits); Assert.Equal(0, auth.Calls);
        using var reopened = new AccountRepository(_root); await reopened.InitializeAsync(default); Assert.Equal(before, await reopened.FindAsync(before.Id, default));
    });

    private async Task<LifecycleRepository> Storage()
    { var repository = new LifecycleRepository(_root); await repository.Inner.InitializeAsync(default); return repository; }
    private async Task<ShellViewModel> Ready(LifecycleRepository repository, ForbiddenAuthorization auth)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: repository, signIn: auth, sessionClient: auth);
        try { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); return shell; }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static async Task Filter(ShellViewModel shell, string group)
    {
        shell.Group = shell.Groups.Single(item => string.Equals(item.Value, group, StringComparison.OrdinalIgnoreCase));
        shell.Refresh.Execute(null); await shell.Refresh.Execution;
    }
    private static int SuccessEvents(ShellViewModel shell) => shell.Events.Count(item => item.Contains(UpdatedEvent, StringComparison.Ordinal));
    private static void AssertMetadataPreserved(AccountProfile before, AccountProfile after)
        => Assert.Equal(before, after with { Group = before.Group, AdditionalGroups = before.AdditionalGroups, Revision = before.Revision, UpdatedUtc = before.UpdatedUtc });
    private static async Task<IReadOnlyList<AccountProfile>> ReadAll(IAccountRepository repository)
    {
        var result = new List<AccountProfile>();
        for (int page = 0; page < 10; page++)
        {
            var batch = await repository.ReadAsync(new(Page: page), default); result.AddRange(batch.Items);
            if (result.Count >= batch.Total) return result;
        }
        throw new InvalidOperationException("Synthetic fixture unexpectedly exceeded its bounded read.");
    }
    private static async Task ObserveStopped(Task operation)
    { try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }

    private sealed class LifecycleRepository(string root) : IAccountRepository, IAccountGroupLifecycle, IDisposable
    {
        public AccountRepository Inner { get; } = new(root);
        public bool HoldApply, HoldCommittedRead, Disposed;
        public int PreviewThread, ApplyThread, PreviewCalls, Commits;
        public CancellationToken ApplyToken;
        public readonly TaskCompletionSource ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _applyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _committed, _readUsed;
        public void ReleaseApply() => _applyRelease.TrySetResult();
        public void ReleaseRead() => _readRelease.TrySetResult();
        public Task<AccountGroupChangePreview> PreviewGroupChangeAsync(AccountGroupChange change, CancellationToken ct)
        { PreviewThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref PreviewCalls); return Inner.PreviewGroupChangeAsync(change, ct); }
        public async Task<int> ApplyGroupChangeAsync(AccountGroupChangePreview expected, CancellationToken ct)
        {
            ApplyThread = Environment.CurrentManagedThreadId; ApplyToken = ct; ApplyEntered.TrySetResult();
            if (HoldApply) await _applyRelease.Task.ConfigureAwait(false); // Actual owner remains joined even if it cannot cancel this fixture wait.
            ct.ThrowIfCancellationRequested();
            int changed = await Inner.ApplyGroupChangeAsync(expected, ct).ConfigureAwait(false);
            Interlocked.Increment(ref Commits); Volatile.Write(ref _committed, 1); return changed;
        }
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            var result = await Inner.ReadAsync(query, ct).ConfigureAwait(false);
            if (HoldCommittedRead && Volatile.Read(ref _committed) != 0 && Interlocked.CompareExchange(ref _readUsed, 1, 0) == 0)
            { ReadEntered.TrySetResult(); await _readRelease.Task.ConfigureAwait(false); }
            return result;
        }
        public Task InitializeAsync(CancellationToken ct) => Inner.InitializeAsync(ct);
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => Inner.FindAsync(id, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => Inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) => Inner.SaveAsync(profile, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => Inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => Inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => Inner.CompleteCredentialCleanupAsync(id, ct);
        public void Dispose() { if (Disposed) return; Disposed = true; Inner.Dispose(); }
    }
    private sealed class ForbiddenAuthorization : IRobloxSessionSignIn, IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Group metadata must not open sign-in."); }
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Group metadata must not validate credentials."); }
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-group-lifecycle-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected group fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
