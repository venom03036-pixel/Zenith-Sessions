using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    // The real repository is confined to this fixture's owned temporary root. Reflection only
    // observes the already-owned debounce task/token so gates can distinguish an obsolete
    // scheduled query from explicit Refresh; it neither invokes private code nor sleeps.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OlderDebouncedGroupIntentCannotSupersedeAnExplicitRefreshOfChangedMembershipCase(bool extraMembership) => OnUi(async () =>
    {
        var repository = new QueryIntentRepository(_root);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: repository);
        await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic case-changing membership") with
        { Group = extraMembership ? "Primary" : "Raid", AdditionalGroups = extraMembership ? "Raid" : "" });
        var original = shell.SelectedAccount!.Profile;
        // Change storage independently, leaving the old display spelling visible until Refresh.
        await repository.Inner.SaveAsync(original with
        { Group = extraMembership ? "Primary" : "raid", AdditionalGroups = extraMembership ? "raid" : "" }, default);
        shell.Group = shell.Groups.Single(group => group.Value == "Raid");
        var scheduled = ReadAccountQueryField<Task>(shell, "_queryTask");
        var scheduledCancellation = ReadAccountQueryField<CancellationTokenSource>(shell, "_queryCancellation");
        repository.GateGroup = "Raid"; repository.ScheduledToken = scheduledCancellation.Token;
        shell.Refresh.Execute(null); var refresh = shell.Refresh.Execution;
        try
        {
            await repository.ExplicitReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            // Correct code retires the old scheduled owner before I/O. Before the fix, that
            // same owner enters the second repository gate and invalidates explicit Refresh.
            await Task.WhenAny(scheduled, repository.ObsoleteReadEntered.Task).WaitAsync(TimeSpan.FromSeconds(4));
            repository.ReleaseExplicitRead.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal("raid", shell.Group.Value); Assert.NotSame(GroupFilter.All, shell.Group);
            Assert.Equal(original.Id, Assert.Single(shell.Accounts).Profile.Id);
            Assert.DoesNotContain(shell.Groups, group => group.Value == "Raid");
            Assert.Equal(0, repository.ObsoleteReads);
            Assert.Empty(shell.Launches);
        }
        finally
        {
            repository.ReleaseExplicitRead.TrySetResult(); repository.ReleaseObsoleteRead.TrySetResult();
            await scheduled.WaitAsync(TimeSpan.FromSeconds(4)); await refresh.WaitAsync(TimeSpan.FromSeconds(4));
        }
    });

    [Fact]
    public Task SelectionChangedDuringTheFinalMainAccountLookupRemainsThePublishedSelection() => OnUi(async () =>
    {
        var repository = new QueryIntentRepository(_root);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: repository);
        await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("First synthetic profile")); var first = shell.SelectedAccount!.Profile.Id;
        await shell.SaveAccountAsync(AccountProfile.Create("Second synthetic profile")); var second = shell.SelectedAccount!.Profile.Id;
        shell.SelectedAccount = shell.Accounts.Single(row => row.Profile.Id == first); shell.SetSelectedAsMain.Execute(null);
        repository.GatedMain = first;
        shell.Refresh.Execute(null); var refresh = shell.Refresh.Execution;
        try
        {
            await repository.MainLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            shell.SelectedAccount = shell.Accounts.Single(row => row.Profile.Id == second);
            repository.ReleaseMainLookup.TrySetResult(); await refresh.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(second, shell.SelectedAccount!.Profile.Id);
            Assert.Equal(first, shell.MainAccountProfileId); Assert.Equal(2, shell.Accounts.Count);
            Assert.Equal("Second synthetic profile", shell.SelectedAccount.Label);
            Assert.Empty(shell.Launches);
        }
        finally { repository.ReleaseMainLookup.TrySetResult(); await refresh.WaitAsync(TimeSpan.FromSeconds(4)); }
    });

    private static T ReadAccountQueryField<T>(ShellViewModel shell, string field) where T : class
    {
        var info = typeof(ShellViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info); return Assert.IsAssignableFrom<T>(info!.GetValue(shell));
    }
    private sealed class QueryIntentRepository(string root) : IAccountRepository, IDisposable
    {
        public AccountRepository Inner { get; } = new(root);
        public string? GateGroup; public CancellationToken ScheduledToken; public Guid? GatedMain;
        private int _obsoleteReads;
        public int ObsoleteReads => Volatile.Read(ref _obsoleteReads);
        public TaskCompletionSource ExplicitReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObsoleteReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExplicitRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseObsoleteRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MainLookupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMainLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken ct) => Inner.InitializeAsync(ct);
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            if (GateGroup is { } group && string.Equals(query.Group, group, StringComparison.Ordinal))
            {
                if (ct == ScheduledToken)
                {
                    Interlocked.Increment(ref _obsoleteReads); ObsoleteReadEntered.TrySetResult();
                    await ReleaseObsoleteRead.Task;
                }
                else { ExplicitReadEntered.TrySetResult(); await ReleaseExplicitRead.Task; }
            }
            return await Inner.ReadAsync(query, ct);
        }
        public async Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
        {
            if (GatedMain == id) { MainLookupEntered.TrySetResult(); await ReleaseMainLookup.Task; }
            return await Inner.FindAsync(id, ct);
        }
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => Inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) => Inner.SaveAsync(profile, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => Inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => Inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => Inner.CompleteCredentialCleanupAsync(id, ct);
        public void Dispose() => Inner.Dispose();
    }
}
