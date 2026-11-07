using System.Text.Json;
using Xunit;
using System.IO;
using Zenith.MacroHost.Contracts;

namespace Zenith.Sessions.Tests;

// Synthetic metadata and explicit completion order only. No library, document decryption, pipe, UI or input.
public sealed class MacroHostCatalogTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static SavedMacroDescriptor Descriptor(string name = "Saved macro", Guid? id = null, Guid? revision = null)
        => new(id ?? Guid.NewGuid(), revision ?? Guid.NewGuid(), name, "F4", 3, true, "Screen");
    private static SavedMacroCatalog Loaded(params SavedMacroDescriptor[] items)
    {
        var catalog = new SavedMacroCatalog("2.5.8-controlled");
        Assert.True(catalog.CompleteLoad(catalog.BeginLoad(), items)); return catalog;
    }
    private static async Task<MacroCatalogPage> Page(SavedMacroCatalog catalog, MacroCatalogQuery? query = null)
    {
        query ??= new();
        return (await catalog.GetPageAsync(query).WaitAsync(Deadline)).Validated(query);
    }
    private static SavedMacroSaveAttempt Save(SavedMacroCatalog catalog, Guid id)
    {
        var attempt = catalog.BeginSave(id); Assert.NotNull(attempt); return attempt!;
    }

    [Fact]
    public async Task StartingAndFailedLoadAreExplicitInsteadOfHealthyEmptyLibraries()
    {
        var catalog = new SavedMacroCatalog("2.5.8-controlled");
        var initial = await Page(catalog); Assert.Equal(MacroCatalogState.Starting, initial.State);
        Assert.NotEqual(Guid.Empty, initial.HostSessionId); Assert.True(initial.CatalogRevision > 0); Assert.Empty(initial.Items);
        Assert.Null(catalog.BeginSave(Guid.NewGuid()));
        var load = catalog.BeginLoad(); Assert.True(catalog.FailLoad(load));
        var failed = await Page(catalog); Assert.Equal(MacroCatalogState.Unavailable, failed.State);
        Assert.Equal(initial.HostSessionId, failed.HostSessionId); Assert.True(failed.CatalogRevision > initial.CatalogRevision);
        Assert.Empty(failed.Items); Assert.Null(catalog.BeginSave(Guid.NewGuid()));
        Assert.False(catalog.CompleteLoad(load, new[] { Descriptor() }));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
    }

    [Fact]
    public async Task ExplicitSuccessfulEmptyLoadIsAHealthyEmptyCatalog()
    {
        var page = await Page(Loaded());
        Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Equal(0, page.Total);
        Assert.Empty(page.Items); Assert.Null(page.NextOffset);
        Assert.Equal(MacroCatalogMessages.For(MacroCatalogState.Ready), page.Explanation);
    }

    [Fact]
    public async Task LoadedDescriptorsAndReturnedPagesAreFrozenAndDeterministicallySorted()
    {
        var first = Descriptor("zebra"); var second = Descriptor("Alpha"); var third = Descriptor("alpha");
        var caller = new List<SavedMacroDescriptor> { first, third, second };
        var catalog = new SavedMacroCatalog("2.5.8-controlled");
        Assert.True(catalog.CompleteLoad(catalog.BeginLoad(), caller)); caller.Clear();
        var page = await Page(catalog);
        Assert.Equal(new[] { second, third, first }, page.Items); Assert.Equal(3, page.Total);
        Assert.Throws<NotSupportedException>(() => ((IList<SavedMacroDescriptor>)page.Items)[0] = Descriptor("Changed"));
        var token = Save(catalog, first.Id); Assert.True(catalog.PublishSaved(token, Descriptor("new", first.Id, token.SavedRevision)));
        Assert.Equal(first, page.Items[2]);
    }

    [Fact]
    public async Task PagedGenerationCursorNeverMixesRowsAfterASuccessfulSave()
    {
        var items = Enumerable.Range(0, 7).Select(value => Descriptor($"Macro {value:00}")).ToArray(); var catalog = Loaded(items);
        var first = await Page(catalog, new(Limit: 3)); Assert.Equal(3, first.Items.Count); Assert.Equal(3, first.NextOffset);
        var nextQuery = new MacroCatalogQuery(first.NextOffset!.Value, 3, first.HostSessionId, first.CatalogRevision);
        var next = await Page(catalog, nextQuery); Assert.Equal(items.Skip(3).Take(3), next.Items); Assert.Equal(6, next.NextOffset);
        var token = Save(catalog, items[0].Id); Assert.True(catalog.PublishSaved(token, Descriptor("Updated", items[0].Id, token.SavedRevision)));
        var stale = await Page(catalog, nextQuery); Assert.Equal(MacroCatalogState.Changed, stale.State); Assert.Empty(stale.Items);
        Assert.True(stale.CatalogRevision > first.CatalogRevision);
        var refreshed = await Page(catalog); Assert.Equal(7, refreshed.Total); Assert.Contains(refreshed.Items, item => item.SavedRevision == token.SavedRevision);
        Assert.Equal(items.Take(3), first.Items);
    }

    [Fact]
    public async Task RestartedPublisherRejectsPreviousHostCursorEvenWhenNumericRevisionMatches()
    {
        var descriptor = Descriptor(); var first = await Page(Loaded(descriptor)); var restarted = Loaded(descriptor);
        var page = await Page(restarted, new(0, 100, first.HostSessionId, first.CatalogRevision));
        Assert.Equal(MacroCatalogState.Changed, page.State); Assert.NotEqual(first.HostSessionId, page.HostSessionId);
        Assert.Equal(first.CatalogRevision, page.CatalogRevision); Assert.Empty(page.Items);
    }

    [Fact]
    public async Task OffsetBeyondCurrentEndIsChangedRatherThanHealthyEmpty()
    {
        var catalog = Loaded(Descriptor()); var first = await Page(catalog);
        var query = new MacroCatalogQuery(2, 100, first.HostSessionId, first.CatalogRevision);
        Assert.Equal(MacroCatalogState.Changed, (await Page(catalog, query)).State);
    }

    [Fact]
    public async Task NewerSaveCompletionWinsAndOlderSuccessCannotOverwriteIt()
    {
        var old = Descriptor(); var catalog = Loaded(old); var first = Save(catalog, old.Id); var newer = Save(catalog, old.Id);
        var winning = Descriptor("Newer saved metadata", old.Id, newer.SavedRevision);
        Assert.True(catalog.PublishSaved(newer, winning));
        Assert.False(catalog.PublishSaved(first, Descriptor("Late older metadata", old.Id, first.SavedRevision)));
        Assert.False(catalog.FailSave(first));
        Assert.Equal(winning, Assert.Single((await Page(catalog)).Items));
    }

    [Fact]
    public async Task FailedNewerSavePreservesPreviousSavedDescriptorAndStillFencesOlderAttempt()
    {
        var saved = Descriptor(); var catalog = Loaded(saved); var baseline = await Page(catalog);
        var oldAttempt = Save(catalog, saved.Id); var failed = Save(catalog, saved.Id);
        Assert.True(catalog.FailSave(failed)); Assert.False(catalog.PublishSaved(oldAttempt, Descriptor("Obsolete", saved.Id, oldAttempt.SavedRevision)));
        var page = await Page(catalog); Assert.Equal(saved, Assert.Single(page.Items)); Assert.Equal(baseline.CatalogRevision, page.CatalogRevision);
        Assert.False(catalog.PublishSaved(failed, Descriptor("Failed draft", saved.Id, failed.SavedRevision)));
    }

    [Fact]
    public async Task DeleteFencesPendingCompletionWithoutResurrectingOrKeepingItsOldDescriptor()
    {
        var saved = Descriptor(); var catalog = Loaded(saved); var baseline = await Page(catalog); var late = Save(catalog, saved.Id);
        Assert.True(catalog.Remove(saved.Id)); Assert.False(catalog.Remove(saved.Id));
        Assert.False(catalog.PublishSaved(late, Descriptor("Late save", saved.Id, late.SavedRevision)));
        var page = await Page(catalog); Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Empty(page.Items);
        Assert.True(page.CatalogRevision > baseline.CatalogRevision);
        var replacement = Save(catalog, saved.Id); var current = Descriptor("Explicit new save", saved.Id, replacement.SavedRevision);
        Assert.True(catalog.PublishSaved(replacement, current)); Assert.Equal(current, Assert.Single((await Page(catalog)).Items));
        Assert.False(catalog.PublishSaved(late, Descriptor("Still obsolete", saved.Id, late.SavedRevision)));
    }

    [Fact]
    public async Task RemovingAnUnpublishedPendingSaveAlsoFencesItsLateSuccess()
    {
        var catalog = Loaded(); var id = Guid.NewGuid(); var attempt = Save(catalog, id);
        Assert.True(catalog.Remove(id)); Assert.False(catalog.PublishSaved(attempt, Descriptor(id: id, revision: attempt.SavedRevision)));
        Assert.Empty((await Page(catalog)).Items);
    }

    [Fact]
    public async Task DeletedIdsDoNotConsumeALifetimeBudgetAndTheirOldAttemptsRemainInvalid()
    {
        var catalog = Loaded(); SavedMacroSaveAttempt? first = null;
        for (int index = 0; index <= MacroCatalogLimits.MaximumItems; index++)
        {
            var attempt = Save(catalog, Guid.NewGuid()); first ??= attempt;
            Assert.True(catalog.PublishSaved(attempt, Descriptor(id: attempt.MacroId, revision: attempt.SavedRevision)));
            Assert.True(catalog.Remove(attempt.MacroId));
        }
        Assert.NotNull(first);
        Assert.False(catalog.PublishSaved(first!, Descriptor(id: first!.MacroId, revision: first.SavedRevision)));
        var page = await Page(catalog); Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Empty(page.Items);
        Assert.NotNull(catalog.BeginSave(Guid.NewGuid()));
    }

    [Fact]
    public async Task ResetFencesBothSaveAndLoadCompletionsFromEarlierEpochs()
    {
        var saved = Descriptor(); var catalog = Loaded(saved); var lateSave = Save(catalog, saved.Id);
        var lateLoad = catalog.BeginLoad(); catalog.Reset();
        Assert.False(catalog.PublishSaved(lateSave, Descriptor("Late save", saved.Id, lateSave.SavedRevision)));
        Assert.False(catalog.CompleteLoad(lateLoad, new[] { Descriptor("Late library") })); Assert.False(catalog.FailLoad(lateLoad));
        Assert.Equal(MacroCatalogState.Starting, (await Page(catalog)).State);
        var current = catalog.BeginLoad(); var model = Descriptor("Current library"); Assert.True(catalog.CompleteLoad(current, new[] { model }));
        Assert.Equal(model, Assert.Single((await Page(catalog)).Items));
    }

    [Fact]
    public async Task NewerLoadWinsAndLateFailureCannotDowngradeTheSuccessfullyLoadedLibrary()
    {
        var catalog = Loaded(); var older = catalog.BeginLoad(); var newer = catalog.BeginLoad(); var current = Descriptor("Current");
        Assert.True(catalog.CompleteLoad(newer, new[] { current }));
        Assert.False(catalog.CompleteLoad(older, new[] { Descriptor("Old") })); Assert.False(catalog.FailLoad(older));
        Assert.Equal(current, Assert.Single((await Page(catalog)).Items));
    }

    [Fact]
    public async Task ForeignPublisherTokensCannotPublishOrReplaceEitherLibrary()
    {
        var first = Loaded(); var second = Loaded(); var id = Guid.NewGuid(); var token = Save(first, id);
        Assert.False(second.PublishSaved(token, Descriptor(id: id, revision: token.SavedRevision))); Assert.False(second.FailSave(token));
        var load = first.BeginLoad(); Assert.False(second.CompleteLoad(load, new[] { Descriptor() })); Assert.False(second.FailLoad(load));
        Assert.Empty((await Page(second)).Items); Assert.Equal(MacroCatalogState.Starting, (await Page(first)).State);
    }

    [Fact]
    public async Task DuplicateOrInvalidLoadedMetadataMakesTheLibraryUnavailableWithoutPartialRows()
    {
        var catalog = Loaded(); var good = Descriptor(); var attempt = catalog.BeginLoad();
        Assert.False(catalog.CompleteLoad(attempt, new[] { good, good with { SavedRevision = Guid.NewGuid() } }));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
        attempt = catalog.BeginLoad(); Assert.False(catalog.CompleteLoad(attempt, new[] { good, Descriptor() with { Name = "bad\0name" } }));
        var failed = await Page(catalog); Assert.Equal(MacroCatalogState.Unavailable, failed.State); Assert.Empty(failed.Items);
    }

    [Fact]
    public async Task OversizedLibraryIsUnavailableAndNeverSilentlyTruncatedToTenThousand()
    {
        var catalog = Loaded(); var items = Enumerable.Range(0, MacroCatalogLimits.MaximumItems + 1).Select(value => Descriptor($"Macro {value}")).ToArray();
        Assert.False(catalog.CompleteLoad(catalog.BeginLoad(), items));
        var page = await Page(catalog); Assert.Equal(MacroCatalogState.Unavailable, page.State); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task ExactLibraryBudgetAllowsExistingSaveButNewOverflowInvalidatesPartialCatalog()
    {
        var items = Enumerable.Range(0, MacroCatalogLimits.MaximumItems).Select(value => Descriptor($"Macro {value:00000}")).ToArray(); var catalog = Loaded(items);
        var page = await Page(catalog); Assert.Equal(MacroCatalogLimits.MaximumItems, page.Total); Assert.Equal(100, page.Items.Count); Assert.Equal(100, page.NextOffset);
        var save = Save(catalog, items[0].Id); Assert.True(catalog.PublishSaved(save, items[0] with { SavedRevision = save.SavedRevision }));
        Assert.Null(catalog.BeginSave(Guid.NewGuid()));
        var overflow = await Page(catalog); Assert.Equal(MacroCatalogState.Unavailable, overflow.State); Assert.Empty(overflow.Items);
    }

    [Fact]
    public async Task PendingNewSaveBudgetIsBoundedAndFailureOrRemovalReleasesReservations()
    {
        var catalog = Loaded(); var attempts = Enumerable.Range(0, MacroCatalogLimits.MaximumItems).Select(_ => Save(catalog, Guid.NewGuid())).ToArray();
        Assert.True(catalog.FailSave(attempts[0])); var replacement = Save(catalog, Guid.NewGuid());
        Assert.True(catalog.Remove(attempts[1].MacroId)); var another = Save(catalog, Guid.NewGuid());
        Assert.Null(catalog.BeginSave(Guid.NewGuid()));
        Assert.False(catalog.PublishSaved(replacement, Descriptor(id: replacement.MacroId, revision: replacement.SavedRevision)));
        Assert.False(catalog.PublishSaved(another, Descriptor(id: another.MacroId, revision: another.SavedRevision)));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
        Assert.True(catalog.CompleteLoad(catalog.BeginLoad(), Array.Empty<SavedMacroDescriptor>()));
        Assert.NotNull(catalog.BeginSave(Guid.NewGuid()));
    }

    [Fact]
    public async Task SuccessfulPublicationRequiresTheExactAttemptIdAndRevisionAndReportsInvalidMetadataExplicitly()
    {
        var catalog = Loaded(Descriptor()); var id = Guid.NewGuid(); var token = Save(catalog, id);
        Assert.False(catalog.PublishSaved(token, Descriptor(id: Guid.NewGuid(), revision: token.SavedRevision)));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
        Assert.True(catalog.CompleteLoad(catalog.BeginLoad(), Array.Empty<SavedMacroDescriptor>())); token = Save(catalog, id);
        Assert.False(catalog.PublishSaved(token, Descriptor(id: id, revision: Guid.NewGuid())));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
        Assert.True(catalog.CompleteLoad(catalog.BeginLoad(), Array.Empty<SavedMacroDescriptor>())); token = Save(catalog, id);
        Assert.False(catalog.PublishSaved(token, Descriptor(id: id, revision: token.SavedRevision) with { TargetMode = "ExecuteProcess" }));
        Assert.Equal(MacroCatalogState.Unavailable, (await Page(catalog)).State);
    }

    [Fact]
    public void QueryValidationRejectsUnboundedOrIncompleteCursors()
    {
        var host = Guid.NewGuid();
        var invalid = new[] { new MacroCatalogQuery(-1), new(10001), new(Limit: 0), new(Limit: 101),
            new(1), new(HostSessionId: Guid.Empty, CatalogRevision: 1), new(HostSessionId: host), new(CatalogRevision: 1),
            new(HostSessionId: host, CatalogRevision: 0) };
        foreach (var query in invalid) Assert.Throws<ArgumentException>(() => query.Validated());
        Assert.Equal(10000, new MacroCatalogQuery(10000, 100, host, 1).Validated().Offset);
    }

    [Fact]
    public void DescriptorValidationBoundsDisplayMetadataAndRejectsMalformedUtf16OrControlCharacters()
    {
        var good = Descriptor(); var invalid = new[] { good with { Id = Guid.Empty }, good with { SavedRevision = Guid.Empty },
            good with { Name = "" }, good with { Name = new string('a', 201) }, good with { Name = "bad\nname" },
            good with { Name = "\ud800" }, good with { Name = "\udfff" }, good with { Hotkey = new string('a', 81) },
            good with { TopLevelActions = -1 }, good with { TopLevelActions = 100001 }, good with { TargetMode = "Unknown" } };
        foreach (var descriptor in invalid) Assert.Throws<ArgumentException>(() => descriptor.Validated());
        Assert.Equal(200, (good with { Name = new string('a', 200), Hotkey = "", TopLevelActions = 100000 }).Validated().Name.Length);
        foreach (string mode in new[] { "Legacy", "Screen", "ActiveExternalWindow", "Application", "SpecificWindow" }) (good with { TargetMode = mode }).Validated();
        Assert.Equal("😀", (good with { Name = "😀" }).Validated().Name);
    }

    [Fact]
    public void PageValidationFreezesCallerCollectionsAndRejectsForgedCursorOrImpossibleShapes()
    {
        var host = Guid.NewGuid(); var caller = new List<SavedMacroDescriptor> { Descriptor("A"), Descriptor("B") };
        var query = new MacroCatalogQuery(Limit: 2);
        var page = new MacroCatalogPage(MacroCatalogState.Ready, host, 5, "controlled", caller, 3, 2, MacroCatalogMessages.For(MacroCatalogState.Ready));
        var frozen = page.Validated(query); caller.Clear(); Assert.Equal(2, frozen.Items.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<SavedMacroDescriptor>)frozen.Items).Clear());
        var malformed = new[] { frozen with { HostSessionId = Guid.Empty }, frozen with { CatalogRevision = 0 }, frozen with { Total = 10001 },
            frozen with { NextOffset = 1 }, frozen with { Explanation = "Untrusted peer text" }, frozen with { State = (MacroCatalogState)99 },
            frozen with { Items = new[] { frozen.Items[0], frozen.Items[0] } }, frozen with { Items = Array.Empty<SavedMacroDescriptor>() },
            frozen with { Items = new[] { Descriptor() with { Id = Guid.Empty }, Descriptor() } } };
        foreach (var value in malformed) Assert.Throws<InvalidDataException>(() => value.Validated(query));
        Assert.Throws<InvalidDataException>(() => frozen.Validated(query with { HostSessionId = Guid.NewGuid(), CatalogRevision = 5 }));
        Assert.Throws<InvalidDataException>(() => frozen.Validated(query with { HostSessionId = host, CatalogRevision = 6 }));
    }

    [Fact]
    public void FailureFactoriesHaveClosedStatesFixedMessagesAndNoRowsOrExecutionFields()
    {
        foreach (var state in Enum.GetValues<MacroCatalogState>().Where(state => state != MacroCatalogState.Ready))
        {
            var page = MacroCatalogPage.Failure(state).Validated(); Assert.Equal(Guid.Empty, page.HostSessionId);
            Assert.Equal(0, page.CatalogRevision); Assert.Empty(page.Items); Assert.Equal(0, page.Total); Assert.Null(page.NextOffset);
            Assert.Equal(MacroCatalogMessages.For(state), page.Explanation);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => MacroCatalogPage.Failure(MacroCatalogState.Ready));
        var descriptor = Descriptor(); using var json = JsonDocument.Parse(JsonSerializer.Serialize(descriptor));
        Assert.Equal(new[] { "Enabled", "Hotkey", "Id", "Name", "SavedRevision", "TargetMode", "TopLevelActions" },
            json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(1, MacroHostProtocol.Version); Assert.Equal("Zenith.Macros.Personal", MacroHostProtocol.Product);
        Assert.Equal(256 * 1024, MacroHostProtocol.MaximumFrameBytes);
    }

    [Fact]
    public async Task CancelledPageRequestDoesNotMutateSavedMetadataOrPoisonSubsequentRequests()
    {
        var saved = Descriptor(); var catalog = Loaded(saved); var baseline = await Page(catalog);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetPageAsync(new(), stop.Token));
        var next = await Page(catalog); Assert.Equal(baseline.CatalogRevision, next.CatalogRevision); Assert.Equal(saved, Assert.Single(next.Items));
    }
}
