using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ClientWindowArrangementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static ClientDisplay Display(string id = "synthetic-left", int scale = 100) => new(id, id,
        new(-1920, -120, 1920, 1080), scale, false);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        (nint)(1000 + pid), "Synthetic", false, true, 0, 0, null, Now);
    private sealed class Adapter : IClientWindowAdapter
    {
        public ClientDisplay[] Monitors = [Display()];
        public readonly List<(SessionObservation Session, PixelRectangle Bounds)> Moves = [];
        public readonly List<ClientWindowAction> Changes = [];
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public readonly Dictionary<nint, PixelRectangle> Positions = [];
        public bool Current = true, Minimized, Maximized, Resizable = true, Denied, Block, Confirm = true;
        public Func<ClientDisplayList>? DisplayQuery;
        public Action? BeforeMutation, AfterMutation;
        public ClientDisplayList Displays(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return DisplayQuery?.Invoke() ?? new(Array.AsReadOnly(Monitors), "Synthetic"); }
        public ClientWindowCheck Check(SessionObservation session) => new(Current && session.ProcessStartUtcTicks == Now.AddMinutes(-1).UtcTicks
            && session.Window == (nint)(1000 + session.ProcessId), Minimized, true, Resizable && !Minimized && !Maximized, "Synthetic client identity changed.", Maximized);
        public WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped)
        {
            BeforeMutation?.Invoke();
            var state = Check(session);
            if (!state.Current) return WindowPostStatus.Invalid;
            if (!state.CanArrange) return WindowPostStatus.Unsupported;
            if (stopped()) return WindowPostStatus.Cancelled;
            if (Denied) return WindowPostStatus.Denied;
            Entered.Set();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic mutation was not released.");
            Moves.Add((session, bounds)); if (Confirm) Positions[session.Window] = bounds;
            AfterMutation?.Invoke(); return WindowPostStatus.Accepted;
        }
        public WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped)
        {
            BeforeMutation?.Invoke(); if (!Check(session).Current) return WindowPostStatus.Invalid;
            if (stopped()) return WindowPostStatus.Cancelled;
            Changes.Add(action); Minimized = action == ClientWindowAction.Minimize; return WindowPostStatus.Accepted;
        }
        public PixelRectangle? Bounds(SessionObservation session) => Positions.TryGetValue(session.Window, out var value) ? value : null;
    }

    [Theory]
    [InlineData(100)] [InlineData(125)] [InlineData(150)] [InlineData(200)]
    public void GeometryUsesPhysicalNegativeOriginsScaledGapsAndMinimums(int scale)
    {
        var display = Display(scale: scale) with { WorkArea = new(-3840, -2160, 3840, 2160) };
        var options = new ClientWindowTileOptions(GapDip: 9, Columns: 3);
        var plan = ClientWindowTilePlanner.Plan(display, 6, options);
        Assert.True(plan.Fits); Assert.Equal(6, plan.Cells.Count);
        int gap = (9 * scale + 99) / 100;
        Assert.Equal(display.WorkArea.X + gap, plan.Cells[0].X); Assert.Equal(display.WorkArea.Y + gap, plan.Cells[0].Y);
        Assert.All(plan.Cells, cell =>
        {
            Assert.True(display.WorkArea.Contains(cell)); Assert.True(cell.Width >= (320 * scale + 99) / 100);
            Assert.True(cell.Height >= (240 * scale + 99) / 100);
        });
        for (int i = 0; i < plan.Cells.Count; i++) for (int j = i + 1; j < plan.Cells.Count; j++)
        {
            var a = plan.Cells[i]; var b = plan.Cells[j];
            Assert.True(a.X + a.Width + gap <= b.X || b.X + b.Width + gap <= a.X
                || a.Y + a.Height + gap <= b.Y || b.Y + b.Height + gap <= a.Y);
        }
    }

    [Fact]
    public void UnfitMinimumsInvalidScaleAndOverflowNeverProduceOffscreenOrUndersizedCells()
    {
        Assert.False(ClientWindowTilePlanner.Plan(Display(scale: 200), 16, new()).Fits);
        Assert.False(ClientWindowTilePlanner.Plan(Display() with { ScalePercent = 0 }, 1, new()).Fits);
        Assert.False(ClientWindowTilePlanner.Plan(Display() with { WorkArea = new(int.MaxValue - 10, 0, 100, 1000) }, 1, new()).Fits);
        Assert.False(ClientWindowTilePlanner.Plan(Display(), 129, new()).Fits);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientWindowTilePlanner.Plan(Display(), 1, new(GapDip: -1)));
    }

    [Fact]
    public async Task StableProcessOrderAndChosenMonitorAreFrozenBeforeAsynchronousWork()
    {
        var adapter = new Adapter { Monitors = [Display("primary") with { IsPrimary = true, WorkArea = new(0, 0, 1920, 1080) }, Display()] };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        SessionObservation[] selected = [Session(40), Session(10), Session(30)];
        var result = await operations.ArrangeAsync(selected, Display().Id, new(), default);
        Assert.True(result.AllApplied); Assert.False(result.Cancelled);
        Assert.Equal(new[] { 10, 30, 40 }, adapter.Moves.Select(move => move.Session.ProcessId));
        Assert.All(adapter.Moves, move => Assert.True(Display().WorkArea.Contains(move.Bounds)));
    }

    [Fact]
    public async Task ChangingTheCallerSelectionWhileQueuedCannotRedirectTheFrozenRequest()
    {
        var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        await lane.WaitAsync();
        SessionObservation[] selected = [Session(8), Session(3)];
        var request = operations.ArrangeAsync(selected, Display().Id, new(), default);
        selected[0] = Session(99); selected[1] = Session(98); lane.Release();
        Assert.True((await request).AllApplied);
        Assert.Equal(new[] { 3, 8 }, adapter.Moves.Select(move => move.Session.ProcessId));
    }

    [Fact]
    public async Task IdentityChangedAtMutationBoundaryIsNeverMoved()
    {
        var adapter = new Adapter(); adapter.BeforeMutation = () => adapter.Current = false;
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ArrangeAsync([Session(1)], Display().Id, new(), default);
        Assert.Equal(ClientWindowPlacementStatus.InvalidClient, Assert.Single(result.Results).Status); Assert.Empty(adapter.Moves);
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)]
    public async Task MinimizedAndNonresizableClientsAreNotAutomaticallyRestored(bool minimized, bool fixedSize)
    {
        var adapter = new Adapter { Minimized = minimized, Resizable = !fixedSize };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ArrangeAsync([Session(1)], Display().Id, new(), default);
        Assert.Equal(ClientWindowPlacementStatus.UnsupportedState, Assert.Single(result.Results).Status);
        Assert.Empty(adapter.Moves); Assert.Empty(adapter.Changes);
    }

    [Fact]
    public async Task DisplayWorkareaChangesReturnExplicitPartialResultsWithoutMovingLaterClients()
    {
        var adapter = new Adapter(); int displayQueries = 0;
        adapter.DisplayQuery = () => new(++displayQueries <= 3 ? new[] { Display() }
            : new[] { Display() with { WorkArea = new(-1600, 0, 1600, 900) } }, "Synthetic changed display");
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ArrangeAsync([Session(1), Session(2)], Display().Id, new(), default);
        Assert.Equal(ClientWindowPlacementStatus.Applied, result.Results[0].Status);
        Assert.Equal(ClientWindowPlacementStatus.DisplayUnavailable, result.Results[1].Status); Assert.Single(adapter.Moves);
    }

    [Fact]
    public async Task MissingDisplayAndUnfitLayoutDispatchNoMutations()
    {
        var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var missing = await operations.ArrangeAsync([Session(1)], "removed-display", new(), default);
        Assert.Equal(ClientWindowPlacementStatus.DisplayUnavailable, Assert.Single(missing.Results).Status);
        var unfit = await operations.ArrangeAsync([Session(1), Session(2)], Display().Id, new(MinimumWidthDip: 8192), default);
        Assert.All(unfit.Results, result => Assert.Equal(ClientWindowPlacementStatus.LayoutUnavailable, result.Status)); Assert.Empty(adapter.Moves);
    }

    [Fact]
    public async Task DeniedAndUnconfirmedRequestsNeverReportApplied()
    {
        var adapter = new Adapter { Denied = true }; using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        Assert.Equal(ClientWindowPlacementStatus.Denied, Assert.Single((await operations.ArrangeAsync([Session(1)], Display().Id, new(), default)).Results).Status);
        adapter.Denied = false; adapter.Confirm = false;
        var result = await operations.ArrangeAsync([Session(1)], Display().Id, new(), default);
        Assert.Equal(ClientWindowPlacementStatus.NotConfirmed, Assert.Single(result.Results).Status); Assert.False(result.AllApplied);
    }

    [Fact]
    public async Task PanicAfterPostedRequestCancelsEveryLaterMutation()
    {
        var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        adapter.AfterMutation = operations.Cancel;
        var result = await operations.ArrangeAsync([Session(1), Session(2), Session(3)], Display().Id, new(), default);
        Assert.True(result.Cancelled); Assert.Single(adapter.Moves);
        Assert.All(result.Results, row => Assert.Equal(ClientWindowPlacementStatus.Cancelled, row.Status));
    }

    [Fact]
    public async Task CancellationAndDuplicateSelectionsDoNotDispatch()
    {
        var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.True((await operations.ArrangeAsync([Session(1), Session(2)], Display().Id, new(), stop.Token)).Cancelled);
        var duplicates = await operations.ArrangeAsync([Session(1), Session(1)], Display().Id, new(), default);
        Assert.All(duplicates.Results, row => Assert.Equal(ClientWindowPlacementStatus.InvalidClient, row.Status)); Assert.Empty(adapter.Moves);
    }

    [Fact]
    public async Task ArrangementAndMinimizeRestoreShareOneLaneAndQueuedWorkIsFencedByPanic()
    {
        var adapter = new Adapter { Block = true }; using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        var arrangement = operations.ArrangeAsync([Session(1)], Display().Id, new(), default);
        Assert.True(await Task.Run(() => adapter.Entered.Wait(TimeSpan.FromSeconds(5))));
        var minimize = operations.ChangeAsync(Session(2), ClientWindowAction.Minimize, default);
        Assert.False(minimize.IsCompleted); Assert.Empty(adapter.Changes);
        operations.Cancel(); adapter.Release.Set();
        Assert.True((await arrangement).Cancelled); Assert.False((await minimize).Applied); Assert.Empty(adapter.Changes);
    }

    [Fact]
    public async Task RestoreDoesNotClaimCompletionWhileTheVisibleWindowRemainsMaximized()
    {
        var adapter = new Adapter { Maximized = true }; using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeAsync(Session(1), ClientWindowAction.Restore, default);
        Assert.False(result.Applied); Assert.Single(adapter.Changes);
    }
}
