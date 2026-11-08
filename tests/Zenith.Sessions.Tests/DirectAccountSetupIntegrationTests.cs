using System.IO;
using System.Text;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private const string DirectCookie = "synthetic-direct-account-owned-sign-in";
    private static ShellViewModel DirectShell(string root, DirectSignIn signIn, DirectClient client, IAccountRepository? accounts = null)
        => new(root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: accounts, signIn: signIn, sessionClient: client);
    // The share-link client, the private server list and every public directory are supplied, so no request can reach the network.
    private static ShellViewModel DirectShell(string root, DirectSignIn signIn, DirectClient client, IRobloxShareLinkClient shareLinks,
        IPrivateServerStore privateServers, SearchDirectory directory)
        => new(root, false, Dispatcher.CurrentDispatcher, new Monitor(), publicDirectory: directory, serverDirectory: new SearchServers(), gameDirectory: directory,
            signIn: signIn, sessionClient: client, shareLinkClient: shareLinks, privateServerStore: privateServers);

    [Fact]
    public Task AddAccountNeedsNoAliasOrExistingProfileAndPublishesOnlyTheVerifiedResult() => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient { Hold = true };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.True(shell.CanAddRobloxAccount);
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(shell.IsAuthorizing); Assert.False(shell.CanAddRobloxAccount);
            Assert.Empty(shell.Accounts); Assert.NotEqual(Guid.Empty, signIn.ProfileId);
            using var before = new AccountRepository(_root); Assert.Equal(0, (await before.ReadAsync(new(), default)).Total);
            Assert.Equal(DirectCookie, signIn.Returned!.CopyValue());
        }
        finally { client.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        var added = Assert.Single(shell.Accounts); Assert.Equal(signIn.ProfileId, added.Profile.Id); Assert.Same(added, shell.SelectedAccount);
        Assert.Equal("direct_player", added.Profile.Label); Assert.Equal("direct_player", added.Profile.Username); Assert.Equal(456, added.Profile.RobloxUserId);
        Assert.Equal(456, added.KnownUserId); Assert.True(added.AuthorizationGeneration > 0); Assert.Equal("General", added.Profile.Group);
        Assert.Null(added.Profile.PreferredPlaceId); Assert.False(added.Profile.IsFavorite); Assert.Equal("", added.Profile.AdditionalGroups);
        using var vault = new ProtectedCredentialVault(_root); var summary = (await vault.GetSummaryAsync(added.Profile.Id, default))!;
        Assert.Equal(RobloxSessionIdentity.Provider, summary.Provider); Assert.Equal(AuthorizationHealth.Linked, summary.Health);
        Assert.Equal(456, summary.VerifiedUserId); Assert.Equal("direct_player", summary.VerifiedUsername);
        Assert.Equal(DirectCookie, (await vault.ReadAsync(added.Profile.Id, default))!.SessionCookie);
        Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); Assert.False(shell.IsAuthorizing);
        Assert.Contains("direct_player", shell.Notice); DirectNoSecrets(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task SelectingAnotherExistingProfileCannotRedirectThePendingAddOrItsFinalSelection() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing A") with { Notes = "Keep A", Group = "Family", AdditionalGroups = "Raid", PreferredPlaceId = 42 });
        await shell.SaveAccountAsync(AccountProfile.Create("Existing B") with { Notes = "Keep B", Priority = 37 });
        var originals = shell.Accounts.Select(row => row.Profile).ToArray(); var first = shell.Accounts.First(row => row.Alias == "Existing A");
        var second = shell.Accounts.First(row => row.Alias == "Existing B"); shell.SelectedAccount = first;
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.DoesNotContain(signIn.ProfileId, originals.Select(row => row.Id));
            shell.SelectedAccount = second; Assert.True(shell.IsAuthorizing);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(second.Profile.Id, shell.SelectedAccount?.Profile.Id); Assert.Equal(3, shell.Accounts.Count);
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        foreach (var original in originals) { Assert.Equal(original, await repository.FindAsync(original.Id, default)); Assert.Null(await vault.GetSummaryAsync(original.Id, default)); }
        Assert.Equal(456, (await vault.GetSummaryAsync(signIn.ProfileId, default))!.VerifiedUserId);
        Assert.Equal("direct_player", (await repository.FindAsync(signIn.ProfileId, default))!.Username); DirectNoSecrets(shell);
    });

    [Fact]
    public Task AddingAnAlreadySavedAccountRefreshesItsSignInKeepsItsDetailsAndSelectsItsRow() => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        var first = Assert.Single(shell.Accounts).Profile;
        await shell.SaveAccountAsync(first with { Label = "Main alt", Notes = "Keep these notes", Group = "Family", AdditionalGroups = "Raid" });
        await shell.SaveAccountAsync(AccountProfile.Create("Other row"));
        var original = shell.Accounts.First(row => row.Profile.Id == first.Id).Profile; Assert.Equal("Main alt", original.Label);
        shell.SelectedAccount = shell.Accounts.First(row => row.Alias == "Other row");
        using var vault = new ProtectedCredentialVault(_root); var before = (await vault.ReadAsync(original.Id, default))!;
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.NotEqual(original.Id, signIn.ProfileId); Assert.Equal(2, shell.Accounts.Count);
        var row = shell.Accounts.First(item => item.Profile.Id == original.Id); Assert.Equal(original, row.Profile); Assert.Same(row, shell.SelectedAccount);
        var after = (await vault.ReadAsync(original.Id, default))!;
        Assert.Equal(before with { Generation = before.Generation + 1 }, after); Assert.Equal(after.Generation, row.AuthorizationGeneration);
        Assert.Contains("Refreshed", shell.Notice); Assert.Contains("@direct_player", shell.Notice); Assert.DoesNotContain("Added", shell.Notice);
        using var repository = new AccountRepository(_root); Assert.Equal(2, (await repository.ReadAsync(new(), default)).Total);
        Assert.Null(await repository.FindAsync(signIn.ProfileId, default));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "accounts.vault"), "*.dpapi")); DirectNoSecrets(shell);
    });

    [Fact]
    public Task RemovingTheRowAnAddIsRefreshingStopsThatAddAndWaitsForItBeforeRemovingTheRow() => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        using var vault = new ProtectedCredentialVault(_root); var gate = new DirectReloadGate(repository, vault);
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client, gate); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        var row = Assert.Single(shell.Accounts);
        // The second add verifies the same player, moves onto this row, and then checks the row still exists.
        gate.HoldFind = row.Profile.Id; shell.AddRobloxAccount.Execute(null); Task adding = shell.AddRobloxAccount.Execution; Task? removing = null;
        try
        {
            await gate.FindEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotEqual(row.Profile.Id, signIn.ProfileId);
            removing = shell.DeleteAccountsAsync([row]);
            bool stopped = await Task.WhenAny(gate.FindCancelled.Task, Task.Delay(TimeSpan.FromSeconds(5))) == gate.FindCancelled.Task;
            Assert.True(stopped, "Removing the row did not stop the add that was refreshing that row's saved sign-in.");
            Assert.False(removing.IsCompleted); Assert.False(adding.IsCompleted);
        }
        finally { gate.ReleaseFind(); await adding.WaitAsync(TimeSpan.FromSeconds(5)); if (removing is not null) await removing.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Empty(shell.Accounts); Assert.False(shell.IsAuthorizing); Assert.Equal("", shell.ErrorNotice);
        Assert.StartsWith("Removed 1 profile(s).", shell.Notice); Assert.DoesNotContain("Refreshed", shell.Notice);
        Assert.Null(await repository.FindAsync(row.Profile.Id, default)); Assert.Null(await repository.FindAsync(signIn.ProfileId, default));
        Assert.Null(await vault.ReadAsync(row.Profile.Id, default)); Assert.Null(await vault.ReadAsync(signIn.ProfileId, default));
        DirectNoSecrets(shell);
    });

    [Theory]
    [InlineData("invalid-identity")]
    [InlineData("expired")]
    public Task InvalidOrExpiredSignInCannotCreateAnAdvertisedSavedAccount(string failure) => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Expired = failure == "expired" }; var client = new DirectClient { InvalidIdentity = failure == "invalid-identity" };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.False(shell.IsAuthorizing);
        using var repository = new AccountRepository(_root); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
        Assert.False(Directory.Exists(Path.Combine(_root, "accounts.vault")) && Directory.GetFiles(Path.Combine(_root, "accounts.vault"), "*.dpapi").Length > 0);
        Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ClosingSignInIsNormalCancellationEvenWhenTheShellTokenWasNotCancelled() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { DialogCancelled = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Empty(shell.Accounts); Assert.Null(shell.SelectedAccount); Assert.False(shell.IsAuthorizing); Assert.Equal(0, client.Calls);
        Assert.Contains("cancel", shell.Notice, StringComparison.OrdinalIgnoreCase); Assert.False(signIn.Token.IsCancellationRequested); DirectNoSecrets(shell);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LockOrPanicJoinsLateValidationAndCannotPublishANewAccountOrLateFailure(bool panic, bool failLate) => OnUi(async () =>
    {
        var signIn = new DirectSignIn(); var client = new DirectClient { Hold = true, FailLate = failLate };
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Keep existing selection")); var original = Assert.Single(shell.Accounts).Profile;
        shell.SelectedAccount = shell.Accounts[0]; shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution; string? notice = null;
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); notice = shell.Notice;
            Assert.True(client.Token.IsCancellationRequested); Assert.False(execution.IsCompleted); Assert.True(shell.IsAuthorizing);
        }
        finally { client.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(notice, shell.Notice); Assert.Equal(original, Assert.Single(shell.Accounts).Profile); Assert.Equal(original.Id, shell.SelectedAccount?.Profile.Id);
        Assert.False(shell.IsAuthorizing); Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue());
        using var repository = new AccountRepository(_root); Assert.Equal(1, (await repository.ReadAsync(new(), default)).Total);
        using var vault = new ProtectedCredentialVault(_root); Assert.Null(await vault.GetSummaryAsync(signIn.ProfileId, default));
        DirectNoSecrets(shell); Assert.Empty(shell.Launches);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StopDuringPostcommitReloadPreservesSavedDataButNeverSelectsOrAnnouncesTheLateAdd(bool panic) => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var existing = await repository.SaveAsync(AccountProfile.Create("Existing selected profile"), default);
        using var vault = new ProtectedCredentialVault(_root); var gate = new DirectReloadGate(repository, vault);
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client, gate); await shell.InitializeAsync(); shell.SelectedAccount = Assert.Single(shell.Accounts);
        gate.Armed = true; shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution; string? notice = null;
        try
        {
            var summary = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(signIn.ProfileId, summary.ProfileId);
            Assert.Equal(AuthorizationHealth.Linked, summary.Health); Assert.False(execution.IsCompleted);
            if (panic) shell.Panic.Execute(null); else shell.LockDashboard(); notice = shell.Notice;
        }
        finally { gate.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(notice, shell.Notice); Assert.Equal(existing.Id, shell.SelectedAccount?.Profile.Id); Assert.False(shell.IsAuthorizing);
        Assert.NotNull(await repository.FindAsync(signIn.ProfileId, default)); Assert.Equal(456, (await vault.GetSummaryAsync(signIn.ProfileId, default))!.VerifiedUserId);
        Assert.Equal(existing, await repository.FindAsync(existing.Id, default)); Assert.DoesNotContain("added", shell.Notice, StringComparison.OrdinalIgnoreCase);
        DirectNoSecrets(shell);
    });

    [Fact]
    public Task AddOwnsTheSharedAuthorizationLaneAgainstLinkCheckAndRepeatedAddCommands() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing")); shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.AddRobloxAccount.Execute(null); Task execution = shell.AddRobloxAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); string notice = shell.Notice;
            shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution; shell.RefreshAuthorization.Execute(null); await shell.RefreshAuthorization.Execution;
            shell.AddRobloxAccount.Execute(null); Assert.Same(execution, shell.AddRobloxAccount.Execution);
            Assert.Equal(1, signIn.Calls); Assert.Equal(0, client.Calls); Assert.True(shell.IsAuthorizing); Assert.False(shell.CanAddRobloxAccount); Assert.Equal(notice, shell.Notice);
            shell.CancelAuthorization.Execute(null); Assert.True(signIn.Token.IsCancellationRequested);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(shell.Accounts); Assert.False(shell.IsAuthorizing); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ExistingLinkOwnershipBlocksDirectAddWithoutReplacingItsCancellationOwner() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client); await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Existing")); shell.SelectedAccount = Assert.Single(shell.Accounts);
        shell.LinkAccount.Execute(null); Task execution = shell.LinkAccount.Execution;
        try
        {
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(shell.CanAddRobloxAccount);
            shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution; Assert.Equal(1, signIn.Calls);
            shell.CancelAuthorization.Execute(null); Assert.True(signIn.Token.IsCancellationRequested); Assert.False(execution.IsCompleted);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(shell.Accounts); Assert.Null(shell.Accounts[0].KnownUserId); DirectNoSecrets(shell);
    });

    [Fact]
    public Task ShutdownWaitsForTheActualUncooperativeAddOwnerBeforeDisposingItsResources() => OnUi(async () =>
    {
        var signIn = new DirectSignIn { Hold = true }; var client = new DirectClient(); var shell = DirectShell(_root, signIn, client);
        Task? disposing = null; Task execution = Task.CompletedTask;
        try
        {
            await shell.InitializeAsync(); shell.AddRobloxAccount.Execute(null); execution = shell.AddRobloxAccount.Execution;
            await signIn.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); disposing = shell.DisposeAsync().AsTask();
            Assert.True(signIn.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted); Assert.False(execution.IsCompleted);
            Assert.Equal(0, signIn.Disposals); Assert.False(shell.CanAddRobloxAccount);
        }
        finally { signIn.Release(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); await (disposing ?? shell.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, signIn.Disposals); Assert.Throws<ObjectDisposedException>(() => signIn.Returned!.CopyValue()); Assert.Equal(0, client.Calls);
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default); Assert.Equal(0, (await repository.ReadAsync(new(), default)).Total);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockedOrFailedStartupCannotOpenDirectAccountSignIn(bool startupFailure) => OnUi(async () =>
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var gate = new DirectReloadGate(repository, vault) { FailInitialization = startupFailure };
        var signIn = new DirectSignIn(); var client = new DirectClient();
        await using var shell = DirectShell(_root, signIn, client, gate); await shell.InitializeAsync();
        if (!startupFailure) shell.LockDashboard(); else Assert.False(shell.IsReady);
        string notice = shell.Notice; Assert.False(shell.CanAddRobloxAccount); shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
        Assert.Equal(0, signIn.Calls); Assert.Equal(0, client.Calls); Assert.Equal(notice, shell.Notice); Assert.Empty(shell.Accounts);
    });

    [Fact]
    public Task ShareLinkAndSavedPrivateServerFlowsLeaveNoCodeInTheDataFolderOrDiagnostics() => OnUi(async () =>
    {
        // Synthetic values only. The account signs in through the local fakes above; the share-link client, game lookup and
        // server list are local fakes. The saved list is the real DPAPI store on the fixture root, wrapped to inject read failures.
        const string LinkCode = "Wq3Er7Ty1Ui5Op9As2Df6Gh0Jk4Lz8Xc", ShareCode = "c4e8a2f6b0d3e7a1c5f9b2d6e0a4c8f3";
        const string ShareUrl = "https://www.roblox.com/share?code=" + ShareCode + "&type=Server";
        string[] markers = [ShareCode, LinkCode[..^4]]; // Only the last four characters of a private server code may ever be shown.
        AssertCodeScanFindsPlantedMarkers(markers);
        var shareClient = new DirectShareClient(LinkCode, ShareUrl); var store = new DirectPrivateServers(new PrivateServerStore(_root));
        var directory = new SearchDirectory(new()); var shell = DirectShell(_root, new DirectSignIn(), new DirectClient(), shareClient, store, directory);
        var shown = new StringBuilder(); string saved = Path.Combine(_root, "private-servers.dat");
        // ErrorNotice is where Fail shows an error, and it shows an InvalidDataException's message verbatim, so it is read with the rest.
        void Capture() => shown.AppendLine(shell.Notice).AppendLine(shell.ErrorNotice).AppendLine(shell.ShareLink.Status).AppendLine(shell.ShareLink.ResultTitle)
            .AppendLine(shell.ShareLink.ResultDetail).AppendLine(shell.DestinationSearch.Status);
        try
        {
            await shell.InitializeAsync(); shell.AddRobloxAccount.Execute(null); await shell.AddRobloxAccount.Execution;
            Guid profile = Assert.Single(shell.Accounts).Profile.Id; Assert.Same(shell.Accounts[0], shell.SelectedAccount);
            // Opening Join a game reads the saved list once. That read fails with a type ShellViewModel.Fail would show verbatim,
            // so forwarding the store's exception there would put the code in ErrorNotice.
            store.FailingReads.Enqueue(new InvalidDataException("synthetic damaged list near code " + LinkCode));
            shell.Navigate.Execute("JoinGame"); shell.SetPreviewWindowVisible(true); await shell.ShareLink.ActiveOperation; Capture();
            shell.ShareLink.LinkInput = ShareUrl; await shell.ShareLink.CheckAsync(); Capture();
            Assert.True(shell.ShareLink.HasPrivateResult); Assert.Contains("…" + LinkCode[^4..], shell.ShareLink.ResultDetail);
            await shell.ShareLink.SaveAsFavoriteAsync(); Capture(); var row = Assert.Single(shell.ShareLink.Saved);
            // The code is stored now, so a clean scan of the saved list proves it is held only as DPAPI ciphertext.
            using (var code = await store.Inner.ReadCodeAsync(row.Id, default))
            { bool same = false; code.Reveal(span => same = span.SequenceEqual(LinkCode.AsSpan())); Assert.True(same); }
            Assert.Empty(FilesQuoting(_root, [saved], markers));
            store.FailingReads.Enqueue(new IOException("synthetic lock on code " + LinkCode)); await shell.ShareLink.LoadSavedAsync(); Capture();
            await shell.ShareLink.LoadSavedAsync(); Capture(); shell.ShareLink.SelectedSaved = Assert.Single(shell.ShareLink.Saved);
            await shell.ShareLink.RemoveSelectedAsync(); Capture(); Assert.Empty(shell.ShareLink.Saved);
            // The client now fails with transport text that quotes the link and the code: once from the box, once from the Find box.
            shell.ShareLink.LinkInput = ShareUrl; await shell.ShareLink.CheckAsync(); Capture(); Assert.False(shell.ShareLink.HasResult);
            shell.ServerBrowser.LinkInput = ShareUrl; await shell.DestinationSearch.SearchOrUseAsync(); Capture();
            Assert.Equal(3, shareClient.Calls); Assert.True(shareClient.UsedLinkedSession); Assert.Equal(2, store.FailedReads); Assert.Empty(directory.Queries);
            // One synthetic launch request for this account gives the launch evidence report something to export.
            var request = DiagnosticOperation(); PublishDiagnosticQueue(shell, request with { Intent = request.Intent with { ProfileId = profile } });
            shell.Navigate.Execute("Diagnostics"); shell.RefreshDiagnostics.Execute(null);
            // An empty log would pass vacuously, so the injected store failures must be visible as typed events.
            Assert.Contains(shell.DiagnosticRows, item => item.Event.Kind == ManagerEventKind.OperationFailed);
            shown.AppendLine(shell.BuildDiagnosticReport()).AppendLine(shell.BuildVisibleDiagnosticText()).AppendLine(shell.BuildNativeTrialEvidenceReport());
            foreach (string line in shell.Events) shown.AppendLine(line);
            Capture();
        }
        finally { await shell.DisposeAsync(); } // Flushes and releases diagnostics, history, settings and the saved list before the scan.
        string[] files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories); string journal = Path.Combine(_root, "diagnostics", "manager.jsonl");
        Assert.Contains(saved, files); Assert.Contains(journal, files);
        // The failures reached the disk journal as typed records, so its clean scan below is not vacuous either.
        Assert.Contains("\"OperationFailed\"", File.ReadAllText(journal), StringComparison.Ordinal);
        Assert.Empty(FilesQuoting(_root, files, markers)); // Names each leaking file relative to the data folder.
        foreach (string forbidden in markers.Append("?code=")) Assert.DoesNotContain(forbidden, shown.ToString(), StringComparison.Ordinal);
    });

    /// <summary>Files (relative to root) whose bytes contain any marker in UTF-8 or UTF-16LE. Files still open elsewhere are read shared.</summary>
    private static string[] FilesQuoting(string root, IEnumerable<string> files, IReadOnlyList<string> markers)
    {
        byte[][] patterns = markers.SelectMany(marker => new[] { Encoding.UTF8.GetBytes(marker), Encoding.Unicode.GetBytes(marker) }).ToArray();
        var hits = new List<string>();
        foreach (string file in files)
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { bytes = new byte[stream.Length]; stream.ReadExactly(bytes); }
            foreach (byte[] pattern in patterns)
                if (new ReadOnlySpan<byte>(bytes).IndexOf(new ReadOnlySpan<byte>(pattern)) >= 0) { hits.Add(Path.GetRelativePath(root, file)); break; }
        }
        return hits.ToArray();
    }
    /// <summary>The scan must be able to fail: each marker planted in either encoding is found, and a clean file is not.</summary>
    private static void AssertCodeScanFindsPlantedMarkers(IReadOnlyList<string> markers)
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-code-scan-guard-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var planted = new List<string>();
            foreach (string marker in markers)
                foreach (var encoding in new Encoding[] { new UTF8Encoding(false), Encoding.Unicode })
                {
                    string file = Path.Combine(folder, planted.Count + ".dat"); File.WriteAllText(file, "{\"note\":\"before " + marker + " after\"}", encoding); planted.Add(file);
                }
            string clean = Path.Combine(folder, "clean.dat"); File.WriteAllText(clean, "…" + markers[0][^4..] + " " + markers[1][..4]);
            Assert.Equal(planted.Select(file => Path.GetFileName(file)), FilesQuoting(folder, planted.Append(clean), markers));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void DirectNoSecrets(ShellViewModel shell)
    { Assert.DoesNotContain(DirectCookie, shell.Notice); Assert.DoesNotContain(shell.Events, item => item.Contains(DirectCookie, StringComparison.Ordinal)); }
    private sealed class DirectShareClient(string code, string link) : IRobloxShareLinkClient
    {
        public int Calls; public bool UsedLinkedSession = true;
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
        // The first check resolves a private server; every later one fails with transport text that quotes the link and the code.
        public Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink shareLink, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        {
            UsedLinkedSession &= credential.CopyValue() == DirectCookie;
            return ++Calls == 1 ? Task.FromResult(new RobloxShareLinkResolution(RobloxShareLinkOutcome.PrivateServer, 5, 6, 7, new ProtectedPrivateServerCode(code)))
                : Task.FromException<RobloxShareLinkResolution>(new System.Net.Http.HttpRequestException("synthetic failure for " + link + " code " + code));
        }
    }
    /// <summary>The real store, plus queued reads that each fail once with the queued exception. Disposed by the shell with the store.</summary>
    private sealed class DirectPrivateServers(PrivateServerStore inner) : IPrivateServerStore, IDisposable
    {
        public PrivateServerStore Inner { get; } = inner;
        public readonly Queue<Exception> FailingReads = new(); public int FailedReads; // Read on the UI thread only, like the shell's own calls.
        public Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct)
        {
            if (!FailingReads.TryDequeue(out var failure)) return Inner.ReadAsync(ct);
            FailedReads++; return Task.FromException<IReadOnlyList<SavedPrivateServer>>(failure);
        }
        public Task<SavedPrivateServer> AddAsync(string name, long placeId, long universeId, long? ownerUserId, string? gameName, ProtectedPrivateServerCode protectedCode, CancellationToken ct)
            => Inner.AddAsync(name, placeId, universeId, ownerUserId, gameName, protectedCode, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => Inner.DeleteAsync(id, revision, ct);
        public Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id, CancellationToken ct) => Inner.ReadCodeAsync(id, ct);
        public void Dispose() => Inner.Dispose();
    }
    private sealed class DirectSignIn : IRobloxSessionSignIn, IAsyncDisposable
    {
        public bool Hold, Expired, DialogCancelled; public int Calls, Disposals; public Guid ProfileId; public CancellationToken Token;
        public RobloxSessionCredential? Returned;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; ProfileId = id; Token = ct; Entered.TrySetResult();
            if (Hold) await _release.Task.ConfigureAwait(false); // intentionally ignore cancel until actual owner completes
            if (DialogCancelled) throw new OperationCanceledException("Synthetic user closed sign-in.");
            return Returned = new(DirectCookie, Expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : null);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class DirectClient : IRobloxSessionClient
    {
        public bool Hold, InvalidIdentity, FailLate; public int Calls; public CancellationToken Token;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.Equal(DirectCookie, credential.CopyValue()); Calls++; Token = ct; Entered.TrySetResult();
            if (Hold) await _release.Task.ConfigureAwait(false);
            if (FailLate) throw new RobloxSessionException(RobloxSessionFailure.Network, DirectCookie);
            return new(new(InvalidIdentity ? 0 : 456, "direct_player", RobloxSessionIdentity.Issuer, "456"));
        }
    }
    private sealed class DirectReloadGate(IAccountRepository inner, IAuthorizationMetadataReader metadata) : IAccountRepository
    {
        public bool Armed, FailInitialization;
        public readonly TaskCompletionSource<AuthorizationMetadata> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        public void Release() => _release.TrySetResult();
        public Task InitializeAsync(CancellationToken ct) => FailInitialization ? Task.FromException(new InvalidDataException("Synthetic startup failure.")) : inner.InitializeAsync(ct);
        public async Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct)
        {
            var page = await inner.ReadAsync(query, ct).ConfigureAwait(false);
            if (Armed && Volatile.Read(ref _used) == 0)
                foreach (var row in page.Items)
                    if (row.RobloxUserId == 456 && await metadata.GetSummaryAsync(row.Id, ct).ConfigureAwait(false) is { Health: AuthorizationHealth.Linked } summary
                        && Interlocked.Exchange(ref _used, 1) == 0)
                    { Entered.TrySetResult(summary); await _release.Task.ConfigureAwait(false); break; }
            return page;
        }
        // The first lookup of HoldFind waits for ReleaseFind and, like an uncooperative store, ignores cancellation until then.
        public Guid HoldFind;
        public readonly TaskCompletionSource FindEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            FindCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _findRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _findHeld;
        public void ReleaseFind() => _findRelease.TrySetResult();
        public async Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct)
        {
            if (id != Guid.Empty && id == HoldFind && Interlocked.Exchange(ref _findHeld, 1) == 0)
            {
                using var stopped = ct.Register(() => FindCancelled.TrySetResult());
                FindEntered.TrySetResult(); await _findRelease.Task.ConfigureAwait(false);
            }
            return await inner.FindAsync(id, ct).ConfigureAwait(false);
        }
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile account, CancellationToken ct) => inner.SaveAsync(account, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => inner.CompleteCredentialCleanupAsync(id, ct);
    }
}
