using System.ComponentModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ClientWindowBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 21, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        (nint)(pid + 1000), "Synthetic", false, true, 0, 0, null, Now);
    private sealed class RevokingSelection(IReadOnlyList<SessionObservation> selected, Action revoke) : IReadOnlyList<SessionObservation>
    {
        public int Count => selected.Count;
        public SessionObservation this[int index] => selected[index];
        public IEnumerator<SessionObservation> GetEnumerator() { revoke(); return selected.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Adapter : IClientWindowAdapter
    {
        public readonly Dictionary<int, SessionObservation> Current = [];
        public readonly Dictionary<int, (bool Minimized, bool Maximized, bool Visible)> States = [];
        public readonly List<(int Pid, ClientWindowAction Action)> Posted = [];
        public readonly Dictionary<nint, PixelRectangle> Positions = [];
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public Action<SessionObservation>? BeforeChange, AfterChange;
        public int? DeniedPid, ErrorPid, BlockPid, UnconfirmedPid;
        public int Moves;
        public Adapter(params int[] ids) { foreach (int id in ids) { Current[id] = Session(id); States[id] = (false, false, true); } }
        public ClientDisplayList Displays(CancellationToken ct) => new(new[] { new ClientDisplay("synthetic", "Synthetic", new(0, 0, 1920, 1080), 100, true) }, "Synthetic");
        private bool Matches(SessionObservation session) => Current.TryGetValue(session.ProcessId, out var expected)
            && expected.ProcessStartUtcTicks == session.ProcessStartUtcTicks && expected.Window == session.Window;
        public ClientWindowCheck Check(SessionObservation session)
        {
            var state = States[session.ProcessId];
            return new(Matches(session), state.Minimized, state.Visible, !state.Minimized && !state.Maximized,
                "Synthetic exact process/window identity changed.", state.Maximized);
        }
        public WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped)
        {
            BeforeChange?.Invoke(session);
            if (!Matches(session)) return WindowPostStatus.Invalid;
            if (stopped()) return WindowPostStatus.Cancelled;
            if (session.ProcessId == DeniedPid) return WindowPostStatus.Denied;
            if (session.ProcessId == ErrorPid) throw new Win32Exception(5, "SYNTHETIC_PRIVATE_FAILURE");
            if (session.ProcessId == BlockPid)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic operation release was not signalled.");
                if (stopped()) return WindowPostStatus.Cancelled;
            }
            Posted.Add((session.ProcessId, action));
            if (session.ProcessId != UnconfirmedPid) States[session.ProcessId] = (action == ClientWindowAction.Minimize, false, true);
            AfterChange?.Invoke(session); return WindowPostStatus.Accepted;
        }
        public WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped)
        {
            if (!Matches(session)) return WindowPostStatus.Invalid;
            if (stopped()) return WindowPostStatus.Cancelled;
            Moves++; Positions[session.Window] = bounds; return WindowPostStatus.Accepted;
        }
        public PixelRectangle? Bounds(SessionObservation session) => Positions.TryGetValue(session.Window, out var bounds) ? bounds : null;
    }

    [Theory]
    [InlineData(ClientWindowAction.Minimize)]
    [InlineData(ClientWindowAction.Restore)]
    public async Task ExplicitBatchFreezesAndOrdersEveryIdentityAndReportsObservedState(ClientWindowAction action)
    {
        var adapter = new Adapter(9, 2, 5); if (action == ClientWindowAction.Restore)
            foreach (int id in adapter.States.Keys.ToArray()) adapter.States[id] = (true, false, true);
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        await lane.WaitAsync();
        SessionObservation[] selected = [Session(9), Session(2), Session(5)];
        var task = operations.ChangeManyAsync(selected, action, default);
        selected[0] = Session(999); lane.Release();
        var report = await task;
        Assert.True(report.AllApplied); Assert.False(report.Cancelled);
        Assert.Equal(new[] { 2, 5, 9 }, adapter.Posted.Select(item => item.Pid));
        Assert.All(report.Results, result => { Assert.Null(result.RequestedBounds); Assert.Equal(ClientWindowPlacementStatus.Applied, result.Status); });
        Assert.All(adapter.Posted, item => Assert.Equal(action, item.Action)); Assert.Equal(0, adapter.Moves);
    }

    [Fact]
    public async Task ChangedPidStartOrHwndIsRejectedAtEachMutationBoundary()
    {
        var adapter = new Adapter(1, 2, 3);
        adapter.BeforeChange = session =>
        {
            if (session.ProcessId == 2) adapter.Current[2] = Session(2) with { ProcessStartUtcTicks = Session(2).ProcessStartUtcTicks + 1 };
            if (session.ProcessId == 3) adapter.Current[3] = Session(3) with { Window = (nint)7777 };
        };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeManyAsync([Session(1), Session(2), Session(3)], ClientWindowAction.Minimize, default);
        Assert.Equal(new[] { ClientWindowPlacementStatus.Applied, ClientWindowPlacementStatus.InvalidClient, ClientWindowPlacementStatus.InvalidClient }, result.Results.Select(row => row.Status));
        Assert.Equal(1, Assert.Single(adapter.Posted).Pid);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrPanicPreservesConfirmedResultsAndCancelsEveryRemainingClient(bool panic)
    {
        var adapter = new Adapter(1, 2, 3); using var stop = new CancellationTokenSource();
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        adapter.BeforeChange = session => { if (session.ProcessId == 2) { if (panic) operations.Cancel(); else stop.Cancel(); } };
        var result = await operations.ChangeManyAsync([Session(3), Session(2), Session(1)], ClientWindowAction.Minimize, stop.Token);
        Assert.True(result.Cancelled); Assert.Equal(3, result.Results.Count);
        Assert.Equal(ClientWindowPlacementStatus.Applied, result.Results[0].Status);
        Assert.All(result.Results.Skip(1), row => Assert.Equal(ClientWindowPlacementStatus.Cancelled, row.Status));
        Assert.Equal(1, Assert.Single(adapter.Posted).Pid);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AWaitingBatchIsFencedWithoutDispatchingAnyClient(bool panic)
    {
        var adapter = new Adapter(1, 2); using var stop = new CancellationTokenSource();
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        await lane.WaitAsync();
        var task = operations.ChangeManyAsync([Session(1), Session(2)], ClientWindowAction.Restore, stop.Token);
        if (panic) operations.Cancel(); else stop.Cancel();
        lane.Release();
        var result = await task;
        Assert.True(result.Cancelled); Assert.All(result.Results, row => Assert.Equal(ClientWindowPlacementStatus.Cancelled, row.Status));
        Assert.Empty(adapter.Posted);
    }

    [Fact]
    public async Task PanicWhileFreezingSelectionCannotBecomePermissionForTheNewGeneration()
    {
        var adapter = new Adapter(1, 2); using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        var selected = new RevokingSelection(new[] { Session(1), Session(2) }, operations.Cancel);
        var batch = await operations.ChangeManyAsync(selected, ClientWindowAction.Minimize, default);
        Assert.True(batch.Cancelled); Assert.Empty(adapter.Posted);
        var arrangement = await operations.ArrangeAsync(selected, "synthetic", new(), default);
        Assert.True(arrangement.Cancelled); Assert.Equal(0, adapter.Moves);
    }

    [Fact]
    public async Task AStopAfterPostingIsReportedAsUnconfirmedCancellationAndCannotBeRecalled()
    {
        var adapter = new Adapter(1, 2); using var lane = new SemaphoreSlim(1, 1);
        var operations = new ClientWindowOperations(lane, adapter);
        adapter.AfterChange = session => { if (session.ProcessId == 1) operations.Cancel(); };
        var result = await operations.ChangeManyAsync([Session(1), Session(2)], ClientWindowAction.Minimize, default);
        Assert.True(result.Cancelled); Assert.False(result.AllApplied); Assert.Single(adapter.Posted);
        Assert.All(result.Results, row => Assert.Equal(ClientWindowPlacementStatus.Cancelled, row.Status));
        Assert.Contains("posted", result.Results[0].Explanation);
        Assert.True(adapter.States[1].Minimized); // The result deliberately does not pretend the earlier request was undone.
    }

    [Fact]
    public async Task ClientLossAfterPostIsNotReportedAsConfirmed()
    {
        var adapter = new Adapter(1, 2); adapter.AfterChange = session => { if (session.ProcessId == 1) adapter.Current.Remove(1); };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeManyAsync([Session(1), Session(2)], ClientWindowAction.Minimize, default);
        Assert.Equal(ClientWindowPlacementStatus.InvalidClient, result.Results[0].Status);
        Assert.Equal(ClientWindowPlacementStatus.Applied, result.Results[1].Status);
    }

    [Fact]
    public async Task DeniedAndNativeErrorResultsAreExplicitAndDoNotLoseOtherClientResults()
    {
        var adapter = new Adapter(1, 2, 3) { DeniedPid = 2, ErrorPid = 3 };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeManyAsync([Session(1), Session(2), Session(3)], ClientWindowAction.Minimize, default);
        Assert.Equal(new[] { ClientWindowPlacementStatus.Applied, ClientWindowPlacementStatus.Denied, ClientWindowPlacementStatus.NotConfirmed }, result.Results.Select(row => row.Status));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_FAILURE", result.Results[2].Explanation);
        Assert.Equal(1, Assert.Single(adapter.Posted).Pid);
    }

    [Fact]
    public async Task RestoreRemainingMaximizedNeverClaimsAnAppliedState()
    {
        var adapter = new Adapter(1, 2) { UnconfirmedPid = 1 }; adapter.States[1] = (false, true, true); adapter.States[2] = (true, false, true);
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeManyAsync([Session(1), Session(2)], ClientWindowAction.Restore, default);
        Assert.Equal(ClientWindowPlacementStatus.NotConfirmed, result.Results[0].Status);
        Assert.Equal(ClientWindowPlacementStatus.Applied, result.Results[1].Status);
    }

    [Fact]
    public async Task BatchSingleAndArrangementRequestsUseTheSameLane()
    {
        var adapter = new Adapter(1, 2, 3, 4) { BlockPid = 1 };
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var batch = operations.ChangeManyAsync([Session(2), Session(1)], ClientWindowAction.Minimize, default);
        Assert.True(await Task.Run(() => adapter.Entered.Wait(TimeSpan.FromSeconds(5))));
        var single = operations.ChangeAsync(Session(3), ClientWindowAction.Minimize, default);
        var arrange = operations.ArrangeAsync([Session(4)], "synthetic", new(), default);
        Assert.False(single.IsCompleted); Assert.False(arrange.IsCompleted); Assert.Empty(adapter.Posted); Assert.Equal(0, adapter.Moves);
        adapter.Release.Set();
        Assert.True((await batch).AllApplied); Assert.True((await single).Applied); Assert.True((await arrange).AllApplied);
        Assert.Equal(new[] { 1, 2, 3 }, adapter.Posted.Select(item => item.Pid)); Assert.Equal(1, adapter.Moves);
    }

    [Fact]
    public async Task DuplicateEmptyAndOverLimitRequestsAreBoundedAndExplicit()
    {
        var adapter = new Adapter(1); using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        Assert.Empty((await operations.ChangeManyAsync([], ClientWindowAction.Minimize, default)).Results);
        var duplicate = await operations.ChangeManyAsync([Session(1), Session(1)], ClientWindowAction.Minimize, default);
        Assert.All(duplicate.Results, row => Assert.Equal(ClientWindowPlacementStatus.InvalidClient, row.Status)); Assert.Empty(adapter.Posted);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.ChangeManyAsync(Enumerable.Range(1, 129).Select(Session).ToArray(), ClientWindowAction.Minimize, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => operations.ChangeManyAsync([Session(1)], (ClientWindowAction)900, default));
    }

    [Fact]
    public async Task MaximumBatchReturnsExactlyOneStateResultPerPinnedClient()
    {
        int[] ids = Enumerable.Range(1, 128).ToArray(); var adapter = new Adapter(ids);
        using var lane = new SemaphoreSlim(1, 1); var operations = new ClientWindowOperations(lane, adapter);
        var result = await operations.ChangeManyAsync(ids.Reverse().Select(Session).ToArray(), ClientWindowAction.Minimize, default);
        Assert.True(result.AllApplied); Assert.Equal(128, result.Results.Count); Assert.Equal(128, adapter.Posted.Count);
        Assert.Equal(ids, result.Results.Select(row => row.Session.ProcessId));
    }
}
