using System.ComponentModel;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(77L)]
    public Task AccountJoinOpensWithOrWithoutPreferenceAndPreservesExactRequestDraftAndQueue(long? preferredPlace) => OnUi(async () =>
    {
        var directory = new SearchDirectory(new());
        var provider = new PreviewProvider { AutoReady = true };
        await using var shell = JoinShell(directory, provider);
        await shell.InitializeAsync();
        shell.SetPreviewWindowVisible(true);
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic join account") with { PreferredPlaceId = preferredPlace });
        var account = Assert.Single(shell.Accounts);
        shell.SelectedAccount = account;
        account.IsChecked = true;
        var client = shell.Sessions[0];
        shell.SelectedSession = client;
        client.IsChecked = true;
        const string job = "11111111-2222-3333-4444-555555555555";
        shell.ServerBrowser.LinkInput = $"https://www.roblox.com/games/start?placeId=42&gameInstanceId={job}";
        shell.ServerBrowser.ResolveInput();
        var destination = shell.ServerBrowser.Destination;
        shell.QueueTargetedAccounts.Execute(null);
        await shell.QueueTargetedAccounts.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var operation = Assert.Single(shell.Launches).Operation;
        Assert.Equal(LaunchState.Blocked, operation.State);
        Assert.Equal(LaunchProblem.ProviderUnavailable, operation.Problem);
        shell.ServerBrowser.LinkInput = "Unfinished game search";
        shell.Navigate.Execute("Accounts");
        string paused = shell.QueuePauseLabel;

        shell.OpenJoinGameForSelectedAccount.Execute(null);
        shell.OpenJoinGameForSelectedAccount.Execute(null);

        Assert.True(shell.IsJoinGame);
        Assert.True(shell.IsSessionWorkspace);
        Assert.Same(account, shell.SelectedAccount);
        Assert.Equal(preferredPlace, account.Profile.PreferredPlaceId);
        Assert.True(account.IsChecked);
        Assert.Same(client, shell.SelectedSession);
        Assert.True(client.IsChecked);
        Assert.Same(destination, shell.ServerBrowser.Destination);
        Assert.Equal(42, destination!.PlaceId);
        Assert.Equal(job, destination.JobId);
        Assert.Equal("Unfinished game search", shell.ServerBrowser.LinkInput);
        Assert.Equal(operation, Assert.Single(shell.Launches).Operation);
        Assert.Equal(paused, shell.QueuePauseLabel);
        Assert.Empty(directory.Queries);
        Assert.Equal(0, directory.GameRequests);
        Assert.Equal(0, directory.ProfileRequests);
        Assert.Equal(0, provider.CaptureCalls);
    });

    [Theory]
    [InlineData("startup")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("locked")]
    [InlineData("shutdown")]
    public Task AccountJoinDirectExecutionRejectsUnavailableOrNoncurrentSelection(string unavailable) => OnUi(async () =>
    {
        var directory = new SearchDirectory(new());
        var provider = new PreviewProvider { AutoReady = true };
        await using var shell = JoinShell(directory, provider);
        if (unavailable == "startup")
        {
            var pending = new AccountRow(AccountProfile.Create("Pending startup"));
            shell.Accounts.Add(pending);
            shell.SelectedAccount = pending;
        }
        else
        {
            await shell.InitializeAsync();
            await shell.SaveAccountAsync(AccountProfile.Create("Synthetic guarded account"));
            shell.SelectedAccount = Assert.Single(shell.Accounts);
        }
        shell.Navigate.Execute("Accounts");
        switch (unavailable)
        {
            case "missing":
                shell.SelectedAccount = null;
                break;
            case "stale":
                shell.SelectedAccount = new AccountRow(shell.SelectedAccount!.Profile);
                break;
            case "locked":
                shell.LockDashboard();
                break;
            case "shutdown":
                await shell.DisposeAsync();
                break;
        }
        string notice = shell.Notice;

        shell.OpenJoinGameForSelectedAccount.Execute(null);

        Assert.True(shell.IsAccounts);
        Assert.Equal(notice, shell.Notice);
        Assert.Empty(shell.Launches);
        Assert.Empty(directory.Queries);
        Assert.Equal(0, directory.GameRequests);
        Assert.Equal(0, directory.ProfileRequests);
        Assert.Equal(0, provider.CaptureCalls);
    });

    [Theory]
    [InlineData("redirect")]
    [InlineData("lock")]
    [InlineData("panic")]
    public Task AccountJoinDoesNotOverwriteReentrantNavigationOrStopFeedback(string change) => OnUi(async () =>
    {
        var directory = new SearchDirectory(new());
        var provider = new PreviewProvider { AutoReady = true };
        await using var shell = JoinShell(directory, provider);
        await shell.InitializeAsync();
        shell.SetPreviewWindowVisible(true);
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic reentrant account"));
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.Navigate.Execute("Accounts");
        bool observed = false;
        string? stoppedNotice = null;
        void RouteChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (observed || args.PropertyName != nameof(ShellViewModel.Tab) || !shell.IsJoinGame) return;
            observed = true;
            if (change == "redirect") shell.Navigate.Execute("Accounts");
            else
            {
                if (change == "lock") shell.LockDashboard();
                else shell.Panic.Execute(null);
                stoppedNotice = shell.Notice;
            }
        }
        shell.PropertyChanged += RouteChanged;
        try { shell.OpenJoinGameForSelectedAccount.Execute(null); }
        finally { shell.PropertyChanged -= RouteChanged; }

        Assert.True(observed);
        if (change == "redirect") Assert.True(shell.IsAccounts);
        else
        {
            Assert.True(shell.IsJoinGame);
            Assert.Equal(change == "lock", shell.IsLocked);
            Assert.Equal(stoppedNotice, shell.Notice);
            Assert.Contains(change == "lock" ? "locked" : "Panic", shell.Notice, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(shell.Launches);
        Assert.Empty(directory.Queries);
        Assert.Equal(0, directory.GameRequests);
        Assert.Equal(0, directory.ProfileRequests);
        Assert.Equal(0, provider.CaptureCalls);
    });
}
