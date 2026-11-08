using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientWindowCheck(bool Current, bool Minimized, bool Visible, bool CanArrange, string Explanation, bool Maximized = false);
internal enum WindowPostStatus { Accepted, Invalid, Unsupported, Denied, Cancelled, Changed }
internal interface IClientWindowAdapter
{
    ClientDisplayList Displays(CancellationToken ct);
    ClientWindowCheck Check(SessionObservation session);
    /// <summary>Places the window's visible frame on <paramref name="bounds"/> (the outer rect may extend into invisible borders).</summary>
    WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped);
    WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped);
    /// <summary>Outer window rect, including any invisible resize borders.</summary>
    PixelRectangle? Bounds(SessionObservation session);
    /// <summary>Visible window frame that tiling confirms; equals <see cref="Bounds"/> for windows without invisible borders.</summary>
    PixelRectangle? VisibleBounds(SessionObservation session) => Bounds(session);
    ClientWindowGeometry? Geometry(SessionObservation session) => null;
    WindowPostStatus Resize(SessionObservation session, ClientWindowGeometry expected, PixelRectangle bounds, Func<bool> stopped) => WindowPostStatus.Unsupported;
}

/// <summary>One mutation lane shared with minimize/restore. Each native post is awaited; already posted async requests cannot be recalled.</summary>
internal sealed class ClientWindowOperations(SemaphoreSlim lane, IClientWindowAdapter adapter)
{
    private long _generation;
    public void Cancel() => Interlocked.Increment(ref _generation);
    private bool Stopped(long generation, CancellationToken ct) => ct.IsCancellationRequested || Interlocked.Read(ref _generation) != generation;
    public Task<ClientDisplayList> DisplaysAsync(CancellationToken ct) => Task.Run(() => adapter.Displays(ct), ct);

