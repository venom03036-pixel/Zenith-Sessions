using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed class OAuthAttempt
{
    private readonly string _verifier = RandomText();
    private readonly string _state = RandomText();
    private int _consumed;
    public string ClientId { get; }
    public Uri Callback { get; }
    public Uri AuthorizeUri { get; }
    internal string Verifier => _verifier;
    public OAuthAttempt(string clientId, int port)
    {
        ValidateClient(clientId);
        if (port is < 1024 or > 65535) throw new ArgumentException("Choose a callback port between 1024 and 65535.");
        ClientId = clientId; Callback = new Uri($"http://localhost:{port}/zenith/oauth/");
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        AuthorizeUri = new Uri("https://apis.roblox.com/oauth/v1/authorize?" + FormQuery(new Dictionary<string,string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = Callback.AbsoluteUri, ["scope"] = "openid profile",
            ["response_type"] = "code", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = _state, ["prompt"] = "select_account"
        }));
    }
    public string ConsumeCallback(Uri callback)
    {
        var query = ValidateCallback(callback);
        if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new AccountLinkException("This sign-in callback has already been used.");
        if (query.ContainsKey("error")) throw new AccountLinkException("Roblox did not approve this sign-in. Your previous authorization is unchanged.");
        if (!query.TryGetValue("code", out string? code) || code.Length is < 1 or > 8192 || code.Any(char.IsControl))
            throw new AccountLinkException("Roblox returned an invalid sign-in code. Start sign-in again.");
        return code;
    }
    internal Dictionary<string,string> ValidateCallback(Uri callback)
    {
        if (callback.Scheme != Callback.Scheme || callback.Host != Callback.Host || callback.Port != Callback.Port
            || callback.AbsolutePath != Callback.AbsolutePath || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || callback.Query.Length > 16384) throw new AccountLinkException("The sign-in callback does not match this attempt.");
        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out string? state) || !FixedEquals(state, _state))
            throw new AccountLinkException("The sign-in callback could not be verified. Start sign-in again.");
        return query;
    }
    public override string ToString() => "OAuthAttempt { One-time PKCE values = [protected] }";
    internal static void ValidateClient(string clientId)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 100 || clientId.Any(c => !char.IsAsciiDigit(c)))
            throw new AccountLinkException("Save your registered Roblox OAuth client ID in Settings before linking an account.");
    }
    internal static Dictionary<string,string> ParseQuery(string query)
    {
        var values = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (string entry in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=',2); string key = Uri.UnescapeDataString(pair[0].Replace('+',' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+',' ')) : "";
            if (!values.TryAdd(key, value)) throw new AccountLinkException("The callback contains repeated fields. Start sign-in again.");
        }
        return values;
    }
    private static bool FixedEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string RandomText() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static string FormQuery(IReadOnlyDictionary<string,string> fields) => string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
}

public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc)
{ public override string ToString() => "OAuthTokens { [protected] }"; }

