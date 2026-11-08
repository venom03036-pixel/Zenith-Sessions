using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The Direct ticket native launch mode (R-api-7, owner decision of 2026-11-08) behind the same capture seam the official
/// page uses, so the preparer, provider, admission, pacing, Panic fences and dispatch are unchanged. For one preparation it
/// asks Roblox for a launch ticket with the selected account's saved sign-in, builds the roblox-player request itself for
/// the frozen destination, and accepts it through the grammar the official-page capture validates
/// (<see cref="NativeRobloxHandoff.AcceptBuilt"/>); nothing is rewritten after that. No window opens and no click is needed.
/// The credential travels on as the capture's current credential (or Roblox's replacement of it), so the existing
/// same-player verification still runs before any grant. One request at a time; the ticket client is borrowed, not owned.
/// A ticket proves neither the account nor arrival, and the outcome is diagnostic evidence only.
/// </summary>
public sealed class DirectTicketNativeHandoffCapture : IRobloxNativeHandoffCapture
{
    private readonly IRobloxLaunchTicketClient _tickets;
    private readonly TimeProvider _time;
    private int _inFlight;

    public DirectTicketNativeHandoffCapture(IRobloxLaunchTicketClient tickets, TimeProvider? time = null)
    {
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        _time = time ?? TimeProvider.System;
    }

    public RobloxHandoffProbeOutcome? Refusal => Volatile.Read(ref _inFlight) != 0 ? RobloxHandoffProbeOutcome.Busy : null;

    public async Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(credential); ct.ThrowIfCancellationRequested();
        if (context.Destination.ServerKind == RobloxServerKind.Private) return new(RobloxHandoffProbeOutcome.ContextRejected, mode: NativeLaunchMode.DirectTicket);
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return new(RobloxHandoffProbeOutcome.Busy, mode: NativeLaunchMode.DirectTicket);
        try
        {
            try { context.Validated(); }
            catch (ArgumentException) { return new(RobloxHandoffProbeOutcome.ContextRejected, mode: NativeLaunchMode.DirectTicket); }
            // The anti-forgery token is scoped to this profile and its verified account, never another identity.
            using var answer = await _tickets.RequestAsync(credential, new(context.ProfileId, context.ExpectedUserId), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Owned here until it moves onto the returned capture; any other exit disposes it.
            RobloxSessionCredential? current = answer.TakeRotatedCredential();
            try
            {
                var outcome = answer.Outcome switch
                {
                    RobloxLaunchTicketOutcome.Issued => RobloxHandoffProbeOutcome.TicketIssued,
                    RobloxLaunchTicketOutcome.SignInRequired or RobloxLaunchTicketOutcome.Challenge => RobloxHandoffProbeOutcome.SignInRequired,
                    RobloxLaunchTicketOutcome.Refused => RobloxHandoffProbeOutcome.Refused,
                    RobloxLaunchTicketOutcome.RateLimited => RobloxHandoffProbeOutcome.RateLimited,
                    _ => RobloxHandoffProbeOutcome.Failed
                };
                if (outcome != RobloxHandoffProbeOutcome.TicketIssued) return new(outcome, mode: NativeLaunchMode.DirectTicket);
                using var ticket = answer.TakeTicket() ?? throw new InvalidOperationException("An issued launch ticket answer carried no ticket.");
                // The same session that asked for the ticket goes on to the same-player verification, exactly as the page's current cookie does.
                current ??= new RobloxSessionCredential(credential.CopyValue(), credential.ExpiresUtc);
                string request = NativeRobloxHandoffRequest.Build(context, ticket, NativeRobloxHandoffRequest.NewBrowserTrackerId(), Guid.NewGuid(), _time);
                var accepted = NativeRobloxHandoff.AcceptBuilt(context, request, _time);
                if (accepted.Status != NativeHandoffCaptureStatus.AcceptedSourceGrammar || accepted.Candidate is null)
                    return new(RobloxHandoffProbeOutcome.MalformedHandoff, shapeStatus: accepted.Status, mode: NativeLaunchMode.DirectTicket);
                var capture = new NativeBrowserCapture(RobloxHandoffProbeOutcome.TicketIssued, accepted.Candidate, current,
                    NativeHandoffCaptureStatus.AcceptedSourceGrammar, NativeLaunchMode.DirectTicket);
                current = null; return capture;
            }
            finally { current?.Dispose(); }
        }
        finally { Volatile.Write(ref _inFlight, 0); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The ticket client belongs to the shell and is disposed there.
    public override string ToString() => "DirectTicketNativeHandoffCapture { Ticket = [redacted], Session = [protected], Native identity = unverified }";
}
