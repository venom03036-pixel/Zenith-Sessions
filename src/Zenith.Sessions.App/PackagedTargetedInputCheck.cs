using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// The real packaged owner with an in-memory transport. No HWND, physical input or account is accessed.
internal static class PackagedTargetedInputCheck
{
    public static async Task VerifyAsync(CancellationToken ct)
    {
        if (typeof(TargetedInputCoordinator).Assembly.GetName().Name != "Zenith.Automation.Input"
            || typeof(PixelRectangle).Assembly != typeof(TargetedInputCoordinator).Assembly
            || !typeof(LaunchIntent).Assembly.GetForwardedTypes().Contains(typeof(TargetedInputRun)))
            throw new InvalidDataException("The packaged shared input runtime or compatibility forwards are unavailable.");
        var transport = new ControlledTransport();
        await using var owner = new TargetedInputCoordinator(transport, new(MaximumRuns: 2));
        var target = new TargetedInputTarget(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.UtcTicks, (nint)100,
            new(-1000, 50, 640, 480), 1);
        var first = owner.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidDataException("The controlled input lease was unavailable.");
        var second = owner.TryOpen(target with { RegistrationId = Guid.NewGuid(), Window = (nint)101 }, TargetedInputPurpose.OwnedWindowDiagnostic).Run
            ?? throw new InvalidDataException("The second controlled input lease was unavailable.");
        if (owner.TryOpen(target with { RegistrationId = Guid.NewGuid() }, TargetedInputPurpose.BroadcastFollower).Run is not null)
            throw new InvalidDataException("A second purpose stole an owned input target.");
        var a = await first.SubmitAsync([new(TargetedInputKind.KeyDown, 135), new(TargetedInputKind.LeftDown, X: 20, Y: 30)], ct);
        var b = await second.SubmitAsync([new(TargetedInputKind.KeyDown, 134)], ct);
        // Pause, resume, clear and stop take no token; the stage deadline still bounds each wait.
        var paused = await first.PauseAsync().WaitAsync(ct);
        if (a.SubmittedCount != 2 || b.SubmittedCount != 1 || paused.State != TargetedInputRunState.Paused
            || paused.LogicalHolds != 2 || paused.SubmittedHolds != 0 || second.Snapshot.SubmittedHolds != 1)
            throw new InvalidDataException("The packaged owner mixed independent holds or failed pause cleanup.");
        var resumed = await first.ResumeAsync().WaitAsync(ct);
        if (resumed.State != TargetedInputRunState.Running || resumed.SubmittedHolds != 2)
            throw new InvalidDataException("The packaged owner failed unfinished-hold restoration.");
        var cleared = await first.ClearHoldsAsync().WaitAsync(ct);
        if (cleared.State != TargetedInputRunState.Running || cleared.LogicalHolds != 0 || cleared.SubmittedHolds != 0
            || owner.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run is not null)
            throw new InvalidDataException("Clearing holds lost the packaged target lease or retained restore intent.");
        if ((await first.SubmitAsync([new(TargetedInputKind.KeyDown, 136), new(TargetedInputKind.KeyUp, 136)], ct)).SubmittedCount != 2)
            throw new InvalidDataException("The packaged owner could not continue after clearing holds.");
        await first.PauseAsync().WaitAsync(ct);
        if ((await first.ClearHoldsAsync().WaitAsync(ct)).State != TargetedInputRunState.Paused
            || (await first.ResumeAsync().WaitAsync(ct)).SubmittedHolds != 0)
            throw new InvalidDataException("Clear erased pause state or restored cleared holds.");
        owner.SignalStopAll();
        var stopped = await Task.WhenAll(first.StopAsync(), second.StopAsync()).WaitAsync(ct);
        if (stopped.Any(run => run.State != TargetedInputRunState.Stopped || run.SubmittedHolds != 0 || run.LogicalHolds != 0)
            || transport.Commands.Count != 12 || transport.Commands.Any(command => command.Target.Window is not (100 or 101)))
            throw new InvalidDataException("The packaged owner failed terminal cleanup or emitted an unexpected target.");
        foreach (var group in transport.Commands.GroupBy(command => command.RunId))
        {
            long sequence = 0;
            foreach (var command in group) if (command.Sequence != ++sequence)
                throw new InvalidDataException("The packaged input sequence was not contiguous.");
        }
    }
    private sealed class ControlledTransport : ITargetedInputTransport
    {
        public readonly List<TargetedInputCommand> Commands = [];
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(TargetedInputValidation.Current); }
        public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!admissionOpen()) return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            Commands.Add(command); return ValueTask.FromResult(TargetedInputSubmission.Queued);
        }
    }
}
