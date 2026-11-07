using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class DirectoryIntegrationTests
{
    private async Task SeedPublicIdentityAsync()
    {
        using var profiles = new AccountRepository(_root); await profiles.InitializeAsync(default);
        var profile = await profiles.SaveAsync(AccountProfile.Create("Private alias"), default);
        using var vault = new ProtectedCredentialVault(_root);
        await vault.SaveAsync(new(profile.Id, RobloxOAuthIdentity.Provider, 123, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
            "trusted_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "123"), default);
    }

    [Fact]
    public Task AutomaticHeadshotsRemainOfflineByDefaultEvenForKnownSavedIdentities() => OnUi(async () =>
    {
        await SeedPublicIdentityAsync(); var directory = new PublicDirectory();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), directory, new Servers());
        vm.SetPreviewWindowVisible(true); await vm.InitializeAsync(); vm.Navigate.Execute("Accounts");
        await vm.PublicImageOperation; Assert.Equal(0, directory.Calls);
        vm.RefreshPublicAccount.Execute(Assert.Single(vm.Accounts)); await vm.RefreshPublicAccount.Execution;
        Assert.Equal(1, directory.Calls); Assert.Equal("Public display", vm.Accounts[0].PublicDisplayName);
    });

    [Fact]
    public Task AutomaticHeadshotsNeedVisibleAccountsAndKeepIdentityAndGlobalNoticeSeparate() => OnUi(async () =>
    {
        await SeedPublicIdentityAsync(); var directory = new PublicDirectory();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), directory, new Servers(), enableAutomaticPublicImages: true);
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts"); Assert.Equal(0, directory.Calls);
        string notice = vm.Notice; vm.SetPreviewWindowVisible(true); await vm.PublicImageOperation;
        var row = Assert.Single(vm.Accounts); Assert.Equal(1, directory.Calls); Assert.Equal("Public display", row.PublicDisplayName);
        Assert.Equal("trusted_name", row.Label); Assert.Equal("@trusted_name", row.Username); Assert.Equal(notice, vm.Notice);
        vm.Navigate.Execute("Overview"); vm.Navigate.Execute("Accounts"); await vm.PublicImageOperation;
        vm.Refresh.Execute(null); await vm.Refresh.Execution; await vm.PublicImageOperation;
        Assert.Equal(1, directory.Calls); Assert.Equal("Public display", Assert.Single(vm.Accounts).PublicDisplayName);
    });

    [Fact]
    public Task ImportedEditablePlayerIdNeverTriggersAutomaticHeadshots() => OnUi(async () =>
    {
        var directory = new PublicDirectory();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), directory, new Servers(), enableAutomaticPublicImages: true);
        await vm.InitializeAsync(); await vm.SaveAccountAsync(AccountProfile.Create("Entered ID") with { RobloxUserId = 123 });
        vm.Navigate.Execute("Accounts"); vm.SetPreviewWindowVisible(true); await vm.PublicImageOperation;
        Assert.Null(Assert.Single(vm.Accounts).KnownUserId); Assert.Equal(0, directory.Calls);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task AutomaticLateImagesCannotReappearAfterPanicOrLock(bool locked) => OnUi(async () =>
    {
        await SeedPublicIdentityAsync(); var directory = new PublicDirectory { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), directory, new Servers(), enableAutomaticPublicImages: true);
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts"); vm.SetPreviewWindowVisible(true);
        string notice;
        try { await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (locked) vm.LockDashboard(); else vm.Panic.Execute(null); notice = vm.Notice; }
        finally { directory.Pending.TrySetResult(directory.Profile); }
        await vm.PublicImageOperation;
        Assert.Null(vm.Accounts[0].Avatar); Assert.Equal("", vm.Accounts[0].PublicDisplayName); Assert.Equal(notice, vm.Notice);
        Assert.Equal("trusted_name", vm.Accounts[0].Label); Assert.Equal(1, directory.Calls);
    });

    [Fact]
    public Task ChangedCurrentGrantBlocksLatePageImagePublication() => OnUi(async () =>
    {
        await SeedPublicIdentityAsync(); var directory = new PublicDirectory { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), directory, new Servers(), enableAutomaticPublicImages: true);
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts"); vm.SetPreviewWindowVisible(true);
        var row = Assert.Single(vm.Accounts);
        try
        {
            await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            row.SetCachedAuthorization(new(row.Profile.Id, "fixture", RobloxOAuthIdentity.Provider, RobloxOAuthIdentity.Issuer, "123", 123,
                "trusted_name", 2, AuthorizationHealth.Linked, DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow));
        }
        finally { directory.Pending.TrySetResult(directory.Profile); }
        await vm.PublicImageOperation;
        Assert.Equal(2, row.AuthorizationGeneration); Assert.Equal("", row.PublicDisplayName); Assert.Null(row.Avatar);
    });
}
