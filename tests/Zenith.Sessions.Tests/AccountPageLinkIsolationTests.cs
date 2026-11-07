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
}
