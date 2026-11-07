using System.Diagnostics;
using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.NativeProbe.Infrastructure;

/// <summary>One diagnostic transport. Only registered, current own-process HWND incarnations are eligible.</summary>
internal sealed class OwnedWindowMessageExecutor : ITargetedInputTransport
{
    private const int MaximumRegistrations = 32, MaximumPending = 256, MaximumReceipts = 512;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Registration> _registrations = [];
    private readonly Dictionary<nint, Guid> _windows = [];
    private readonly long _processStart;

    public OwnedWindowMessageExecutor()
    {
        using var process = Process.GetCurrentProcess();
        _processStart = process.StartTime.ToUniversalTime().Ticks;
    }

    public TargetedInputTarget Register(nint window)
    {
        lock (_gate)
        {
            if (_registrations.Count >= MaximumRegistrations || _windows.ContainsKey(window)
                || !OwnWindow(window) || !TryContentBounds(window, out var bounds))
                throw new InvalidOperationException("Only a bounded new own-window incarnation can be registered.");
            var target = new TargetedInputTarget(Guid.NewGuid(), Environment.ProcessId, _processStart, window, bounds, 1);
            _registrations.Add(target.RegistrationId, new(target));
            _windows.Add(window, target.RegistrationId);
            return target;
        }
    }

    public void Revoke(TargetedInputTarget target)
    {
        lock (_gate)
            if (_registrations.TryGetValue(target.RegistrationId, out var registration) && registration.Target == target)
                registration.Revoked = true;
    }

