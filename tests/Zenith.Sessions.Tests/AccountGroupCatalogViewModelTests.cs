using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class AccountGroupCatalogViewModelTests
{
    [Fact]
    public async Task CatalogLoadsOnlyExplicitPagesAndKeepsTheSelectedStableIdOutsideThePage()
    {
        var catalog = new Catalog(205); await using var model = Model(catalog, "Group 150");
        Assert.Equal(0, catalog.Reads); Assert.False(model.CanCreateGroup);
        await Run(model.RefreshCatalog); Assert.Equal(100, model.CatalogGroups.Count); Assert.Equal(catalog.Items[150].Id, model.SelectedGroup!.Id);
        Assert.Equal("Group 150", model.SourceName); Assert.Equal(1, catalog.NameFinds); Assert.Contains("205", model.CatalogPageLabel);
        Guid selected = model.SelectedGroup.Id;
        await Run(model.NextCatalogPage); Assert.Equal(1, catalog.Queries[^1].Page); Assert.Equal(selected, model.SelectedGroup!.Id);
        await Run(model.NextCatalogPage); Assert.Equal(5, model.CatalogGroups.Count); Assert.False(model.CanNextCatalogPage);
        Assert.Equal(selected, model.SelectedGroup!.Id); Assert.True(catalog.IdFinds > 0); Assert.All(catalog.Queries, query => Assert.Equal(100, query.PageSize));
        await Run(model.PreviousCatalogPage); Assert.Equal(1, catalog.Queries[^1].Page); Assert.Equal(selected, model.SelectedGroup!.Id);
        Assert.Equal(0, catalog.Writes);
    }

    [Fact]
    public async Task SearchDraftMakesNoRequestAndDoesNotReuseAnotherSearchPage()
    {
        var catalog = new Catalog(205); await using var model = Model(catalog); await Run(model.RefreshCatalog); await Run(model.NextCatalogPage);
        int reads = catalog.Reads; model.CatalogSearch = "  Group 20  "; Assert.Equal(reads, catalog.Reads); Assert.False(model.CanNextCatalogPage); Assert.False(model.CanPreviousCatalogPage);
        await Run(model.RefreshCatalog); Assert.Equal(0, catalog.Queries[^1].Page); Assert.Equal("Group 20", model.CatalogSearch);
        Assert.Equal(5, model.CatalogGroups.Count); Assert.Contains("5", model.CatalogPageLabel); Assert.Equal(0, catalog.Writes);
    }

    [Fact]
    public async Task CreateSavesAnEmptyNamedGroupOnlyOnExplicitCommandAndPinsCanonicalColorAndCatalogRevision()
    {
        var catalog = new Catalog(0); await using var model = Model(catalog); await Run(model.RefreshCatalog);
        model.NewGroupName = "  Empty raid  "; model.NewColorHex = "#aa12F0"; Assert.Equal(0, catalog.Writes);
        await Run(model.CreateGroup); var group = Assert.Single(catalog.Items);
        Assert.Equal("Empty raid", group.Name); Assert.Equal("#AA12F0", group.ColorHex); Assert.Equal(1, catalog.LastExpectedRevision);
        Assert.Equal(group.Id, model.SelectedGroup!.Id); Assert.Equal("", model.NewGroupName); Assert.Contains("Created empty group", model.Status);
        Assert.Equal(1, catalog.Writes); Assert.Null(model.Preview); Assert.False(model.CanApply);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#123")]
    [InlineData("#GGFF00")]
    [InlineData("#1122334455667788990011223344556677")]
    public async Task InvalidColorDraftKeepsTheNameAndNeverCallsTheStore(string color)
    {
        var catalog = new Catalog(0); await using var model = Model(catalog); await Run(model.RefreshCatalog);
        model.NewGroupName = "Keep draft"; model.NewColorHex = color; await Run(model.CreateGroup);
        Assert.Equal(0, catalog.Writes); Assert.Equal("Keep draft", model.NewGroupName); Assert.Equal(color, model.NewColorHex); Assert.Contains("#RRGGBB", model.Status);
    }

    [Fact]
    public async Task SaveColorUsesTheSelectedRevisionAndReportsActualNoopWithoutAdvancingMetadata()
    {
        var catalog = new Catalog(2); await using var model = Model(catalog); await Run(model.RefreshCatalog); var original = model.SelectedGroup!;
        model.SelectedColorHex = "#aabbcc"; Assert.Equal(0, catalog.Writes); await Run(model.SaveGroupColor);
        Assert.Equal(original, catalog.LastExpected); Assert.Equal(original.Id, model.SelectedGroup!.Id); Assert.Equal("#AABBCC", model.SelectedGroup.ColorHex);
        long revision = model.CatalogRevision; var saved = model.SelectedGroup; await Run(model.SaveGroupColor);
        Assert.Equal(saved, model.SelectedGroup); Assert.Equal(revision, model.CatalogRevision); Assert.Contains("Nothing changed", model.Status);
        model.SelectedColorChoice = GroupPresentation.Colors[0]; await Run(model.SaveGroupColor); Assert.Null(model.SelectedGroup!.ColorHex);
    }

    [Fact]
    public async Task MoveResolvesTheRealAdjacentGroupAndRetainsTheSelectedId()
    {
        var catalog = new Catalog(150); await using var model = Model(catalog); await Run(model.RefreshCatalog);
        model.SelectedGroup = model.CatalogGroups[50]; Guid selected = model.SelectedGroup!.Id; var neighbor = catalog.Items[49];
        await Run(model.MoveGroupUp);
        Assert.Equal(AccountGroupMoveDirection.Up, catalog.LastDirection); Assert.Equal(selected, model.SelectedGroup!.Id); Assert.Equal(49, model.SelectedGroup.SortOrder);
        Assert.Equal(50, catalog.Items.Single(group => group.Id == neighbor.Id).SortOrder); Assert.Contains("not account priority", model.Status);
    }

    [Fact]
    public async Task BoundaryMoveFromAFilteredPageReportsNoChangeInsteadOfClaimingReorder()
    {
        var catalog = new Catalog(2); await using var model = Model(catalog); model.CatalogSearch = "Group 000"; await Run(model.RefreshCatalog);
        Assert.True(model.CanMoveGroupUp); var original = model.SelectedGroup; long revision = model.CatalogRevision;
        await Run(model.MoveGroupUp); Assert.Equal(original, model.SelectedGroup); Assert.Equal(revision, model.CatalogRevision); Assert.Contains("No order change", model.Status);
    }

    [Theory]
    [InlineData(AccountGroupChangeKind.Rename)]
    [InlineData(AccountGroupChangeKind.Remove)]
    public async Task ReviewedExistingEmptyGroupsCanBeChangedWithZeroMembershipWrites(AccountGroupChangeKind kind)
    {
        var catalog = new Catalog(1); Guid originalId = catalog.Items[0].Id;
        await using var model = new AccountGroupManagerViewModel("Group 000", () => catalog.Items.Select(item => item.Name).ToArray(), () => true,
            (change, _) => Task.FromResult(new AccountGroupChangePreview(change, 0, 0, false, new string('A', 64), true, catalog.Revision)),
            (preview, _) =>
            {
                if (preview.Change.Kind == AccountGroupChangeKind.Remove) catalog.Items.Clear();
                else catalog.Items[0] = catalog.Items[0] with { Name = preview.Change.DestinationName, Revision = 2 };
                catalog.Revision++; return Task.FromResult(0);
            }, catalog);
        await Run(model.RefreshCatalog); model.Choice = AccountGroupManagerViewModel.Choices.Single(choice => choice.Kind == kind); model.DestinationName = "New group";
        await Run(model.Review); Assert.True(model.CanApply); Assert.Contains("empty", model.Status); await Run(model.Apply);
        Assert.Contains("No stored group memberships", model.Status);
        if (kind == AccountGroupChangeKind.Rename) { Assert.Equal(originalId, model.SelectedGroup!.Id); Assert.Equal("New group", model.SourceName); }
        else { Assert.Empty(model.CatalogGroups); Assert.Null(model.SelectedGroup); }
    }

    [Fact]
    public async Task EmptyMergeConflictStillRequiresExplicitMergeAndAnotherReview()
    {
        await using var model = new AccountGroupManagerViewModel("Empty", () => new[] { "Empty", "Target" }, () => true,
            (change, _) => Task.FromResult(new AccountGroupChangePreview(change, 0, 0, true, new string('A', 64), true, 1)), (_, _) => Task.FromResult(0));
        model.DestinationName = "Target"; await Run(model.Review); Assert.False(model.CanApply); Assert.Contains("Enable merging", model.Status);
        model.AllowMerge = true; Assert.False(model.CanApply); await Run(model.Review); Assert.True(model.CanApply);
    }

    [Fact]
    public async Task LateReadCannotReplaceAPageAfterItsSearchDraftChanges()
    {
        var catalog = new Catalog(3); await using var model = Model(catalog); await Run(model.RefreshCatalog); var before = model.CatalogGroups; var selected = model.SelectedGroup;
        var gate = catalog.ReadGate = new(); model.RefreshCatalog.Execute(null);
        try { await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); model.CatalogSearch = "new draft"; Assert.True(gate.Token.IsCancellationRequested); }
        finally { gate.Release.TrySetResult(); await model.RefreshCatalog.Execution.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Same(before, model.CatalogGroups); Assert.Equal(selected, model.SelectedGroup); Assert.Contains("Review the current", model.Status); Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FenceRejectsLateCreateSuccessOrFailureAndRetainsTheStopNotice(bool fail)
    {
        var catalog = new Catalog(0); await using var model = Model(catalog); await Run(model.RefreshCatalog); model.NewGroupName = "Late group";
        var gate = catalog.WriteGate = new() { Fail = fail }; model.CreateGroup.Execute(null); string? notice = null;
        try { await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); model.Fence("Stopped by test Panic"); notice = model.Status; Assert.True(gate.Token.IsCancellationRequested); Assert.False(model.CreateGroup.Execution.IsCompleted); }
        finally { gate.Release.TrySetResult(); await model.CreateGroup.Execution.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(notice, model.Status); Assert.Empty(model.CatalogGroups); Assert.Null(model.SelectedGroup); Assert.False(model.CanCreateGroup); Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task DisposalJoinsTheActualUncooperativeCatalogOwnerAndIsIdempotent()
    {
        var catalog = new Catalog(1); var model = Model(catalog); var gate = catalog.ReadGate = new(); model.RefreshCatalog.Execute(null); Task? disposing = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); disposing = model.DisposeAsync().AsTask();
            Assert.Same(disposing, model.DisposeAsync().AsTask()); Assert.False(disposing.IsCompleted); Assert.True(gate.Token.IsCancellationRequested);
        }
        finally { gate.Release.TrySetResult(); await (disposing ?? model.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Empty(model.CatalogGroups); Assert.False(model.CanReview); Assert.False(model.CanRefreshCatalog);
    }

    [Fact]
    public async Task ChangingSelectionCannotRetargetAColorWriteAlreadyInFlight()
    {
        var catalog = new Catalog(2); await using var model = Model(catalog); await Run(model.RefreshCatalog); var expected = model.SelectedGroup!;
        model.SelectedColorHex = "#AABBCC"; var gate = catalog.WriteGate = new(); model.SaveGroupColor.Execute(null); Guid selected = Guid.Empty;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); model.SelectedGroup = model.CatalogGroups[1]; selected = model.SelectedGroup!.Id;
            Assert.Equal(expected, catalog.LastExpected); Assert.True(gate.Token.IsCancellationRequested);
        }
        finally { gate.Release.TrySetResult(); await model.SaveGroupColor.Execution.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(selected, model.SelectedGroup!.Id); Assert.Equal("#AABBCC", catalog.Items.Single(group => group.Id == expected.Id).ColorHex);
        Assert.Null(catalog.Items.Single(group => group.Id == selected).ColorHex); Assert.Contains("Review the current", model.Status);
    }

    [Fact]
    public async Task ACommittedCreateWithFailedReloadReportsCompletionAndRequiresAnExplicitRefresh()
    {
        var catalog = new Catalog(0); await using var model = Model(catalog); await Run(model.RefreshCatalog);
        model.NewGroupName = "Actually saved"; catalog.FailReadAfterWrite = true; await Run(model.CreateGroup);
        Assert.Equal("Actually saved", Assert.Single(catalog.Items).Name); Assert.Empty(model.CatalogGroups); Assert.Equal("Actually saved", model.NewGroupName);
        Assert.Contains("operation finished", model.Status); Assert.False(model.CanCreateGroup); Assert.True(model.CanRefreshCatalog);
        int writes = catalog.Writes; await Run(model.CreateGroup); Assert.Equal(writes, catalog.Writes);
    }

    [Fact]
    public async Task CatalogConflictKeepsTheColorDraftAndDisablesFurtherWritesUntilReload()
    {
        var catalog = new Catalog(1); await using var model = Model(catalog); await Run(model.RefreshCatalog);
        model.SelectedColorHex = "#AABBCC"; catalog.Conflict = true; await Run(model.SaveGroupColor);
        Assert.Equal("#AABBCC", model.SelectedColorHex); Assert.Null(Assert.Single(catalog.Items).ColorHex); Assert.Contains("changed elsewhere", model.Status);
        Assert.False(model.CanSaveGroupColor); Assert.True(model.CanRefreshCatalog);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    [InlineData("color")]
    [InlineData("wrong-page")]
    [InlineData("revision")]
    [InlineData("zero-revision")]
    public async Task InvalidProviderPagesCannotBePublishedOrEnableCatalogWrites(string problem)
    {
        var catalog = new Catalog(101) { BadPage = problem }; await using var model = Model(catalog); await Run(model.RefreshCatalog);
        Assert.Empty(model.CatalogGroups); Assert.False(model.CanCreateGroup); Assert.False(model.CanSaveGroupColor); Assert.Contains("Reload", model.Status);
    }

    [Fact]
    public async Task CatalogAndMembershipReviewShareOneOwnerRatherThanQueueingCompetingCommands()
    {
        int reviews = 0; var catalog = new Catalog(1);
        await using var model = new AccountGroupManagerViewModel("Group 000", () => new[] { "Group 000" }, () => true,
            (change, _) => { reviews++; return Task.FromResult(new AccountGroupChangePreview(change, 1, 1, false, new string('A', 64))); }, (_, _) => Task.FromResult(1), catalog);
        model.DestinationName = "Renamed"; model.NewGroupName = "New"; var gate = catalog.ReadGate = new(); model.RefreshCatalog.Execute(null);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); await Run(model.Review); await Run(model.CreateGroup); await Run(model.MoveGroupDown);
            Assert.Equal(0, reviews); Assert.Equal(0, catalog.Writes); Assert.True(model.IsBusy);
        }
        finally { gate.Release.TrySetResult(); await model.RefreshCatalog.Execution.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.False(model.IsBusy); Assert.Equal(1, catalog.Reads);
    }

    [Fact]
    public async Task ShowAccountsIsExplicitAndRoutesTheSelectedStableIdWithoutChangingCatalogMetadata()
    {
        var catalog = new Catalog(2); Guid? routed = null;
        await using var model = Model(catalog, show: (id, _) => { routed = id; return Task.CompletedTask; });
        await Run(model.RefreshCatalog); Assert.Null(routed); model.SelectedGroup = model.CatalogGroups[1]; Assert.Null(routed);
        Assert.True(model.CanShowAccounts); await Run(model.ShowAccounts);
        Assert.Equal(model.SelectedGroup!.Id, routed); Assert.Contains("Close this dialog", model.Status); Assert.Contains("no accounts were launched", model.Status);
        Assert.Equal(0, catalog.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowAccountsCancellationAndDisposalJoinTheActualRouteAndSuppressLateFeedback(bool fail)
    {
        var catalog = new Catalog(1); var hold = new Hold { Fail = fail };
        var model = Model(catalog, show: (_, ct) => hold.Wait(ct)); await Run(model.RefreshCatalog); model.ShowAccounts.Execute(null);
        Task? closing = null; string? notice = null;
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); model.Fence("Stopped by test Lock"); closing = model.DisposeAsync().AsTask(); notice = model.Status;
            Assert.True(hold.Token.IsCancellationRequested); Assert.False(closing.IsCompleted); Assert.False(model.ShowAccounts.Execution.IsCompleted);
        }
        finally { hold.Release.TrySetResult(); await (closing ?? model.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(notice, model.Status); Assert.DoesNotContain("Accounts are filtered", model.Status); Assert.False(model.CanShowAccounts); Assert.Equal(0, catalog.Writes);
    }

    [Fact]
    public async Task MissingGroupRouteShowsRecoveryFeedbackWithoutAFalseFilteredResult()
    {
        var catalog = new Catalog(1);
        await using var model = Model(catalog, show: (_, _) => Task.FromException(new InvalidOperationException("Synthetic private provider detail.")));
        await Run(model.RefreshCatalog); await Run(model.ShowAccounts);
        Assert.Contains("no longer available", model.Status); Assert.DoesNotContain("private provider", model.Status); Assert.DoesNotContain("Accounts are filtered", model.Status); Assert.Equal(0, catalog.Writes); Assert.False(model.IsBusy);
    }

    private static AccountGroupManagerViewModel Model(Catalog catalog, string? selected = null, Func<Guid, CancellationToken, Task>? show = null) => new(selected,
        () => catalog.Items.Select(item => item.Name).Take(1000).ToArray(), () => true,
        (_, _) => throw new InvalidOperationException("Unexpected account membership review."), (_, _) => throw new InvalidOperationException("Unexpected account membership change."), catalog, show);
    private static async Task Run(AsyncCommand command) { command.Execute(null); await command.Execution.WaitAsync(TimeSpan.FromSeconds(3)); }
    private sealed class Hold
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token; public bool Fail;
        public async Task Wait(CancellationToken ct) { Token = ct; Entered.TrySetResult(); await Release.Task; if (Fail) throw new IOException("Synthetic late catalog failure."); }
    }
    private sealed class Catalog : IAccountGroupCatalog
    {
        public readonly List<AccountGroupDefinition> Items = [];
        public readonly List<AccountGroupCatalogQuery> Queries = [];
        public long Revision; public int Reads, Writes, NameFinds, IdFinds; public long LastExpectedRevision;
        public AccountGroupDefinition? LastExpected; public AccountGroupMoveDirection LastDirection;
        public Hold? ReadGate, WriteGate; public bool FailReadAfterWrite, Conflict; public string? BadPage;
        private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public Catalog(int count) { for (int index = 0; index < count; index++) Items.Add(new(Guid.NewGuid(), $"Group {index:D3}", null, index, 1, Now)); Revision = 1; }
        public async Task<AccountGroupCatalogPage> ReadGroupCatalogAsync(AccountGroupCatalogQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Reads++; Queries.Add(query);
            if (FailReadAfterWrite && Writes > 0) throw new IOException("Synthetic reload failure.");
            var matching = Items.Where(group => group.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).OrderBy(group => group.SortOrder).ToArray();
            var page = new AccountGroupCatalogPage(matching.Skip(query.Page * query.PageSize).Take(query.PageSize).ToArray(), matching.Length, query.Page, query.PageSize, Revision);
            page = BadPage switch
            {
                "oversize" => page with { Items = Items.Take(101).ToArray() }, "duplicate" => page with { Items = new[] { Items[0], Items[0] } },
                "color" => page with { Items = new[] { Items[0] with { ColorHex = "invalid" } } }, "wrong-page" => page with { Page = query.Page + 1 },
                "revision" => page with { CatalogRevision = -1 }, "zero-revision" => page with { CatalogRevision = 0 }, _ => page
            };
            var gate = ReadGate; ReadGate = null; if (gate is not null) await gate.Wait(ct); return page;
        }
        public Task<AccountGroupDefinition?> FindGroupAsync(string name, CancellationToken ct) { NameFinds++; return Task.FromResult(Items.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))); }
        public Task<AccountGroupDefinition?> FindGroupAsync(Guid id, CancellationToken ct) { IdFinds++; return Task.FromResult(Items.FirstOrDefault(group => group.Id == id)); }
        public async Task<AccountGroupDefinition> CreateGroupAsync(string name, string? colorHex, long expectedCatalogRevision, CancellationToken ct)
        {
            Check(expectedCatalogRevision); var gate = WriteGate; WriteGate = null; if (gate is not null) await gate.Wait(ct);
            var created = new AccountGroupDefinition(Guid.NewGuid(), name, colorHex, Items.Count == 0 ? 0 : Items.Max(group => group.SortOrder) + 1, 1, Now);
            Items.Add(created); Writes++; Revision++; return created;
        }
        public async Task<AccountGroupDefinition> SetGroupColorAsync(AccountGroupDefinition expected, string? colorHex, long expectedCatalogRevision, CancellationToken ct)
        {
            Check(expectedCatalogRevision); LastExpected = expected; var gate = WriteGate; WriteGate = null; if (gate is not null) await gate.Wait(ct);
            int index = Items.FindIndex(group => group.Id == expected.Id); if (index < 0 || Items[index] != expected) throw new AccountGroupCatalogConflictException();
            if (expected.ColorHex == colorHex) return expected;
            var saved = expected with { ColorHex = colorHex, Revision = expected.Revision + 1, UpdatedUtc = Now.AddTicks(expected.Revision) };
            Items[index] = saved; Writes++; Revision++; return saved;
        }
        public Task<AccountGroupDefinition> MoveGroupAsync(AccountGroupDefinition expected, AccountGroupMoveDirection direction, long expectedCatalogRevision, CancellationToken ct)
        {
            Check(expectedCatalogRevision); LastExpected = expected; LastDirection = direction;
            var order = Items.OrderBy(group => group.SortOrder).ToArray(); int index = Array.FindIndex(order, group => group.Id == expected.Id);
            int neighbor = index + (direction == AccountGroupMoveDirection.Up ? -1 : 1); if (index < 0) throw new AccountGroupCatalogConflictException();
            if (neighbor < 0 || neighbor >= order.Length) return Task.FromResult(expected);
            var other = order[neighbor]; var moved = expected with { SortOrder = other.SortOrder, Revision = expected.Revision + 1 };
            Items[Items.FindIndex(group => group.Id == other.Id)] = other with { SortOrder = expected.SortOrder, Revision = other.Revision + 1 };
            Items[Items.FindIndex(group => group.Id == expected.Id)] = moved; Writes++; Revision++; return Task.FromResult(moved);
        }
        private void Check(long expected) { LastExpectedRevision = expected; if (Conflict || expected != Revision) throw new AccountGroupCatalogConflictException(); }
    }
}
