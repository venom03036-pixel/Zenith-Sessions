using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Controlled STA application/repository integration; no window is shown and no account endpoint is used.</summary>
public sealed class AccountGroupIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-account-group-ui-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task MembershipsSurviveProjectionEditingFavoriteExportAndUnauthenticatedImport() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Source") with
        { Group = "Primary", AdditionalGroups = "Beta\r\nGamma\nprimary\nbeta", Notes = "Original note", PreferredPlaceId = 6564161046 });
        var source = vm.SelectedAccount!.Profile; Assert.Equal("Beta\nGamma", source.AdditionalGroups);
        var summary = new AuthorizationMetadata(source.Id, source.Id.ToString("N"), RobloxSessionIdentity.Provider,
            RobloxSessionIdentity.Issuer, "7654321", 7654321, "group_verified", 1, AuthorizationHealth.Linked,
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        using (var projection = new AccountIdentityProjection(_root, repository, new ProjectionMetadata(summary)))
        {
            await projection.PrepareAsync(summary, default);
            await vm.SaveAccountAsync(source with { Label = "Edited source", Notes = "Retained after projection" });
            await projection.CompleteAsync(source.Id, summary.Generation, default);
        }
        await Refresh(vm);
        var projected = Assert.Single(vm.Accounts).Profile;
        Assert.Equal("group_verified", projected.Username); Assert.Equal(7654321L, projected.RobloxUserId);
        Assert.Equal("Primary", projected.Group); Assert.Equal("Beta\nGamma", projected.AdditionalGroups);
        Assert.Equal("Retained after projection", projected.Notes);
        vm.SelectedAccount = Assert.Single(vm.Accounts);
        vm.ToggleFavorite.Execute(null); await vm.ToggleFavorite.Execution;
        await vm.SaveAccountAsync(vm.SelectedAccount!.Profile with { Label = "Final source" });
        var saved = Assert.Single(vm.Accounts).Profile;
        Assert.True(saved.IsFavorite); Assert.Equal("Beta\nGamma", saved.AdditionalGroups);
        Assert.Equal(new[] { "Primary", "Beta", "Gamma" }, saved.GroupNames);

        string path = Path.Combine(_root, "groups-v4.json"); await vm.ExportAsync(path);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            Assert.Equal(4, json.RootElement.GetProperty("Version").GetInt32());
            var exported = Assert.Single(json.RootElement.GetProperty("Profiles").EnumerateArray());
            Assert.Equal("Beta\nGamma", exported.GetProperty("AdditionalGroups").GetString());
            Assert.False(exported.TryGetProperty("GroupNames", out _));
            Assert.False(exported.TryGetProperty("GroupsSummary", out _));
            foreach (string secret in new[] { "SessionCookie", "AccessToken", "RefreshToken", "CredentialRef", "AuthorizationGeneration" })
                Assert.False(exported.TryGetProperty(secret, out _));
        }
        // Re-importing a row this library already has (same name and username) is now skipped, so rename
        // the source first to keep exercising the unauthenticated copy of every exported field.
        await vm.SaveAccountAsync(Assert.Single(vm.Accounts).Profile with { Label = "Renamed source" });
        await vm.ImportAsync(path);
        Assert.Equal(2, vm.AccountCount);
        var imported = Assert.Single(vm.Accounts.Where(row => row.Profile.Id != source.Id)).Profile;
        Assert.NotEqual(Guid.Empty, imported.Id); Assert.Null(imported.RobloxUserId);
        Assert.Equal("Primary", imported.Group); Assert.Equal("Beta\nGamma", imported.AdditionalGroups);
        Assert.Equal("group_verified", imported.Username); Assert.Equal("Final source", imported.Label);
        Assert.True(imported.IsFavorite); Assert.Equal("Retained after projection", imported.Notes);
        Assert.Equal(6564161046L, imported.PreferredPlaceId);
        Assert.Equal(saved.RobloxUserId, (await repository.FindAsync(source.Id, default))!.RobloxUserId);
        await AssertNoAuthorizationOrQueue(vm, source.Id, imported.Id);
    });

    [Fact]
    public Task ActualGroupFilterMatchesPrimaryAndExtraMembershipsExactly() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("First") with { Group = "Alpha", AdditionalGroups = "Shared\nTeam" });
        Guid first = vm.SelectedAccount!.Profile.Id;
        await vm.SaveAccountAsync(AccountProfile.Create("Second") with { Group = "Shared", AdditionalGroups = "AlphaBeta" });
        Guid second = vm.SelectedAccount!.Profile.Id;
        await vm.SaveAccountAsync(AccountProfile.Create("Third") with { Group = "Other", AdditionalGroups = "ALPHA\nsharedish" });
        Guid third = vm.SelectedAccount!.Profile.Id;
        Assert.Equal(1, vm.Groups.Count(group => string.Equals(group.Value, "alpha", StringComparison.OrdinalIgnoreCase)));
        vm.Group = vm.Groups.Single(group => string.Equals(group.Value, "alpha", StringComparison.OrdinalIgnoreCase));
        await Refresh(vm); Assert.Equal(new[] { first, third }.Order(), vm.Accounts.Select(row => row.Profile.Id).Order());
        vm.Group = vm.Groups.Single(group => group.Value == "Shared");
        await Refresh(vm); Assert.Equal(new[] { first, second }.Order(), vm.Accounts.Select(row => row.Profile.Id).Order());
        vm.Group = vm.Groups.Single(group => group.Value == "sharedish");
        await Refresh(vm); Assert.Equal(third, Assert.Single(vm.Accounts).Profile.Id);
        var lowerCase = await repository.ReadAsync(new(Group: "alpha"), default);
        Assert.Equal(new[] { first, third }.Order(), lowerCase.Items.Select(profile => profile.Id).Order());
        Assert.Empty((await repository.ReadAsync(new(Group: "Alph"), default)).Items);
        Assert.Equal(3, vm.AccountCount); Assert.Empty(vm.Launches);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReloadRetainsTheSelectedGroupWhenItsOnlyMembershipChangesDisplayCase(bool extraMembership) => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Case-changing membership") with
        { Group = extraMembership ? "Primary" : "Raid", AdditionalGroups = extraMembership ? "Raid" : "" });
        var original = vm.SelectedAccount!.Profile;
        vm.Group = vm.Groups.Single(group => group.Value == "Raid"); await Refresh(vm);
        Assert.Equal("Raid", vm.Group.Value); Assert.Equal(original.Id, Assert.Single(vm.Accounts).Profile.Id);
        await repository.Inner.SaveAsync(original with
        { Group = extraMembership ? "Primary" : "raid", AdditionalGroups = extraMembership ? "raid" : "" }, default);
        await Refresh(vm);
        Assert.Equal("raid", vm.Group.Value); Assert.NotSame(GroupFilter.All, vm.Group);
        Assert.Equal(original.Id, Assert.Single(vm.Accounts).Profile.Id);
        Assert.DoesNotContain(vm.Groups, group => group.Value == "Raid"); Assert.Empty(vm.Launches);
    });

    [Fact]
    public Task BulkAddPreservesPrimaryOrderAndNoopRevisionsWithoutAuthorizationOrQueue() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Already a member") with { Group = "Primary A", AdditionalGroups = "Earlier\nShared" });
        var already = vm.SelectedAccount!.Profile;
        await vm.SaveAccountAsync(AccountProfile.Create("New member") with { Group = "Primary B", AdditionalGroups = "Earlier\nLater" });
        var added = vm.SelectedAccount!.Profile;
        await vm.SaveAccountAsync(AccountProfile.Create("Unselected") with { Group = "Primary C" });
        var untouched = vm.SelectedAccount!.Profile;
        await vm.AddAccountsToGroupAsync([already, added], " Shared ");
        Assert.Contains("Added 1 account(s)", vm.Notice);
        var currentAlready = (await repository.FindAsync(already.Id, default))!;
        var currentAdded = (await repository.FindAsync(added.Id, default))!;
        Assert.Equal(already, currentAlready); Assert.Equal("Primary B", currentAdded.Group);
        Assert.Equal("Earlier\nLater\nShared", currentAdded.AdditionalGroups); Assert.Equal(added.Revision + 1, currentAdded.Revision);
        Assert.Equal(untouched, await repository.FindAsync(untouched.Id, default));
        await vm.AddAccountsToGroupAsync([currentAlready, currentAdded], "shared");
        Assert.Contains("Nothing changed", vm.Notice);
        Assert.Equal(currentAlready, await repository.FindAsync(already.Id, default));
        Assert.Equal(currentAdded, await repository.FindAsync(added.Id, default));
        await AssertNoAuthorizationOrQueue(vm, already.Id, added.Id, untouched.Id);
    });

    [Fact]
    public Task StaleNoopRecipientRollsBackTheWholeActualBulkChange() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Would change")); var candidate = vm.SelectedAccount!.Profile;
        await vm.SaveAccountAsync(AccountProfile.Create("Already belongs") with { AdditionalGroups = "Target" });
        var staleNoop = vm.SelectedAccount!.Profile;
        var independent = await repository.Inner.SaveAsync(staleNoop with { Notes = "Independent revision" }, default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => vm.AddAccountsToGroupAsync([candidate, staleNoop], "Target"));
        Assert.Equal(candidate, await repository.FindAsync(candidate.Id, default));
        Assert.Equal(independent, await repository.FindAsync(staleNoop.Id, default));
        Assert.False(AccountGroups.Contains((await repository.FindAsync(candidate.Id, default))!, "Target"));
        await AssertNoAuthorizationOrQueue(vm, candidate.Id, staleNoop.Id);
    });

    [Fact]
    public Task ChangingCallerArrayAndCheckedSelectionCannotRetargetThePinnedGroupChange() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Pinned recipient")); var pinned = vm.SelectedAccount!.Profile;
        await vm.SaveAccountAsync(AccountProfile.Create("Later selection")); var later = vm.SelectedAccount!.Profile;
        vm.Accounts.Single(row => row.Profile.Id == pinned.Id).IsChecked = true;
        AccountProfile[] recipients = vm.Accounts.Where(row => row.IsChecked).Select(row => row.Profile).ToArray();
        repository.ReleaseGroup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = vm.AddAccountsToGroupAsync(recipients, " Frozen group ");
        recipients[0] = later;
        try
        {
            await repository.GroupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (var row in vm.Accounts) row.IsChecked = row.Profile.Id == later.Id;
            vm.SelectedAccount = vm.Accounts.Single(row => row.Profile.Id == later.Id);
            Assert.Equal(pinned.Id, Assert.Single(repository.RequestedProfiles).Id);
            Assert.Equal(pinned.Revision, Assert.Single(repository.RequestedProfiles).Revision);
            Assert.Equal("Frozen group", repository.RequestedGroup);
        }
        finally { repository.ReleaseGroup.TrySetResult(); await operation; }
        Assert.True(AccountGroups.Contains((await repository.FindAsync(pinned.Id, default))!, "Frozen group"));
        Assert.False(AccountGroups.Contains((await repository.FindAsync(later.Id, default))!, "Frozen group"));
        Assert.Equal(1, repository.GroupCalls); await AssertNoAuthorizationOrQueue(vm, pinned.Id, later.Id);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LockAndPanicCancelPendingGroupEditsAndKeepTheStopNotice(bool locked) => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Pending group recipient")); var before = vm.SelectedAccount!.Profile;
        repository.ReleaseGroup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = vm.AddAccountsToGroupAsync([before], "Cancelled group");
        try
        {
            await repository.GroupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (locked) vm.LockDashboard(); else vm.Panic.Execute(null);
            Assert.True(repository.GroupToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { repository.ReleaseGroup.TrySetResult(); await ObserveStopped(operation); }
        Assert.Equal(before, await repository.FindAsync(before.Id, default));
        Assert.Contains(locked ? "Controls locked" : "Panic stop", vm.Notice);
        await AssertNoAuthorizationOrQueue(vm, before.Id);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LateCompletionAfterACommitReloadsMembershipWithoutReplacingStopNotice(bool locked) => OnUi(async () =>
    {
        var repository = new GroupRepository(_root) { CommitBeforeRelease = true };
        await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Committed group recipient")); var before = vm.SelectedAccount!.Profile;
        repository.ReleaseGroup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = vm.AddAccountsToGroupAsync([before], "Committed group");
        try
        {
            await repository.GroupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(AccountGroups.Contains((await repository.FindAsync(before.Id, default))!, "Committed group"));
            if (locked) vm.LockDashboard(); else vm.Panic.Execute(null);
            Assert.True(repository.GroupToken.IsCancellationRequested); Assert.False(operation.IsCompleted);
        }
        finally { repository.ReleaseGroup.TrySetResult(); await operation; }
        Assert.True(AccountGroups.Contains(Assert.Single(vm.Accounts).Profile, "Committed group"));
        Assert.Contains(locked ? "Controls locked" : "Panic stop", vm.Notice);
        await AssertNoAuthorizationOrQueue(vm, before.Id);
    });

    [Fact]
    public Task StartupUnavailableLockedAndUnsupportedLibrariesRejectNewGroupWork() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root) { FailInitialization = true };
        await using (var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: repository))
        {
            var profile = AccountProfile.Create("Uncommitted synthetic profile");
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.AddAccountsToGroupAsync([profile], "Group"));
            await vm.InitializeAsync(); Assert.False(vm.IsReady);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.AddAccountsToGroupAsync([profile], "Group"));
            Assert.Equal(0, repository.GroupCalls); Assert.Empty(vm.Launches);
        }
        string liveRoot = Path.Combine(_root, "locked"); var live = new GroupRepository(liveRoot);
        await using (var vm = await Ready(live, liveRoot))
        {
            await vm.SaveAccountAsync(AccountProfile.Create("Locked recipient")); var profile = vm.SelectedAccount!.Profile;
            vm.LockDashboard();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.AddAccountsToGroupAsync([profile], "Group"));
            Assert.Equal(0, live.GroupCalls); Assert.Equal(profile, await live.FindAsync(profile.Id, default));
        }
        string unsupportedRoot = Path.Combine(_root, "unsupported"); var unsupported = new RepositoryAdapter(unsupportedRoot);
        await using (var vm = await Ready(unsupported, unsupportedRoot))
        {
            await vm.SaveAccountAsync(AccountProfile.Create("Unsupported recipient"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.AddAccountsToGroupAsync([vm.SelectedAccount!.Profile], "Group"));
            Assert.Empty(vm.Launches);
        }
    });

    [Theory]
    [InlineData(1, "Unexpected extra")]
    [InlineData(2, "Unexpected extra")]
    [InlineData(1, " ")]
    [InlineData(2, " ")]
    public Task LegacyExportsCarryingExtraGroupsAreRejectedBeforeAnyProfileIsImported(int version, string extras) => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        await vm.SaveAccountAsync(AccountProfile.Create("Existing library") with { AdditionalGroups = "Keep" });
        var existing = Assert.Single(vm.Accounts).Profile;
        string path = Path.Combine(_root, "legacy-extra-groups.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = version, Profiles = new[] {
            LegacyMetadata("First otherwise-valid imported row", ""), LegacyMetadata("Unsupported memberships", extras) } }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => vm.ImportAsync(path));
        Assert.Contains("version 3", error.Message);
        Assert.Equal(existing, Assert.Single((await repository.ReadAsync(new(), default)).Items));
        Assert.Equal(existing, Assert.Single(vm.Accounts).Profile); Assert.Equal(1, vm.AccountCount);
        await AssertNoAuthorizationOrQueue(vm, existing.Id);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task LegacyExportsWithAnExplicitEmptyAdditionalGroupsFieldRemainCompatible(int version) => OnUi(async () =>
    {
        var repository = new GroupRepository(_root); await using var vm = await Ready(repository);
        string path = Path.Combine(_root, "legacy-empty-extra-groups.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = version, Profiles = new[] { LegacyMetadata("Compatible legacy profile", "") } }));
        await vm.ImportAsync(path);
        var imported = Assert.Single(vm.Accounts).Profile;
        Assert.Equal("Compatible legacy profile", imported.Label); Assert.Empty(imported.AdditionalGroups);
        Assert.Equal(new[] { "General" }, imported.GroupNames); Assert.Null(imported.RobloxUserId);
        await AssertNoAuthorizationOrQueue(vm, imported.Id);
    });

    [Fact]
    public Task MovingAnExportToAnotherLibraryKeepsSavedGroupsColorsOrderEmptyGroupsAndFavorites() => OnUi(async () =>
    {
        string path = await ExportMovedLibrary();
        string root = Path.Combine(_root, "destination"); var destination = new AccountRepository(root);
        await using var vm = await Ready(destination, root);
        await vm.ImportAsync(path);
        // Creation order, not alphabetical order; "Empty" has no accounts in the source library.
        Assert.Equal(new (string, string?)[] { ("Zeta", "#FF0000"), ("Alpha", null), ("Empty", "#00FF00") }, await CatalogNames(destination));
        Assert.Equal(2, vm.AccountCount);
        var main = Assert.Single(vm.Accounts, row => row.Profile.Label == "Main").Profile;
        Assert.True(main.IsFavorite); Assert.Equal("Zeta", main.Group); Assert.Null(main.RobloxUserId);
        Assert.Contains("Imported 2 profiles", vm.Notice);
        await AssertNoAuthorizationOrQueue(vm, vm.Accounts.Select(row => row.Profile.Id).ToArray());
    });

    [Fact]
    public Task ImportingTheSameExportTwiceSkipsProfilesAlreadyInTheLibraryAndKeepsExistingGroupColors() => OnUi(async () =>
    {
        string path = await ExportMovedLibrary();
        string root = Path.Combine(_root, "destination"); var destination = new AccountRepository(root);
        await using var vm = await Ready(destination, root);
        IAccountGroupCatalog catalog = destination;
        await catalog.CreateGroupAsync("ALPHA", "#ABCDEF", (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision, default);
        await vm.ImportAsync(path);
        await vm.ImportAsync(path);
        Assert.Equal(2, vm.AccountCount);
        Assert.Equal(new[] { "Alt", "Main" }, vm.Accounts.Select(row => row.Profile.Label).Order());
        Assert.Contains("Imported 0 profiles", vm.Notice); Assert.Contains("Skipped 2 already in this library", vm.Notice);
        // An existing group keeps this library's color and position; missing groups follow it in their exported order.
        Assert.Equal(new (string, string?)[] { ("ALPHA", "#ABCDEF"), ("Zeta", "#FF0000"), ("Empty", "#00FF00") }, await CatalogNames(destination));
    });

    [Fact]
    public Task ImportAddsEveryMissingSavedGroupAndProfileAsOneCatalogChange() => OnUi(async () =>
    {
        string path = await ExportMovedLibrary();
        string root = Path.Combine(_root, "destination"); var destination = new AccountRepository(root);
        await using var vm = await Ready(destination, root);
        IAccountGroupCatalog catalog = destination;
        long before = (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision;
        await vm.ImportAsync(path);
        Assert.Equal(3, (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).Total); Assert.Equal(2, vm.AccountCount);
        // One transaction: three new saved groups and two profiles advance the catalog once, not once per group.
        Assert.Equal(before + 1, (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AnImportThatFailsPartWayAddsNoSavedGroupOrProfile(bool laterGroupFails) => OnUi(async () =>
    {
        var repository = new AccountRepository(_root); await using var vm = await Ready(repository, _root);
        await vm.SaveAccountAsync(AccountProfile.Create("Existing") with { Group = "Edge" });
        var existing = Assert.Single(vm.Accounts).Profile;
        // Only one more saved group fits after "Edge"; the next group created cannot get a list position.
        await SetSortOrder((await repository.FindGroupAsync("Edge", default))!.Id, long.MaxValue - 1);
        IAccountGroupCatalog catalog = repository;
        var before = await CatalogNames(catalog); long revision = (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision;
        var groups = new List<object> { new { Name = "First new", ColorHex = "#112233", SortOrder = 0 } };
        // Either a later saved group fails, or the profile's own membership group fails after the saved groups.
        if (laterGroupFails) groups.Add(new { Name = "Second new", ColorHex = (string?)null, SortOrder = 1 });
        string path = Path.Combine(_root, "fails-part-way.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 4, Groups = groups, Profiles = new[] { new
        {
            Id = Guid.NewGuid(), Label = "Imported", Username = "", Group = laterGroupFails ? "Edge" : "Not saved yet", Priority = 0, UpdatedUtc = DateTimeOffset.UtcNow
        } } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => vm.ImportAsync(path));
        Assert.Equal(before, await CatalogNames(catalog));
        Assert.Equal(revision, (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision);
        Assert.Equal(existing, Assert.Single((await repository.ReadAsync(new(), default)).Items));
    });
    private async Task SetSortOrder(Guid group, long order)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "accounts.db"), Pooling = false }.ToString());
        await db.OpenAsync(); db.CreateCollation("ZENITH_GROUP", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        using var command = db.CreateCommand(); command.CommandText = "UPDATE account_groups SET sort_order=$order WHERE id=$id";
        command.Parameters.AddWithValue("$order", order); command.Parameters.AddWithValue("$id", group.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<string> ExportMovedLibrary()
    {
        string root = Path.Combine(_root, "source"), path = Path.Combine(_root, "moved-library.json");
        var repository = new AccountRepository(root);
        await using var vm = await Ready(repository, root);
        IAccountGroupCatalog catalog = repository;
        foreach (var (name, color) in new (string, string?)[] { ("Zeta", "#ff0000"), ("Alpha", null), ("Empty", "#00FF00") })
            await catalog.CreateGroupAsync(name, color, (await catalog.ReadGroupCatalogAsync(new(PageSize: 1), default)).CatalogRevision, default);
        await vm.SaveAccountAsync(AccountProfile.Create("Main") with { Username = "MainUser", Group = "Zeta", IsFavorite = true });
        await vm.SaveAccountAsync(AccountProfile.Create("Alt") with { Username = "AltUser", Group = "Alpha", AdditionalGroups = "Zeta" });
        await vm.ExportAsync(path);
        return path;
    }
    private static async Task<(string, string?)[]> CatalogNames(IAccountGroupCatalog catalog)
        => (await catalog.ReadGroupCatalogAsync(new(PageSize: 1000), default)).Items.Select(item => (item.Name, item.ColorHex)).ToArray();

    [Fact]
    public Task DisposeJoinsTheActualLateGroupOwnerBeforeDisposingItsRepository() => OnUi(async () =>
    {
        var repository = new GroupRepository(_root) { CommitBeforeRelease = true };
        var vm = await Ready(repository); Task? disposing = null; Task operation = Task.CompletedTask;
        try
        {
            await vm.SaveAccountAsync(AccountProfile.Create("Exit recipient"));
            repository.ReleaseGroup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = vm.AddAccountsToGroupAsync([vm.SelectedAccount!.Profile], "Committed before exit");
            await repository.GroupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            disposing = vm.DisposeAsync().AsTask();
            Assert.True(repository.GroupToken.IsCancellationRequested);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(disposing.IsCompleted); Assert.False(repository.Disposed); Assert.False(operation.IsCompleted);
        }
        finally
        {
            repository.ReleaseGroup?.TrySetResult();
            await ObserveStopped(operation);
            await (disposing ?? vm.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(repository.Disposed); Assert.True(operation.IsCompleted);
    });

    private async Task<ShellViewModel> Ready(IAccountRepository repository, string? root = null)
    {
        var vm = new ShellViewModel(root ?? _root, false, Dispatcher.CurrentDispatcher, new Monitor(), accounts: repository);
        try { await vm.InitializeAsync(); Assert.True(vm.IsReady); return vm; }
        catch { await vm.DisposeAsync(); throw; }
    }
    private static async Task Refresh(ShellViewModel vm)
    { vm.Refresh.Execute(null); await vm.Refresh.Execution; }
    private async Task AssertNoAuthorizationOrQueue(ShellViewModel vm, params Guid[] ids)
    {
        Assert.Empty(vm.Launches);
        using var vault = new ProtectedCredentialVault(vm.DataRoot);
        foreach (Guid id in ids) Assert.Null(await vault.GetSummaryAsync(id, default));
        Assert.All(vm.Accounts, row => Assert.Equal("Not linked", row.Identity));
    }
    private static async Task ObserveStopped(Task operation)
    { try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    private static object LegacyMetadata(string label, string extras) => new
    {
        Id = Guid.NewGuid(), Label = label, Username = "", Group = "General", Priority = 0,
        UpdatedUtc = DateTimeOffset.UtcNow, AdditionalGroups = extras
    };

    private class RepositoryAdapter(string root) : IAccountRepository, IDisposable
    {
        public AccountRepository Inner { get; } = new(root);
        public bool FailInitialization, Disposed;
        public Task InitializeAsync(CancellationToken ct) => FailInitialization ? Task.FromException(new IOException("Synthetic unavailable library")) : Inner.InitializeAsync(ct);
        public Task<AccountProfile?> FindAsync(Guid id, CancellationToken ct) => Inner.FindAsync(id, ct);
        public Task<AccountPage> ReadAsync(AccountQuery query, CancellationToken ct) => Inner.ReadAsync(query, ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct) => Inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile, CancellationToken ct) => Inner.SaveAsync(profile, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct) => Inner.DeleteAsync(id, revision, ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.CreateManyAsync(profiles, ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken ct) => Inner.DeleteManyAsync(profiles, ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct) => Inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id, CancellationToken ct) => Inner.CompleteCredentialCleanupAsync(id, ct);
        public void Dispose() { if (Disposed) return; Disposed = true; Inner.Dispose(); }
    }
    private sealed class GroupRepository(string root) : RepositoryAdapter(root), IAccountGroupEditor
    {
        public TaskCompletionSource? ReleaseGroup;
        public TaskCompletionSource GroupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AccountProfile> RequestedProfiles = [];
        public string? RequestedGroup;
        public CancellationToken GroupToken;
        public int GroupCalls;
        public bool CommitBeforeRelease;
        public async Task<int> ApplyGroupAsync(IReadOnlyList<AccountProfile> expected, string group, CancellationToken ct)
        {
            RequestedProfiles = expected; RequestedGroup = group; GroupToken = ct; GroupCalls++;
            int changed = 0;
            if (CommitBeforeRelease) changed = await Inner.ApplyGroupAsync(expected, group, ct);
            GroupStarted.TrySetResult();
            if (ReleaseGroup is not null)
            {
                if (CommitBeforeRelease) await ReleaseGroup.Task; // Actual commit completed; its late callback remains owned.
                else await ReleaseGroup.Task.WaitAsync(ct);
            }
            return CommitBeforeRelease ? changed : await Inner.ApplyGroupAsync(expected, group, ct);
        }
    }
    private sealed class ProjectionMetadata(AuthorizationMetadata summary) : IAuthorizationMetadataReader
    {
        public Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct) => Task.FromResult<AuthorizationMetadata?>(id == summary.ProfileId ? summary : null);
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new InvalidOperationException("Unexpected inventory read by selected-profile projection");
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) => throw new InvalidOperationException("Unexpected identity search by projection");
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = Run());
            async Task Run()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-account-group-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected group fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
