using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>Owned browser result. Every external scheme was cancelled; no native start occurred.</summary>
public sealed class NativeBrowserCapture : IDisposable
{
    private NativeHandoffCandidate? _candidate;
    private RobloxSessionCredential? _credential;
    public RobloxHandoffProbeOutcome Outcome { get; }
    public NativeHandoffCaptureStatus? ShapeStatus { get; }
    [JsonIgnore] public NativeHandoffCandidate? Candidate => Volatile.Read(ref _candidate);
    [JsonIgnore] public RobloxSessionCredential? CurrentCredential => Volatile.Read(ref _credential);
    public NativeBrowserCapture(RobloxHandoffProbeOutcome outcome, NativeHandoffCandidate? candidate = null,
        RobloxSessionCredential? currentCredential = null, NativeHandoffCaptureStatus? shapeStatus = null)
    {
        if (!Enum.IsDefined(outcome) || shapeStatus.HasValue && !Enum.IsDefined(shapeStatus.Value))
            throw new ArgumentException("The browser preparation outcome is invalid.");
        Outcome = outcome; _candidate = candidate; _credential = currentCredential; ShapeStatus = shapeStatus;
    }
    public NativeHandoffCandidate? TakeCandidate() => Interlocked.Exchange(ref _candidate, null);
    public void Dispose()
    {
        Interlocked.Exchange(ref _candidate, null)?.Dispose();
        Interlocked.Exchange(ref _credential, null)?.Dispose();
    }
    public override string ToString() => $"NativeBrowserCapture {{ Outcome = {Outcome}, Handoff = [redacted], Credential = [protected] }}";
}

public interface IRobloxNativeHandoffCapture : IAsyncDisposable
{
    // Receives only the selected account's owned credential. Must cancel external dispatch
    // synchronously and join actual browser cleanup before returning its current credential.
    Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct);
    // Same meaning as IRobloxHandoffProbe.Refusal: read before the grant is reserved.
    RobloxHandoffProbeOutcome? Refusal => null;
}

public enum NativeHandoffPreparationStatus
{ Ready, Busy, RequestChanged, SignInRequired, IdentityMismatch, AuthorizationChanged, CaptureRejected, CleanupFailed, Expired, Cancelled, Failed }

/// <summary>Short-lived owned preparation, never queue data or verified native identity.</summary>
public sealed class NativeHandoffPreparation : IDisposable
{
    [JsonIgnore] public NativeHandoffCandidate Candidate { get; }
    [JsonIgnore] public AuthorizationWriteSnapshot FinalGrant { get; }
    public LaunchIntent Intent { get; }
    public NativeHandoffPreparation(NativeHandoffCandidate candidate, AuthorizationWriteSnapshot finalGrant, LaunchIntent intent)
    { Candidate = candidate; FinalGrant = finalGrant; Intent = intent; }
    public void Dispose() => Candidate.Dispose();
    public override string ToString() => "NativeHandoffPreparation { Handoff = [redacted], Grant = [protected], Native identity = unverified }";
}
public sealed record NativeHandoffPreparationResult(NativeHandoffPreparationStatus Status,
    [property: JsonIgnore] NativeHandoffPreparation? Preparation = null, NativeBrowserPreparationFacts? BrowserFacts = null);

public interface IRobloxNativeHandoffPreparer
{
    // This occurs before capacity reservation. The scheduler must explicitly adopt only this
    // operation's final intent/grant, revalidate it, and reserve fresh capacity at dispatch.
    Task<NativeHandoffPreparationResult> PrepareNativeHandoffAsync(LaunchIntent intent, IRobloxNativeHandoffCapture browser, CancellationToken ct);
}
