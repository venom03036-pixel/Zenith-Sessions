using System.Windows.Threading;
using Zenith.MacroHost.Contracts;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task MacroAssignmentUsesTheExplicitSavedAccountAndSurvivesManagerRestartWithoutRunning() => OnUi(async () =>
    {
        Guid accountId;
        var host = new AssignmentHost();
        await using (var shell = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor(), macroCatalogClient: host))
        {
            await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
            shell.SelectedAccount = shell.Accounts[0]; accountId = shell.SelectedAccount.Profile.Id;
            await shell.MacroHostCatalog.RefreshAsync(); shell.MacroHostCatalog.SelectedMacro = Assert.Single(shell.MacroHostCatalog.Macros);
            await shell.MacroAssignments.RefreshAsync(); shell.MacroAssignments.ChooseTarget(MacroAssignmentTargetKind.Account);
            // The explicit target remains pinned when the visible selection changes.
            shell.SelectedAccount = shell.Accounts[1];
            await shell.MacroAssignments.AssignAsync();
            var saved = Assert.Single(shell.MacroAssignments.Assignments);
            Assert.Equal(accountId, saved.Target.Id); Assert.Equal(MacroAssignmentTargetKind.Account, saved.Target.Kind);
            Assert.Equal(host.Macro.Id, saved.MacroId); Assert.Equal(2, host.Calls); Assert.Empty(shell.Launches);
        }
        await using (var reopened = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor()))
        {
            await reopened.InitializeAsync(); await reopened.MacroAssignments.RefreshAsync();
            var restored = Assert.Single(reopened.MacroAssignments.Assignments);
            Assert.Equal(accountId, restored.Target.Id); Assert.Equal(host.Macro.Id, restored.MacroId);
            Assert.Empty(reopened.Launches); Assert.Empty(reopened.MacroHostCatalog.Macros);
            Assert.False(reopened.MacroAssignments.CanAssign); // Nothing connects/runs on restoration.
        }
    });

    [Fact]
    public Task MacroAssignmentTargetsUseSavedGroupAndBundleIdentitiesRatherThanNamesOrCopiedMembers() => OnUi(async () =>
    {
        var host = new AssignmentHost();
        await using var shell = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor(), macroCatalogClient: host);
        await shell.InitializeAsync(); await shell.MacroHostCatalog.RefreshAsync();
        shell.MacroHostCatalog.SelectedMacro = Assert.Single(shell.MacroHostCatalog.Macros);
        await shell.MacroAssignments.RefreshAsync();
        var group = shell.GroupCatalogChoices.First(); await shell.ShowGroupAccountsAsync(group.Id, default);
        shell.MacroAssignments.ChooseTarget(MacroAssignmentTargetKind.Group); await shell.MacroAssignments.AssignAsync();
        var grouped = Assert.Single(shell.MacroAssignments.Assignments);
        Assert.Equal(new(MacroAssignmentTargetKind.Group, group.Id), grouped.Target);
        shell.Accounts[0].IsChecked = true; await shell.RecipientBundles.SaveCheckedAsync("Assignment fixture bundle");
        var bundle = shell.RecipientBundles.SelectedBundle!;
        shell.MacroAssignments.ChooseTarget(MacroAssignmentTargetKind.Bundle); await shell.MacroAssignments.AssignAsync();
        Assert.Equal(2, shell.MacroAssignments.Assignments.Count);
        Assert.Contains(shell.MacroAssignments.Assignments, value => value.Target == new MacroAssignmentTarget(MacroAssignmentTargetKind.Bundle, bundle.Id));
        Assert.All(shell.MacroAssignments.Assignments, value => Assert.Equal(host.Macro.Id, value.MacroId));
        Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task ADeletedSelectedAccountCannotBeReplacedByAnotherWithTheSameLabel() => OnUi(async () =>
    {
        var host = new AssignmentHost();
        await using var shell = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor(), macroCatalogClient: host);
        await shell.InitializeAsync(); shell.SelectedAccount = shell.Accounts[0]; var original = shell.SelectedAccount.Profile;
        await shell.MacroHostCatalog.RefreshAsync(); shell.MacroHostCatalog.SelectedMacro = Assert.Single(shell.MacroHostCatalog.Macros);
        await shell.MacroAssignments.RefreshAsync(); shell.MacroAssignments.ChooseTarget(MacroAssignmentTargetKind.Account);
        using var repository = new AccountRepository(_root);
        await repository.DeleteAsync(original.Id, original.Revision, default);
        var replacement = await repository.SaveAsync(AccountProfile.Create(original.Label), default);
        await shell.MacroAssignments.AssignAsync();
        Assert.Empty(shell.MacroAssignments.Assignments); Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(1, host.Calls); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task AssignmentRemovalRequiresConfirmationAndLockLeavesNoExecutionRoute() => OnUi(async () =>
    {
        var host = new AssignmentHost();
        await using var shell = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor(), macroCatalogClient: host);
        await shell.InitializeAsync(); shell.SelectedAccount = shell.Accounts[0];
        await shell.MacroHostCatalog.RefreshAsync(); shell.MacroHostCatalog.SelectedMacro = Assert.Single(shell.MacroHostCatalog.Macros);
        await shell.MacroAssignments.RefreshAsync(); shell.MacroAssignments.ChooseTarget(MacroAssignmentTargetKind.Account);
        await shell.MacroAssignments.AssignAsync(); shell.MacroAssignments.SelectedAssignment = Assert.Single(shell.MacroAssignments.Assignments);
        await shell.MacroAssignments.RemoveAsync(); Assert.Single(shell.MacroAssignments.Assignments);
        shell.MacroAssignments.ConfirmRemoval = true; await shell.MacroAssignments.RemoveAsync(); Assert.Empty(shell.MacroAssignments.Assignments);
        shell.LockDashboard(); Assert.False(shell.MacroAssignments.CanManage); Assert.False(shell.MacroAssignments.CanAssign);
        await shell.MacroAssignments.RefreshAsync(); Assert.Empty(shell.Launches);
    });

    private sealed class AssignmentHost : IZenithMacroCatalogClient
    {
        private readonly SavedMacroCatalog _catalog = new("2.5.12-assignment-fixture");
        public SavedMacroDescriptor Macro { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "Reusable owned macro", "Ctrl+Shift+9", 3, true, "Screen");
        public int Calls;
        public AssignmentHost() { Assert.True(_catalog.CompleteLoad(_catalog.BeginLoad(), new[] { Macro })); }
        public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default) { Calls++; return _catalog.GetPageAsync(query, ct); }
    }
}