    /// <summary>Called from actual own WM_NCDESTROY / disposed-source lifecycle, never inferred from a failed query.</summary>
    public void MarkDestroyed(TargetedInputTarget target)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(target.RegistrationId, out var registration) || registration.Target != target) return;
            registration.Destroyed = true;
            registration.Pending.Clear();
            if (_windows.TryGetValue(target.Window, out var nonce) && nonce == target.RegistrationId) _windows.Remove(target.Window);
        }
    }

    public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(ValidateLocked(target, cleanup));
    }

    private TargetedInputValidation ValidateLocked(TargetedInputTarget target, bool cleanup)
    {
        if (!target.IsValid || !_registrations.TryGetValue(target.RegistrationId, out var registration)
            || registration.Target != target) return TargetedInputValidation.Unavailable;
        if (registration.Destroyed) return TargetedInputValidation.Gone;
        if (registration.Revoked || target.ProcessId != Environment.ProcessId || target.ProcessStartUtcTicks != _processStart
            || !_windows.TryGetValue(target.Window, out var nonce) || nonce != target.RegistrationId || !OwnWindow(target.Window))
            return TargetedInputValidation.Unavailable;
        // Geometry changes cannot erase ownership of already submitted holds on the live original.
        if (cleanup) return TargetedInputValidation.Current;
        if (!TryContentBounds(target.Window, out var bounds)) return TargetedInputValidation.Unavailable;
        return bounds == target.ContentBounds ? TargetedInputValidation.Current : TargetedInputValidation.GeometryChanged;
    }

    public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(admissionOpen);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (command.RunId == Guid.Empty || command.Sequence <= 0 || !TryMessage(command, out var message, out var parameter, out var position)
                || ValidateLocked(command.Target, command.Cleanup) != TargetedInputValidation.Current)
                return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            var registration = _registrations[command.Target.RegistrationId];
            if (registration.Pending.Count >= MaximumPending || registration.Receipts.Count + registration.Pending.Count >= MaximumReceipts)
                return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            if (registration.LastRun == command.RunId && command.Sequence <= registration.LastSequence)
                return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            // Evidence only: this observation never authorizes submission or requests focus.
            var posted = new Posted(command.RunId, command.Sequence, message, parameter, position,
                Native.GetForegroundWindow() == command.Target.Window);
            // Reserve observation capacity before native submission; no allocation may fail after it queues.
            registration.Pending.Add(posted);
            try
            {
                if (ValidateLocked(command.Target, command.Cleanup) != TargetedInputValidation.Current)
                { registration.Pending.RemoveAt(registration.Pending.Count - 1); return ValueTask.FromResult(TargetedInputSubmission.Rejected); }
                ct.ThrowIfCancellationRequested();
                if (!admissionOpen())
                { registration.Pending.RemoveAt(registration.Pending.Count - 1); return ValueTask.FromResult(TargetedInputSubmission.Rejected); }
                // No await, query or focus request between admission and this native permission point.
                if (!Native.PostMessage(command.Target.Window, message, parameter, position))
                { registration.Pending.RemoveAt(registration.Pending.Count - 1); return ValueTask.FromResult(TargetedInputSubmission.Rejected); }
                registration.LastRun = command.RunId; registration.LastSequence = command.Sequence;
                return ValueTask.FromResult(TargetedInputSubmission.Queued);
            }
            catch (OperationCanceledException)
            { registration.Pending.RemoveAt(registration.Pending.Count - 1); throw; }
            catch (Exception)
            {
                registration.Pending.RemoveAt(registration.Pending.Count - 1);
                return ValueTask.FromResult(TargetedInputSubmission.Uncertain);
            }
        }
    }

    /// <summary>Cooperating receiver evidence, separate from the transport's Queued result.</summary>
    public void ObserveProcessed(TargetedInputTarget target, nint window, int message, nint parameter, nint position)
    {
        lock (_gate)
        {
            if (window != target.Window || !_registrations.TryGetValue(target.RegistrationId, out var registration)
                || registration.Target != target || registration.Destroyed) return;
            if (registration.Pending.Count == 0) return;
            var posted = registration.Pending[0];
            if (posted.Message != message || posted.Parameter != parameter || posted.Position != position) return;
            registration.Pending.RemoveAt(0);
            registration.Receipts.Add(posted);
        }
    }

    public long[] ProcessedSequences(TargetedInputTarget target, Guid runId)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(target.RegistrationId, out var registration) || registration.Target != target) return [];
            return registration.Receipts.Where(receipt => receipt.RunId == runId).Select(receipt => receipt.Sequence).ToArray();
        }
    }
    public int PendingCount(TargetedInputTarget target)
    { lock (_gate) return _registrations.TryGetValue(target.RegistrationId, out var value) && value.Target == target ? value.Pending.Count : 0; }

    public OwnedInputReceiptSummary ProcessedSummary(TargetedInputTarget target, Guid runId)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(target.RegistrationId, out var registration) || registration.Target != target)
                return new(0, 0, 0, 0, 0, 0);
            int count = 0, down = 0, up = 0, leftDown = 0, leftUp = 0, foreground = 0;
            foreach (var receipt in registration.Receipts)
            {
                if (receipt.RunId != runId) continue;
                count++;
                if (receipt.Message == 0x100) down++;
                else if (receipt.Message == 0x101) up++;
                else if (receipt.Message == 0x201) leftDown++;
                else if (receipt.Message == 0x202) leftUp++;
                if (receipt.ForegroundBeforeAdmission) foreground++;
            }
            return new(count, down, up, leftDown, leftUp, foreground);
        }
    }

    private static bool TryMessage(TargetedInputCommand command, out uint message, out nint parameter, out nint position)
    {
        message = 0; parameter = 0; position = 0;
        var signal = command.Signal;
        if (!signal.IsValid(command.Target) || command.Cleanup && signal.Kind is not (TargetedInputKind.KeyUp or TargetedInputKind.LeftUp)
            || (command.Modifiers & ~TargetedInputModifiers.LeftButton) != 0) return false;
        switch (signal.Kind)
        {
            case TargetedInputKind.KeyDown or TargetedInputKind.KeyUp when signal.Key == 0x87:
                message = signal.Kind == TargetedInputKind.KeyDown ? 0x100u : 0x101u;
                parameter = 0x87; // F24 only, never global/raw input.
                position = signal.Kind == TargetedInputKind.KeyUp ? unchecked((nint)0xC0000001u)
                    : command.RepeatedDown ? 0x40000001 : 1;
                return true;
            case TargetedInputKind.LeftDown or TargetedInputKind.LeftUp:
                if (signal.X > short.MaxValue || signal.Y > short.MaxValue) return false;
                message = signal.Kind == TargetedInputKind.LeftDown ? 0x201u : 0x202u;
                parameter = command.Modifiers.HasFlag(TargetedInputModifiers.LeftButton) ? 1 : 0;
                position = (nint)((signal.Y << 16) | signal.X);
                return true;
            default: return false;
        }
    }

    private static bool OwnWindow(nint window)
    { Native.GetWindowThreadProcessId(window, out uint pid); return window != 0 && pid == Environment.ProcessId && Native.IsWindow(window); }

    private static bool TryContentBounds(nint window, out PixelRectangle bounds)
    {
        bounds = default;
        nint previous = Native.SetThreadDpiAwarenessContext((nint)(-4));
        if (previous == 0) return false;
        try
        {
            var origin = new Native.Point();
            if (!Native.GetClientRect(window, out var client) || !Native.ClientToScreen(window, ref origin)) return false;
            long width = (long)client.Right - client.Left, height = (long)client.Bottom - client.Top;
            if (width is < 1 or > short.MaxValue || height is < 1 or > short.MaxValue) return false;
            bounds = new(origin.X, origin.Y, (int)width, (int)height);
            return bounds.IsValid;
        }
        finally { Native.SetThreadDpiAwarenessContext(previous); }
    }

    private sealed class Registration(TargetedInputTarget target)
    {
        public TargetedInputTarget Target { get; } = target;
        public bool Revoked, Destroyed;
        public Guid LastRun;
        public long LastSequence;
        public List<Posted> Pending { get; } = new(MaximumPending);
        public List<Posted> Receipts { get; } = new(MaximumReceipts);
    }
    private sealed record Posted(Guid RunId, long Sequence, uint Message, nint Parameter, nint Position, bool ForegroundBeforeAdmission);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint parameter, nint position);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    }
}

internal sealed record OwnedInputReceiptSummary(int Count, int KeyDown, int KeyUp, int LeftDown, int LeftUp,
    int ForegroundBeforeAdmission)
{
    public bool Balanced => KeyDown == KeyUp && LeftDown == LeftUp;
}
