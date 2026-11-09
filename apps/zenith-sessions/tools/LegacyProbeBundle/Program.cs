using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

if (args is ["--make-fixture", var fixtureArgument])
    return await MakeFixtureAsync(Path.GetFullPath(fixtureArgument));

if (args is not [var snapshotArgument, var username] || username.Length is < 1 or > 20
    || !username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
{
    Console.Error.WriteLine("Usage: LegacyProbeBundle <isolated legacy snapshot> <Roblox username>");
    return 2;
}

try
{
    string snapshot = Path.GetFullPath(snapshotArgument);
    // This experimental helper consumes an explicitly staged copy. The installed store is rollback data.
    if (!File.Exists(Path.Combine(snapshot, "zenith-rust-probe-snapshot"))
        || !File.Exists(Path.Combine(snapshot, "accounts.db"))
        || !Directory.Exists(Path.Combine(snapshot, "accounts.vault")))
        throw new InvalidOperationException("A separately staged probe snapshot is required.");

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var ct = timeout.Token;
    using var accounts = new AccountRepository(snapshot);
    var matches = (await accounts.ReadAsync(new(Search: username), ct)).Items
        .Where(profile => profile.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches is not [var account] || account.RobloxUserId is not > 0)
        throw new InvalidOperationException("The snapshot does not contain one matching saved Roblox identity.");

    using var vault = new ProtectedCredentialVault(snapshot);
    if ((await vault.GetPasswordStatusAsync(ct)).Mode != VaultPasswordMode.Disabled)
        throw new InvalidOperationException("This probe cannot export a password-protected vault. It requires a user-unlocked migration flow.");
    var credential = await vault.ReadAsync(account.Id, ct);
    if (credential is null || credential.ProfileId != account.Id || credential.VerifiedUserId != account.RobloxUserId
        || credential.Provider != RobloxSessionIdentity.Provider || credential.Health != AuthorizationHealth.Linked
        || credential.ExpiresUtc <= DateTimeOffset.UtcNow
        || !RobloxSessionIdentity.HasVerifiedContext(credential.Issuer, credential.Subject, credential.VerifiedUserId)
        || !RobloxSessionIdentity.IsValidCookie(credential.SessionCookie))
        throw new InvalidOperationException("The selected account needs to sign in again before this probe can launch it.");

    byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        profileId = account.Id,
        username = account.Username,
        userId = credential.VerifiedUserId,
        generation = credential.Generation,
        expiresUnixSeconds = credential.ExpiresUtc.ToUnixTimeSeconds(),
        cookie = credential.SessionCookie
    });
    try
    {
        byte[] encrypted = ProtectedData.Protect(clear, "ZenithSessions/Rust/LaunchProbe/v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        string output = Path.Combine(snapshot, "probe.dpapi");
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(encrypted, ct);
        file.Flush(flushToDisk: true);
    }
    finally { CryptographicOperations.ZeroMemory(clear); }
    Console.WriteLine("Prepared one current-user protected probe bundle.");
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
    or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Probe bundle preparation failed ({ex.GetType().Name}). The legacy store was not changed.");
    return 1;
}

static async Task<int> MakeFixtureAsync(string root)
{
    const string username = "ZenithFixture";
    const long userId = 4242;
    if (Directory.Exists(root) || File.Exists(root))
        throw new InvalidOperationException("The fixture directory must be new.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var accounts = new AccountRepository(root);
    using var vault = new ProtectedCredentialVault(root);
    var ct = timeout.Token;
    await accounts.InitializeAsync(ct);
    var profile = await accounts.SaveAsync(AccountProfile.Create(username) with
    {
        Username = username, RobloxUserId = userId,
    }, ct);
    await vault.SaveAsync(new AuthorizationRecord(profile.Id, RobloxSessionIdentity.Provider, userId, 1,
        "", "", DateTimeOffset.UtcNow.AddHours(1), username, AuthorizationHealth.Linked,
        RobloxSessionIdentity.Issuer, "4242", "local-synthetic-fixture-only"), ct);
    await File.WriteAllBytesAsync(Path.Combine(root, "zenith-rust-probe-snapshot"), [], ct);
    Console.WriteLine("Created a synthetic legacy account and vault fixture.");
    return 0;
}
