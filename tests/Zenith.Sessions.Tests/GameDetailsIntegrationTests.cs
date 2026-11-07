using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class DirectoryIntegrationTests
{
    [Fact]
    public Task CapacityAdviceTracksActualRecipientsAndUsesOnlyTheExplicitServerPin() => OnUi(async () =>
    {
        var servers = new CapacityServers();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), servers);
        await vm.InitializeAsync();
        await vm.SaveAccountAsync(AccountProfile.Create("First"));
        await vm.SaveAccountAsync(AccountProfile.Create("Second"));
        await vm.SaveAccountAsync(AccountProfile.Create("Third"));
        vm.SelectedAccount = vm.Accounts[0];
        vm.ServerBrowser.UsePreferredPlace(6564161046);
        vm.ServerBrowser.Browse.Execute(null); await vm.ServerBrowser.Browse.Execution;
        vm.ServerBrowser.SelectedServer = Assert.Single(vm.ServerBrowser.Servers);
        Assert.Contains("unknown", vm.RequestedCapacitySummary);
        vm.ServerBrowser.UseServer.Execute(null);
        Assert.Equal(1, vm.DestinationRecipientCount); Assert.Contains("2 open slots for 1 recipients", vm.RequestedCapacitySummary);
        foreach (var row in vm.Accounts) row.IsChecked = true;
        Assert.Equal(3, vm.DestinationRecipientCount); Assert.Contains("exceeds", vm.RequestedCapacitySummary);
        vm.QueueTargetedAccounts.Execute(null);
        vm.ServerBrowser.UsePreferredPlace(999);
        foreach (var row in vm.Accounts) row.IsChecked = false;
        await vm.QueueTargetedAccounts.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(3, vm.Launches.Count); Assert.All(vm.Launches, row =>
        {
            Assert.Equal(6564161046, row.Operation.Intent.PlaceId);
            Assert.Equal(CapacityServers.Job, row.Operation.Intent.RequestedJobId);
            Assert.Equal(LaunchProblem.ProviderUnavailable, row.Operation.Problem);
        });
        Assert.Contains("2 open slots for 3 recipients", vm.Notice); Assert.Contains("Place 6564161046", vm.Notice);
        Assert.Contains("unknown", vm.RequestedCapacitySummary); Assert.Equal(1, servers.Calls);
        vm.SelectedAccount = null; Assert.Equal(0, vm.DestinationRecipientCount);
        Assert.Contains("at least one recipient", vm.RequestedCapacitySummary);
        vm.LockDashboard(); Assert.Contains("locked", vm.RequestedCapacitySummary);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateGameLookupCannotPublishAfterLockOrPanicAndUnlockNeverReloadsAutomatically(bool panic) => OnUi(async () =>
    {
        var games = new PendingGames();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), new Servers(), gameDirectory: games);
        await vm.InitializeAsync(); await vm.SaveAccountAsync(AccountProfile.Create("Synthetic alias"));
        vm.ServerBrowser.UsePreferredPlace(6564161046); Assert.Equal(0, games.Calls);
        vm.GameDetails.Load.Execute(null); await games.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, games.Calls); Assert.True(vm.GameDetails.IsBusy);
        if (panic) vm.Panic.Execute(null); else vm.LockDashboard();
        Assert.True(games.Token.IsCancellationRequested);
        games.Pending.SetResult(new(PublicDirectoryState.Success, new(6564161046, 1, 2, "Synthetic game", 3, "Creator", PublicRobloxCreatorKind.User, 1, 20, DateTimeOffset.UtcNow), "Synthetic"));
        await vm.GameDetails.Load.Execution;
        Assert.Null(vm.GameDetails.Game); Assert.Null(vm.GameDetails.Icon); Assert.Equal(0, games.IconCalls);
        if (!panic) vm.Unlock.Execute(null);
        Assert.Equal(1, games.Calls); Assert.Null(vm.GameDetails.Game);
        Assert.Null(Assert.Single(vm.Accounts).KnownUserId); Assert.Equal("Synthetic alias", vm.Accounts[0].Label);
        Assert.Empty(vm.Launches);
    });

    private sealed class CapacityServers : IRobloxPublicServerDirectory
    {
        public const string Job = "11111111-2222-4333-8444-000000000001";
        public int Calls;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RobloxPublicServerResult(RobloxPublicServerStatus.Complete,
                [new(6564161046, Job, 18, 20, null, null, DateTimeOffset.UtcNow)], null, null, "Synthetic"));
        }
    }
    private sealed class PendingGames : IPublicRobloxGameDirectory
    {
        public int Calls, IconCalls;
        public CancellationToken Token;
        public TaskCompletionSource<PublicDirectoryResult<PublicRobloxGame>> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long place, CancellationToken ct)
        { Calls++; Token = ct; Started.TrySetResult(); return Pending.Task; }
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universe, CancellationToken ct)
        { IconCalls++; return Task.FromResult(new PublicDirectoryResult<PublicRobloxGameIcon>(PublicDirectoryState.Unknown, null, "Synthetic")); }
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon, CancellationToken ct) => throw new InvalidOperationException("No synthetic icon was supplied.");
        public void ClearMemoryCache() { }
    }
}
