using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>Optional first-party sink capability. The invoking context, not player-global options, owns this dispatch.</summary>
public interface IPlaybackSessionBoundSink : IPlaybackSessionSink
{
    ValueTask EmitSessionInputAsync(PlaybackSessionContext owner, InputEvent input, bool cleanup, CancellationToken ct);
}

/// <summary>A final observation, not an atomic foreground reservation or evidence of receiver consumption.</summary>
public enum PlaybackInputAdmission { Allowed, Paused, TargetUnavailable, Stopped }

/// <summary>No ordinary input was submitted. The session must unwind native locks before requesting a manual pause.</summary>
public sealed class PlaybackInputDeferredException : InvalidOperationException
{
    public PlaybackInputAdmission Admission { get; }
    public PlaybackInputDeferredException(PlaybackInputAdmission admission)
        : base(admission == PlaybackInputAdmission.Paused
            ? "Playback was paused before input submission. Resume manually to continue."
            : "The selected target is unavailable. Focus it, then resume manually.")
    {
        if (admission is not (PlaybackInputAdmission.Paused or PlaybackInputAdmission.TargetUnavailable))
            throw new ArgumentOutOfRangeException(nameof(admission), "Only a deferred playback admission is valid.");
        Admission = admission;
    }
}
