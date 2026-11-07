using Zenith.MacroHost.Contracts;
using Zenith.Sessions.App;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class MacroAssignmentHostReviewTests
{
    private static readonly Guid Host = Guid.NewGuid();
    private static SavedMacroDescriptor Macro(int index) => new(new Guid(index + 1, 0, 0, new byte[8]),
        new Guid(index + 500, 0, 0, new byte[8]), "Saved macro " + index, "F4", 2, true, "Screen");
    private static MacroCatalogPage Page(MacroCatalogQuery query, int total = 101) => new(MacroCatalogState.Ready, Host, 1,
        "2.5.12-fixture", Enumerable.Range(query.Offset, Math.Max(0, Math.Min(query.Limit, total - query.Offset))).Select(Macro).ToArray(),
        total, query.Offset + query.Limit < total ? query.Offset + query.Limit : null, MacroCatalogMessages.For(MacroCatalogState.Ready));

    [Fact]
    public async Task CaptureRequiresASelectedDescriptorInTheCurrentNonstalePage()
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        vm.SelectedMacro = Macro(0); Assert.Null(vm.CaptureSelection()); Assert.Equal(0, client.Calls);
        await vm.RefreshAsync(); vm.SelectedMacro = Macro(100); Assert.Null(vm.CaptureSelection());
        vm.SelectedMacro = vm.Macros[0]; Assert.NotNull(vm.CaptureSelection());
        vm.SelectedMacro = vm.Macros[0] with { Name = "Forged display fields" }; Assert.Null(vm.CaptureSelection());
        vm.SelectedMacro = vm.Macros[0]; vm.CancelWork(); Assert.Null(vm.CaptureSelection());
    }

    [Fact]
    public async Task RevalidationPinsTheActualOffFirstPageAndNeverRedirectsSelection()
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); await vm.NextAsync(); vm.SelectedMacro = Assert.Single(vm.Macros);
        var selected = vm.CaptureSelection()!; MacroCatalogQuery? requested = null;
        client.Read = (query, _) => { requested = query; vm.SelectedMacro = null; return Task.FromResult(Page(query)); };
        Assert.True(await vm.RevalidateSelectionAsync(selected));
        Assert.NotNull(requested); Assert.Equal(100, requested.Offset); Assert.Equal(Host, requested.HostSessionId);
        Assert.Equal(1, requested.CatalogRevision); Assert.Equal(Macro(100), selected.Macro); Assert.Null(vm.SelectedMacro);
    }

    [Theory]
    [InlineData(MacroCatalogState.Changed)]
    [InlineData(MacroCatalogState.Disconnected)]
    [InlineData(MacroCatalogState.Unavailable)]
    public async Task ObservedFailureInvalidatesChoiceWithoutCallingAnOffPageMacroMissing(MacroCatalogState state)
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); vm.SelectedMacro = vm.Macros[0]; var selected = vm.CaptureSelection()!;
        client.Read = (_, _) => Task.FromResult(MacroCatalogPage.Failure(state));
        Assert.False(await vm.RevalidateSelectionAsync(selected)); Assert.True(vm.IsStale); Assert.Null(vm.CaptureSelection());
        Assert.Contains(selected.Macro, vm.Macros);
    }

    [Fact]
    public async Task ReentrantStopDuringTheReviewCannotReturnAValidatedChoice()
    {
        // CancelWork itself raises Macros; use a one-shot observer to avoid recursive event delivery.
        int notifications = 0;
        await using var owned = new MacroHostCatalogViewModel(new Client(), () => true);
        await owned.RefreshAsync(); owned.SelectedMacro = owned.Macros[0]; var expected = owned.CaptureSelection()!;
        owned.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(owned.Macros) && notifications++ == 0) owned.CancelWork(); };
        Assert.False(await owned.RevalidateSelectionAsync(expected)); Assert.Null(owned.CaptureSelection());
    }

    [Fact]
    public async Task ForeignGenerationOrPageDoesNotInvokeTheClientAndCancellationRemainsExplicit()
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); vm.SelectedMacro = vm.Macros[0]; var selected = vm.CaptureSelection()!;
        Assert.False(await vm.RevalidateSelectionAsync(selected with { HostSessionId = Guid.NewGuid() }));
        Assert.False(await vm.RevalidateSelectionAsync(selected with { Offset = 100 })); Assert.Equal(1, client.Calls);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RevalidateSelectionAsync(selected, stop.Token));
        Assert.Equal(1, client.Calls);
    }

    private sealed class Client : IZenithMacroCatalogClient
    {
        public int Calls;
        public Func<MacroCatalogQuery, CancellationToken, Task<MacroCatalogPage>> Read = (query, _) => Task.FromResult(Page(query));
        public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default) { Calls++; return Read(query, ct); }
    }
}
