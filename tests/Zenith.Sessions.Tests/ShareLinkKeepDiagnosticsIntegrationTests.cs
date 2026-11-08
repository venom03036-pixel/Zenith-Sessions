using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// N-B1a-6: the shell wires the saved-session service's absorbed-failure callback to Diagnostics. Roblox sent a replacement
// session with a share-link answer, and that replacement could not be verified: an expected keep failure. The answer stands,
// nothing is shown as an error, the saved sign-in is unchanged, and Diagnostics gets one Sign-in entry that carries only the
// failure's type, never its text.
public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task ShareLinkReplacementThatCannotBeKeptRecordsOneSignInDiagnosticWithOnlyItsType() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new RotatingShareLinks(HealthCookie(91));
        await using var shell = await HealthShell(signIn, client, linked: 1, shareLinks: shareLinks);
        // Verifying the replacement fails with text that quotes it, so the test can show that text goes nowhere.
        client.Replies[HealthCookie(91)] = HealthReply.Network;
        var row = HealthRow(shell, 1); shell.SelectedAccount = row; var profile = row.Profile.Id;
        shell.ClearDiagnostics.Execute(null); // Only what the check records stays in view.

        shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
        await shell.ShareLink.CheckAsync();

        Assert.Equal(1, shareLinks.Calls);
        Assert.Equal(new[] { HealthCookie(91) }, client.Snapshot().Select(call => call.Cookie)); // The replacement was checked once.
        Assert.Equal("This share link has expired. Ask for a new one.", shell.ShareLink.Status); // The answer stands.
        Assert.Equal("", shell.ErrorNotice);
        Assert.Equal("Saved link", HealthRow(shell, 1).Identity);
        using (var vault = new ProtectedCredentialVault(_root))
        {
            var saved = (await vault.ReadAsync(profile, default))!;
            Assert.Equal((AuthorizationHealth.Linked, HealthCookie(1)), (saved.Health, saved.SessionCookie));
        }

        shell.RefreshDiagnostics.Execute(null);
        var failure = Assert.Single(shell.DiagnosticRows, entry => entry.Event.Kind == ManagerEventKind.OperationFailed);
        Assert.Equal(ManagerSubsystem.SignIn, failure.Event.Subsystem);
        Assert.Equal("Exception", failure.Event.ErrorType); // A RobloxSessionException is named by its safe type class only.
        Assert.Equal("Sign-in", failure.Subsystem);
        foreach (string text in new[] { shell.BuildVisibleDiagnosticText(), shell.BuildDiagnosticReport() })
        { Assert.DoesNotContain(HealthCookiePrefix, text); Assert.DoesNotContain("Synthetic outage", text); }
        HealthNoSecrets(shell);
    });

    /// <summary>Answers "expired" and sends a replacement session with the answer, as Roblox may when it rotates the cookie.</summary>
    private sealed class RotatingShareLinks(string replacement) : IRobloxShareLinkClient
    {
        public int Calls;
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new RobloxShareLinkResolution(RobloxShareLinkOutcome.Expired, rotatedCredential: new RobloxSessionCredential(replacement)));
        }
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
    }
}
