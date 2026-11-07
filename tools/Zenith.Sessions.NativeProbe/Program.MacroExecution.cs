using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Zenith.MacroHost.Execution;
using Zenith.Sessions.Capabilities;
using Zenith.Sessions.Core;
using Zenith.Sessions.NativeProbe.Infrastructure;

namespace Zenith.Sessions.NativeProbe;

internal static partial class Program
{
    private static async Task CheckMacroExecutionAsync(Receiver first, Receiver second, Receiver cover,
        List<NativeCheck> checks, CancellationToken ct)
    {
        var transport = new OwnedWindowMessageExecutor();
        var targetA = transport.Register(first.Handle); var targetB = transport.Register(second.Handle);
        Action<nint, int, nint, nint> observeA = (window, message, parameter, position) =>
            transport.ObserveProcessed(targetA, window, message, parameter, position);
        Action<nint, int, nint, nint> observeB = (window, message, parameter, position) =>
            transport.ObserveProcessed(targetB, window, message, parameter, position);
        Action<nint> destroyedA = window => { if (window == targetA.Window) transport.MarkDestroyed(targetA); };
        Action<nint> destroyedB = window => { if (window == targetB.Window) transport.MarkDestroyed(targetB); };
        first.ObserveInput = observeA; second.ObserveInput = observeB;
        first.NativeDestroyed += destroyedA; second.NativeDestroyed += destroyedB;
        var input = new TargetedInputCoordinator(transport, new(MaximumRuns: 4));
        var runner = new TargetedMacroRunner(input, new TargetedMacroInputSupport(new[] { 0x87 }, TargetedMacroSignals.LeftButton),
            (target, scope) => scope == TargetedMacroScope.OwnedWindowDiagnostic && (target == targetA || target == targetB), maximumRuns: 4);
        try
        {
            var source = DiagnosticMacro(TimeSpan.FromSeconds(2));
            var plan = TargetedMacroPlan.Compile(source, TargetedMacroScope.OwnedWindowDiagnostic);
            // The caller changes its own source after compilation. Both actual interpreters must
            // use the privately frozen supported fields rather than this now-incompatible model.
            ((MacroStep[])source.Steps)[0] = new KeyPressStep(0x86);
            source.Settings.RepeatUntilStopped = true;
            var unsupported = TargetedMacroPlan.Compile(new MacroModel
            {
                Settings = FiniteSettings(), Steps = new MacroStep[] { new KeyPressStep(0x86) }
            }, TargetedMacroScope.OwnedWindowDiagnostic);
            var rejectedKey = runner.TryStart(unsupported, targetA);
            var foreign = targetA with { RegistrationId = Guid.NewGuid(), Window = cover.Handle };
            var rejectedRecipient = runner.TryStart(plan, foreign);
            if (rejectedKey.Run is { } unexpectedKey) await unexpectedKey.StopAsync();
            if (rejectedRecipient.Run is { } unexpectedRecipient) await unexpectedRecipient.StopAsync();
            checks.Add(new("Macro preflight refuses unsupported input and unregistered recipient",
                rejectedKey.Status == TargetedMacroStartStatus.UnsupportedInput && rejectedKey.Run is null
                && rejectedRecipient.Status == TargetedMacroStartStatus.Rejected && rejectedRecipient.Run is null
                && transport.PendingCount(targetA) == 0 && cover.Keys == 0 && cover.Clicks == 0,
                "A complete F23 plan and a forged cover-window registration were refused before input ownership/emission. No foreground fallback or other-process recipient was used."));

            var a = StartMacro(runner, plan, targetA);
            await WaitForAsync(() => transport.ProcessedSummary(targetA, a.Id).LeftDown == 1, ct);
            var b = StartMacro(runner, plan, targetB);
            var paused = await a.PauseAsync(ct);
            await DrainReceiptsAsync(transport, targetA, targetB, ct);
            var pausedReceipts = transport.ProcessedSummary(targetA, a.Id);
            var completedB = await b.Completion.WaitAsync(ct);
            await DrainReceiptsAsync(transport, targetA, targetB, ct);
            var whilePaused = a.GetSnapshot();
            checks.Add(new("Real shared macro has independent interpreter pause and progress",
                a.Id != b.Id && paused.State == TargetedMacroState.Paused
                && paused.Input.LogicalHolds == 2 && paused.Input.SubmittedHolds == 0
                && pausedReceipts.KeyDown == 1 && pausedReceipts.KeyUp == 1
                && pausedReceipts.LeftDown == 1 && pausedReceipts.LeftUp == 1
                && completedB.Status == TargetedMacroResultStatus.Completed && completedB.Snapshot.CompletedSteps == 5
                && whilePaused.State == TargetedMacroState.Paused && !a.Completion.IsCompleted,
                $"Both real MacroExecutor instances used one frozen five-step plan. B completed {completedB.Snapshot.CompletedSteps} steps while A stayed paused; A released only its own two holds. The mutated caller source was not executed."));

            var resumed = await a.ResumeAsync(ct); var completedA = await a.Completion.WaitAsync(ct);
            await DrainReceiptsAsync(transport, targetA, targetB, ct);
            var receiptsA = transport.ProcessedSummary(targetA, a.Id); var receiptsB = transport.ProcessedSummary(targetB, b.Id);
            checks.Add(new("Macro resume restores unfinished holds and both interpreters finish",
                resumed.State == TargetedMacroState.Running && completedA.Status == TargetedMacroResultStatus.Completed
                && completedA.Snapshot.CompletedSteps == 5 && CleanInput(completedA.Snapshot.Input) && CleanInput(completedB.Snapshot.Input)
                && receiptsA.KeyDown == 2 && receiptsA.KeyUp == 2 && receiptsA.LeftDown == 2 && receiptsA.LeftUp == 2
                && receiptsB.KeyDown == 1 && receiptsB.KeyUp == 1 && receiptsB.LeftDown == 1 && receiptsB.LeftUp == 1,
                $"A acknowledged {receiptsA.Count} key/button commands including pause release and unfinished-hold restoration; B acknowledged {receiptsB.Count}. Both terminal input snapshots released all logical/submitted holds."));
            checks.Add(new("Macro receiver acknowledgments are ordered and unfocused observations",
                Ordered(transport, targetA, a.Id, receiptsA.Count) && Ordered(transport, targetB, b.Id, receiptsB.Count)
                && receiptsA.ForegroundBeforeAdmission == 0 && receiptsB.ForegroundBeforeAdmission == 0,
                $"Every queued command was acknowledged in per-run sequence order. Recipient foreground samples before admission: A={receiptsA.ForegroundBeforeAdmission}, B={receiptsB.ForegroundBeforeAdmission}. This proves cooperating message receivers only, not Roblox/raw input."));

            var clear = input.TryOpen(targetA, TargetedInputPurpose.OwnedWindowDiagnostic).Run
                ?? throw new InvalidOperationException("The completed macro did not release its owned receiver.");
            try
            {
                await clear.SubmitAsync(new[] { new TargetedInputSignal(TargetedInputKind.KeyDown, Key: 0x87),
                    new TargetedInputSignal(TargetedInputKind.LeftDown, X: 20, Y: 20) }, ct);
                var cleared = await clear.ClearHoldsAsync();
                var competing = input.TryOpen(targetA, TargetedInputPurpose.OwnedWindowDiagnostic).Run;
                bool retained = competing is null;
                if (competing is not null) await competing.StopAsync();
                var afterClear = await clear.SubmitAsync(new[] { new TargetedInputSignal(TargetedInputKind.KeyDown, Key: 0x87),
                    new TargetedInputSignal(TargetedInputKind.KeyUp, Key: 0x87) }, ct);
                var stopped = await clear.StopAsync();
                await DrainReceiptsAsync(transport, targetA, targetB, ct);
                var observed = transport.ProcessedSummary(targetA, clear.Snapshot.RunId);
                checks.Add(new("Clear holds retains ownership for actual subsequent input",
                    cleared.State == TargetedInputRunState.Running && cleared.LogicalHolds == 0 && cleared.SubmittedHolds == 0
                    && retained && afterClear.Status == TargetedInputDispatchStatus.Submitted && afterClear.SubmittedCount == 2
                    && CleanInput(stopped) && observed.Count == 6 && observed.Balanced && Ordered(transport, targetA, clear.Snapshot.RunId, 6),
                    "The shared clear transition released F24/left holds while retaining the HWND lease, refused a competing run, then delivered a later F24 pair through that same input run."));
            }
            finally { await clear.StopAsync(); }

            var heldPlan = TargetedMacroPlan.Compile(DiagnosticMacro(TimeSpan.FromSeconds(2)), TargetedMacroScope.OwnedWindowDiagnostic);
            var stopRun = StartMacro(runner, heldPlan, targetA);
            await WaitForAsync(() => transport.ProcessedSummary(targetA, stopRun.Id).LeftDown == 1, ct);
            var stoppedMacro = await stopRun.StopAsync();
            await DrainReceiptsAsync(transport, targetA, targetB, ct);
            int afterStop = transport.ProcessedSummary(targetA, stopRun.Id).Count;
            var attemptedResume = await stopRun.ResumeAsync(ct);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var stopReceipts = transport.ProcessedSummary(targetA, stopRun.Id);
            checks.Add(new("Interpreter Stop joins holds and cannot resume a completed run",
                stoppedMacro.Status == TargetedMacroResultStatus.Stopped && CleanInput(stoppedMacro.Snapshot.Input)
                && attemptedResume.State == TargetedMacroState.Stopped && afterStop == 4 && stopReceipts.Count == afterStop
                && stopReceipts.Balanced && Ordered(transport, targetA, stopRun.Id, afterStop),
                "Stopping during the interpreter delay joined actual key/button releases. A subsequent Resume did not replay a step or issue another message."));

            var panicA = StartMacro(runner, heldPlan, targetA); var panicB = StartMacro(runner, heldPlan, targetB);
            await WaitForAsync(() => transport.ProcessedSummary(targetA, panicA.Id).LeftDown == 1
                && transport.ProcessedSummary(targetB, panicB.Id).LeftDown == 1, ct);
            runner.SignalPanic();
            var panicResults = await Task.WhenAll(panicA.Completion, panicB.Completion).WaitAsync(ct);
            await DrainReceiptsAsync(transport, targetA, targetB, ct);
            var panicReceiptsA = transport.ProcessedSummary(targetA, panicA.Id); var panicReceiptsB = transport.ProcessedSummary(targetB, panicB.Id);
            checks.Add(new("Interpreter Panic fences every run and joins each owned release",
                panicResults.All(value => value.Status == TargetedMacroResultStatus.Stopped && CleanInput(value.Snapshot.Input))
                && panicReceiptsA.Count == 4 && panicReceiptsB.Count == 4 && panicReceiptsA.Balanced && panicReceiptsB.Balanced
                && Ordered(transport, targetA, panicA.Id, 4) && Ordered(transport, targetB, panicB.Id, 4)
                && !panicA.GetSnapshot().AccountVerified && !panicA.GetSnapshot().RobloxQualified
                && !panicB.GetSnapshot().AccountVerified && !panicB.GetSnapshot().RobloxQualified,
                "Both actual interpreter tasks completed only after their own F24/left releases. AccountVerified=false and RobloxQualified=false; no host execution IPC or game behavior was qualified."));
            checks.Add(new("Macro execution keeps the unrelated cover a nonrecipient",
                cover.Keys == 0 && cover.Clicks == 0 && transport.PendingCount(targetA) == 0 && transport.PendingCount(targetB) == 0,
                "The unchanged cover received no diagnostic key/button messages. Native submission and receiver acknowledgment stayed inside two exact registered own-process window incarnations."));
        }
        finally
        {
            try { runner.SignalPanic(); await runner.DisposeAsync(); }
            finally
            {
                try { await input.DisposeAsync(); }
                finally
                {
                    first.ObserveInput = null; second.ObserveInput = null;
                    first.NativeDestroyed -= destroyedA; second.NativeDestroyed -= destroyedB;
                }
            }
        }
    }

