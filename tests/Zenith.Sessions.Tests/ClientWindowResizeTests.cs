using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Pure planning and a synthetic adapter only. No HWND query or real window operation is made.
public sealed class ClientWindowResizeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        (nint)(1000 + pid), "Synthetic", false, true, 0, 0, null, Now);
    private static ClientDisplay Display(string id = "synthetic-left", int scale = 100) => new(id, id, new(-1920, -120, 1920, 1080), scale, false);
    private static ClientWindowGeometry Geometry(ClientDisplay? display = null, int x = -1000, int y = 50,
        int width = 640, int height = 480, int left = 7, int top = 29, int right = 10, int bottom = 10) =>
        new(new(x, y, width + left + right, height + top + bottom), new(x + left, y + top, width, height), display ?? Display());

    [Theory]
    [InlineData(160, 120)]
    [InlineData(320, 240)]
    [InlineData(8192, 8192)]
    public void PhysicalContentBoundsAcceptDocumentedLimits(int width, int height)
    { var size = new ClientContentSize(width, height); Assert.Same(size, size.Validated()); }

    [Theory]
    [InlineData(159, 120)]
    [InlineData(160, 119)]
    [InlineData(8193, 120)]
    [InlineData(160, 8193)]
    [InlineData(0, 0)]
    [InlineData(-1, int.MaxValue)]
    public void InvalidSizesAreRejectedBeforeAnyNativeWork(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ClientContentSize(width, height).Validated());

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(225)]
    public void PlannerUsesOddObservedFrameMarginsAndDoesNotScaleRequestedContent(int scale)
    {
        var geometry = Geometry(Display(scale: scale)); var plan = ClientWindowResizePlanner.Plan(geometry, new(321, 241));
        Assert.True(plan.Fits); Assert.Equal(new PixelRectangle(-1000, 50, 338, 280), plan.OuterBounds);
        Assert.True(geometry.Display.WorkArea.Contains(plan.OuterBounds!.Value));
    }

    [Fact]
    public void PlannerClampsWithinTheCurrentNegativeMonitorInsteadOfCrossingTheSeam()
    {
        var geometry = Geometry(x: -200, y: 850); var plan = ClientWindowResizePlanner.Plan(geometry, new(640, 480));
        Assert.True(plan.Fits); Assert.Equal(new PixelRectangle(-657, 441, 657, 519), plan.OuterBounds);
        Assert.True(geometry.Display.WorkArea.Contains(plan.OuterBounds!.Value));
        Assert.Equal(0, (long)plan.OuterBounds.Value.X + plan.OuterBounds.Value.Width);
    }

    [Fact]
    public void ContentThatFitsWithoutItsFrameIsStillRejectedWhenTheOuterWindowCannotFit()
    {
        var plan = ClientWindowResizePlanner.Plan(Geometry(), new(1920, 1080));
        Assert.False(plan.Fits); Assert.Null(plan.OuterBounds); Assert.Contains("frame", plan.Explanation);
        Assert.False(ClientWindowResizePlanner.Plan(Geometry(), new(8192, 8192)).Fits);
    }

    [Fact]
    public void InvalidGeometryAndExtremeCoordinatesCannotProduceUnsafeBounds()
    {
        var geometry = Geometry();
        Assert.False(ClientWindowResizePlanner.Plan(geometry with { ContentBounds = new(-3000, 0, 640, 480) }, new(320, 240)).Fits);
        Assert.False(ClientWindowResizePlanner.Plan(geometry with { Display = geometry.Display with { WorkArea = default } }, new(320, 240)).Fits);
        var edge = new ClientDisplay("edge", "edge", new(int.MaxValue - 1000, int.MinValue, 1000, 1000), 100, false);
        var plan = ClientWindowResizePlanner.Plan(Geometry(edge, int.MaxValue - 700, int.MinValue + 1), new(640, 480));
        Assert.True(plan.Fits); Assert.True(edge.WorkArea.Contains(plan.OuterBounds!.Value));
    }

    [Fact]
    public async Task ExactContentIsConfirmedUsingEachClientsOwnMonitorAndMargins()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        var secondDisplay = new ClientDisplay("right", "right", new(0, 0, 2560, 1400), 150, true);
        adapter.Monitors = [Display(), secondDisplay];
        adapter.Geometries[Session(1).Window] = Geometry();
        adapter.Geometries[Session(2).Window] = Geometry(secondDisplay, 100, 80, left: 11, top: 44, right: 15, bottom: 15);
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(2), Session(1)], new(320, 240), default);
        Assert.True(result.AllApplied); Assert.False(result.Cancelled); Assert.Equal(2, adapter.Posts.Count);
        Assert.Equal(new PixelRectangle(-1000, 50, 337, 279), adapter.Posts[0].Bounds);
        Assert.Equal(new PixelRectangle(100, 80, 346, 299), adapter.Posts[1].Bounds);
        Assert.All(result.Results, row => Assert.Contains("content size 320 × 240", row.Explanation));
        Assert.Equal(0, adapter.StateChanges); Assert.Equal(0, adapter.TilingMoves);
    }

    [Fact]
    public async Task OuterBoundsAloneCannotFalselyConfirmTheContentSize()
    {
        using var adapter = new Adapter { WrongContent = true }; using var lane = new SemaphoreSlim(1, 1);
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(1)], new(320, 240), default);
        Assert.Single(adapter.Posts); Assert.Equal(ClientWindowPlacementStatus.NotConfirmed, Assert.Single(result.Results).Status);
        Assert.False(result.AllApplied); Assert.Contains("content size", result.Results[0].Explanation);
    }

    [Theory]
    [InlineData("minimized")]
    [InlineData("maximized")]
    [InlineData("hidden")]
    [InlineData("fixed")]
    [InlineData("unavailable")]
    [InlineData("reused-pid")]
    [InlineData("reused-window")]
    public async Task UnsupportedOrReusedClientsAreNeverResized(string condition)
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); var session = Session(1);
        switch (condition)
        {
            case "minimized": adapter.Minimized = true; break;
            case "maximized": adapter.Maximized = true; break;
            case "hidden": adapter.Visible = false; break;
            case "fixed": adapter.Resizable = false; break;
            case "unavailable": adapter.NoGeometry = true; break;
            case "reused-pid": session = session with { ProcessStartUtcTicks = Now.UtcTicks }; break;
            case "reused-window": session = session with { Window = (nint)9999 }; break;
        }
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([session], new(320, 240), default);
        Assert.False(result.AllApplied); Assert.Empty(adapter.Posts); Assert.Equal(0, adapter.StateChanges);
        Assert.Equal(condition.StartsWith("reused", StringComparison.Ordinal) ? ClientWindowPlacementStatus.InvalidClient : ClientWindowPlacementStatus.UnsupportedState,
            Assert.Single(result.Results).Status);
    }

    [Theory]
    [InlineData("workarea")]
    [InlineData("scale")]
    [InlineData("removed")]
    public async Task DisplayChangesBeforeDispatchPreventTheRequest(string change)
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        adapter.DisplayQuery = () => new(change switch
        {
            "removed" => Array.Empty<ClientDisplay>(),
            "scale" => [Display(scale: 150)],
            _ => [Display() with { WorkArea = new(-1920, -120, 1920, 1000) }]
        }, "Synthetic change");
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(1)], new(320, 240), default);
        Assert.Equal(ClientWindowPlacementStatus.DisplayUnavailable, Assert.Single(result.Results).Status); Assert.Empty(adapter.Posts);
    }

    [Fact]
    public async Task GeometryChangedAtTheLastNativeBoundaryIsRejectedInsteadOfUsingOldMargins()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        adapter.BeforePost = () => adapter.Geometries[Session(1).Window] = Geometry(left: 11);
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(1)], new(320, 240), default);
        Assert.Equal(ClientWindowPlacementStatus.NotConfirmed, Assert.Single(result.Results).Status); Assert.Empty(adapter.Posts);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("pid")]
    [InlineData("state")]
    public async Task ChangedContextAfterAnAcceptedRequestCannotBeReportedApplied(string change)
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        adapter.AfterPost = () =>
        {
            if (change == "pid") adapter.Current = false;
            else if (change == "state") adapter.Maximized = true;
            else adapter.Geometries[Session(1).Window] = adapter.Geometries[Session(1).Window] with { Display = Display(scale: 150) };
        };
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(1)], new(320, 240), default);
        Assert.Single(adapter.Posts); Assert.False(result.AllApplied);
        Assert.Equal(change switch { "pid" => ClientWindowPlacementStatus.InvalidClient, "state" => ClientWindowPlacementStatus.UnsupportedState,
            _ => ClientWindowPlacementStatus.DisplayUnavailable }, Assert.Single(result.Results).Status);
    }

    [Fact]
    public async Task ATooLargeClientDoesNotPreventOtherSelectedClientsFromBeingSized()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        var small = Display() with { Id = "small", WorkArea = new(-1000, 0, 300, 250) };
        adapter.Monitors = [Display(), small]; adapter.Geometries[Session(1).Window] = Geometry(small);
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync([Session(1), Session(2)], new(320, 240), default);
        Assert.Equal(ClientWindowPlacementStatus.LayoutUnavailable, result.Results[0].Status);
        Assert.Equal(ClientWindowPlacementStatus.Applied, result.Results[1].Status); Assert.Single(adapter.Posts);
    }

    [Fact]
    public async Task DuplicateProcessOrWindowSelectionsAreRejectedWithoutDoubleMutation()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        var result = await new ClientWindowOperations(lane, adapter).ResizeAsync(
            [Session(1), Session(1) with { Window = (nint)9999 }, Session(2), Session(3) with { Window = Session(2).Window }], new(320, 240), default);
        Assert.All(result.Results, row => Assert.Equal(ClientWindowPlacementStatus.InvalidClient, row.Status)); Assert.Empty(adapter.Posts);
    }

    [Fact]
    public async Task QueuedSelectionAndSizeArePinnedAndExplicitCancellationFencesTheWholeLane()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); await lane.WaitAsync();
        var operations = new ClientWindowOperations(lane, adapter); var selected = new[] { Session(1) };
        Task<ClientWindowArrangementResult> resize = operations.ResizeAsync(selected, new(320, 240), default);
        selected[0] = Session(2); lane.Release();
        Assert.Equal(Session(1), Assert.Single((await resize).Results).Session); Assert.Equal(Session(1), Assert.Single(adapter.Posts).Session);
        await lane.WaitAsync();
        Task<ClientWindowArrangementResult> cancelled = operations.ResizeAsync([Session(3)], new(640, 480), default);
        operations.Cancel(); lane.Release();
        Assert.True((await cancelled).Cancelled); Assert.Single(adapter.Posts);
    }

    [Fact]
    public async Task LateAcceptedOsRequestIsJoinedButCancelledAndNoLaterWindowIsChanged()
    {
        using var adapter = new Adapter { Block = true }; using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        Task<ClientWindowArrangementResult> resize = operations.ResizeAsync([Session(1), Session(2)], new(320, 240), default);
        Task<WindowActionResult>? minimize = null;
        try
        {
            await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            minimize = operations.ChangeAsync(Session(3), ClientWindowAction.Minimize, default);
            operations.Cancel(); Assert.False(resize.IsCompleted); Assert.False(minimize.IsCompleted);
            adapter.Release.Set();
            var result = await resize.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Cancelled); Assert.All(result.Results, row => Assert.Equal(ClientWindowPlacementStatus.Cancelled, row.Status));
            Assert.False((await minimize.WaitAsync(TimeSpan.FromSeconds(3))).Applied);
            Assert.Single(adapter.Posts); Assert.Equal(0, adapter.StateChanges);
        }
        finally { adapter.Release.Set(); await resize; if (minimize is not null) await minimize; }
    }

    [Fact]
    public async Task CancellationBeforeQueuedDispatchDoesNotQueryOrChangeAWindow()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1); await lane.WaitAsync();
        var operations = new ClientWindowOperations(lane, adapter); using var stop = new CancellationTokenSource();
        Task<ClientWindowArrangementResult> pending = operations.ResizeAsync([Session(1)], new(320, 240), stop.Token);
        try { stop.Cancel(); Assert.True((await pending).Cancelled); Assert.Equal(0, adapter.Checks); Assert.Empty(adapter.Posts); }
        finally { lane.Release(); await pending; }
    }

    [Fact]
    public async Task BatchBoundsAndDefaultUnsupportedGeometryRemainCompatible()
    {
        using var adapter = new Adapter(); using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.ResizeAsync(Enumerable.Range(1, 129).Select(Session).ToArray(), new(320, 240), default));
        Assert.Empty(adapter.Posts);
        Assert.Empty((await operations.ResizeAsync([], new(320, 240), default)).Results);
        var oldAdapter = new LegacyAdapter();
        var result = await new ClientWindowOperations(lane, oldAdapter).ResizeAsync([Session(1)], new(320, 240), default);
        Assert.Equal(ClientWindowPlacementStatus.UnsupportedState, Assert.Single(result.Results).Status);
    }

    private sealed class LegacyAdapter : IClientWindowAdapter
    {
        public ClientDisplayList Displays(CancellationToken ct) => new([Display()], "Synthetic");
        public ClientWindowCheck Check(SessionObservation session) => new(true, false, true, true, "Synthetic");
        public WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped) => throw new InvalidOperationException();
        public WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped) => throw new InvalidOperationException();
        public PixelRectangle? Bounds(SessionObservation session) => null;
    }
    private sealed class Adapter : IClientWindowAdapter, IDisposable
    {
        public ClientDisplay[] Monitors = [Display()];
        public readonly Dictionary<nint, ClientWindowGeometry> Geometries = [];
        public readonly List<(SessionObservation Session, PixelRectangle Bounds)> Posts = [];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public bool Current = true, Minimized, Maximized, Visible = true, Resizable = true, NoGeometry, WrongContent, Block;
        public int Checks, StateChanges, TilingMoves;
        public Func<ClientDisplayList>? DisplayQuery; public Action? BeforePost, AfterPost;
        public ClientDisplayList Displays(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return DisplayQuery?.Invoke() ?? new(Monitors, "Synthetic"); }
        public ClientWindowCheck Check(SessionObservation session)
        {
            Interlocked.Increment(ref Checks);
            return new(Current && session.ProcessId > 0 && session.ProcessStartUtcTicks == Now.AddMinutes(-1).UtcTicks
                && session.Window == (nint)(1000 + session.ProcessId), Minimized, Visible, Visible && Resizable && !Minimized && !Maximized, "Synthetic identity changed", Maximized);
        }
        public ClientWindowGeometry? Geometry(SessionObservation session)
        {
            if (NoGeometry || !Check(session).Current) return null;
            return Geometries.TryGetValue(session.Window, out var geometry) ? geometry : ClientWindowResizeTests.Geometry();
        }
        public WindowPostStatus Resize(SessionObservation session, ClientWindowGeometry expected, PixelRectangle bounds, Func<bool> stopped)
        {
            BeforePost?.Invoke();
            if (!Check(session).Current) return WindowPostStatus.Invalid;
            if (!Check(session).CanArrange) return WindowPostStatus.Unsupported;
            if (Geometry(session) != expected) return WindowPostStatus.Changed;
            if (stopped()) return WindowPostStatus.Cancelled;
            Posts.Add((session, bounds)); Entered.TrySetResult();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic resize gate timed out.");
            var content = expected.ContentBounds; var outer = expected.OuterBounds;
            Geometries[session.Window] = expected with
            {
                OuterBounds = bounds,
                ContentBounds = new(bounds.X + content.X - outer.X, bounds.Y + content.Y - outer.Y,
                    bounds.Width - outer.Width + content.Width - (WrongContent ? 1 : 0), bounds.Height - outer.Height + content.Height)
            };
            AfterPost?.Invoke(); return WindowPostStatus.Accepted;
        }
        public WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped) { TilingMoves++; return WindowPostStatus.Accepted; }
        public WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped) { StateChanges++; return WindowPostStatus.Accepted; }
        public PixelRectangle? Bounds(SessionObservation session) => Geometry(session)?.OuterBounds;
        public void Dispose() => Release.Dispose();
    }
}
