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
    public enum HealthReply { Valid, Invalid, Challenge, Network, TimedOut, NotProcessed }

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

        // The challenged row's button follows its saved sign-in, not its wording: it offers sign-in, and
        // choosing it opens Roblox sign-in instead of queuing a launch that would fail closed.
        var challenged = HealthRow(shell, 3);
        Assert.Equal("Sign in", challenged.PowerLabel);
        Assert.DoesNotContain("Launch this account's preferred game", challenged.PowerExplanation);
        client.Replies.Remove(HealthCookie(3)); signIn.Next = HealthCookie(3);
        shell.AccountPower.Execute(challenged); await shell.AccountPower.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { ids[3] }, signIn.Snapshot());
        Assert.DoesNotContain(shell.Launches, launch => launch.Operation.Intent.ProfileId == ids[3]);
        Assert.Equal("Saved link", HealthRow(shell, 3).Identity); Assert.Equal("Launch", HealthRow(shell, 3).PowerLabel);
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

    // R-accounts-1: Roblox unreachable or busy for a check stops the batch and loses no saved sign-in.
    [Fact]
    public Task CheckedSignInsStopWhenRobloxNeverProcessedACheckAndKeepThatSignIn() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 4);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var order = shell.Accounts.Select(row => (Cookie: CookieOf(row), row.Profile.Id)).ToArray();
        client.Replies[order[1].Cookie] = HealthReply.NotProcessed;

        shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { order[0].Cookie, order[1].Cookie }, client.Snapshot().Select(call => call.Cookie));
        using var vault = new ProtectedCredentialVault(_root);
        foreach (var account in order) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(account.Id, default))!.Health);
        Assert.Equal("Saved link", shell.Accounts.Single(row => row.Profile.Id == order[1].Id).Identity);
        Assert.Contains("could not be reached or was busy", shell.Notice); Assert.Contains("1 verified", shell.Notice);
        Assert.Contains("3 not checked", shell.Notice); Assert.DoesNotContain("sign-in because", shell.Notice);
        Assert.DoesNotContain("need sign-in", shell.Notice); Assert.DoesNotContain("needs sign-in", shell.Notice);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    // M-L1a-5: another saved-session operation (here a share-link check) holding the link service refuses the check
    // before anything is read or changed. That is not "needs sign-in": the batch stops and counts every account as not checked.
    [Fact]
    public Task CheckedSignInsStopWhenAnotherAccountOperationHoldsTheSignInServiceAndChangeNothing() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new HoldingShareLinks();
        await using var shell = await HealthShell(signIn, client, linked: 3, shareLinks: shareLinks);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var ids = shell.Accounts.Select(row => row.Profile.Id).ToArray();
        shell.SelectedAccount = HealthRow(shell, 1);
        shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
        Task sharing = shell.ShareLink.CheckAsync();
        try
        {
            await shareLinks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { shareLinks.Release(); await sharing.WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.Empty(client.Snapshot());
        Assert.Contains("stopped because another account operation was running", shell.Notice); Assert.Contains("3 not checked", shell.Notice);
        Assert.DoesNotContain("need sign-in", shell.Notice); Assert.DoesNotContain("needs sign-in", shell.Notice); Assert.DoesNotContain("sign-in because", shell.Notice);
        foreach (var row in shell.Accounts) Assert.Equal("Saved link", row.Identity);
        using var vault = new ProtectedCredentialVault(_root);
        foreach (var id in ids) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(id, default))!.Health);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    // N-B1a-1: a checked row removed from the library while the batch runs is skipped: it is never sent to Roblox and is not
    // counted as needing sign-in, whether its removal still holds the row or has already removed it. The rest are checked.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CheckedSignInsSkipARowRemovedWhileTheyRunAndCheckTheRest(bool removalFinished) => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var signIn = new HealthSignIn(); var client = new HealthClient(); var gate = new HealthRemovalGate(repository) { Hold = !removalFinished };
        await using var shell = await HealthShell(signIn, client, linked: 3, accounts: gate);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var order = shell.Accounts.Select(row => (Cookie: CookieOf(row), row.Profile.Id)).ToArray();
        client.HoldCookie = order[0].Cookie;
        shell.CheckCheckedSignIns.Execute(null); Task checking = shell.CheckCheckedSignIns.Execution; Task? removing = null;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            removing = shell.DeleteAccountsAsync([shell.Accounts.Single(row => row.Profile.Id == order[1].Id)]);
            if (removalFinished) await removing.WaitAsync(TimeSpan.FromSeconds(10));
            else await gate.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // The removal now holds the row and its saved sign-in.
            client.Release(); await checking.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            client.Release(); gate.Release(); await checking.WaitAsync(TimeSpan.FromSeconds(10));
            if (removing is not null) await removing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(new[] { order[0].Cookie, order[2].Cookie }, client.Snapshot().Select(call => call.Cookie));
        string summary = Assert.Single(shell.Events, item => item.Contains("Saved sign-in check", StringComparison.Ordinal));
        Assert.Contains("Saved sign-in check finished. 2 verified.", summary);
        Assert.Contains("1 checked account was being removed from your library, so it was not checked.", summary);
        Assert.DoesNotContain("need sign-in", summary); Assert.DoesNotContain("needs sign-in", summary); Assert.DoesNotContain("left unchanged", summary);
        // Whichever finished last wrote the notice: the check after a finished removal, the removal after the check.
        Assert.StartsWith(removalFinished ? "Saved sign-in check finished." : "Removed 1 profile(s).", shell.Notice);
        using var vault = new ProtectedCredentialVault(_root);
        foreach (var kept in new[] { order[0].Id, order[2].Id }) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(kept, default))!.Health);
        Assert.Null(await repository.FindAsync(order[1].Id, default)); Assert.Null(await vault.ReadAsync(order[1].Id, default));
        Assert.Equal(2, shell.Accounts.Count); Assert.Equal("", shell.ErrorNotice); Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    // N-B1a-2: removing a row stops the share-link check working on it. That check has its own owner, outside the sign-in lane,
    // so the removal neither cancels nor waits for a saved sign-in check of another row that is still running (Roblox has
    // answered it and it is reloading the list): that check reports its own result, and the removal finishes without it.
    [Fact]
    public Task RemovingTheRowAShareLinkCheckUsesLeavesASavedSignInCheckOfAnotherRowRunning() => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new HoldingShareLinks(); var gate = new HealthRemovalGate(repository);
        await using var shell = await HealthShell(signIn, client, linked: 2, shareLinks: shareLinks, accounts: gate);
        var checkedRow = HealthRow(shell, 1); var sharedRow = HealthRow(shell, 2); var notices = new List<string>();
        int Verified() => shell.Events.Count(item => item.Contains("Roblox sign-in identity verified", StringComparison.Ordinal));
        int verifiedBefore = Verified();
        shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShellViewModel.Notice)) notices.Add(shell.Notice); };
        shell.SelectedAccount = checkedRow; gate.HoldNextRead();
        shell.RefreshAuthorization.Execute(null); Task refreshing = shell.RefreshAuthorization.Execution; Task? sharing = null, removing = null;
        bool removalWaited;
        try
        {
            // The check's own saved-session operation has ended; the sign-in lane still runs it while its reload waits.
            await gate.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.True(shell.IsAuthorizing);
            shell.SelectedAccount = sharedRow;
            shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
            sharing = shell.ShareLink.CheckAsync(); await shareLinks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            removing = shell.DeleteAccountsAsync([sharedRow]);
            removalWaited = await Task.WhenAny(removing, Task.Delay(TimeSpan.FromSeconds(5))) != removing;
        }
        finally
        {
            gate.ReleaseRead(); shareLinks.Release(); await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
            if (sharing is not null) await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            if (removing is not null) await removing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.DoesNotContain("Account sign-in cancelled. Existing profiles were preserved.", notices);
        Assert.Equal(verifiedBefore + 1, Verified());
        Assert.False(removalWaited, "The removal waited for a saved sign-in check of a row it was not removing.");
        Assert.Contains(notices, notice => notice.StartsWith("Removed 1 profile(s).", StringComparison.Ordinal));
        Assert.StartsWith("Saved sign-in verified for @health_1.", shell.Notice);
        Assert.Equal(new[] { HealthCookie(1) }, client.Snapshot().Select(call => call.Cookie));
        using var vault = new ProtectedCredentialVault(_root);
        Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(checkedRow.Profile.Id, default))!.Health);
        Assert.Null(await repository.FindAsync(sharedRow.Profile.Id, default)); Assert.Null(await vault.ReadAsync(sharedRow.Profile.Id, default));
        Assert.Equal(checkedRow.Profile.Id, Assert.Single(shell.Accounts).Profile.Id);
        Assert.Equal("", shell.ErrorNotice); Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    // O-C1-2: removing the row a share-link check uses stops that check inside the saved-session service, not through the
    // panel's own stop. On Join a game the list then reloads with nothing selected, which restarts nothing, so the panel
    // itself must say the check stopped and why instead of leaving "Checking…" over a check that has ended.
    [Fact]
    public Task RemovingTheRowAShareLinkCheckUsesSaysTheCheckStoppedBecauseTheAccountWasRemoved() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new HoldingShareLinks();
        await using var shell = await HealthShell(signIn, client, linked: 2, shareLinks: shareLinks, publicDirectory: new SearchDirectory(new SearchClock()));
        var keptRow = HealthRow(shell, 1); var sharedRow = HealthRow(shell, 2);
        shell.Navigate.Execute("JoinGame"); shell.SelectedAccount = sharedRow; Assert.True(shell.IsJoinGame);
        shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
        Task sharing = shell.ShareLink.CheckAsync(); Task? removing = null;
        try
        {
            await shareLinks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Contains("Checking the share link", shell.ShareLink.Status);
            removing = shell.DeleteAccountsAsync([sharedRow]); await removing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            shareLinks.Release(); await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            if (removing is not null) await removing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Null(shell.SelectedAccount); Assert.Equal(keptRow.Profile.Id, Assert.Single(shell.Accounts).Profile.Id);
        Assert.False(shell.ShareLink.IsBusy); Assert.False(shell.ShareLink.HasResult);
        Assert.Equal("Share link check stopped because the account, or its saved sign-in, was removed. Paste the link again to check it with a signed-in account.", shell.ShareLink.Status);
        Assert.StartsWith("Removed 1 profile(s).", shell.Notice); Assert.Equal("", shell.ErrorNotice); Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
        using var vault = new ProtectedCredentialVault(_root);
        Assert.Null(await vault.ReadAsync(sharedRow.Profile.Id, default)); Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(keptRow.Profile.Id, default))!.Health);
    });

    // O-C1-2: removing the saved sign-in of the account a share-link check uses stops the check the same way, inside the
    // service; the row stays selected (the same profile is re-selected after the reload), so only the panel can say why it ended.
    [Fact]
    public Task RemovingTheSavedSignInAShareLinkCheckUsesSaysTheCheckStoppedBecauseItWasRemoved() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient(); var shareLinks = new HoldingShareLinks();
        await using var shell = await HealthShell(signIn, client, linked: 2, shareLinks: shareLinks, publicDirectory: new SearchDirectory(new SearchClock()));
        var sharedRow = HealthRow(shell, 2);
        shell.Navigate.Execute("JoinGame"); shell.SelectedAccount = sharedRow;
        shell.ShareLink.LinkInput = "https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
        Task sharing = shell.ShareLink.CheckAsync();
        try
        {
            await shareLinks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Contains("Checking the share link", shell.ShareLink.Status);
            shell.DisconnectAccount.Execute(null); await shell.DisconnectAccount.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { shareLinks.Release(); await sharing.WaitAsync(TimeSpan.FromSeconds(10)); }

        Assert.Equal(sharedRow.Profile.Id, shell.SelectedAccount?.Profile.Id); Assert.Equal("Not linked", HealthRow(shell, 2).Identity);
        Assert.False(shell.ShareLink.IsBusy); Assert.False(shell.ShareLink.HasResult);
        Assert.Equal("Share link check stopped because the account, or its saved sign-in, was removed. Paste the link again to check it with a signed-in account.", shell.ShareLink.Status);
        Assert.StartsWith("Saved sign-in removed from Zenith.", shell.Notice); Assert.Equal("", shell.ErrorNotice); Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
        using var vault = new ProtectedCredentialVault(_root);
        Assert.Null(await vault.ReadAsync(sharedRow.Profile.Id, default)); Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(HealthRow(shell, 1).Profile.Id, default))!.Health);
    });

    // M-L1a-5: Roblox never processed a check, and another operation replaced its fence before the restore. The restore is
    // refused (the newer sign-in is kept), but Roblox was still unreachable or busy, so the batch must stop there.
    [Fact]
    public Task CheckedSignInsStopWhenAnUnprocessedChecksRestoreFindsANewerSignIn() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 4);
        foreach (var row in shell.Accounts) row.IsChecked = true;
        var order = shell.Accounts.Select(row => (Cookie: CookieOf(row), row.Profile.Id)).ToArray();
        client.Replies[order[1].Cookie] = HealthReply.NotProcessed;
        client.OnCall = async cookie =>
        {
            if (cookie != order[1].Cookie) return;
            // Another operation saves this account's sign-in again while Roblox is answering, replacing the check's fence.
            using var other = new ProtectedCredentialVault(_root);
            var snapshot = await other.ReadWriteSnapshotAsync(order[1].Id, default);
            var newer = snapshot.Record! with { Generation = snapshot.NextGeneration, Health = AuthorizationHealth.Linked };
            Assert.Equal(AuthorizationMutationStatus.Applied, (await other.TryCommitSnapshotAsync(snapshot, newer, Guid.NewGuid(), default)).Status);
        };

        shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { order[0].Cookie, order[1].Cookie }, client.Snapshot().Select(call => call.Cookie));
        Assert.Contains("could not be reached or was busy", shell.Notice); Assert.Contains("1 verified", shell.Notice); Assert.Contains("3 not checked", shell.Notice);
        Assert.DoesNotContain("need sign-in", shell.Notice); Assert.DoesNotContain("needs sign-in", shell.Notice); Assert.DoesNotContain("sign-in because", shell.Notice);
        using var vault = new ProtectedCredentialVault(_root);
        foreach (var account in order) Assert.Equal(AuthorizationHealth.Linked, (await vault.GetSummaryAsync(account.Id, default))!.Health);
        foreach (var row in shell.Accounts) Assert.Equal("Saved link", row.Identity);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
    });

    // M-L1a-5: a row whose saved sign-in could not be read because protected storage was busy is not a row without one.
    [Fact]
    public Task CheckedSignInsCountRowsWhoseStatusCouldNotBeReadSeparatelyFromRowsWithNoSavedSignIn() => OnUi(async () =>
    {
        var signIn = new HealthSignIn(); var client = new HealthClient();
        await using var shell = await HealthShell(signIn, client, linked: 2);
        shell.Search = "Health 01"; await shell.AccountQuery; Assert.Single(shell.Accounts);
        using (new FileStream(Path.Combine(_root, "accounts.vault", ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Health 02 has a saved sign-in but was not on the page before, so the busy store leaves it with no last-known one.
            shell.Search = ""; await shell.AccountQuery;
            var busy = HealthRow(shell, 2); Assert.True(busy.SignInStatusBusy); Assert.Null(busy.KnownUserId);
            busy.IsChecked = true;

            shell.CheckCheckedSignIns.Execute(null); await shell.CheckCheckedSignIns.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(client.Snapshot());
        Assert.Contains("Nothing was sent to Roblox.", shell.Notice);
        Assert.DoesNotContain("no saved sign-in", shell.Notice);
        Assert.Contains("1 checked account's sign-in status could not be read because protected storage was busy", shell.Notice);
        Assert.False(shell.IsAuthorizing); HealthNoSecrets(shell);
        await shell.AccountQuery;
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
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
    private async Task<ShellViewModel> HealthShell(HealthSignIn signIn, HealthClient client, int linked, int unlinked = 0, IRobloxShareLinkClient? shareLinks = null,
        IAccountRepository? accounts = null, IPublicRobloxDirectory? publicDirectory = null)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: accounts, signIn: signIn, sessionClient: client, shareLinkClient: shareLinks,
            publicDirectory: publicDirectory);
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
            client.Clear(); signIn.Clear(); return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static void HealthNoSecrets(ShellViewModel shell)
    { Assert.DoesNotContain(HealthCookiePrefix, shell.Notice); Assert.DoesNotContain(shell.Events, item => item.Contains(HealthCookiePrefix, StringComparison.Ordinal)); }

    private sealed class HealthSignIn : IRobloxSessionSignIn
    {
        public string Next = "";
        private readonly List<Guid> _calls = [];
        public void Clear() { lock (_calls) _calls.Clear(); }
        public Guid[] Snapshot() { lock (_calls) return _calls.ToArray(); }
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); lock (_calls) _calls.Add(id); return Task.FromResult(new RobloxSessionCredential(Next)); }
    }
    private sealed class HealthClient : IRobloxSessionClient
    {
        private readonly List<(string Cookie, long At)> _calls = [];
        public readonly Dictionary<string, HealthReply> Replies = [];
        public string? HoldCookie; public CancellationToken HeldToken;
        /// <summary>Runs before each reply, while Roblox is "answering"; a test uses it to act as another operation.</summary>
        public Func<string, Task>? OnCall;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public void Clear() { lock (_calls) _calls.Clear(); }
        public (string Cookie, long At)[] Snapshot() { lock (_calls) return _calls.ToArray(); }
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); string cookie = credential.CopyValue();
            lock (_calls) _calls.Add((cookie, Stopwatch.GetTimestamp()));
            if (OnCall is { } onCall) await onCall(cookie).ConfigureAwait(false);
            if (cookie == HoldCookie)
            { HeldToken = ct; Entered.TrySetResult(); await _release.Task.ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
            // Synthetic failure text carries the cookie to prove it never reaches Notice or Events.
            switch (Replies.GetValueOrDefault(cookie))
            {
                case HealthReply.Invalid: throw new RobloxSessionException(RobloxSessionFailure.InvalidSession, "Synthetic 401 " + cookie);
                case HealthReply.Challenge: throw new RobloxSessionException(RobloxSessionFailure.Challenge, "Synthetic challenge " + cookie);
                case HealthReply.Network: throw new RobloxSessionException(RobloxSessionFailure.Network, "Synthetic outage " + cookie);
                case HealthReply.TimedOut: throw new RobloxSessionException(RobloxSessionFailure.TimedOut, "Synthetic timeout " + cookie);
                case HealthReply.NotProcessed: throw new RobloxSessionException(RobloxSessionFailure.NotProcessed, "Synthetic refusal " + cookie);
            }
            int index = int.Parse(cookie[HealthCookiePrefix.Length..], CultureInfo.InvariantCulture); long userId = 1000 + index;
            return new(new(userId, "health_" + index, RobloxSessionIdentity.Issuer, userId.ToString(CultureInfo.InvariantCulture)));
        }
    }
    /// <summary>The library, except that when <see cref="Hold"/> is set a profile removal waits, holding its rows, until released,
    /// and after <see cref="HoldNextRead"/> the next account-list read (a command's reload) waits until released.</summary>
    private sealed class HealthRemovalGate(IAccountRepository inner) : IAccountRepository
    {
        public bool Hold;
        public readonly TaskCompletionSource DeleteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        private int _holdNextRead;
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNextRead() => Volatile.Write(ref _holdNextRead, 1);
        public void ReleaseRead() => _readRelease.TrySetResult();
        public async Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct)
        {
            if (Hold) { DeleteEntered.TrySetResult(); await _release.Task.WaitAsync(ct).ConfigureAwait(false); }
            await inner.DeleteManyAsync(profiles, ct).ConfigureAwait(false);
        }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _holdNextRead, 0) == 1) { ReadEntered.TrySetResult(); await _readRelease.Task.WaitAsync(ct).ConfigureAwait(false); }
            return await inner.ReadAsync(query, ct).ConfigureAwait(false);
        }
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct) => inner.SaveAsync(account, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }
    /// <summary>A share-link check that holds the saved-session service until released, then answers "unavailable" (no write).</summary>
    private sealed class HoldingShareLinks : IRobloxShareLinkClient
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new(RobloxShareLinkOutcome.Unavailable);
        }
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
    }
}
