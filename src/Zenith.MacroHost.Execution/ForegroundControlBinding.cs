using MacroSuite.Core.Abstractions;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Execution;

/// <summary>Binds one explicit host grant to an independently authorized client-window receipt and the captured Stop generation.</summary>
public sealed class ForegroundControlBinding
{
    private readonly CompanionForegroundPlayback _playback;
    private readonly Guid _hostSession;
    private readonly long _generation;
    private readonly Func<int, ForegroundControlRequest, CancellationToken, ValueTask<TargetWindowInfo?>> _ownedWindow;

    public ForegroundControlBinding(CompanionForegroundPlayback playback, Guid hostSession, long generation,
        Func<int, ForegroundControlRequest, CancellationToken, ValueTask<TargetWindowInfo?>> authorizeOwnedWindow)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        if (hostSession == Guid.Empty || generation < 0) throw new ArgumentException("A current host grant is required.");
        _hostSession = hostSession;
        _generation = generation;
        _ownedWindow = authorizeOwnedWindow ?? throw new ArgumentNullException(nameof(authorizeOwnedWindow));
    }

    public async Task<ForegroundControlAdmission> StartAsync(int peer, ForegroundControlRequest request, CancellationToken ct)
    {
        if (peer <= 0 || !request.IsValid || request.HostSessionId != _hostSession)
            return new(null, ForegroundControlOutcome.InvalidRequest);
        ct.ThrowIfCancellationRequested();
        var candidate = await _ownedWindow(peer, request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (candidate is not { IsOwnWindow: false } window || window.Handle.ToInt64() != request.Window
            || window.ProcessId != request.ProcessId
            || window.ProcessInstance != DateTime.FromFileTimeUtc(request.CreationFileTime).Ticks)
            return new(null, ForegroundControlOutcome.TargetUnavailable);
        var reference = new SavedMacroReference(request.HostSessionId, request.MacroId, request.SavedRevision);
        var started = _playback.Start(reference, window, _generation, ct);
        return started.Operation is { } run ? new(new Operation(run), ForegroundControlOutcome.None)
            : new(null, Outcome(started.Rejection ?? ForegroundPlaybackStatus.Faulted));
    }

    private static ForegroundControlOutcome Outcome(ForegroundPlaybackStatus status) => status switch
    {
        ForegroundPlaybackStatus.InvalidRequest => ForegroundControlOutcome.InvalidRequest,
        ForegroundPlaybackStatus.Busy => ForegroundControlOutcome.Busy,
        ForegroundPlaybackStatus.Stopped => ForegroundControlOutcome.Stopped,
        ForegroundPlaybackStatus.PreparationRejected => ForegroundControlOutcome.PreparationRejected,
        ForegroundPlaybackStatus.TargetUnavailable => ForegroundControlOutcome.TargetUnavailable,
        ForegroundPlaybackStatus.InputHeld => ForegroundControlOutcome.InputHeld,
        ForegroundPlaybackStatus.LicenseRequired => ForegroundControlOutcome.LicenseRequired,
        ForegroundPlaybackStatus.Completed => ForegroundControlOutcome.Completed,
        ForegroundPlaybackStatus.CleanupFailed => ForegroundControlOutcome.CleanupFailed,
        _ => ForegroundControlOutcome.Faulted
    };

    private sealed class Operation(CompanionForegroundPlayback.Run run) : IForegroundControlOperation
    {
        public Guid Id => run.Id;
        public Task Completion => run.Completion;
        public ForegroundControlSnapshot Snapshot => run.Completion.IsCompleted
            ? new(ForegroundControlState.Finished,
                run.Completion.IsCompletedSuccessfully ? Outcome(run.Completion.Result.Status) : ForegroundControlOutcome.Faulted)
            : new(run.IsStopping ? ForegroundControlState.Stopping : run.IsPaused ? ForegroundControlState.Paused
                : run.HasSession ? ForegroundControlState.Running : ForegroundControlState.Preparing);
        public Task<bool> PauseAsync(CancellationToken ct) => run.PauseAsync(ct);
        public Task<bool> ResumeAsync(CancellationToken ct) => run.ResumeAsync(ct);
        public void Stop() => run.Stop();
    }
}