    private static MacroSettings FiniteSettings() => new()
    { MaxRuntime = TimeSpan.FromSeconds(8), MaxIterations = 64, MaxLoopDepth = 1, PlaybackSpeed = 1, RepeatCount = 1 };

    private static MacroModel DiagnosticMacro(TimeSpan delay) => new()
    {
        Settings = FiniteSettings(),
        Steps = new MacroStep[]
        {
            new KeyPressStep { KeyCode = 0x87, Stroke = "Down", DelayAfterMs = 0 },
            new ClickStep { Position = new Point(20, 20), IsClientRelative = true, Button = MouseButton.Left, Stroke = "Down", DelayAfterMs = 0 },
            new DelayStep(delay),
            new ClickStep { Position = new Point(20, 20), IsClientRelative = true, Button = MouseButton.Left, Stroke = "Up", DelayAfterMs = 0 },
            new KeyPressStep { KeyCode = 0x87, Stroke = "Up", DelayAfterMs = 0 }
        }
    };

    private static TargetedMacroRun StartMacro(TargetedMacroRunner runner, TargetedMacroPlan plan, TargetedInputTarget target)
    {
        var result = runner.TryStart(plan, target);
        return result.Status == TargetedMacroStartStatus.Started && result.Run is not null ? result.Run
            : throw new InvalidOperationException("The own-window interpreter fixture could not start.");
    }

    private static bool CleanInput(TargetedInputRunSnapshot value) => value.State == TargetedInputRunState.Stopped
        && value.LogicalHolds == 0 && value.SubmittedHolds == 0;

    private static bool Ordered(OwnedWindowMessageExecutor transport, TargetedInputTarget target, Guid runId, int count)
        => count > 0 && transport.ProcessedSequences(target, runId).SequenceEqual(Enumerable.Range(1, count).Select(value => (long)value));

    private static Task DrainReceiptsAsync(OwnedWindowMessageExecutor transport, TargetedInputTarget a, TargetedInputTarget b, CancellationToken ct)
        => WaitForAsync(() => transport.PendingCount(a) == 0 && transport.PendingCount(b) == 0, ct);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("The owned interpreter receiver did not reach its bounded observation point.");
            await Task.Delay(5, ct);
        }
        ct.ThrowIfCancellationRequested();
    }
}
