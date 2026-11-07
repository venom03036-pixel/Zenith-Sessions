using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class AccountQueryPublicationTests : IDisposable
{
    private readonly string _name = "zenith-query-publication-tests-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateMainLookupCannotOverwriteNewerSelectionPagingCountsOrGroups(bool renameMain) => OnDispatcherAsync(async () =>
    {
        // All data is synthetic and local. The delegate gates repository I/O, not dispatcher timing.
        using var storage = new AccountRepository(Root);
        await storage.InitializeAsync(default);
        var main = await storage.SaveAsync(AccountProfile.Create("Main fixture") with { Group = "Main group" }, default);
        var previous = await storage.SaveAsync(AccountProfile.Create("Previous selection") with { Group = "Old group" }, default);
        using (var settings = new SettingsStore(Root))
            await settings.SaveAsync(new(Performance: new(ManagerPerformanceProfile.MainAccountPriority, main.Id)), default);
        var accounts = new MainLookupGate(storage, main.Id);
        await using var vm = new ShellViewModel(Root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), accounts: accounts);
        await vm.InitializeAsync();
        Assert.True(vm.IsReady);
        Assert.Equal(2, vm.Accounts.Count);
        vm.SelectedAccount = vm.Accounts.Single(row => row.Profile.Id == previous.Id);
        accounts.Arm(); // Initialization's main lookup must complete normally before this gate exists.

        vm.Refresh.Execute(null);
        Task olderRefresh = vm.Refresh.Execution;
        var newest = (renameMain?main with {Label="Renamed main fixture"}:AccountProfile.Create("Newer selected fixture")) with { Group = "New group" };
        int expectedCount=renameMain?2:3;
        string? newestPageLabel = null;
        string?[] newestGroups = [];
        try
        {
            await accounts.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(olderRefresh.IsCompleted);
            // A different owned command can publish a newer query while the old Refresh awaits I/O.
            await vm.SaveAccountAsync(newest).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(newest.Id, vm.SelectedAccount?.Profile.Id);
            Assert.Equal(expectedCount, vm.AccountCount);
            Assert.Equal(expectedCount, vm.Accounts.Count);
            Assert.Equal($"1–{expectedCount} of {expectedCount} profiles", vm.PageLabel);
            Assert.Equal(renameMain?newest.Label:main.Label,vm.MainAccountDisplay);
            Assert.Contains(vm.Groups, group => group.Value == "New group");
            newestPageLabel = vm.PageLabel;
            newestGroups = vm.Groups.Select(group => group.Value).ToArray();
        }
        finally
        {
            // Also release/observe the blocked task if an assertion fails, before VM shutdown.
            accounts.Release();
            await olderRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, accounts.GatedLookups);
        Assert.Equal(newest.Id, vm.SelectedAccount?.Profile.Id);
        Assert.Equal(expectedCount, vm.AccountCount);
        Assert.Equal(expectedCount, vm.Accounts.Count);
        Assert.Equal(renameMain?newest.Label:main.Label,vm.MainAccountDisplay);
        Assert.Equal(newestPageLabel, vm.PageLabel);
        Assert.Equal(newestGroups, vm.Groups.Select(group => group.Value).ToArray());
        Assert.Contains(vm.Groups, group => group.Value == "New group");
    }).WaitAsync(TimeSpan.FromSeconds(20));

    private sealed class MainLookupGate(IAccountRepository inner, Guid mainId) : IAccountRepository
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed, _gatedLookups;
        public Task Entered => _entered.Task;
        public int GatedLookups => Volatile.Read(ref _gatedLookups);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public void Release() => _release.TrySetResult(true);
        public async Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
        {
            AccountProfile? result = await inner.FindAsync(id, ct).ConfigureAwait(false);
            if (id == mainId && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Interlocked.Increment(ref _gatedLookups);
                _entered.TrySetResult(true);
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return result;
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

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB,
                20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), []));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run();
            operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
