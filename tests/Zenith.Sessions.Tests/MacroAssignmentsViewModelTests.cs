using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.MacroHost.Contracts;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Synthetic targets, host metadata and owned in-memory persistence, plus one owned temporary bundle folder; no pipe, vault, capture or input.</summary>
public sealed class MacroAssignmentsViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MacroAssignmentTarget Target(int index, MacroAssignmentTargetKind kind = MacroAssignmentTargetKind.Account)
        => new(kind, new Guid(index + 1, 0, 0, new byte[8]));
    private static SavedMacroDescriptor Macro(int index) => new(new Guid(index + 1001, 0, 0, new byte[8]),
        new Guid(index + 2001, 0, 0, new byte[8]), "Saved macro " + index, "F4", 2, true, "Screen");
    private static MacroAssignment Row(int index) => new(Target(index), "Target " + index, Macro(0).Id, Macro(0).Name);

    [Fact]
    public async Task ConstructionAndSelectionDoNotReadStorageOrResolveTargets()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0, f.Store.Reads); Assert.False(f.Vm.CanAssign); Assert.Empty(f.Vm.Assignments);
        f.Vm.ChooseAccount.Execute(null); Assert.Equal(0, f.ResolveCalls); Assert.Equal(0, f.Store.Reads);
        Assert.Equal(f.Choice, f.Vm.ChosenTarget); Assert.Contains("Account", f.Vm.TargetSummary);
        await f.Vm.RefreshAsync(); Assert.True(f.Vm.CanAssign); Assert.Equal(1, f.Store.Reads);
    }

    [Theory]
    [InlineData(MacroAssignmentTargetKind.Account)]
    [InlineData(MacroAssignmentTargetKind.Group)]
    [InlineData(MacroAssignmentTargetKind.Bundle)]
    public async Task AssignPersistsStableTargetAndMacroIdsWithResolvedNameOnly(MacroAssignmentTargetKind kind)
    {
        await using var f = await Fixture.Create();
        f.Choice = new(Target(5, kind), "Old hint"); f.Resolve = (_, _) => Task.FromResult<string?>("Current saved target");
        await f.Vm.RefreshAsync(); f.Vm.ChooseTarget(kind); await f.Vm.AssignAsync();
        var saved = Assert.Single(f.Store.Rows);
        Assert.Equal(f.Choice.Target, saved.Target); Assert.Equal("Current saved target", saved.TargetName);
        Assert.Equal(Macro(0).Id, saved.MacroId); Assert.Equal(Macro(0).Name, saved.MacroName);
        Assert.Equal(2, f.Client.Calls); Assert.Equal(1, f.Store.Saves); Assert.Equal(0, f.Store.Removes);
        Assert.False(f.Vm.IsStale); Assert.Contains("saved", f.Vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaterTargetAndHostSelectionsCannotRedirectAnAlreadyCapturedAssignment()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var original = f.Choice!; var entered = Gate(); var release = Gate();
        f.Resolve = async (_, _) => { entered.TrySetResult(); await release.Task; return "Resolved original"; };
        var assigning = f.Vm.AssignAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            f.Choice = new(Target(99, MacroAssignmentTargetKind.Bundle), "Different bundle"); f.Vm.ChooseBundle.Execute(null);
            f.Host.SelectedMacro = f.Host.Macros[1];
        }
        finally { release.TrySetResult(); }
        await assigning.WaitAsync(Deadline);
        var saved = Assert.Single(f.Store.Rows); Assert.Equal(original.Target, saved.Target); Assert.Equal(Macro(0).Id, saved.MacroId);
        Assert.Equal(f.Choice, f.Vm.ChosenTarget); Assert.Equal(Macro(1).Id, f.Host.SelectedMacro!.Id);
    }

    [Fact]
    public async Task MissingTargetKeepsSavedReferencesAndDoesNotWriteOrRevalidateHost()
    {
        await using var f = await Fixture.Create(Row(0)); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        f.Resolve = (_, _) => Task.FromResult<string?>(null); int calls = f.Client.Calls;
        await f.Vm.AssignAsync(); Assert.Equal(0, f.Store.Saves); Assert.Equal(calls, f.Client.Calls);
        Assert.Single(f.Vm.Assignments); Assert.Single(f.Store.Rows); Assert.Contains("no longer exists", f.Vm.Status);
    }

    [Fact]
    public async Task CredentialLikeTargetNameIsExplainedAtCaptureAndNotCaptured()
    {
        await using var f = await Fixture.Create(); f.Choice = new(Target(0), "Roblox: main");
        await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        Assert.Null(f.Vm.ChosenTarget); Assert.False(f.Vm.CanAssign);
        Assert.Contains("link, access code or credential", f.Vm.Status); Assert.Contains("Rename", f.Vm.Status);
        Assert.DoesNotContain("Roblox: main", f.Vm.Status); Assert.DoesNotContain("Roblox: main", f.Vm.TargetSummary);
        Assert.Empty(f.Failures); Assert.Equal(0, f.ResolveCalls);
    }

    [Fact]
    public async Task RenamedTargetWithCredentialLikeNameIsRejectedWithSpecificStatusAndNoWrite()
    {
        await using var f = await Fixture.Create(Row(1)); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        f.Resolve = (_, _) => Task.FromResult<string?>("Roblox: main"); int hostCalls = f.Client.Calls; var rows = f.Vm.Assignments;
        await f.Vm.AssignAsync();
        Assert.Equal(0, f.Store.Saves); Assert.Equal(hostCalls, f.Client.Calls); Assert.Same(rows, f.Vm.Assignments);
        Assert.Contains("target", f.Vm.Status); Assert.Contains("link, access code or credential", f.Vm.Status);
        Assert.DoesNotContain("could not be confirmed", f.Vm.Status); Assert.DoesNotContain("Roblox: main", f.Vm.Status);
        Assert.False(f.Vm.IsStale); Assert.True(f.Vm.CanAssign); Assert.Empty(f.Failures);
        f.Resolve = (_, _) => Task.FromResult<string?>("Main account"); await f.Vm.AssignAsync();
        Assert.Equal(1, f.Store.Saves); Assert.Contains(f.Store.Rows, row => row.TargetName == "Main account");
    }

    [Fact]
    public async Task OlderBundleSavedWithCodeMarkerIsRefusedAtChooseAndAssignThenSavedOnceRenamed()
    {
        // Bundle names could contain "code=" before the shared name rule, and the real store still loads such a bundle.
        string root = Path.Combine(Path.GetTempPath(), "zenith-assignment-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var older = new RecipientBundle(Guid.NewGuid(), "Promo code=1", [Guid.NewGuid()], 1, Guid.NewGuid());
            await File.WriteAllTextAsync(Path.Combine(root, "recipient-bundles.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Revision = 1,
                Items = new[] { new { older.Id, older.Name, older.ProfileIds, older.Revision, older.ChangeId } }
            }));
            using var bundles = new RecipientBundleStore(root);
            var saved = Assert.Single((await bundles.LoadAsync(default)).Bundles); Assert.Equal("Promo code=1", saved.Name);
            var target = new MacroAssignmentTarget(MacroAssignmentTargetKind.Bundle, saved.Id);
            await using var f = await Fixture.Create();
            // Resolved as the shell does: the bundle's current saved name, read from the store when assigning.
            f.Resolve = async (reference, ct) => (await bundles.LoadAsync(ct)).Bundles.FirstOrDefault(item => item.Id == reference.Id)?.Validated().Name;
            await f.Vm.RefreshAsync();

            f.Choice = new(target, saved.Name); f.Vm.ChooseBundle.Execute(null);
            Assert.Null(f.Vm.ChosenTarget); Assert.False(f.Vm.CanAssign); Assert.Equal(0, f.ResolveCalls);
            Assert.Contains("link, access code or credential", f.Vm.Status); Assert.Contains("Rename it, then choose again", f.Vm.Status);
            Assert.DoesNotContain("Promo code=1", f.Vm.Status); Assert.DoesNotContain("Promo code=1", f.Vm.TargetSummary);

            // Captured under an earlier plain hint, but the name saved in the store is the older "code=" one.
            f.Choice = new(target, "Promo night"); f.Vm.ChooseBundle.Execute(null); Assert.Equal(f.Choice, f.Vm.ChosenTarget);
            int hostCalls = f.Client.Calls; await f.Vm.AssignAsync();
            Assert.Equal(1, f.ResolveCalls); Assert.Equal(0, f.Store.Saves); Assert.Empty(f.Store.Rows); Assert.Equal(hostCalls, f.Client.Calls);
            Assert.Contains("link, access code or credential", f.Vm.Status); Assert.Contains("Rename the target, then assign again", f.Vm.Status);
            Assert.DoesNotContain("Promo code=1", f.Vm.Status); Assert.DoesNotContain("could not be confirmed", f.Vm.Status);
            Assert.False(f.Vm.IsStale); Assert.True(f.Vm.CanAssign);

            var renamed = await bundles.RenameAsync(saved.Id, saved.Revision, saved.ChangeId, "Promo night", default);
            f.Choice = new(target, renamed.Name); f.Vm.ChooseBundle.Execute(null); Assert.Equal(f.Choice, f.Vm.ChosenTarget);
            await f.Vm.AssignAsync();
            var row = Assert.Single(f.Store.Rows); Assert.Equal(target, row.Target); Assert.Equal("Promo night", row.TargetName);
            Assert.Equal(1, f.Store.Saves); Assert.Contains("Assignment saved", f.Vm.Status); Assert.Empty(f.Failures);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SavedMacroWithLinkLikeNameIsRejectedWithSpecificStatusAndNoWrite()
    {
        await using var f = await Fixture.Create(); f.Client.Describe = index => Macro(index) with { Name = "Visit www.example" };
        await f.Host.RefreshAsync(); f.Host.SelectedMacro = f.Host.Macros[0];
        await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null); Assert.NotNull(f.Vm.ChosenTarget);
        int hostCalls = f.Client.Calls; await f.Vm.AssignAsync();
        Assert.Equal(0, f.Store.Saves); Assert.Equal(hostCalls, f.Client.Calls);
        Assert.Contains("saved macro", f.Vm.Status); Assert.Contains("link, access code or credential", f.Vm.Status);
        Assert.DoesNotContain("target", f.Vm.Status); Assert.DoesNotContain("www.example", f.Vm.Status);
        Assert.DoesNotContain("could not be confirmed", f.Vm.Status); Assert.False(f.Vm.IsStale); Assert.Empty(f.Failures);
    }

    [Fact]
    public async Task ChangedHostDuringReviewRejectsTheWriteWithoutCallingItMissing()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        f.Client.Read = (_, _) => Task.FromResult(MacroCatalogPage.Failure(MacroCatalogState.Changed));
        await f.Vm.AssignAsync(); Assert.Equal(0, f.Store.Saves); Assert.True(f.Host.IsStale); Assert.False(f.Vm.CanAssign);
        Assert.Contains("revalidated", f.Vm.Status); Assert.DoesNotContain("missing", f.Vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OffPageSavedMacroIsUnreviewedAndSelectionSetterDoesNotFetchIt()
    {
        var existing = Row(0) with { MacroId = Macro(999).Id, MacroName = Macro(999).Name };
        await using var f = await Fixture.Create(existing); await f.Vm.RefreshAsync(); f.Host.SelectedMacro = null;
        int reads = f.Store.Reads, hostReads = f.Client.Calls;
        f.Vm.SelectedAssignment = f.Vm.Assignments[0]; f.Vm.ConfirmRemoval = true;
        Assert.Equal(reads, f.Store.Reads); Assert.Equal(hostReads, f.Client.Calls); Assert.Equal(0, f.ResolveCalls);
        Assert.False(f.Vm.CanAssign); Assert.True(f.Vm.CanRemove);
        Assert.Contains("unreviewed, not missing", f.Vm.Status); Assert.Equal(existing.MacroId, f.Vm.SelectedAssignment!.MacroId);
    }

    [Fact]
    public async Task PagedReadsPinTheStoreStampAndChangedGenerationPreservesRowsAsStale()
    {
        await using var f = await Fixture.Create(Enumerable.Range(0, 101).Select(Row).ToArray());
        await f.Vm.RefreshAsync(); Assert.Equal(100, f.Vm.Assignments.Count); Assert.True(f.Vm.CanNext);
        await f.Vm.NextAsync(); Assert.Single(f.Vm.Assignments); Assert.True(f.Vm.CanPrevious);
        Assert.Equal(100, f.Store.LastQuery!.Offset); Assert.NotNull(f.Store.LastQuery.Expected);
        var last = f.Vm.Assignments; f.Store.ForeignChange(); await f.Vm.PreviousAsync();
        Assert.Same(last, f.Vm.Assignments); Assert.True(f.Vm.IsStale); Assert.False(f.Vm.CanPrevious); Assert.False(f.Vm.CanAssign);
        await f.Vm.RefreshAsync(); Assert.Equal(100, f.Vm.Assignments.Count); Assert.False(f.Vm.IsStale); Assert.Null(f.Store.LastQuery!.Expected);
    }

    [Fact]
    public async Task ADelayedRefreshPreservesTheNewerUnrelatedSelectionAndResetsConfirmation()
    {
        await using var f = await Fixture.Create(Row(0), Row(1)); await f.Vm.RefreshAsync();
        f.Vm.SelectedAssignment = f.Vm.Assignments[0]; f.Vm.ConfirmRemoval = true;
        var entered = Gate(); var release = Gate();
        f.Store.ReadHook = async (_, _) =>
        { entered.TrySetResult(); await release.Task; return new(f.Store.Stamp, new[] { Row(0), Row(1) }, 2, null); };
        var pending = f.Vm.RefreshAsync();
        try { await entered.Task.WaitAsync(Deadline); f.Vm.SelectedAssignment = f.Vm.Assignments[1]; }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Deadline); Assert.Equal(Target(1), f.Vm.SelectedAssignment!.Target);
        Assert.Same(f.Vm.Assignments[1], f.Vm.SelectedAssignment); Assert.False(f.Vm.ConfirmRemoval);
    }

    [Fact]
    public async Task MalformedBorrowedPageCannotReplaceAValidSnapshot()
    {
        await using var f = await Fixture.Create(Row(0)); await f.Vm.RefreshAsync(); var old = f.Vm.Assignments;
        f.Store.ReadHook = (_, _) => Task.FromResult(new MacroAssignmentPage(f.Store.Stamp, new[] { Row(1), Row(1) }, 2, null));
        await f.Vm.RefreshAsync(); Assert.Same(old, f.Vm.Assignments); Assert.True(f.Vm.IsStale); Assert.Single(f.Failures);
    }

    [Fact]
    public async Task RemovalNeedsConfirmationAndPinsItsTargetAcrossSelectionChanges()
    {
        await using var f = await Fixture.Create(Row(0), Row(1)); await f.Vm.RefreshAsync();
        f.Vm.SelectedAssignment = f.Vm.Assignments[0];
        await f.Vm.RemoveAsync(); Assert.Equal(0, f.Store.Removes);
        f.Vm.ConfirmRemoval = true; var target = f.Vm.SelectedAssignment.Target; var entered = Gate(); var release = Gate();
        f.Store.RemoveHook = async (selected, expected, _) =>
        { entered.TrySetResult(); await release.Task; return f.Store.CommitRemove(selected, expected); };
        var removing = f.Vm.RemoveAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline); f.Vm.SelectedAssignment = f.Vm.Assignments[1];
            Assert.False(f.Vm.ConfirmRemoval);
        }
        finally { release.TrySetResult(); }
        await removing.WaitAsync(Deadline);
        Assert.DoesNotContain(f.Store.Rows, row => row.Target == target); Assert.Single(f.Store.Rows);
        Assert.Equal(Target(1), f.Vm.SelectedAssignment!.Target); Assert.Equal(1, f.Client.Calls); Assert.Equal(0, f.ResolveCalls);
    }

    [Fact]
    public async Task EqualButForeignSelectionCannotRemoveAPageOwnedReference()
    {
        await using var f = await Fixture.Create(Row(0)); await f.Vm.RefreshAsync();
        f.Vm.SelectedAssignment = f.Vm.Assignments[0] with { }; f.Vm.ConfirmRemoval = true;
        Assert.Null(f.Vm.SelectedAssignment); Assert.False(f.Vm.CanRemove); await f.Vm.RemoveAsync(); Assert.Equal(0, f.Store.Removes);
    }

    [Fact]
    public async Task NoopAssignmentKeepsTheStampAndReportsNoChange()
    {
        await using var f = await Fixture.Create(Row(0)); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var stamp = f.Store.Stamp; await f.Vm.AssignAsync(); Assert.Equal(stamp, f.Store.Stamp);
        Assert.Contains("already matches", f.Vm.Status); Assert.Single(f.Store.Rows); Assert.False(f.Vm.IsStale);
    }

    [Fact]
    public async Task SuccessfulCommitFollowedByFailedRefreshIsNotReportedAsFailedSave()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        f.Store.ReadHook = (_, _) => throw new IOException("private-refresh-marker"); await f.Vm.AssignAsync();
        Assert.Single(f.Store.Rows); Assert.Empty(f.Vm.Assignments); Assert.True(f.Vm.IsStale);
        Assert.Contains("was saved", f.Vm.Status); Assert.Contains("could not be read", f.Vm.Status);
        Assert.DoesNotContain("private-refresh-marker", f.Vm.Status); Assert.DoesNotContain("write could not be confirmed", f.Vm.Status);
        Assert.All(f.Failures, error => Assert.DoesNotContain("private-refresh-marker", error.ToString()));
    }

    [Theory]
    [InlineData("undefined")]
    [InlineData("unchanged-advanced")]
    [InlineData("applied-old")]
    public async Task InvalidWriteReceiptsNeverBecomeFalseSuccess(string damage)
    {
        await using var f = await Fixture.Create(Row(1)); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        f.Store.SaveHook = (_, expected, _) => Task.FromResult(damage switch
        {
            "undefined" => new MacroAssignmentWriteResult((MacroAssignmentWriteState)99, expected),
            "unchanged-advanced" => new MacroAssignmentWriteResult(MacroAssignmentWriteState.Unchanged, expected with { Revision = expected.Revision + 1 }),
            _ => new MacroAssignmentWriteResult(MacroAssignmentWriteState.Applied, expected)
        });
        await f.Vm.AssignAsync(); Assert.True(f.Vm.IsStale); Assert.Contains("may already be saved", f.Vm.Status);
        Assert.Equal(1, f.Store.Reads); Assert.Single(f.Store.Rows); Assert.Equal(0, f.Store.Removes);
    }

    [Fact]
    public async Task AlreadyCancelledCallerCannotResolveOrWriteAndMustRefreshAgain()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await f.Vm.AssignAsync(cancelled.Token); Assert.Equal(0, f.ResolveCalls); Assert.Equal(0, f.Store.Saves);
        Assert.True(f.Vm.IsStale); Assert.False(f.Vm.CanAssign);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("busy")]
    [InlineData("capacity")]
    [InlineData("damaged")]
    [InlineData("future")]
    [InlineData("unsafe")]
    [InlineData("unknown")]
    public async Task TypedFailuresKeepLastRowsAndUseFixedFeedback(string kind)
    {
        await using var f = await Fixture.Create(Row(1)); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var old = f.Vm.Assignments;
        Exception problem = kind switch
        {
            "conflict" => new MacroAssignmentConflictException(), "busy" => new MacroAssignmentBusyException(),
            "capacity" => new MacroAssignmentCapacityException(), "future" => new MacroAssignmentStorageException(MacroAssignmentStorageProblem.UnsupportedVersion),
            "unsafe" => new MacroAssignmentStorageException(MacroAssignmentStorageProblem.UnsafePath),
            "unknown" => new MacroAssignmentStorageException(MacroAssignmentStorageProblem.OutcomeUnknown),
            _ => new MacroAssignmentStorageException(MacroAssignmentStorageProblem.Damaged)
        };
        f.Store.SaveHook = (_, _, _) => throw problem;
        await f.Vm.AssignAsync(); Assert.Same(old, f.Vm.Assignments); Assert.Single(f.Store.Rows); Assert.True(f.Vm.IsStale);
        Assert.False(f.Vm.CanAssign); Assert.Equal(0, f.Store.Removes); Assert.Single(f.Failures);
        if (kind == "unknown") Assert.Contains("may already be saved", f.Vm.Status);
    }

    [Fact]
    public async Task StopDuringIgnoringWriteRetainsActualCommitWithoutLateSuccessOrCompensation()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var entered = Gate(); var release = Gate();
        f.Store.SaveHook = async (value, stamp, _) =>
        { entered.TrySetResult(); await release.Task; return f.Store.CommitSave(value, stamp); };
        var assigning = f.Vm.AssignAsync(); string notice;
        try
        {
            await entered.Task.WaitAsync(Deadline); f.Vm.CancelWork(); notice = f.Vm.Status;
            Assert.True(f.Vm.IsBusy); await f.Vm.RefreshAsync(); Assert.Equal(1, f.Store.Reads); Assert.False(assigning.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await assigning.WaitAsync(Deadline); Assert.Equal(notice, f.Vm.Status); Assert.True(f.Vm.IsStale);
        Assert.Empty(f.Vm.Assignments); Assert.Single(f.Store.Rows); Assert.Equal(0, f.Store.Removes);
        Assert.Contains("may already be saved", notice); f.Store.SaveHook = null;
        await f.Vm.RefreshAsync(); Assert.Single(f.Vm.Assignments); Assert.False(f.Vm.IsStale);
    }

    [Fact]
    public async Task LockDuringTargetResolutionPreventsWriteAndDoesNotAutoResume()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var entered = Gate(); var release = Gate();
        f.Resolve = async (_, _) => { entered.TrySetResult(); await release.Task; return "Target 0"; };
        var pending = f.Vm.AssignAsync();
        try { await entered.Task.WaitAsync(Deadline); f.Allowed = false; f.Vm.RefreshAvailability(); }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Deadline); Assert.Equal(0, f.Store.Saves); Assert.True(f.Vm.IsStale); Assert.False(f.Vm.CanManage);
        f.Allowed = true; f.Vm.RefreshAvailability(); Assert.False(f.Vm.CanAssign); Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task DisposeJoinsIgnoringWriteAndDoesNotDisposeBorrowedHostOrStore()
    {
        var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null);
        var entered = Gate(); var release = Gate(); Task? disposing = null;
        f.Store.SaveHook = async (value, stamp, _) => { entered.TrySetResult(); await release.Task; return f.Store.CommitSave(value, stamp); };
        var pending = f.Vm.AssignAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline); disposing = f.Vm.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted); Assert.False(f.Store.Disposed); Assert.False(f.Client.Disposed);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending, disposing ?? f.Vm.DisposeAsync().AsTask()).WaitAsync(Deadline); }
        Assert.Empty(f.Vm.Assignments); Assert.Single(f.Store.Rows); Assert.False(f.Vm.CanManage); Assert.False(f.Store.Disposed); Assert.False(f.Client.Disposed);
        await f.DisposeAsync(); Assert.True(f.Store.Disposed); Assert.True(f.Client.Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReentrantStopOrDisposeFromBusySeesTheActualOwnedTaskBeforeAnyIo(bool dispose)
    {
        await using var f = await Fixture.Create(); Task? disposal = null; bool triggered = false;
        f.Vm.PropertyChanged += (_, args) =>
        {
            if (triggered || args.PropertyName != nameof(f.Vm.IsBusy) || !f.Vm.IsBusy) return;
            triggered = true; Assert.False(f.Vm.ActiveOperation.IsCompleted);
            if (dispose) disposal = f.Vm.DisposeAsync().AsTask(); else f.Vm.CancelWork();
        };
        await f.Vm.RefreshAsync().WaitAsync(Deadline); if (disposal is not null) await disposal.WaitAsync(Deadline);
        Assert.True(triggered); Assert.Equal(0, f.Store.Reads); Assert.False(f.Vm.IsBusy); Assert.True(f.Vm.IsStale);
    }

    [Fact]
    public async Task ReentrantStopDuringCommittedRowsPublicationKeepsStopNoticeAndStaleRows()
    {
        await using var f = await Fixture.Create(); await f.Vm.RefreshAsync(); f.Vm.ChooseAccount.Execute(null); bool stopped = false;
        f.Vm.PropertyChanged += (_, args) =>
        {
            if (!stopped && args.PropertyName == nameof(f.Vm.Assignments) && f.Vm.Assignments.Count > 0)
            { stopped = true; f.Vm.CancelWork(); }
        };
        await f.Vm.AssignAsync(); Assert.True(stopped); Assert.True(f.Vm.IsStale); Assert.Single(f.Vm.Assignments);
        Assert.Contains("stopped", f.Vm.Status); Assert.Equal(0, f.Store.Removes);
    }

    [Fact]
    public async Task DirectCommandsCannotBypassUnavailableControls()
    {
        await using var f = await Fixture.Create(); f.Allowed = false;
        f.Vm.ChooseAccount.Execute(null); f.Vm.Refresh.Execute(null); f.Vm.Assign.Execute(null); f.Vm.Remove.Execute(null);
        await Task.WhenAll(f.Vm.Refresh.Execution, f.Vm.Assign.Execution, f.Vm.Remove.Execution);
        Assert.Null(f.Vm.ChosenTarget); Assert.Equal(0, f.Store.Reads); Assert.Equal(0, f.Store.Saves); Assert.Equal(0, f.ResolveCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; } = new();
        public HostClient Client { get; } = new();
        public MacroHostCatalogViewModel Host { get; }
        public MacroAssignmentsViewModel Vm { get; }
        public MacroAssignmentTargetChoice? Choice = new(Target(0), "Target 0");
        public bool Allowed = true; public int ResolveCalls;
        public List<Exception> Failures { get; } = [];
        public Func<MacroAssignmentTarget, CancellationToken, Task<string?>> Resolve = (target, _) => Task.FromResult<string?>("Target " + (target.Id.ToByteArray()[0] - 1));
        private Fixture()
        {
            Host = new(Client, () => Allowed);
            Vm = new(Store, Host, _ => Choice, (target, ct) => { ResolveCalls++; return Resolve(target, ct); }, () => Allowed, Failures.Add);
        }
        public static async Task<Fixture> Create(params MacroAssignment[] rows)
        {
            var f = new Fixture(); f.Store.Rows.AddRange(rows); if (rows.Length > 0) f.Store.Stamp = new(Guid.NewGuid(), 1);
            await f.Host.RefreshAsync(); f.Host.SelectedMacro = f.Host.Macros[0]; return f;
        }
        public async ValueTask DisposeAsync() { await Vm.DisposeAsync(); await Host.DisposeAsync(); await Store.DisposeAsync(); }
    }
    private sealed class HostClient : IZenithMacroCatalogClient, IAsyncDisposable
    {
        private readonly Guid _session = Guid.NewGuid(); public int Calls; public bool Disposed;
        public Func<MacroCatalogQuery, CancellationToken, Task<MacroCatalogPage>>? Read;
        public Func<int, SavedMacroDescriptor> Describe = Macro;
        public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default)
        {
            Calls++; if (Read is not null) return Read(query, ct);
            return Task.FromResult(new MacroCatalogPage(MacroCatalogState.Ready, _session, 1, "owned-fixture",
                Enumerable.Range(query.Offset, Math.Min(query.Limit, 2 - query.Offset)).Select(Describe).ToArray(), 2, null, MacroCatalogMessages.For(MacroCatalogState.Ready)));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Store : IMacroAssignmentStore, IAsyncDisposable
    {
        public List<MacroAssignment> Rows { get; } = [];
        public MacroAssignmentStamp Stamp = MacroAssignmentStamp.Empty;
        public int Reads, Saves, Removes; public bool Disposed; public MacroAssignmentQuery? LastQuery;
        public Func<MacroAssignmentQuery, CancellationToken, Task<MacroAssignmentPage>>? ReadHook;
        public Func<MacroAssignment, MacroAssignmentStamp, CancellationToken, Task<MacroAssignmentWriteResult>>? SaveHook;
        public Func<MacroAssignmentTarget, MacroAssignmentStamp, CancellationToken, Task<MacroAssignmentWriteResult>>? RemoveHook;
        public Task<MacroAssignmentPage> ReadAsync(MacroAssignmentQuery query, CancellationToken ct)
        {
            Reads++; LastQuery = query; if (ReadHook is not null) return ReadHook(query, ct);
            if (query.Expected is { } expected && expected != Stamp) throw new MacroAssignmentConflictException();
            var rows = Rows.OrderBy(row => row.Target.Id).Skip(query.Offset).Take(query.Limit).ToArray();
            return Task.FromResult(new MacroAssignmentPage(Stamp, rows, Rows.Count, query.Offset + rows.Length < Rows.Count ? query.Offset + rows.Length : null));
        }
        public Task<MacroAssignmentWriteResult> SaveAsync(MacroAssignment value, MacroAssignmentStamp expected, CancellationToken ct)
        { Saves++; return SaveHook?.Invoke(value, expected, ct) ?? Task.FromResult(CommitSave(value, expected)); }
        public MacroAssignmentWriteResult CommitSave(MacroAssignment value, MacroAssignmentStamp expected)
        {
            if (expected != Stamp) throw new MacroAssignmentConflictException();
            var previous = Rows.FindIndex(row => row.Target == value.Target);
            if (previous >= 0 && Rows[previous] == value) return new(MacroAssignmentWriteState.Unchanged, Stamp);
            if (previous >= 0) Rows[previous] = value; else Rows.Add(value);
            Advance(); return new(MacroAssignmentWriteState.Applied, Stamp);
        }
        public Task<MacroAssignmentWriteResult> RemoveAsync(MacroAssignmentTarget target, MacroAssignmentStamp expected, CancellationToken ct)
        { Removes++; return RemoveHook?.Invoke(target, expected, ct) ?? Task.FromResult(CommitRemove(target, expected)); }
        public MacroAssignmentWriteResult CommitRemove(MacroAssignmentTarget target, MacroAssignmentStamp expected)
        {
            if (expected != Stamp) throw new MacroAssignmentConflictException();
            if (Rows.RemoveAll(row => row.Target == target) == 0) return new(MacroAssignmentWriteState.Unchanged, Stamp);
            Advance(); return new(MacroAssignmentWriteState.Applied, Stamp);
        }
        private void Advance() => Stamp = Stamp == MacroAssignmentStamp.Empty ? new(Guid.NewGuid(), 1) : Stamp with { Revision = Stamp.Revision + 1 };
        public void ForeignChange() => Stamp = new(Guid.NewGuid(), 1);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
