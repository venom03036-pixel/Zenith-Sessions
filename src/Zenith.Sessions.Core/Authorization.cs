using System.Globalization;

namespace Zenith.Sessions.Core;

public enum AuthorizationHealth { Linked, RefreshPending, SignInRequired }

public static class RobloxOAuthIdentity
{
    public const string Provider = "roblox-oauth";
    // Pinned to Roblox's official OIDC discovery document, not inferred from a generic OIDC subject.
    public const string Issuer = "https://apis.roblox.com/oauth/";
    public static bool HasVerifiedContext(string? issuer, string? subject) => issuer == Issuer
        && subject is { Length: > 0 and <= 255 } && !subject.Any(char.IsControl) && !string.IsNullOrWhiteSpace(subject);
}

public static class RobloxSessionIdentity
{
    public const string Provider = "roblox-session";
    public const string Issuer = "https://users.roblox.com/";
    public const int MaximumCookieLength = 16 * 1024;

    // A session subject is the verified UserId returned by the fixed authenticated-user endpoint.
    // It is never inferred from a public profile, an editable label, or an OAuth subject.
    public static bool HasVerifiedContext(string? issuer, string? subject, long? userId = null) => issuer == Issuer
        && long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
        && subject == parsed.ToString(CultureInfo.InvariantCulture) && (!userId.HasValue || userId.Value == parsed);

    public static bool IsValidCookie(string? cookie)
    {
        if (cookie is not { Length: > 0 and <= MaximumCookieLength }) return false;
        foreach (char value in cookie)
            if (value is < '!' or > '~' or ';' or ',') return false;
        return true;
    }
}

public static class AuthorizationIdentity
{
    public static bool HasVerifiedContext(string? provider, string? issuer, string? subject, long? userId = null) =>
        (!userId.HasValue || userId.Value > 0) && (provider switch
        {
            RobloxOAuthIdentity.Provider => RobloxOAuthIdentity.HasVerifiedContext(issuer, subject),
            RobloxSessionIdentity.Provider => RobloxSessionIdentity.HasVerifiedContext(issuer, subject, userId),
            _ => false
        });
}

// This record is never part of metadata exports, settings, diagnostics or pipe messages.
public sealed record AuthorizationRecord(Guid ProfileId, string Provider, long VerifiedUserId, long Generation,
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc,
    string VerifiedUsername = "", AuthorizationHealth Health = AuthorizationHealth.Linked,
    string Issuer = "", string Subject = "", string SessionCookie = "")
{
    public override string ToString() => $"AuthorizationRecord {{ ProfileId = {ProfileId}, Generation = {Generation}, Health = {Health}, Tokens = [protected] }}";
    public AuthorizationRecord Validated()
    {
        if (ProfileId == Guid.Empty || Provider is not (RobloxOAuthIdentity.Provider or RobloxSessionIdentity.Provider) || VerifiedUserId <= 0 || Generation <= 0
            || AccessToken is null || AccessToken.Length > 16384 || AccessToken.Any(char.IsControl)
            || RefreshToken is null || RefreshToken.Length > 16384 || RefreshToken.Any(char.IsControl)
            || VerifiedUsername is null || VerifiedUsername.Length > 100 || VerifiedUsername.Any(char.IsControl)
            || !Enum.IsDefined(Health) || ExpiresUtc == default || Issuer is null || Subject is null || SessionCookie is null)
            throw new ArgumentException("The protected authorization record is invalid. Sign in again or restore its valid backup.");
        if (Provider == RobloxSessionIdentity.Provider)
        {
            if (!AuthorizationIdentity.HasVerifiedContext(Provider, Issuer, Subject, VerifiedUserId)
                || AccessToken.Length != 0 || RefreshToken.Length != 0 || Health == AuthorizationHealth.RefreshPending
                || SessionCookie.Length == 0 && Health != AuthorizationHealth.SignInRequired
                || SessionCookie.Length != 0 && !RobloxSessionIdentity.IsValidCookie(SessionCookie))
                throw new ArgumentException("The saved sign-in identity context or session material is invalid. Sign in again.");
            return this;
        }
        if (SessionCookie.Length != 0)
            throw new ArgumentException("OAuth authorization cannot contain saved sign-in material. Sign in again.");
        // Legacy data lacks authenticated issuer/subject context. Keep the profile binding only as
        // historical metadata; never invent context or reuse its old authorization material.
        if (Issuer.Length == 0 && Subject.Length == 0)
            return this with { AccessToken = "", RefreshToken = "", SessionCookie = "", Health = AuthorizationHealth.SignInRequired };
        if (!AuthorizationIdentity.HasVerifiedContext(Provider, Issuer, Subject, VerifiedUserId)
            || Health != AuthorizationHealth.SignInRequired && string.IsNullOrWhiteSpace(RefreshToken)
            || AccessToken.Length != 0 && string.IsNullOrWhiteSpace(AccessToken))
            throw new ArgumentException("The authorization identity context or reusable grant is invalid. Sign in again.");
        return this;
    }
}

public interface IAccountCredentialVault
{
    Task<AuthorizationRecord?> ReadAsync(Guid profileId, CancellationToken ct);
    Task SaveAsync(AuthorizationRecord record, CancellationToken ct);
    Task DeleteAsync(Guid profileId, CancellationToken ct);
}

public interface IRobloxAuthorizationIdentityReader
{
    // Checks only known providers' non-secret identity claims. Ambiguous/incomplete state must
    // fail closed; it never scans or decrypts the saved credential inventory.
    Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long userId, CancellationToken ct);
}

public sealed record LinkedIdentity(long UserId, string Username, string Issuer = "", string Subject = "");

public interface IAccountLinkService
{
    Task<LinkedIdentity> LinkAsync(Guid profileId, string clientId, int callbackPort, CancellationToken ct);
    Task<LinkedIdentity> RefreshAsync(Guid profileId, string clientId, CancellationToken ct);
    Task<bool> DisconnectAsync(Guid profileId, string clientId, CancellationToken ct);
}

public sealed class AccountLinkException(string message) : Exception(message)
{
    /// <summary>
    /// Another saved-sign-in operation held the service, so this one was refused before it read or changed anything.
    /// A caller that works through many accounts stops on it instead of reporting each one as needing sign-in.
    /// </summary>
    public bool OperationInProgress { get; init; }
    /// <summary>
    /// The profile is being removed from the library, or no longer exists, so this operation was refused. A caller that
    /// works through many accounts skips that account and counts it as not checked, not as needing sign-in.
    /// </summary>
    public bool ProfileRemoved { get; init; }
}
