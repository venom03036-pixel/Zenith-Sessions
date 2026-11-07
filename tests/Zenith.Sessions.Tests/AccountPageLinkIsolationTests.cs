using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class DirectoryIntegrationTests
{
    [Fact]
    public Task OneInconsistentVaultRecordMarksOnlyItsOwnRowUnavailable() => OnUi(async () =>
    {
        Guid good, broken;
        using (var profiles = new AccountRepository(_root))
        {
            await profiles.InitializeAsync(default);
            good = (await profiles.SaveAsync(AccountProfile.Create("Good alias"), default)).Id;
            broken = (await profiles.SaveAsync(AccountProfile.Create("Broken alias"), default)).Id;
        }
        using (var vault = new ProtectedCredentialVault(_root))
        {
            await vault.SaveAsync(new(good, RobloxOAuthIdentity.Provider, 123, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
                "good_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "123"), default);
            await vault.SaveAsync(new(broken, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
                "broken_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "456"), default);
        }
        // External damage (partial restore, sync, corruption) to one profile's identity pointer only.
        await File.WriteAllBytesAsync(Path.Combine(_root, "accounts.vault", broken.ToString("N") + ".dpapi.identity"), "{"u8.ToArray());

        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), new Servers());
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts");

        Assert.Equal(2, vm.Accounts.Count);
        var goodRow = vm.Accounts.Single(row => row.Profile.Id == good);
        var brokenRow = vm.Accounts.Single(row => row.Profile.Id == broken);
        Assert.Equal("Saved link", goodRow.Identity);
        Assert.Equal(123, goodRow.KnownUserId);
        Assert.Equal(1, goodRow.AuthorizationGeneration);
        Assert.Equal("Link index unavailable", brokenRow.Identity);
        Assert.Null(brokenRow.KnownUserId);
        Assert.Null(brokenRow.AuthorizationGeneration);
    });

    private FileStream HoldVaultWriterLock() =>
        new(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public Task BusyVaultKeepsSavedLinkInsteadOfAskingForSignInAndRetriesOnce() => OnUi(async () =>
    {
        await SeedPublicIdentityAsync();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), new Servers());
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts");
        var before = Assert.Single(vm.Accounts);
        Assert.Equal("Saved link", before.Identity); Assert.Equal(123, before.KnownUserId);

        AccountRow busy;
        using (HoldVaultWriterLock())
        {
            // The vault reports busy after its 2-second lease deadline while another owner holds the writer lock.
            vm.Refresh.Execute(null); await vm.Refresh.Execution;
            busy = Assert.Single(vm.Accounts);
            Assert.NotEqual("Link index unavailable", busy.Identity);
            Assert.DoesNotContain("Sign in again", busy.LinkedAccount);
            Assert.Equal(123, busy.KnownUserId);
            Assert.Equal(1, busy.AuthorizationGeneration);
            Assert.NotEqual("Sign in", busy.PowerLabel);
        }

        // One page-preserving retry runs after the store frees up, with no user action.
        await vm.AccountQuery;
        var retried = Assert.Single(vm.Accounts);
        Assert.NotSame(busy, retried);
        Assert.Equal("Saved link", retried.Identity);
        Assert.Equal(123, retried.KnownUserId);
        Assert.Equal(1, retried.AuthorizationGeneration);
    });

    [Fact]
    public Task BusyVaultRowWithoutAnEarlierLinkOffersRefreshInsteadOfSignIn() => OnUi(async () =>
    {
        Guid alpha, beta;
        using (var profiles = new AccountRepository(_root))
        {
            await profiles.InitializeAsync(default);
            alpha = (await profiles.SaveAsync(AccountProfile.Create("Alpha alias"), default)).Id;
            beta = (await profiles.SaveAsync(AccountProfile.Create("Beta alias"), default)).Id;
        }
        using (var vault = new ProtectedCredentialVault(_root))
        {
            await vault.SaveAsync(new(alpha, RobloxOAuthIdentity.Provider, 123, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
                "alpha_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "123"), default);
            await vault.SaveAsync(new(beta, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
                "beta_name", Issuer: RobloxOAuthIdentity.Issuer, Subject: "456"), default);
        }
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), new Servers());
        await vm.InitializeAsync(); vm.Navigate.Execute("Accounts");
        vm.Search = "Alpha"; await vm.AccountQuery;
        Assert.Equal(alpha, Assert.Single(vm.Accounts).Profile.Id);

        using (HoldVaultWriterLock())
        {
            vm.Search = ""; await vm.AccountQuery;
            Assert.Equal(2, vm.Accounts.Count);
            var alphaRow = vm.Accounts.Single(row => row.Profile.Id == alpha);
            var betaRow = vm.Accounts.Single(row => row.Profile.Id == beta);
            Assert.Equal("Saved link", alphaRow.Identity); Assert.Equal(123, alphaRow.KnownUserId);
            // Beta was not on the page before, so there is no last-known link to keep.
            Assert.Equal("Sign-in status busy", betaRow.Identity);
            Assert.DoesNotContain("Sign in again", betaRow.LinkedAccount);
            Assert.Null(betaRow.KnownUserId);
            Assert.Equal("Refresh", betaRow.PowerLabel);
            Assert.DoesNotContain("Connect this saved profile", betaRow.PowerExplanation);

            // Its main button reloads the page; it never opens an interactive sign-in.
            vm.AccountPower.Execute(betaRow); await vm.AccountPower.Execution;
            Assert.False(vm.IsAuthorizing);
            Assert.Contains("still busy", vm.Notice);
            Assert.Equal("Sign-in status busy", vm.Accounts.Single(row => row.Profile.Id == beta).Identity);
        }

        await vm.AccountQuery;
        var retried = vm.Accounts.Single(row => row.Profile.Id == beta);
        Assert.Equal("Saved link", retried.Identity);
        Assert.Equal(456, retried.KnownUserId);
        Assert.Equal("Saved link", vm.Accounts.Single(row => row.Profile.Id == alpha).Identity);
    });
}
