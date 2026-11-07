using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Synthetic fixtures only: no browser, Roblox account, real vault or network is used.
public sealed class SessionAuthorizationStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-session-storage-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Magic = "ZENITH-VAULT\n"u8.ToArray();
    private const string Cookie = "synthetic-owned-webview-session_A|B-123";
    private string VaultRoot => Path.Combine(_root, "accounts.vault");
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    private static AuthorizationRecord Session(Guid id, long generation = 1, long userId = 123) => new(id,
        RobloxSessionIdentity.Provider, userId, generation, "", "", DateTimeOffset.MaxValue, "Synthetic_Account",
        Issuer: RobloxSessionIdentity.Issuer, Subject: userId.ToString(CultureInfo.InvariantCulture), SessionCookie: Cookie);
    private static AuthorizationRecord OAuth(Guid id, long generation = 1) => new(id,
        RobloxOAuthIdentity.Provider, 123, generation, "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10),
        "Synthetic_Account", Issuer: RobloxOAuthIdentity.Issuer, Subject: "opaque-subject");
    private static AuthorizationMetadata Summary(AuthorizationRecord record) => new(record.ProfileId, record.ProfileId.ToString("N"),
        record.Provider, record.Issuer, record.Subject, record.VerifiedUserId, record.VerifiedUsername, record.Generation,
        record.Health, record.ExpiresUtc, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+123")]
    [InlineData("0123")]
    [InlineData("123 ")]
    [InlineData(" 123")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("１２３")]
    [InlineData("")]
    [InlineData(null)]
    public void SessionSubjectsMustBeCanonicalPositiveInt64Values(string? subject)
    {
        Assert.False(AuthorizationIdentity.HasVerifiedContext(RobloxSessionIdentity.Provider, RobloxSessionIdentity.Issuer, subject));
        Assert.Throws<ArgumentException>(() => (Session(Guid.NewGuid()) with { Subject = subject! }).Validated());
    }

    [Fact]
    public void IdentityContextsKeepSessionAndOpaqueOAuthSubjectsSeparate()
    {
        var maximum = Session(Guid.NewGuid(), userId: long.MaxValue);
        Assert.Same(maximum, maximum.Validated());
        Assert.True(AuthorizationIdentity.HasVerifiedContext(maximum.Provider, maximum.Issuer, maximum.Subject, long.MaxValue));
        Assert.False(AuthorizationIdentity.HasVerifiedContext(maximum.Provider, maximum.Issuer, maximum.Subject, 123));
        Assert.False(AuthorizationIdentity.HasVerifiedContext(maximum.Provider, RobloxOAuthIdentity.Issuer, maximum.Subject, long.MaxValue));
        Assert.False(AuthorizationIdentity.HasVerifiedContext(RobloxOAuthIdentity.Provider, maximum.Issuer, maximum.Subject, long.MaxValue));
        Assert.False(AuthorizationIdentity.HasVerifiedContext("unrecognized-provider", maximum.Issuer, maximum.Subject));
        Assert.True(AuthorizationIdentity.HasVerifiedContext(RobloxOAuthIdentity.Provider, RobloxOAuthIdentity.Issuer, "opaque-subject", 123));
        Assert.False(AuthorizationIdentity.HasVerifiedContext(RobloxOAuthIdentity.Provider, RobloxOAuthIdentity.Issuer, "opaque-subject", 0));
    }

    [Theory]
    [InlineData("contains space")]
    [InlineData("contains;semicolon")]
    [InlineData("contains,comma")]
    [InlineData("contains\ttab")]
    [InlineData("contains\r\nheader")]
    [InlineData("contains\0nul")]
    [InlineData("contains\u007fdelete")]
    [InlineData("containsé")]
    [InlineData("")]
    [InlineData(null)]
    public void LinkedSessionRejectsMalformedCookieWithoutEchoingIt(string? cookie)
    {
        Assert.False(RobloxSessionIdentity.IsValidCookie(cookie));
        var error = Assert.Throws<ArgumentException>(() => (Session(Guid.NewGuid()) with { SessionCookie = cookie! }).Validated());
        if (!string.IsNullOrEmpty(cookie)) Assert.DoesNotContain(cookie, error.ToString());
    }

    [Fact]
    public void CookieBoundsAndSignInRequiredDoNotWeakenLinkedValidation()
    {
        var record = Session(Guid.NewGuid()) with { SessionCookie = new string('x', RobloxSessionIdentity.MaximumCookieLength) };
        Assert.Same(record, record.Validated());
        Assert.Throws<ArgumentException>(() => (record with { SessionCookie = record.SessionCookie + "x" }).Validated());
        var signedOut = record with { SessionCookie = "", Health = AuthorizationHealth.SignInRequired };
        Assert.Same(signedOut, signedOut.Validated());
        Assert.Throws<ArgumentException>(() => (signedOut with { SessionCookie = "bad;cookie" }).Validated());
        Assert.Throws<ArgumentException>(() => (signedOut with { Issuer = "", Subject = "" }).Validated());
        Assert.DoesNotContain(Cookie, Session(Guid.NewGuid()).ToString());
    }

    [Theory]
    [InlineData("access")]
    [InlineData("refresh")]
    [InlineData("pending")]
    [InlineData("user")]
    [InlineData("issuer")]
    [InlineData("provider")]
    [InlineData("health")]
    public async Task InvalidSessionCannotChangeTheExistingDurableGeneration(string change)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        var invalid = record with { Generation = 2 };
        invalid = change switch
        {
            "access" => invalid with { AccessToken = "synthetic-mixed-access" },
            "refresh" => invalid with { RefreshToken = "synthetic-mixed-refresh" },
            "pending" => invalid with { Health = AuthorizationHealth.RefreshPending },
            "user" => invalid with { VerifiedUserId = 456 },
            "issuer" => invalid with { Issuer = RobloxOAuthIdentity.Issuer },
            "provider" => invalid with { Provider = RobloxOAuthIdentity.Provider },
            _ => invalid with { Health = (AuthorizationHealth)99 }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveAsync(invalid, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(2, await vault.AllocateNextGenerationAsync(record.ProfileId, 1, default));
    }

    [Fact]
    public void OAuthCannotCarryOrConvertSessionMaterialEvenWithoutLegacyContext()
    {
        var oauth = OAuth(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => (oauth with { SessionCookie = Cookie }).Validated());
        Assert.Throws<ArgumentException>(() => (oauth with { Issuer = "", Subject = "", SessionCookie = Cookie }).Validated());
        var historical = (oauth with { Issuer = "", Subject = "" }).Validated();
        Assert.Equal(AuthorizationHealth.SignInRequired, historical.Health);
        Assert.Empty(historical.AccessToken); Assert.Empty(historical.RefreshToken); Assert.Empty(historical.SessionCookie);
    }

    [Fact]
    public async Task SessionRoundTripKeepsTheCookieOnlyInsideTheProtectedPayload()
    {
        var record = Session(Guid.NewGuid());
        using (var vault = new ProtectedCredentialVault(_root)) await vault.SaveAsync(record, default);
        using var reopened = new ProtectedCredentialVault(_root);
        Assert.Equal(record, await reopened.ReadAsync(record.ProfileId, default));
        var metadata = (await reopened.GetSummaryAsync(record.ProfileId, default))!;
        Assert.Equal(record.Provider, metadata.Provider); Assert.Equal(record.Subject, metadata.Subject);
        Assert.Equal(record.VerifiedUserId, metadata.KnownUserId); Assert.False(metadata.IsCurrentAuthorization);
        Assert.Contains("Last-known", metadata.EvidenceLabel);
        Assert.DoesNotContain("SessionCookie", JsonSerializer.Serialize(metadata));
        foreach (string path in Directory.EnumerateFiles(VaultRoot, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Cookie, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        byte[] cipher = Convert.FromBase64String(envelope["ProtectedPayload"]!.GetValue<string>());
        byte[] entropy = Entropy(record.ProfileId, 2), clear = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var payload = JsonDocument.Parse(clear);
            var authorization = payload.RootElement.GetProperty("Authorization");
            Assert.Equal(Cookie, authorization.GetProperty("SessionCookie").GetString());
            Assert.Equal("", authorization.GetProperty("AccessToken").GetString());
            Assert.Equal("", authorization.GetProperty("RefreshToken").GetString());
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(entropy); }
    }

    [Fact]
    public async Task SessionMetadataReadsDoNotDecryptTheSecretAndCannotAuthorize()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        byte[] invalidCipher = [1, 2, 3, 4];
        envelope["ProtectedPayload"] = Convert.ToBase64String(invalidCipher);
        envelope["PayloadDigest"] = Convert.ToHexString(SHA256.HashData(invalidCipher));
        WriteEnvelope(record.ProfileId, envelope);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        var summary = Assert.Single(await vault.ReadSummariesAsync([record.ProfileId], default));
        Assert.False(summary.IsCurrentAuthorization);
        Assert.Equal(record.ProfileId, (await vault.FindByIdentityAsync(record.Provider, record.Issuer, record.Subject, default))!.ProfileId);
        Assert.Equal(record.ProfileId, (await vault.FindByRobloxUserIdAsync(record.VerifiedUserId, default))!.ProfileId);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
    }

    [Fact]
    public async Task ReservedGenerationImmediatelyClearsTheOldCookieAndSurvivesDelete()
    {
        var record = Session(Guid.NewGuid());
        using (var vault = new ProtectedCredentialVault(_root))
        {
            await vault.SaveAsync(record, default);
            Assert.Equal(2, await vault.AllocateNextGenerationAsync(record.ProfileId, 1, default));
            var stale = (await vault.ReadAsync(record.ProfileId, default))!;
            Assert.Equal(AuthorizationHealth.SignInRequired, stale.Health);
            Assert.Empty(stale.SessionCookie); Assert.Empty(stale.AccessToken); Assert.Empty(stale.RefreshToken);
            Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(record.ProfileId, default))!.Health);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(record, default));
            await vault.SaveAsync(record with { Generation = 2, SessionCookie = "synthetic-rotated-session" }, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(record with { SessionCookie = "synthetic-late-session" }, default));
            Assert.Equal("synthetic-rotated-session", (await vault.ReadAsync(record.ProfileId, default))!.SessionCookie);
            await vault.DeleteAsync(record.ProfileId, default);
        }
        using var reopened = new ProtectedCredentialVault(_root);
        Assert.Null(await reopened.ReadAsync(record.ProfileId, default));
        Assert.Equal(4, await reopened.AllocateNextGenerationAsync(record.ProfileId, 0, default));
    }

    [Fact]
    public async Task ProviderContextsStayIsolatedAndRobloxUserOwnershipSpansBothProviders()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var oauth = OAuth(Guid.NewGuid()); var session = Session(Guid.NewGuid(), userId: 456);
        await vault.SaveAsync(oauth, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(Session(Guid.NewGuid()), default));
        await vault.SaveAsync(session, default);
        Assert.Equal(oauth.ProfileId, (await vault.FindByIdentityAsync(oauth.Provider, oauth.Issuer, oauth.Subject, default))!.ProfileId);
        Assert.Equal(session.ProfileId, (await vault.FindByIdentityAsync(session.Provider, session.Issuer, session.Subject, default))!.ProfileId);
        Assert.Equal(oauth.ProfileId, (await vault.FindByRobloxUserIdAsync(oauth.VerifiedUserId, default))!.ProfileId);
        Assert.Equal(session.ProfileId, (await vault.FindByRobloxUserIdAsync(session.VerifiedUserId, default))!.ProfileId);
        Assert.Null(await vault.FindByRobloxUserIdAsync(999, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(Session(Guid.NewGuid(), userId: 456), default));
        byte[] original = await File.ReadAllBytesAsync(RecordPath(oauth.ProfileId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(Session(oauth.ProfileId, 2), default));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(oauth.ProfileId)));
        Assert.Equal("synthetic-refresh", (await vault.ReadAsync(oauth.ProfileId, default))!.RefreshToken);
        await Assert.ThrowsAsync<ArgumentException>(() => vault.FindByIdentityAsync(session.Provider, oauth.Issuer, session.Subject, default));
    }

    [Fact]
    public async Task ConcurrentOAuthAndSessionSavesHaveOneDurableUserOwner()
    {
        using var first = new ProtectedCredentialVault(_root);
        using var second = new ProtectedCredentialVault(_root);
        var oauth = OAuth(Guid.NewGuid()); var session = Session(Guid.NewGuid());
        Exception?[] results = await Task.WhenAll(
            Xunit.Record.ExceptionAsync(() => first.SaveAsync(oauth, default)),
            Xunit.Record.ExceptionAsync(() => second.SaveAsync(session, default)));
        Assert.Single(results, error => error is null);
        Assert.Single(results, error => error is InvalidOperationException);
        var found = (await first.FindByRobloxUserIdAsync(123, default))!;
        Assert.Contains(found.ProfileId, new[] { oauth.ProfileId, session.ProfileId });
        Assert.Single(Directory.GetFiles(VaultRoot, "*.dpapi"));
    }

    [Fact]
    public async Task UserLookupRejectsEarlierAmbiguousClaimsWithoutDecryptingOrGuessing()
    {
        var oauth = OAuth(Guid.NewGuid()); var session = Session(Guid.NewGuid());
        string otherRoot = Path.Combine(_root, "earlier-provider-fixture"), otherVault = Path.Combine(otherRoot, "accounts.vault");
        using (var main = new ProtectedCredentialVault(_root)) await main.SaveAsync(oauth, default);
        using (var other = new ProtectedCredentialVault(otherRoot)) await other.SaveAsync(session, default);
        // Assemble only synthetic earlier provider-scoped records. This models an existing
        // duplicate index without weakening the production writer's new uniqueness invariant.
        foreach (string source in Directory.GetFiles(otherVault, "*", SearchOption.AllDirectories))
        {
            // Coordination state is not part of the synthetic ambiguous identity fixture.
            if (Path.GetRelativePath(otherVault, source) == ProtectedCredentialVault.WriterLockFileName) continue;
            string target = Path.Combine(VaultRoot, Path.GetRelativePath(otherVault, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        using var vault = new ProtectedCredentialVault(_root);
        byte[] oauthBefore = await File.ReadAllBytesAsync(RecordPath(oauth.ProfileId));
        byte[] sessionBefore = await File.ReadAllBytesAsync(RecordPath(session.ProfileId));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.FindByRobloxUserIdAsync(123, default));
        Assert.Equal(oauthBefore, await File.ReadAllBytesAsync(RecordPath(oauth.ProfileId)));
        Assert.Equal(sessionBefore, await File.ReadAllBytesAsync(RecordPath(session.ProfileId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(session with { Generation = 2 }, default));
    }

    [Fact]
    public async Task UserLookupReadsOnlyKnownClaimsAndPreservesIncompleteOriginals()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        await File.WriteAllTextAsync(RecordPath(Guid.NewGuid()), "unrelated-corrupt-fixture");
        Assert.Equal(record.ProfileId, (await vault.FindByRobloxUserIdAsync(123, default))!.ProfileId);
        File.Delete(RecordPath(record.ProfileId));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.FindByRobloxUserIdAsync(123, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vault.FindByRobloxUserIdAsync(0, default));
    }

    [Theory]
    [InlineData("Subject", "456")]
    [InlineData("Issuer", "https://apis.roblox.com/oauth/")]
    [InlineData("Provider", "unrecognized-provider")]
    public async Task SwappedSummaryContextIsRejectedAndItsOriginalPreserved(string field, string value)
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        JsonObject envelope = ReadEnvelope(record.ProfileId); envelope["Metadata"]![field] = value;
        WriteEnvelope(record.ProfileId, envelope);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetSummaryAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
    }

    [Fact]
    public async Task EarlierV2OAuthPayloadWithoutSessionFieldRemainsReadableWithoutRewrite()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = OAuth(Guid.NewGuid()); await vault.SaveAsync(record, default);
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        byte[] cipher = Convert.FromBase64String(envelope["ProtectedPayload"]!.GetValue<string>());
        byte[] entropy = Entropy(record.ProfileId, 2), clear = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser);
        byte[] oldPayload = [], oldCipher = [];
        try
        {
            var payload = JsonNode.Parse(clear)!;
            Assert.True(payload["Authorization"]!.AsObject().Remove("SessionCookie"));
            oldPayload = JsonSerializer.SerializeToUtf8Bytes(payload);
            oldCipher = ProtectedData.Protect(oldPayload, entropy, DataProtectionScope.CurrentUser);
            envelope["ProtectedPayload"] = Convert.ToBase64String(oldCipher);
            envelope["PayloadDigest"] = Convert.ToHexString(SHA256.HashData(oldCipher));
            WriteEnvelope(record.ProfileId, envelope);
            byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
            Assert.Equal(record with { AccessToken = "" }, await vault.ReadAsync(record.ProfileId, default));
            Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(oldPayload); CryptographicOperations.ZeroMemory(oldCipher);
        }
    }

    [Fact]
    public async Task LegacyV1OAuthRemainsSignInRequiredAndNeverBecomesASessionCookie()
    {
        var record = OAuth(Guid.NewGuid()) with { Issuer = "", Subject = "" };
        Directory.CreateDirectory(VaultRoot);
        var payload = JsonSerializer.SerializeToNode(record)!; payload.AsObject().Remove("SessionCookie");
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(payload), entropy = Entropy(record.ProfileId, 1);
        byte[] cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
        try
        {
            await File.WriteAllBytesAsync(RecordPath(record.ProfileId), cipher);
            using var vault = new ProtectedCredentialVault(_root);
            var historical = (await vault.ReadAsync(record.ProfileId, default))!;
            Assert.Equal(RobloxOAuthIdentity.Provider, historical.Provider);
            Assert.Equal(AuthorizationHealth.SignInRequired, historical.Health);
            Assert.Empty(historical.AccessToken); Assert.Empty(historical.RefreshToken); Assert.Empty(historical.SessionCookie);
            Assert.Null(await vault.GetSummaryAsync(record.ProfileId, default));
            Assert.Equal(cipher, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(entropy); }
    }

    [Fact]
    public async Task SessionProjectionUsesOnlyCommittedMatchingNonsecretMetadata()
    {
        using var profiles = new AccountRepository(_root); await profiles.InitializeAsync(default);
        var profile = await profiles.SaveAsync(AccountProfile.Create("Owned login") with { Notes = "Keep these preferences" }, default);
        using var vault = new ProtectedCredentialVault(_root);
        using var projection = new AccountIdentityProjection(_root, profiles, vault);
        var record = Session(profile.Id);
        await projection.PrepareAsync(Summary(record), default);
        string journalPath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "account-identity-pending"), "*.json"));
        string journal = await File.ReadAllTextAsync(journalPath);
        Assert.DoesNotContain(Cookie, journal); Assert.DoesNotContain("SessionCookie", journal);
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.CompleteAsync(profile.Id, 1, default));
        Assert.Empty((await profiles.FindAsync(profile.Id, default))!.Username);
        await vault.SaveAsync(record, default);
        await projection.CompleteAsync(profile.Id, 1, default);
        var saved = (await profiles.FindAsync(profile.Id, default))!;
        Assert.Equal(record.VerifiedUsername, saved.Username); Assert.Equal(record.VerifiedUserId, saved.RobloxUserId);
        Assert.Equal("Keep these preferences", saved.Notes); Assert.Equal("Owned login", saved.Label);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "account-identity-pending"), "*.json"));
        await vault.AllocateNextGenerationAsync(profile.Id, 1, default);
        var staleSummary = (await vault.GetSummaryAsync(profile.Id, default))!;
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(staleSummary, default));
        var wrongContext = Summary(record) with { VerifiedUserId = 456 };
        await Assert.ThrowsAsync<AccountIdentityProjectionException>(() => projection.PrepareAsync(wrongContext, default));
    }

    [Fact]
    public async Task CancelledStorageWorkCannotPublishOrAdvanceTheSavedSession()
    {
        using var vault = new ProtectedCredentialVault(_root);
        var record = Session(Guid.NewGuid()); await vault.SaveAsync(record, default);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.SaveAsync(record with { Generation = 2 }, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.DeleteAsync(record.ProfileId, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.AllocateNextGenerationAsync(record.ProfileId, 1, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.FindByRobloxUserIdAsync(record.VerifiedUserId, stop.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        Assert.Equal(record, await vault.ReadAsync(record.ProfileId, default));
        Assert.Equal(2, await vault.AllocateNextGenerationAsync(record.ProfileId, 1, default));
    }

    private JsonObject ReadEnvelope(Guid id) => JsonNode.Parse(File.ReadAllBytes(RecordPath(id)).AsSpan(Magic.Length))!.AsObject();
    private void WriteEnvelope(Guid id, JsonObject envelope)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope), bytes = new byte[Magic.Length + json.Length];
        Magic.CopyTo(bytes, 0); json.CopyTo(bytes, Magic.Length); File.WriteAllBytes(RecordPath(id), bytes);
    }
    private static byte[] Entropy(Guid id, int version) => Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v" + version + "/" + id.ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
