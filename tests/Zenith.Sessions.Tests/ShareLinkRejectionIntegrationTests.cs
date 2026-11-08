using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Z27 / M-L1a-6: a route 401 receives one owned identity check before deciding the saved sign-in's health.
public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ShareLinkSignInAnswerMarksTheAccountRowAndProtectedRecordAsNeedingSignInOnlyWhenIdentityAlsoRefuses(bool identityRefuses) => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new RefusingShareLinks(); var directory = new SearchDirectory(new());
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), publicDirectory: directory,
            serverDirectory: new SearchServers(), gameDirectory: directory, signIn: signIn, sessionClient: client, shareLinkClient: shareLinks);
        await shell.InitializeAsync();
        for (int i = 1; i <= 2; i++)
        {
            await shell.SaveAccountAsync(AccountProfile.Create($"Health {i:00}"));
            shell.SelectedAccount = HealthRow(shell, i); signIn.Next = HealthCookie(i);
            shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        }
        client.Clear();
        if (identityRefuses) client.Replies[HealthCookie(1)] = HealthReply.Invalid;
        Assert.Equal("Saved link", HealthRow(shell, 1).Identity); Assert.Equal("Saved link", HealthRow(shell, 2).Identity);
        shell.SelectedAccount = HealthRow(shell, 1); var profile = HealthRow(shell, 1).Profile.Id;
        shell.Navigate.Execute("JoinGame"); // The private-server panel lives on Join game.

        shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
        await shell.ShareLink.CheckAsync();

        Assert.Equal(1, shareLinks.Calls); Assert.Equal(HealthCookie(1), shareLinks.SeenCookie);
        Assert.Equal(identityRefuses ? "Sign in with the selected account to check share links." : "The share link could not be checked right now. Try again later.", shell.ShareLink.Status);
        Assert.Equal(identityRefuses ? "Sign in required" : "Saved link", HealthRow(shell, 1).Identity); Assert.Equal("Saved link", HealthRow(shell, 2).Identity);
        Assert.Equal(profile, shell.SelectedAccount?.Profile.Id); // The reload keeps the selection.
        using (var vault = new ProtectedCredentialVault(_root))
        {
            Assert.Equal(identityRefuses ? AuthorizationHealth.SignInRequired : AuthorizationHealth.Linked, (await vault.GetSummaryAsync(profile, default))!.Health);
            Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(HealthRow(shell, 2).Profile.Id, default))!.Health);
        }
        Assert.Equal(HealthCookie(1), Assert.Single(client.Snapshot()).Cookie); HealthNoSecrets(shell); Assert.DoesNotContain(HealthCookiePrefix, shell.ShareLink.Status);
    });

    private sealed class RefusingShareLinks : IRobloxShareLinkClient
    {
        public int Calls; public string? SeenCookie;
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; SeenCookie = credential.CopyValue(); return Task.FromResult(new RobloxShareLinkResolution(RobloxShareLinkOutcome.SignInRequired)); }
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
    }
}
