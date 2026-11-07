using MacroSuite.Core.Input;
using Zenith.Sessions.Core;

namespace Zenith.MacroHost.Execution;

public enum CompanionMacroOperationPhase { Preparing, Executing, Stopping, Finalizing, Finished, Faulted }

public sealed record CompanionMacroRecipientSnapshot(TargetedInputTarget Target, TargetedMacroStartStatus? StartStatus,
    TargetedMacroRunSnapshot? Run, TargetedMacroResultStatus? Result);

public sealed record CompanionMacroOperationSnapshot(Guid OperationId, CompanionMacroOperationPhase Phase,
    SavedMacroPreparationStatus? Preparation, CompanionMacroOperationStatus? Result,
    IReadOnlyList<CompanionMacroRecipientSnapshot> Recipients);

/// <summary>Tracks one host-local operation and its pinned recipients. This handle cannot control a replacement run.</summary>
public sealed class CompanionMacroOperation
{
    private sealed record Progress(SavedMacroPreparationStatus? Preparation, bool Finalizing);

    private readonly HostInputOperationLease _lease;
    private readonly TargetedInputTarget[] _targets;
    private readonly TaskCompletionSource<CompanionMacroOperationResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TargetedMacroStartResult?[]? _starts;
    private Progress _progress = new(null, false);

    internal CompanionMacroOperation(HostInputOperationLease lease, TargetedInputTarget[] targets)
    {
        _lease = lease;
        _targets = targets;
        _starts = new TargetedMacroStartResult?[targets.Length];
    }

    public Guid Id => _lease.OperationId;

    /// <summary>Completes after all recipients and the host lease finish their actual cleanup.</summary>
    public Task<CompanionMacroOperationResult> Completion => _completion.Task;

    internal void Begin(Func<Task<CompanionMacroOperationResult>> execute)
    {
        _ = Task.Run(async () =>
        {
            try { _completion.SetResult(await execute().ConfigureAwait(false)); }
            catch (OperationCanceledException cancelled) { _completion.SetCanceled(cancelled.CancellationToken); }
            catch (Exception error) { _completion.SetException(error); }
            finally { Volatile.Write(ref _starts, null); }
        });
    }

    internal void PublishPreparation(SavedMacroPreparationStatus preparation)
        => Volatile.Write(ref _progress, new(preparation, false));

    internal void PublishStart(int index, TargetedMacroStartResult start)
        => Volatile.Write(ref _starts![index], start);

    internal void PublishFinalizing()
        => Volatile.Write(ref _progress, _progress with { Finalizing = true });

    /// <summary>Read bounded progress in recipient order. Running recipients are sampled independently.</summary>
    public CompanionMacroOperationSnapshot GetSnapshot()
    {
        var starts = Volatile.Read(ref _starts);
        var terminal = Completion.IsCompletedSuccessfully ? Completion.Result : null;
        var progress = Volatile.Read(ref _progress);
        var phase = terminal is not null ? CompanionMacroOperationPhase.Finished
            : Completion.IsFaulted || Completion.IsCanceled ? CompanionMacroOperationPhase.Faulted
            : progress.Finalizing ? CompanionMacroOperationPhase.Finalizing
            : !_lease.CanContinue ? CompanionMacroOperationPhase.Stopping
            : progress.Preparation is null ? CompanionMacroOperationPhase.Preparing
            : CompanionMacroOperationPhase.Executing;

        var rows = new CompanionMacroRecipientSnapshot[_targets.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            if (terminal is not null)
            {
                var outcome = i < terminal.Recipients.Count ? terminal.Recipients[i] : null;
                rows[i] = new(_targets[i], outcome?.StartStatus, outcome?.Run?.Snapshot, outcome?.Run?.Status);
                continue;
            }

            var start = starts is null ? null : Volatile.Read(ref starts[i]);
            var run = start?.Run;
            var completed = run?.Completion.IsCompletedSuccessfully == true ? run.Completion.Result : null;
            rows[i] = new(_targets[i], start?.Status, completed?.Snapshot ?? run?.GetSnapshot(), completed?.Status);
        }

        return new(Id, phase, terminal?.Preparation ?? progress.Preparation, terminal?.Status, Array.AsReadOnly(rows));
    }

    /// <summary>Immediately closes admission for this operation. Await Completion to join cleanup.</summary>
    public void SignalStop() => _lease.SignalStop();

    /// <summary>Pause an exact recipient. Null means the run is unavailable through this handle.</summary>
    public async Task<TargetedMacroRunSnapshot?> PauseRecipientAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return FindRun(runId) is { } run ? await run.PauseAsync(ct).ConfigureAwait(false) : null;
    }

    /// <summary>Resume an exact recipient. Null means the run is unavailable through this handle.</summary>
    public async Task<TargetedMacroRunSnapshot?> ResumeRecipientAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return FindRun(runId) is { } run ? await run.ResumeAsync(ct).ConfigureAwait(false) : null;
    }

    /// <summary>Stop one recipient and join its cleanup. Null means the run is unavailable through this handle.</summary>
    public async Task<TargetedMacroResult?> StopRecipientAsync(Guid runId)
        => FindRun(runId) is { } run ? await run.StopAsync().ConfigureAwait(false) : null;

    private TargetedMacroRun? FindRun(Guid runId)
    {
        var starts = Volatile.Read(ref _starts);
        if (starts is null || !_lease.CanContinue) return null;
        for (int i = 0; i < starts.Length; i++)
            if (Volatile.Read(ref starts[i])?.Run is { } run && run.Id == runId && !run.Completion.IsCompleted)
                return run;
        return null;
    }
}
