using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Zenith.Sessions.Capabilities;
using Zenith.Sessions.Core;
using Zenith.Sessions.NativeProbe.Infrastructure;

namespace Zenith.Sessions.NativeProbe;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        { Console.Error.WriteLine("Usage: --output <native-report.json>. Creates and tests its own temporary windows only."); return 2; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 2;
        app.Startup += async (_, _) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            DateTimeOffset started = DateTimeOffset.UtcNow;
            var checks = new List<NativeCheck>();
            var windows = new List<Receiver>();
            try
            {
                var first = new Receiver(Brushes.Red); windows.Add(first);
                var second = new Receiver(Brushes.Blue); windows.Add(second);
                var cover = new Receiver(Brushes.DimGray) { Topmost = true }; windows.Add(cover);
                foreach (var window in windows) window.Show();
                if (!Native.GetWindowRect(first.Handle, out var targetBounds))
                    throw new InvalidOperationException("Own window bounds unavailable.");
                bool positioned = Native.SetWindowPos(cover.Handle, -1, targetBounds.Left - 8, targetBounds.Top - 8,
                    targetBounds.Right - targetBounds.Left + 16, targetBounds.Bottom - targetBounds.Top + 16, 0x10); // Own HWND_TOPMOST, no activation.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var center = new Native.Point { X = (targetBounds.Left + targetBounds.Right) / 2, Y = (targetBounds.Top + targetBounds.Bottom) / 2 };
                bool occluded = positioned && Native.WindowFromPoint(center) == cover.Handle;
                await CheckTargetedInputAsync(first, second, cover, checks, deadline.Token);

                try
                {
                    using var capture = new OwnedWindowCapture(first.Handle);
                    using var captureDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    captureDeadline.CancelAfter(TimeSpan.FromSeconds(6));
                    var red = await capture.ReadCenterAsync(captureDeadline.Token);
                    first.Background = Brushes.Lime;
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    (byte R, byte G, byte B, long Timestamp) green = default;
                    for (int i = 0; i < 12; i++)
                    {
                        green = await capture.ReadCenterAsync(captureDeadline.Token);
                        if (green.Timestamp > red.Timestamp && green.G > 180 && green.R < 80) break;
                    }
                    checks.Add(new("Fresh WGC pixels under overlapping own window",
                        occluded && red.R > 180 && red.G < 80 && green.G > 180 && green.R < 80 && green.Timestamp > red.Timestamp,
                        $"Own cover observed over target center={occluded}; RGB changed ({red.R},{red.G},{red.B}) to ({green.R},{green.G},{green.B}); timestamp advanced={green.Timestamp > red.Timestamp}. Pixels stayed in memory."));
                }
                catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or InvalidOperationException or OperationCanceledException)
                {
                    checks.Add(new("Fresh WGC pixels under overlapping own window", false,
                        $"Application-window capture was not established ({ex.GetType().Name}); not a confirmed absence of game content."));
                }
                // The original fourteen checks finish first, including their deliberate receiver
                // destruction. The interpreter reuses the first receiver and a fresh owned second.
                var macroSecond = new Receiver(Brushes.Blue); windows.Add(macroSecond); macroSecond.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                try { await CheckMacroExecutionAsync(first, macroSecond, cover, checks, deadline.Token); }
                catch (Exception ex) when (ex is Zenith.MacroHost.Execution.TargetedMacroPlanException
                    or InvalidOperationException or OperationCanceledException)
                { checks.Add(new("Real interpreter owned-window exercise", false, $"The owned macro exercise could not finish ({ex.GetType().Name}). No Roblox qualification is established.")); }
                result = checks.All(c => c.Passed) ? 0 : 3;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { checks.Add(new("Native probe setup", false, $"Probe could not finish ({ex.GetType().Name}).")); result = 3; }
            finally
            {
                foreach (var window in windows) if (!window.IsDestroyed) window.Close();
                try
                {
                    var report = new NativeProbeReport(1, "zenith-native-probe/0.3.0", EvidenceOrigin.NativeControlledExercise,
                        started, DateTimeOffset.UtcNow, true, checks,
                        "Real MacroSuite interpreter and shared target-input owner, own-process message receivers and one capture owner only. Roblox qualification, account/arrival verification, image matching, host execution IPC, broadcast and reconnect remain unverified; no live release gate is satisfied.");
                    await Reports.WriteAtomicAsync(args[1], JsonSerializer.Serialize(report, Reports.JsonOptions), default);
                    Console.WriteLine($"Native controlled checks: {checks.Count(c => c.Passed)}/{checks.Count}; live Roblox gates remain unverified.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"Native report save failed ({ex.GetType().Name})."); result = 2; }
                app.Shutdown(result);
            }
        };
        app.Run();
        return result;
    }

