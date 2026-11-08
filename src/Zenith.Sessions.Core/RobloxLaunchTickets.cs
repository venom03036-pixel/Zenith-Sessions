using System.Runtime.InteropServices;

namespace Zenith.Sessions.Core;

/// <summary>
/// A Roblox authentication ticket for one native start (R-api-7): a short-lived secret held in a pinned char array,
/// revealed exactly once (into the launch request) and zeroed then or on dispose. It is never serialized, logged, kept
/// past the request or shown; ToString is redacted. Visible ASCII only, bounded, and never a session cookie: a value
/// naming the session cookie is refused as a ticket, never used.
/// </summary>
public sealed class ProtectedLaunchTicket : IDisposable
{
    public const int MaximumLength = 8192;
    private readonly object _gate = new();
    private char[]? _value;
    private GCHandle _pin;

    public ProtectedLaunchTicket(ReadOnlySpan<char> ticket)
    {
        if (!IsValid(ticket)) throw new ArgumentException("A launch ticket is 1–8,192 visible ASCII characters and never a session cookie.");
        _value = ticket.ToArray();
        _pin = GCHandle.Alloc(_value, GCHandleType.Pinned);
    }
    ~ProtectedLaunchTicket() => Release();

    /// <summary>The ticket grammar this app accepts: the same bounds the launch-request parser applies to the gameinfo field.</summary>
    public static bool IsValid(ReadOnlySpan<char> ticket)
    {
        if (ticket.Length is < 1 or > MaximumLength) return false;
        foreach (char value in ticket) if (value is < '!' or > '~') return false;
        return !ticket.Contains("ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) && !ticket.Contains("DO-NOT-SHARE", StringComparison.OrdinalIgnoreCase);
    }

    public int Length { get { lock (_gate) return _value?.Length ?? 0; } }
    /// <summary>True once the ticket was revealed or disposed; it can never be read again.</summary>
    public bool IsConsumed { get { lock (_gate) return _value is null; } }

    /// <summary>Reveals the ticket to the reader once, then zeroes it. The span must not escape the delegate.</summary>
    public void Consume(ProtectedCodeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        char[] value;
        lock (_gate) { value = _value ?? throw new ObjectDisposedException(nameof(ProtectedLaunchTicket)); _value = null; }
        try { reader(value); }
        finally { Array.Clear(value); if (_pin.IsAllocated) _pin.Free(); }
    }

    public void Dispose() { Release(); GC.SuppressFinalize(this); }
    private void Release()
    {
        char[]? value;
        lock (_gate) { value = _value; _value = null; }
        if (value is null) return;
        Array.Clear(value);
        if (_pin.IsAllocated) _pin.Free();
    }
    public override string ToString() => "[launch ticket]";
}

/// <summary>
/// Issued: Roblox returned a ticket for the saved sign-in. SignInRequired: Roblox refused the saved session (401).
/// Challenge: Roblox asks for a human sign-in check. Refused: Roblox refused the request (403) after the one anti-forgery
/// retry. RateLimited: Roblox asked to slow down (429). Unavailable: transport, timeout, redirect, or an answer without a
/// readable ticket; a later request may succeed. None of these is a guess.
/// </summary>
public enum RobloxLaunchTicketOutcome { Issued, SignInRequired, Challenge, Refused, RateLimited, Unavailable }

/// <summary>
/// One ticket request's answer. An issued answer carries exactly one ticket, which belongs to whoever takes it; a
/// replacement session cookie Roblox sent with either response travels separately, unverified, for the caller's
/// same-player check. Disposing the answer disposes whatever nobody took. ToString never shows either.
/// </summary>
public sealed class RobloxLaunchTicketResult : IDisposable
{
    private ProtectedLaunchTicket? _ticket;
    private RobloxSessionCredential? _rotated;
    public RobloxLaunchTicketOutcome Outcome { get; }
    public RobloxLaunchTicketResult(RobloxLaunchTicketOutcome outcome, ProtectedLaunchTicket? ticket = null, RobloxSessionCredential? rotatedCredential = null)
    {
        if (!Enum.IsDefined(outcome) || (outcome == RobloxLaunchTicketOutcome.Issued) != (ticket is not null))
        {
            ticket?.Dispose(); rotatedCredential?.Dispose();
            throw new ArgumentException("An issued launch ticket answer carries exactly one ticket; a refusal carries none.");
        }
        Outcome = outcome; _ticket = ticket; _rotated = rotatedCredential;
    }
    /// <summary>Moves the ticket to the caller, who must consume or dispose it. Later calls return null.</summary>
    public ProtectedLaunchTicket? TakeTicket() => Interlocked.Exchange(ref _ticket, null);
    /// <summary>Moves the unverified replacement session (if any) to the caller, who must dispose it. Later calls return null.</summary>
    public RobloxSessionCredential? TakeRotatedCredential() => Interlocked.Exchange(ref _rotated, null);
    public void Dispose() { TakeTicket()?.Dispose(); TakeRotatedCredential()?.Dispose(); }
    public override string ToString() => "Roblox launch ticket answer { Outcome = " + Outcome + ", Ticket = [redacted], Session = [protected] }";
}

/// <summary>
/// One signed-in launch-ticket request for one saved profile and its verified Roblox account. The credential is
/// borrowed for the call and never retained or logged. The client may keep the anti-forgery (CSRF) token Roblox last
/// accepted for that identity, in memory only, so a repeat request is normally one round trip. Sign-out forgets one
/// profile's tokens; Lock, Panic and shutdown forget all of them.
/// </summary>
public interface IRobloxLaunchTicketClient
{
    Task<RobloxLaunchTicketResult> RequestAsync(RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct);
    void ForgetTokens(Guid profileId);
    void ForgetAllTokens();
}