// The caller owns HttpClient. Production clients disable redirects and cookies; endpoints are never configurable.
public sealed class RobloxOAuthClient(HttpClient http,TimeSpan? requestTimeout=null)
{
    private const string Root = RobloxOAuthIdentity.Issuer + "v1/";
    private readonly TimeSpan _requestTimeout=requestTimeout??TimeSpan.FromSeconds(20);
    public Task<OAuthTokens> ExchangeAsync(OAuthAttempt attempt, string code, CancellationToken ct) => WithDeadline(t=>TokenAsync(new()
    {
        ["grant_type"]="authorization_code", ["client_id"]=attempt.ClientId, ["code"]=code,
        ["code_verifier"]=attempt.Verifier, ["redirect_uri"]=attempt.Callback.AbsoluteUri
    }, t),ct);
    public Task<OAuthTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        OAuthAttempt.ValidateClient(clientId);
        return WithDeadline(t=>TokenAsync(new() { ["grant_type"]="refresh_token", ["client_id"]=clientId, ["refresh_token"]=refreshToken },t),ct);
    }
    private async Task<OAuthTokens> TokenAsync(Dictionary<string,string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "token") { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendAsync(request,ct);
        if (!response.IsSuccessStatusCode) throw new AccountLinkException("Roblox did not accept the authorization. Sign in again; existing profile metadata is preserved.");
        using var json = await ReadJsonAsync(response,ct); var data=json.RootElement; RequireObject(data);
        string access=ReadString(data,"access_token",16384), refresh=ReadString(data,"refresh_token",16384);
        if (ReadString(data,"token_type",20) != "Bearer" || !data.TryGetProperty("expires_in",out var expiry)
            || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out int seconds) || seconds is < 1 or > 86400
            || access.Any(char.IsWhiteSpace) || refresh.Any(char.IsWhiteSpace))
            throw new AccountLinkException("Roblox returned an unsupported authorization response. Sign in again.");
        var scopes=ReadString(data,"scope",2048).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid",StringComparer.Ordinal) || !scopes.Contains("profile",StringComparer.Ordinal))
            throw new AccountLinkException("The OAuth app must grant the openid and profile permissions.");
        return new(access,refresh,DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    public Task<LinkedIdentity> IdentityAsync(string accessToken,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(accessToken) || accessToken.Length>16384 || accessToken.Any(char.IsWhiteSpace) || accessToken.Any(char.IsControl))
            throw new AccountLinkException("A fresh operational access token is required to verify the Roblox identity.");
        return WithDeadline(t=>ReadIdentityAsync(accessToken,t),ct);
    }
    private async Task<LinkedIdentity> ReadIdentityAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + "userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",accessToken);
        using var response = await SendAsync(request,ct);
        if (!response.IsSuccessStatusCode) throw new AccountLinkException("Roblox could not verify the account identity. Sign in again.");
        using var json=await ReadJsonAsync(response,ct); var data=json.RootElement; RequireObject(data);
        string subject=ReadString(data,"sub",255), username=ReadString(data,"preferred_username",100);
        // Generic OIDC sub values remain opaque. Only this pinned Roblox /userinfo adapter
        // maps sub to a numeric Roblox user ID, as explicitly documented by Roblox:
        // https://create.roblox.com/docs/cloud/auth/oauth2-reference#user-information
        // https://apis.roblox.com/oauth/.well-known/openid-configuration
        if (!subject.All(char.IsAsciiDigit) || !long.TryParse(subject,NumberStyles.None,CultureInfo.InvariantCulture,out long id) || id <= 0
            || data.TryGetProperty("iss",out var issuer) && (issuer.ValueKind!=JsonValueKind.String || issuer.GetString()!=RobloxOAuthIdentity.Issuer))
            throw new AccountLinkException("Roblox returned an invalid account identity. No account was linked.");
        ct.ThrowIfCancellationRequested();
        return new(id,username,RobloxOAuthIdentity.Issuer,subject);
    }
    public Task<bool> RevokeAsync(string clientId,string refreshToken,CancellationToken ct)=>WithDeadline(t=>SendRevocationAsync(clientId,refreshToken,t),ct);
    private async Task<bool> SendRevocationAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        OAuthAttempt.ValidateClient(clientId);
        using var request=new HttpRequestMessage(HttpMethod.Post, Root+"token/revoke") { Content=new FormUrlEncodedContent(new Dictionary<string,string>
            { ["client_id"]=clientId, ["token"]=refreshToken, ["token_type_hint"]="refresh_token" }) };
        using var response=await SendAsync(request,ct);
        return response.IsSuccessStatusCode;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        try
        {
            var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.RequestMessage?.RequestUri is { } actual && actual!=request.RequestUri)
            { response.Dispose();throw new AccountLinkException("The Roblox authorization client followed an unexpected redirect. No identity was accepted."); }
            return response;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        { throw new AccountLinkException("The Roblox connection timed out. If this was a token refresh, sign in again instead of reusing the previous refresh token."); }
        catch(HttpRequestException)
        { throw new AccountLinkException("Could not connect to Roblox. Check your connection. After an uncertain token refresh, sign in again; saved profiles are preserved."); }
    }
    private async Task<T> WithDeadline<T>(Func<CancellationToken,Task<T>> work,CancellationToken ct)
    {
        if(_requestTimeout<=TimeSpan.Zero || _requestTimeout>TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_requestTimeout);
        try { return await work(deadline.Token); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        { throw new AccountLinkException("The Roblox connection timed out. Retry sign-in; an uncertain refresh token will not be reused."); }
    }
    private static string ReadString(JsonElement root,string name,int limit)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name,out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString() is not { Length: > 0 } value || value.Length > limit || value.Any(char.IsControl))
            throw new AccountLinkException("Roblox returned an incomplete authorization response. No new authorization was committed.");
        return value;
    }
    private static void RequireObject(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object) throw new AccountLinkException("Roblox returned an unsupported authorization response.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var field in value.EnumerateObject())
            if(!names.Add(field.Name)) throw new AccountLinkException("Roblox returned ambiguous authorization identity fields.");
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit=65536;
        if (response.Content.Headers.ContentLength > limit) throw new AccountLinkException("Roblox returned an oversized authorization response.");
        await using var input=await response.Content.ReadAsStreamAsync(ct); using var buffer=new MemoryStream();
        byte[] bytes=new byte[4096];
        try
        {
            int read;
            while((read=await input.ReadAsync(bytes,ct))>0)
            { if(buffer.Length+read>limit) throw new AccountLinkException("Roblox returned an oversized authorization response."); buffer.Write(bytes,0,read); }
            buffer.Position = 0;
            try { return await JsonDocument.ParseAsync(buffer,new JsonDocumentOptions { MaxDepth=8 },ct); }
            catch(JsonException) { throw new AccountLinkException("Roblox returned an unreadable authorization response. Retry sign-in."); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }
}