    public async Task<WindowActionResult> ChangeAsync(SessionObservation session, ClientWindowAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        long generation = Interlocked.Read(ref _generation);
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ChangeOneAsync(session, action, generation, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); // Preserve the existing single-operation cancellation contract.
            return new(result.Status == ClientWindowPlacementStatus.Applied, result.Explanation);
        }
        finally { lane.Release(); }
    }

    public async Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions,
        ClientWindowAction action, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (sessions.Count > 128) throw new ArgumentException("Select at most 128 clients in one window-state request.", nameof(sessions));
        // Copy and order before the first await. Caller selection changes cannot redirect queued work.
        var ordered = sessions.OrderBy(session => session.ProcessId).ThenBy(session => session.ProcessStartUtcTicks)
            .ThenBy(session => session.Window.ToInt64()).ToArray();
        if (ordered.Length == 0) return new(Array.Empty<ClientWindowPlacementResult>(), false, "Check the clients whose window state should change.");
        var results = new List<ClientWindowPlacementResult>(ordered.Length);
        var duplicates = ordered.GroupBy(session => session.Window).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        bool acquired = false;
        try
        {
            await lane.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            foreach (var session in ordered)
            {
                if (Stopped(generation, ct))
                { results.Add(Cancelled(session)); continue; }
                if (duplicates.Contains(session.Window))
                { results.Add(new(session, null, ClientWindowPlacementStatus.InvalidClient, "The selection contains a duplicate window; select each client once.")); continue; }
                try { results.Add(await ChangeOneAsync(session, action, generation, ct).ConfigureAwait(false)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    results.Add(new(session, null, ClientWindowPlacementStatus.NotConfirmed,
                        "Windows could not confirm this client operation. A posted request may still complete; no success is assumed."));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            while (results.Count < ordered.Length) results.Add(Cancelled(ordered[results.Count]));
        }
        finally { if (acquired) lane.Release(); }
        return new(results.AsReadOnly(), Stopped(generation, ct) || results.Any(result => result.Status == ClientWindowPlacementStatus.Cancelled),
            $"{results.Count(result => result.Status == ClientWindowPlacementStatus.Applied)} of {ordered.Length} client {(action == ClientWindowAction.Minimize ? "minimized" : "restored")} states confirmed. Each remaining result explains its state.");

        static ClientWindowPlacementResult Cancelled(SessionObservation session) => new(session, null, ClientWindowPlacementStatus.Cancelled,
            "Window-state work stopped. A request already posted to Windows cannot be recalled; no later client is changed.");
    }

    // Called only while the common mutation lane is held, for either a single request or an explicit batch.
    private async Task<ClientWindowPlacementResult> ChangeOneAsync(SessionObservation session, ClientWindowAction action,
        long generation, CancellationToken ct)
    {
        if (Stopped(generation, ct)) return new(session, null, ClientWindowPlacementStatus.Cancelled, "The window request was cancelled before dispatch.");
        var posted = await Task.Run(() => adapter.Change(session, action, () => Stopped(generation, ct))).ConfigureAwait(false);
        if (posted != WindowPostStatus.Accepted) return new(session, null, Map(posted), PostExplanation(posted));
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Stopped(generation, ct)) return new(session, null, ClientWindowPlacementStatus.Cancelled,
                "The window request was posted before cancellation; its completion is unconfirmed.");
            var state = await Task.Run(() => adapter.Check(session)).ConfigureAwait(false);
            if (!state.Current) return new(session, null, ClientWindowPlacementStatus.InvalidClient, state.Explanation);
            if (action == ClientWindowAction.Minimize ? state.Minimized : !state.Minimized && !state.Maximized && state.Visible)
                return new(session, null, ClientWindowPlacementStatus.Applied, action == ClientWindowAction.Minimize
                    ? "Client minimized. Minimized-window automation is unsupported." : "Client restored without requesting foreground activation.");
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return new(session, null, ClientWindowPlacementStatus.NotConfirmed,
            "Windows accepted the state request but did not confirm completion. The client remains open; refresh and retry.");
    }

    public async Task<ClientWindowArrangementResult> ArrangeAsync(IReadOnlyList<SessionObservation> sessions, string displayId,
        ClientWindowTileOptions options, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(options); options.Validated();
        if (sessions.Count > 128) throw new ArgumentException("Select at most 128 clients in one arrangement request.", nameof(sessions));
        // Copy before the first await, in the caller's slot order (ClientWindowSlotOrder): the first client gets the first cell.
        // Later caller selection changes cannot redirect queued work.
        var ordered = sessions.ToArray();
        if (ordered.Length == 0) return new(Array.Empty<ClientWindowPlacementResult>(), false, "Check the clients to arrange.");
        var results = new List<ClientWindowPlacementResult>(ordered.Length);
        bool acquired = false;
        try
        {
            await lane.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            if (Stopped(generation, ct)) return All(ClientWindowPlacementStatus.Cancelled, "Arrangement cancelled before dispatch.", true);
            var displays = await DisplaysAsync(ct).ConfigureAwait(false);
            var display = displays.Displays.FirstOrDefault(candidate => candidate.Id == displayId && candidate.IsValid);
            if (display is null) return All(ClientWindowPlacementStatus.DisplayUnavailable, "The chosen display or its scale is unavailable. Refresh displays.");
            var plan = ClientWindowTilePlanner.Plan(display, ordered.Length, options);
            if (!plan.Fits) return All(ClientWindowPlacementStatus.LayoutUnavailable, plan.Explanation);
            var duplicateWindows = ordered.GroupBy(session => session.Window).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            for (int index = 0; index < ordered.Length; index++)
            {
                var session = ordered[index]; var bounds = plan.Cells[index];
                if (Stopped(generation, ct))
                { results.Add(new(session, bounds, ClientWindowPlacementStatus.Cancelled, "No new window request was dispatched after cancellation.")); continue; }
                if (duplicateWindows.Contains(session.Window))
                { results.Add(new(session, bounds, ClientWindowPlacementStatus.InvalidClient, "The selection contains a duplicate window; select each client once.")); continue; }
                // Fresh workarea/scale and client-instance checks happen immediately before every mutation.
                var currentDisplays = await DisplaysAsync(ct).ConfigureAwait(false);
                if (!currentDisplays.Displays.Any(current => current.Id == display.Id && current.WorkArea == display.WorkArea && current.ScalePercent == display.ScalePercent))
                { results.Add(new(session, bounds, ClientWindowPlacementStatus.DisplayUnavailable, "Display workarea or scale changed. Refresh displays before arranging again.")); continue; }
                // A per-monitor-DPI-aware client moved onto a monitor with another scale may apply the WM_DPICHANGED
                // suggested size instead of the requested one. Unknown source geometry (null) keeps the single post.
                var source = await Task.Run(() => adapter.Geometry(session)).ConfigureAwait(false);
                bool crossScale = source is { IsValid: true } && source.Display.ScalePercent != display.ScalePercent;
                var posted = await Task.Run(() => adapter.Move(session, bounds, () => Stopped(generation, ct))).ConfigureAwait(false);
                if (posted != WindowPostStatus.Accepted)
                { results.Add(new(session, bounds, Map(posted), PostExplanation(posted))); continue; }
                var (confirmed, invalid) = await ConfirmBoundsAsync(session, bounds, display, generation, ct).ConfigureAwait(false);
                // Whether the client reported the target monitor after the move; null when that is unknown (no check or no geometry).
                bool? reachedTarget = null;
                if (crossScale && !confirmed && !invalid && !Stopped(generation, ct))
                {
                    // Post the same rectangle exactly once more, only after the client reports the target monitor and scale
                    // (so this post cannot cause another scale change) and behind the same display and panic checks.
                    var landed = await Task.Run(() => adapter.Geometry(session)).ConfigureAwait(false);
                    reachedTarget = landed is { IsValid: true } ? SameDisplay(landed.Display, display) : null;
                    if (reachedTarget == true)
                    {
                        if (!await DisplayStillCurrentAsync(display, ct).ConfigureAwait(false))
                        { results.Add(new(session, bounds, ClientWindowPlacementStatus.DisplayUnavailable, "Display workarea or scale changed. Refresh displays before arranging again.")); continue; }
                        if (!Stopped(generation, ct))
                        {
                            posted = await Task.Run(() => adapter.Move(session, bounds, () => Stopped(generation, ct))).ConfigureAwait(false);
                            if (posted == WindowPostStatus.Accepted)
                                (confirmed, invalid) = await ConfirmBoundsAsync(session, bounds, display, generation, ct).ConfigureAwait(false);
                            else if (!Stopped(generation, ct))
                            { results.Add(new(session, bounds, Map(posted), PostExplanation(posted))); continue; }
                        }
                    }
                }
                results.Add(new(session, bounds, Stopped(generation, ct) ? ClientWindowPlacementStatus.Cancelled
                    : invalid ? ClientWindowPlacementStatus.InvalidClient : confirmed ? ClientWindowPlacementStatus.Applied : ClientWindowPlacementStatus.NotConfirmed,
                    Stopped(generation, ct) ? "The resize was posted before cancellation; completion is unconfirmed and cannot be recalled."
                        : invalid ? "The observed client or window changed after the request."
                        : confirmed ? "Physical window bounds confirmed without requesting activation."
                        : reachedTarget == true ? "The client resized itself when it moved to a monitor with a different scale. Arrange again; if the size still differs, client size constraints may apply."
                        : reachedTarget == false ? "The client did not reach the selected monitor, which uses a different scale. Arrange again; if it stays on its current monitor, the client may be limiting where its window can go."
                        : "Windows accepted the request but did not confirm these bounds. Client size constraints may apply."));
            }
            return Finish();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            while (results.Count < ordered.Length)
            {
                var session = ordered[results.Count];
                results.Add(new(session, null, ClientWindowPlacementStatus.Cancelled,
                    "Arrangement stopped. A request already posted to Windows cannot be recalled; no later client is changed."));
            }
            return Finish();
        }
        finally { if (acquired) lane.Release(); }

        ClientWindowArrangementResult All(ClientWindowPlacementStatus status, string message, bool cancelled = false) =>
            new(Array.AsReadOnly(ordered.Select(session => new ClientWindowPlacementResult(session, null, status, message)).ToArray()), cancelled, message);
        ClientWindowArrangementResult Finish() => new(results.AsReadOnly(), results.Any(result => result.Status == ClientWindowPlacementStatus.Cancelled),
            $"{results.Count(result => result.Status == ClientWindowPlacementStatus.Applied)} of {ordered.Length} client window bounds confirmed. Each remaining result explains its state.");
    }

    // Bounded confirmation (20 × 25 ms) of one posted move: exact physical visible-frame bounds inside an unchanged target
    // display. The outer rect is not compared: it includes invisible resize borders that may extend past the workarea.
    private async Task<(bool Confirmed, bool Invalid)> ConfirmBoundsAsync(SessionObservation session, PixelRectangle bounds,
        ClientDisplay display, long generation, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Stopped(generation, ct)) break;
            var observed = await Task.Run(() => (State: adapter.Check(session), Bounds: adapter.VisibleBounds(session))).ConfigureAwait(false);
            if (!observed.State.Current) return (false, true);
            if (observed.Bounds is { } actual && actual == bounds && display.WorkArea.Contains(actual))
            {
                var confirmedDisplays = await DisplaysAsync(ct).ConfigureAwait(false);
                return (confirmedDisplays.Displays.Any(current => current.Id == display.Id && current.WorkArea == display.WorkArea
                    && current.ScalePercent == display.ScalePercent && current.WorkArea.Contains(actual)), false);
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return (false, false);
    }

    public async Task<ClientWindowArrangementResult> ResizeAsync(IReadOnlyList<SessionObservation> sessions, ClientContentSize size, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(size); size.Validated();
        int count = sessions.Count;
        if (count is < 0 or > 128) throw new ArgumentException("Select at most 128 clients in one content-size request.", nameof(sessions));
        // Copy before the first await. Later checkbox/draft changes cannot redirect this request.
        // Index the bounded IReadOnlyList rather than trusting an unrelated/unbounded enumerator.
        var pinned = new SessionObservation[count];
        for (int index = 0; index < count; index++) pinned[index] = sessions[index]
            ?? throw new ArgumentException("Every selected client must be present.", nameof(sessions));
        var ordered = pinned.OrderBy(session => session.ProcessId).ThenBy(session => session.ProcessStartUtcTicks)
            .ThenBy(session => session.Window.ToInt64()).ToArray();
        if (ordered.Length == 0) return new(Array.Empty<ClientWindowPlacementResult>(), false, "Check the clients whose content size should change.");
        var duplicateWindows = ordered.GroupBy(session => session.Window).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var duplicateProcesses = ordered.GroupBy(session => session.ProcessId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var results = new List<ClientWindowPlacementResult>(ordered.Length); bool acquired = false;
        try
        {
            await lane.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            foreach (var session in ordered)
            {
                if (Stopped(generation, ct)) { results.Add(Cancelled(session)); continue; }
                if (duplicateWindows.Contains(session.Window) || duplicateProcesses.Contains(session.ProcessId))
                { results.Add(new(session, null, ClientWindowPlacementStatus.InvalidClient, "The selection contains a duplicate client or window; select each client once.")); continue; }
                try { results.Add(await ResizeOneAsync(session, size, generation, ct).ConfigureAwait(false)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                { results.Add(new(session, null, ClientWindowPlacementStatus.NotConfirmed, "Windows could not confirm this content-size request. A posted request may still complete; no success is assumed.")); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { while (results.Count < ordered.Length) results.Add(Cancelled(ordered[results.Count])); }
        finally { if (acquired) lane.Release(); }
        return new(results.AsReadOnly(), Stopped(generation, ct) || results.Any(result => result.Status == ClientWindowPlacementStatus.Cancelled),
            $"{results.Count(result => result.Status == ClientWindowPlacementStatus.Applied)} of {ordered.Length} client content sizes confirmed. Each remaining result explains its state.");

        static ClientWindowPlacementResult Cancelled(SessionObservation session) => new(session, null, ClientWindowPlacementStatus.Cancelled,
            "Content-size work stopped. A request already posted to Windows cannot be recalled; no later client is resized.");
    }

    private async Task<ClientWindowPlacementResult> ResizeOneAsync(SessionObservation session, ClientContentSize size, long generation, CancellationToken ct)
    {
        var initial = await Task.Run(() => (State: adapter.Check(session), Geometry: adapter.Geometry(session))).ConfigureAwait(false);
        if (Stopped(generation, ct)) return Result(ClientWindowPlacementStatus.Cancelled, "The content-size request was cancelled before dispatch.");
        if (!initial.State.Current) return Result(ClientWindowPlacementStatus.InvalidClient, initial.State.Explanation);
        if (!initial.State.CanArrange || initial.Geometry is not { IsValid: true } geometry)
            return Result(ClientWindowPlacementStatus.UnsupportedState, "Use a visible, resizable, normal window with confirmed physical content geometry. Restore minimized/maximized clients or leave fullscreen first.");
        var plan = ClientWindowResizePlanner.Plan(geometry, size);
        if (!plan.Fits || plan.OuterBounds is not { } bounds) return Result(ClientWindowPlacementStatus.LayoutUnavailable, plan.Explanation);
        if (!await DisplayStillCurrentAsync(geometry.Display, ct).ConfigureAwait(false))
            return Result(ClientWindowPlacementStatus.DisplayUnavailable, "The client's display workarea or scale changed. Refresh before resizing again.", bounds);
        // The production adapter repeats exact geometry/display/identity checks immediately before its native post.
        if (Stopped(generation, ct)) return Result(ClientWindowPlacementStatus.Cancelled, "The content-size request was cancelled before dispatch.", bounds);
        var posted = await Task.Run(() => adapter.Resize(session, geometry, bounds, () => Stopped(generation, ct))).ConfigureAwait(false);
        if (posted != WindowPostStatus.Accepted) return Result(Map(posted), PostExplanation(posted), bounds);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Stopped(generation, ct)) return Result(ClientWindowPlacementStatus.Cancelled, "The resize was posted before cancellation; its completion is unconfirmed and cannot be recalled.", bounds);
            var observed = await Task.Run(() => (State: adapter.Check(session), Geometry: adapter.Geometry(session))).ConfigureAwait(false);
            if (Stopped(generation, ct)) return Result(ClientWindowPlacementStatus.Cancelled, "The resize was posted before cancellation; its completion is unconfirmed and cannot be recalled.", bounds);
            if (!observed.State.Current) return Result(ClientWindowPlacementStatus.InvalidClient, "The observed client or window changed after the request.", bounds);
            if (!observed.State.CanArrange) return Result(ClientWindowPlacementStatus.UnsupportedState, "The window left its visible, resizable, normal state after the request. Its content size is unconfirmed.", bounds);
            if (observed.Geometry is { IsValid: true } actual)
            {
                if (!SameDisplay(geometry.Display, actual.Display))
                    return Result(ClientWindowPlacementStatus.DisplayUnavailable, "The client's current display workarea or scale changed after the request. Its content size is unconfirmed.", bounds);
                if (actual.OuterBounds == bounds && actual.ContentBounds.Width == size.Width && actual.ContentBounds.Height == size.Height
                    && geometry.Display.WorkArea.Contains(actual.OuterBounds))
                {
                    bool confirmed = await DisplayStillCurrentAsync(geometry.Display, ct).ConfigureAwait(false);
                    if (Stopped(generation, ct)) return Result(ClientWindowPlacementStatus.Cancelled, "The resize was posted before cancellation; its completion is unconfirmed and cannot be recalled.", bounds);
                    return Result(confirmed ? ClientWindowPlacementStatus.Applied : ClientWindowPlacementStatus.DisplayUnavailable,
                        confirmed ? $"Physical content size {size.Width} × {size.Height} confirmed without requesting activation."
                            : "The display changed while confirming the content size. Refresh and retry.", bounds);
                }
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return Result(ClientWindowPlacementStatus.NotConfirmed, "Windows accepted the resize but did not confirm the requested content size. Client size constraints may apply.", bounds);

        ClientWindowPlacementResult Result(ClientWindowPlacementStatus status, string message, PixelRectangle? requested = null) => new(session, requested, status, message);
    }
    private async Task<bool> DisplayStillCurrentAsync(ClientDisplay display, CancellationToken ct)
        => (await DisplaysAsync(ct).ConfigureAwait(false)).Displays.Any(current => current.IsValid && SameDisplay(display, current));
    private static bool SameDisplay(ClientDisplay left, ClientDisplay right) => left.Id == right.Id
        && left.WorkArea == right.WorkArea && left.ScalePercent == right.ScalePercent;

    private static ClientWindowPlacementStatus Map(WindowPostStatus status) => status switch
    {
        WindowPostStatus.Invalid => ClientWindowPlacementStatus.InvalidClient,
        WindowPostStatus.Unsupported => ClientWindowPlacementStatus.UnsupportedState,
        WindowPostStatus.Cancelled => ClientWindowPlacementStatus.Cancelled,
        WindowPostStatus.Changed => ClientWindowPlacementStatus.NotConfirmed,
        _ => ClientWindowPlacementStatus.Denied
    };
    private static string PostExplanation(WindowPostStatus status) => status switch
    {
        WindowPostStatus.Invalid => "The observed client exited or its window changed. Refresh and select it again.",
        WindowPostStatus.Unsupported => "Use a visible, resizable, normal window. Restore minimized/maximized clients or leave fullscreen first.",
        WindowPostStatus.Cancelled => "The window operation was cancelled before dispatch.",
        WindowPostStatus.Changed => "Window geometry or display context changed before dispatch. Refresh and retry; no resize was posted.",
        _ => "Windows denied the window request or its identity could not be checked. No elevation was requested."
    };
}
