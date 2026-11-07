namespace Zenith.Sessions.Core;

/// <summary>Short-lived owned credential. Never include its value in UI, diagnostics, URLs or exports.</summary>
public sealed class RobloxSessionCredential : IDisposable
{
    public const int MaximumLength = RobloxSessionIdentity.MaximumCookieLength;
    private readonly object _gate = new();
    private char[]? _value;
    public DateTimeOffset? ExpiresUtc { get; }
    public RobloxSessionCredential(string value, DateTimeOffset? expiresUtc = null)
    {
        if (!RobloxSessionIdentity.IsValidCookie(value))
            throw new ArgumentException("The saved Roblox session credential is invalid.");
        if (expiresUtc == default(DateTimeOffset)) throw new ArgumentException("The session expiry is invalid.");
        _value = value.ToCharArray(); ExpiresUtc = expiresUtc;
    }
    // The unavoidable header/protected-record string is operational only; callers must not retain or log it.
    public string CopyValue()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_value is null, this); return new string(_value); } }
    public void Dispose() { lock (_gate) { if (_value is not null) Array.Clear(_value); _value = null; } }
    public override string ToString() => "RobloxSessionCredential { [protected] }";
}

public sealed class RobloxSessionValidation(LinkedIdentity identity, RobloxSessionCredential? rotatedCredential = null) : IDisposable
{
    public LinkedIdentity Identity { get; } = identity;
    public RobloxSessionCredential? RotatedCredential { get; } = rotatedCredential;
    public void Dispose() => RotatedCredential?.Dispose();
    public override string ToString() => "RobloxSessionValidation { Credential = [protected] }";
}
/// <summary>
/// Network and TimedOut are ambiguous: Roblox may have handled the request and replaced the session.
/// NotProcessed is only for outcomes that provably did not replace it: the connection never reached
/// Roblox, or Roblox turned the request away as busy (429/503) without sending a session cookie.
/// </summary>
public enum RobloxSessionFailure { InvalidSession, Challenge, Network, TimedOut, InvalidResponse, NotProcessed }
public sealed class RobloxSessionException(RobloxSessionFailure failure, string explanation) : Exception(explanation)
{ public RobloxSessionFailure Failure { get; } = failure; }

public interface IRobloxSessionSignIn
{
    // Only the app-owned official sign-in window may supply this credential, after explicit user Save.
    Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct);
}
public interface IRobloxSessionClient
{
    Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct);
}
/// <summary>
/// Saved metadata and verified website identity only; never native launch or arrival proof.
/// Refreshed means the verified player already had a saved Roblox session on ProfileId, so that
/// row's saved sign-in was replaced and no profile was created.
/// </summary>
public sealed record AddedRobloxAccount(Guid ProfileId, LinkedIdentity Identity, bool Refreshed = false);
public interface IRobloxSessionLinkService : IAsyncDisposable
{
    Task<AddedRobloxAccount> AddAsync(CancellationToken ct);
    Task<LinkedIdentity> LinkAsync(Guid profileId, CancellationToken ct);
    Task<LinkedIdentity> CheckSavedAsync(Guid profileId, CancellationToken ct);
    Task<RobloxHandoffProbeResult> InspectHandoffAsync(Guid profileId, long placeId, IRobloxHandoffProbe probe, CancellationToken ct);
    Task DisconnectAsync(Guid profileId, CancellationToken ct);
    void Cancel();
}