    private static async Task CheckTargetedInputAsync(Receiver first, Receiver second, Receiver cover,
        List<NativeCheck> checks, CancellationToken ct)
    {
        var transport = new OwnedWindowMessageExecutor();
        var targetA = transport.Register(first.Handle); var targetB = transport.Register(second.Handle);
        first.ObserveInput = (window, message, parameter, position) => transport.ObserveProcessed(targetA, window, message, parameter, position);
        second.ObserveInput = (window, message, parameter, position) => transport.ObserveProcessed(targetB, window, message, parameter, position);
        first.NativeDestroyed += window => { if (window == targetA.Window) transport.MarkDestroyed(targetA); };
        second.NativeDestroyed += window => { if (window == targetB.Window) transport.MarkDestroyed(targetB); };
        await using var coordinator = new TargetedInputCoordinator(transport, new(MaximumRuns: 4));
        var runA = coordinator.TryOpen(targetA, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("Own receiver A could not acquire input ownership.");
        var runB = coordinator.TryOpen(targetB, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("Own receiver B could not acquire input ownership.");
        Guid runAId = runA.Snapshot.RunId, runBId = runB.Snapshot.RunId;
        long initialGeneration = runA.Snapshot.Generation;
        var down = new TargetedInputSignal(TargetedInputKind.KeyDown, Key: 0x87);
        var up = new TargetedInputSignal(TargetedInputKind.KeyUp, Key: 0x87);
        var leftDown = new TargetedInputSignal(TargetedInputKind.LeftDown, X: 20, Y: 20);
        var leftUp = new TargetedInputSignal(TargetedInputKind.LeftUp, X: 20, Y: 20);
        bool unfocused = Native.GetForegroundWindow() != first.Handle && Native.GetForegroundWindow() != second.Handle;
        var submitted = await Task.WhenAll(runA.SubmitAsync([down, leftDown], ct), runB.SubmitAsync([down], ct));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        bool physical = (Native.GetAsyncKeyState(0x87) & 0x8000) != 0;
        checks.Add(new("Two independent runs use the common owner",
            submitted[0].Status == TargetedInputDispatchStatus.Submitted && submitted[0].SubmittedCount == 2
            && submitted[1].Status == TargetedInputDispatchStatus.Submitted && submitted[1].SubmittedCount == 1
            && runA.Snapshot.LogicalHolds == 2 && runB.Snapshot.LogicalHolds == 1
            && first.Keys == 1 && first.Clicks == 1 && second.Keys == 1 && second.Clicks == 0 && unfocused,
            $"Independent holds A={runA.Snapshot.LogicalHolds}, B={runB.Snapshot.LogicalHolds}; both unfocused={unfocused}. These are diagnostic runs, not macro-engine or Roblox acceptance."));
        checks.Add(new("Message delivery is not physical keyboard state", !physical,
            $"GetAsyncKeyState(F24) down during posted holds={physical}. Queued messages are not hardware/raw-input state."));

        double originalLeft = first.Left;
        first.Left = originalLeft + 7;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var moved = await transport.ValidateAsync(targetA, false, ct);
        var cleanupIdentity = await transport.ValidateAsync(targetA, true, ct);
        var paused = await runA.PauseAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Pause releases only its run after a geometry change",
            moved == TargetedInputValidation.GeometryChanged && cleanupIdentity == TargetedInputValidation.Current
            && paused.State == TargetedInputRunState.Paused && paused.Generation > initialGeneration
            && paused.LogicalHolds == 2 && paused.SubmittedHolds == 0 && first.Keys == 2 && first.Clicks == 2
            && runB.Snapshot.State == TargetedInputRunState.Running && runB.Snapshot.SubmittedHolds == 1 && second.Keys == 1,
            $"Ordinary validation={moved}; cleanup identity={cleanupIdentity}; paused A submitted holds={paused.SubmittedHolds}; B submitted holds={runB.Snapshot.SubmittedHolds}."));
        first.Left = originalLeft;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var resumed = await runA.ResumeAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Explicit resume restores only unfinished holds",
            resumed.State == TargetedInputRunState.Running && resumed.Generation > paused.Generation
            && resumed.SubmittedHolds == 2 && first.Keys == 3 && first.Clicks == 3 && second.Keys == 1,
            $"Resume state={resumed.State}; generation advanced={resumed.Generation > paused.Generation}; no completed event was replayed."));
        var completed = await runA.SubmitAsync([leftUp, up], ct);
        var lastHold = await runA.SubmitAsync([down], ct);
        var stopped = await runA.StopAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        int keysAfterStop = first.Keys, clicksAfterStop = first.Clicks;
        var stale = await runA.SubmitAsync([down], ct);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Stop advances generation and rejects the old run",
            completed.SubmittedCount == 2 && lastHold.SubmittedCount == 1 && stopped.State == TargetedInputRunState.Stopped
            && stopped.Generation > resumed.Generation && stopped.SubmittedHolds == 0 && stopped.LogicalHolds == 0
            && stale.Status == TargetedInputDispatchStatus.Inactive && first.Keys == keysAfterStop && first.Clicks == clicksAfterStop,
            $"Stopped state={stopped.State}; stale submission={stale.Status}; submitted holds={stopped.SubmittedHolds}. Already queued messages cannot be recalled."));

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct); cancelled.Cancel();
        long bGeneration = runB.Snapshot.Generation;
        var cancelledBatch = await runB.SubmitAsync([leftDown], cancelled.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Cancellation joins cleanup without sending the next event",
            cancelledBatch.Status == TargetedInputDispatchStatus.Cancelled && cancelledBatch.SubmittedCount == 0
            && runB.Snapshot.State == TargetedInputRunState.Stopped && runB.Snapshot.Generation > bGeneration
            && second.Keys == 2 && second.Clicks == 0 && runB.Snapshot.SubmittedHolds == 0,
            $"Cancelled batch={cancelledBatch.Status}; B processed key messages={second.Keys}, click messages={second.Clicks}; stopped={runB.Snapshot.State == TargetedInputRunState.Stopped}."));
        var receiptsA = transport.ProcessedSequences(targetA, runAId); var receiptsB = transport.ProcessedSequences(targetB, runBId);
        checks.Add(new("Receiver processing acknowledges per-run sequence order",
            receiptsA.SequenceEqual(Enumerable.Range(1, 10).Select(value => (long)value))
            && receiptsB.SequenceEqual(new long[] { 1, 2 }) && transport.PendingCount(targetA) == 0 && transport.PendingCount(targetB) == 0,
            $"Cooperating receiver A acknowledged {receiptsA.Length} ordered commands; B acknowledged {receiptsB.Length}. Transport Queued alone was not treated as receiver processing."));

        var emergency = coordinator.TryOpen(targetA, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("A cleaned receiver could not acquire a new run.");
        var emergencyDown = await emergency.SubmitAsync([leftDown], ct);
        long emergencyGeneration = emergency.Snapshot.Generation;
        coordinator.SignalStopAll();
        var emergencyStop = await emergency.StopAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Emergency signal fences and joins the common owner",
            emergencyDown.SubmittedCount == 1 && emergencyStop.State == TargetedInputRunState.Stopped
            && emergencyStop.Generation > emergencyGeneration && emergencyStop.SubmittedHolds == 0
            && transport.ProcessedSequences(targetA, emergency.Snapshot.RunId).SequenceEqual(new long[] { 1, 2 }),
            "The shared emergency signal closed admission and the actual cleanup task released the run-owned left-button message hold."));

        var unregistered = targetA with { RegistrationId = Guid.NewGuid(), Window = cover.Handle };
        var invalidRun = coordinator.TryOpen(unregistered, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("Unregistered-target rejection could not be exercised.");
        var invalid = await invalidRun.SubmitAsync([down], ct); await invalidRun.StopAsync();
        checks.Add(new("Unregistered HWND and nonce are rejected",
            invalid.SubmittedCount == 0 && invalid.Status == TargetedInputDispatchStatus.Faulted && cover.Keys == 0 && cover.Clicks == 0,
            "A structurally valid target cannot submit to an unregistered own cover window. No external HWND was used."));
        var unsupportedRun = coordinator.TryOpen(targetB, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("Unsupported-key rejection could not be exercised.");
        var unsupported = await unsupportedRun.SubmitAsync([new(TargetedInputKind.KeyDown, Key: 0x86)], ct);
        await unsupportedRun.StopAsync();
        checks.Add(new("Unsupported diagnostic input is rejected",
            unsupported.SubmittedCount == 0 && unsupported.Status == TargetedInputDispatchStatus.Faulted && second.Keys == 2,
            "The transport supports only F24 and left-button messages; F23 was rejected without a global-input fallback."));
        transport.Revoke(targetA);
        var revokedRun = coordinator.TryOpen(targetA, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidOperationException("Revoked-nonce rejection could not be exercised.");
        int beforeRevoked = first.Keys;
        var revoked = await revokedRun.SubmitAsync([down], ct); await revokedRun.StopAsync();
        checks.Add(new("Revoked live nonce cannot be reused",
            revoked.SubmittedCount == 0 && first.Keys == beforeRevoked
            && await transport.ValidateAsync(targetA, true, ct) == TargetedInputValidation.Unavailable,
            "Revoking a live registration prevents submission and is not falsely described as destruction."));
        second.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks.Add(new("Actual receiver destruction retires its incarnation",
            second.IsDestroyed && await transport.ValidateAsync(targetB, true, ct) == TargetedInputValidation.Gone,
            "WM_NCDESTROY/source disposal marks the original owned receiver gone. A failed identity query alone never does."));
        checks.Add(new("Cover stays a nonrecipient", cover.Keys == 0 && cover.Clicks == 0,
            $"The overlapping cover processed {cover.Keys + cover.Clicks} diagnostic input messages. No Roblox or other-process recipient was registered."));
    }

    private sealed class Receiver : Window
    {
        public int Keys { get; private set; }
        public int Clicks { get; private set; }
        public nint Handle { get; private set; }
        public bool IsDestroyed { get; private set; }
        public Action<nint, int, nint, nint>? ObserveInput { get; set; }
        public event Action<nint>? NativeDestroyed;
        private HwndSource? _source;
        public Receiver(Brush brush)
        {
            Title = "Temporary Zenith capability probe";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Width = 280; Height = 160; Left = 70; Top = 70;
            ShowActivated = false; ShowInTaskbar = false; Background = brush;
            SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                _source = HwndSource.FromHwnd(Handle); _source.AddHook(Hook);
                _source.Disposed += (_, _) => { if (!Native.IsWindow(Handle)) MarkDestroyed(); _source = null; };
            };
        }
        private void MarkDestroyed()
        { if (IsDestroyed) return; IsDestroyed = true; NativeDestroyed?.Invoke(Handle); }
        private nint Hook(nint window, int message, nint w, nint l, ref bool handled)
        {
            if ((message is 0x100 or 0x101) && w == 0x87) { Keys++; ObserveInput?.Invoke(window, message, w, l); handled = true; }
            if (message is 0x201 or 0x202) { Clicks++; ObserveInput?.Invoke(window, message, w, l); handled = true; }
            if (message == 0x82) MarkDestroyed(); // WM_NCDESTROY: actual original receiver lifecycle.
            return 0;
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    }
}
