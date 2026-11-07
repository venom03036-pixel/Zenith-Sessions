using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

/// <summary>Platform-neutral matching and immutable observations; native event ownership stays in Infrastructure.</summary>
public abstract class TargetWindowServiceBase : ITargetWindowService
{
    private readonly object _gate = new();
    private readonly HashSet<IntPtr> _ownWindows = new();
    private readonly Dictionary<IntPtr, TargetWindowInfo> _known = new();
    private TargetWindowSnapshot _snapshot = TargetWindowSnapshot.Unavailable;
    private long _generation, _sequence;
    private Rectangle _screenBounds;
    private long _lastScreenRefresh;
    protected bool Disposed;
    public abstract bool SupportsWindowTargeting { get; }
    public TargetWindowSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler? Changed;
    protected abstract IReadOnlyList<TargetWindowInfo> ReadWindows();
    protected abstract TargetWindowInfo? ReadWindow(IntPtr handle);
    protected abstract IntPtr ReadForeground();
    protected abstract Rectangle ReadScreenBounds();
    protected abstract bool ActivateWindow(IntPtr handle);
    protected virtual bool IsOwnForeground()
    { var info = ReadWindow(ReadForeground()); return info != null && Track(info).IsOwnWindow; }

    private TargetWindowInfo Track(TargetWindowInfo info)
    {
        lock (_gate)
        {
            bool own = info.ProcessId == Environment.ProcessId || _ownWindows.Contains(info.Handle);
            long generation = _known.TryGetValue(info.Handle, out var previous)
                && PlaybackSessionContext.SameIdentity(previous, info) && previous.ClientBounds == info.ClientBounds
                ? previous.Generation : ++_generation;
            if (previous != null && previous.Generation == generation && previous.ApplicationId == info.ApplicationId
                && previous.Title == info.Title && previous.IsOwnWindow == own) return previous;
            var tracked = info with { Generation = generation, IsOwnWindow = own };
            if (_known.Count >= 512 && !_known.ContainsKey(info.Handle)) _known.Clear();
            _known[info.Handle] = tracked; return tracked;
        }
    }
    /// <summary>Call from the native metadata observer, never from a recording callback.</summary>
    protected void Observe()
    {
        if (Disposed) return;
        TargetWindowSnapshot previous = Snapshot;
        try
        {
            if (Stopwatch.GetElapsedTime(_lastScreenRefresh).TotalSeconds >= 1 || _screenBounds.IsEmpty)
            { var bounds = ReadScreenBounds(); lock (_gate) _screenBounds = bounds; _lastScreenRefresh = Stopwatch.GetTimestamp(); }
            var raw = ReadWindow(ReadForeground());
            var foreground = raw == null ? null : Track(raw);
            var external = foreground is { IsOwnWindow: false } ? foreground : previous.LastExternal;
            var next = new TargetWindowSnapshot(foreground, external, Interlocked.Increment(ref _sequence), Stopwatch.GetTimestamp(), true);
            Volatile.Write(ref _snapshot, next);
            if (!Equals(previous.Foreground, foreground) || !previous.IsReady || previous.Problem != null) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _snapshot, previous with { Foreground = null, ObservedAtTicks = Stopwatch.GetTimestamp(), IsReady = false, Problem = "The desktop target observation is unavailable." });
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public void RegisterOwnWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        lock (_gate) _ownWindows.Add(window);
        Observe();
    }
    public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<TargetWindowInfo>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!SupportsWindowTargeting) return [];
            var result = new List<TargetWindowInfo>();
            foreach (var raw in ReadWindows())
            {
                ct.ThrowIfCancellationRequested();
                var info = Track(raw);
                if (!info.IsOwnWindow && info.ClientBounds.Width > 0 && info.ClientBounds.Height > 0) result.Add(info);
                if (result.Count == 512) break;
            }
            return result;
        }, ct);
    public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null)
    {
        rule.Validate();
        if (rule.Mode == MacroTargetMode.Screen) return new(rule, null);
        if (!SupportsWindowTargeting) throw new NotSupportedException("This desktop supports an approved screen target, not strict application/window targeting. Choose Screen explicitly or use an X11 session.");
        TargetWindowInfo? chosen = null;
        if (rule.Mode == MacroTargetMode.ActiveExternalWindow)
        {
            Observe();
            chosen = preferredExternal ?? (Snapshot.Foreground is { IsOwnWindow: false } current ? current : Snapshot.LastExternal);
            if (chosen != null)
            {
                var fresh = ReadWindow(chosen.Handle);
                if (fresh == null || !PlaybackSessionContext.SameIdentity(chosen, fresh)) chosen = null;
                else chosen = Track(fresh);
            }
        }
        else
        {
            foreach (var raw in ReadWindows())
            {
                var candidate = Track(raw);
                if (candidate.IsOwnWindow || !candidate.ApplicationId.Equals(rule.ApplicationId, StringComparison.OrdinalIgnoreCase)) continue;
                if (rule.Mode == MacroTargetMode.SpecificWindow && !candidate.Title.Equals(rule.WindowTitle, StringComparison.Ordinal)) continue;
                if (preferredExternal != null && PlaybackSessionContext.SameIdentity(candidate, preferredExternal)) { chosen = candidate; break; }
                if (chosen != null) throw new InvalidOperationException("Several windows match this target. Choose the window to use before starting.");
                chosen = candidate;
            }
        }
        if (chosen == null || chosen.IsOwnWindow) throw new InvalidOperationException("The selected application/window is unavailable. Choose it again before starting.");
        return new(rule, chosen);
    }
    public TargetValidation Inspect(ResolvedMacroTarget target)
    {
        if (Disposed) return new(false, false, false, Rectangle.Empty, 0, "The desktop connection ended.");
        if (target.Rule.Mode == MacroTargetMode.Screen)
        {
            bool own = IsOwnForeground(); Rectangle bounds; lock (_gate) bounds = _screenBounds;
            return new(true, true, own, bounds, 0, own ? "Focus the application to automate, then resume manually." : null);
        }
        if (!SupportsWindowTargeting || target.Window == null) return new(false, false, false, Rectangle.Empty, 0, "Strict window targeting is not supported by this desktop connection.");
        var fresh = ReadWindow(target.Window.Handle);
        if (fresh == null || !PlaybackSessionContext.SameIdentity(target.Window, fresh)) return new(false, false, false, Rectangle.Empty, 0, "The selected window closed or restarted. Choose the target again.");
        var tracked = Track(fresh); bool focused = ReadForeground() == tracked.Handle;
        return new(true, focused, tracked.IsOwnWindow, tracked.ClientBounds, tracked.Generation,
            focused && !tracked.IsOwnWindow ? null : "The selected application lost focus. Focus it, then resume manually.");
    }
    public bool TryActivate(ResolvedMacroTarget target)
        => target.Window != null && Inspect(target).IsValid && !target.Window.IsOwnWindow && ActivateWindow(target.Window.Handle);
    public abstract void Dispose();
}
