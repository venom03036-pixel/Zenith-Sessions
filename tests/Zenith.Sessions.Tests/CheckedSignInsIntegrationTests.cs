using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-accounts-0: check many saved sign-ins on demand, one at a time, with a per-account result.
public sealed partial class ClientWindowIntegrationTests
{
    private const string HealthCookiePrefix = "synthetic-health-batch-";
    public enum HealthReply { Valid, Invalid, Challenge, Network, TimedOut }

    [Fact]
    public Task CheckedSignInsAreCheckedOneAtATimeInPageOrderAndEachRowShowsItsResult() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 5, unlinked: 1);
        client.Replies[HealthCookie(2)] = HealthReply.Invalid; client.Replies[HealthCookie(3)] = HealthReply.Challenge;
        foreach (int i in new[] { 1, 2, 3, 4, 6 }) HealthRow(shell, i).IsChecked = true; // 5 stays unchecked; 6 has no saved sign-in.
        var expected = shell.Accounts.Where(row => row.IsChecked && row.KnownUserId is not null).Select(CookieOf).ToArray();
        var ids = Enumerable.Range(1, 6).ToDictionary(i => i, i => HealthRow(shell, i).Profile.Id);

        shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));

        var calls = client.Snapshot();
        Assert.Equal(expected, calls.Select(call => call.Cookie)); Assert.Equal(4, calls.Length);
        for (int i = 1; i < calls.Length; i++)
            Assert.True(Stopwatch.GetElapsedTime(calls[i - 1].At, calls[i].At) >= TimeSpan.FromMilliseconds(390), $"Gap {i} was too short.");
        Assert.Equal("Saved link", HealthRow(shell, 1).Identity); Assert.Equal("Sign in required", HealthRow(shell, 2).Identity);
        Assert.Equal("Security check required", HealthRow(shell, 3).Identity); Assert.Equal("Saved link", HealthRow(shell, 4).Identity);
        Assert.Equal("Saved link", HealthRow(shell, 5).Identity); Assert.Equal("Not linked", HealthRow(shell, 6).Identity);
        using var vault = new ProtectedCredentialVault(_root);
        foreach (int i in new[] { 1, 4, 5 }) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(ids[i], default))!.Health);
        foreach (int i in new[] { 2, 3 }) Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(ids[i], default))!.Health);
        Assert.Contains("2 verified", shell.Notice); Assert.Contains("1 needs sign-in", shell.Notice);
        Assert.Contains("1 needs a Roblox security check", shell.Notice); Assert.Contains("1 checked account has no saved sign-in", shell.Notice);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    [Theory]
    [InlineData(HealthReply.Network, "could not be reached")]
    [InlineData(HealthReply.TimedOut, "timed out")]
    public Task CheckedSignInsStopAtTheFirstUnreachableResultAndLeaveTheRestUnchanged(HealthReply reply, string reason) => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 4);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var order = shell.Accounts.Select(row => (Cookie: CookieOf(row), row.Profile.Id)).ToArray();
        client.Replies[order[1].Cookie] = reply;

        shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { order[0].Cookie, order[1].Cookie }, client.Snapshot().Select(call => call.Cookie));
        using var vault = new ProtectedCredentialVault(_root);
        Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(order[0].Id, default))!.Health);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(order[1].Id, default))!.Health);
        foreach (var later in order.Skip(2)) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(later.Id, default))!.Health);
        Assert.Contains(reason, shell.Notice); Assert.Contains("1 verified", shell.Notice); Assert.Contains("2 not checked", shell.Notice);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("lock")]
    [InlineData("panic")]
    public Task CheckedSignInsShareTheSignInLaneSoCancelLockAndPanicStopTheRest(string stopKind) => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 3);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var order = shell.Accounts.Select(row => (Cookie: CookieOf(row), row.Profile.Id)).ToArray();
        client.HoldCookie = order[0].Cookie;
        shell.CheckCheckedSignIns.Execute(null); Task execution = shell.CheckCheckedSignIns.Execution; string? notice = null;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(shell.IsAuthorizing); Assert.False(shell.CanAddRobloxAccount);
            if (stopKind == "cancel") shell.CancelAuthorization.Execute(null); else if (stopKind == "lock") shell.LockDashboard(); else shell.Panic.Execute(null);
            notice = shell.Notice; Assert.True(client.HeldToken.IsCancellationRequested); Assert.False(execution.IsCompleted);
        }
        finally { client.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(new[] { order[0].Cookie }, client.Snapshot().Select(call => call.Cookie)); Assert.False(shell.IsAuthorizing);
        if (stopKind == "cancel") { Assert.Contains("cancelled", shell.Notice); Assert.Contains("2 not checked", shell.Notice); }
        else Assert.Equal(notice, shell.Notice);
        using var vault = new ProtectedCredentialVault(_root);
        foreach (var later in order.Skip(1)) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(later.Id, default))!.Health);
        HealthNoSecrets(shell);
    });

    [Fact]
    public Task CheckedSignInsWithNothingCheckedContactsNobodyAndSaysWhatToDo() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 1);
        shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution;
        Assert.Empty(client.Snapshot()); Assert.False(shell.IsAuthorizing); Assert.Contains("Check one or more accounts", shell.Notice);
    });

    [Fact]
    public void CheckCheckedSignInsButtonSitsBesideRemoveCheckedInTheAccountToolbar()
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        using var input = typeof(ClientWindowIntegrationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var toolbar = XDocument.Load(input).Descendants(ui + "WrapPanel").Single(element => (string?)element.Attribute(x + "Name") == "AccountToolbar");
        var buttons = toolbar.Elements(ui + "Button").ToArray();
        int index = Array.FindIndex(buttons, button => (string?)button.Attribute("Command") == "{Binding CheckCheckedSignIns}");
        Assert.True(index >= 0, "The account toolbar has no Check checked sign-ins button.");
        Assert.Equal("Check checked sign-ins", (string?)buttons[index].Attribute("Content"));
        Assert.Equal("Remove checked", (string?)buttons[index + 1].Attribute("Content"));
    }

    private static string HealthCookie(int index) => HealthCookiePrefix + index.ToString("00", CultureInfo.InvariantCulture);
    private static string CookieOf(AccountRow row) => HealthCookie((int)(row.KnownUserId!.Value - 1000));
    private static AccountRow HealthRow(ShellViewModel shell, int index) => shell.Accounts.Single(row => row.Alias == $"Health {index:00}");
    private async Task<ShellViewModel> HealthShell(HealthSignIn signIn, HealthClient client, int linked, int unlinked = 0)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), signIn: signIn, sessionClient: client);
        try
        {
            await shell.InitializeAsync();
            for (int i = 1; i <= linked + unlinked; i++) await shell.SaveAccountAsync(AccountProfile.Create($"Health {i:00}"));
            for (int i = 1; i <= linked; i++)
            {
                shell.SelectedAccount = HealthRow(shell, i); signIn.Next = HealthCookie(i);
                shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
                Assert.Equal(1000 + i, HealthRow(shell, i).KnownUserId);
            }
            client.Clear(); return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static void HealthNoSecrets(ShellViewModel shell)
    { Assert.DoesNotContain(HealthCookiePrefix, shell.Notice); Assert.DoesNotContain(shell.Events, item => item.Contains(HealthCookiePrefix, StringComparison.Ordinal)); }

    private sealed class HealthSignIn : IRobloxSessionSignIn
    {
        public string Next = "";
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new RobloxSessionCredential(Next)); }
    }
    private sealed class HealthClient : IRobloxSessionClient
    {
        private readonly List<(string Cookie, long At)> _calls = [];
        public readonly Dictionary<string, HealthReply> Replies = [];
        public string? HoldCookie; public CancellationToken HeldToken;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public void Clear() { lock (_calls) _calls.Clear(); }
        public (string Cookie, long At)[] Snapshot() { lock (_calls) return _calls.ToArray(); }
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); string cookie = credential.CopyValue();
            lock (_calls) _calls.Add((cookie, Stopwatch.GetTimestamp()));
            if (cookie == HoldCookie)
            { HeldToken = ct; Entered.TrySetResult(); await _release.Task.ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
            // Synthetic failure text carries the cookie to prove it never reaches Notice or Events.
            switch (Replies.GetValueOrDefault(cookie))
            {
                case HealthReply.Invalid: throw new RobloxSessionException(RobloxSessionFailure.InvalidSession, "Synthetic 401 " + cookie);
                case HealthReply.Challenge: throw new RobloxSessionException(RobloxSessionFailure.Challenge, "Synthetic challenge " + cookie);
                case HealthReply.Network: throw new RobloxSessionException(RobloxSessionFailure.Network, "Synthetic outage " + cookie);
                case HealthReply.TimedOut: throw new RobloxSessionException(RobloxSessionFailure.TimedOut, "Synthetic timeout " + cookie);
            }
            int index = int.Parse(cookie[HealthCookiePrefix.Length..], CultureInfo.InvariantCulture); long userId = 1000 + index;
            return new(new(userId, "health_" + index, RobloxSessionIdentity.Issuer, userId.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
