using System.Security.Cryptography;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

if (args is ["--make-fixture", var fixtureArgument])
    return await MakeFixtureAsync(Path.GetFullPath(fixtureArgument), false);
if (args is ["--make-password-fixture", var passwordFixtureArgument])
    return await MakeFixtureAsync(Path.GetFullPath(passwordFixtureArgument), true);

if (args is ["--workspace", var workspaceArgument])
    return await LegacyWorkspaceMigration.ExportAsync(Path.GetFullPath(workspaceArgument));

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
    LegacyWorkspaceMigration.RequireSnapshot(snapshot);

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
    if (credential is null || !LegacyWorkspaceMigration.Matches(account, credential)
        || credential.Health != AuthorizationHealth.Linked || credential.ExpiresUtc <= DateTimeOffset.UtcNow)
        throw new InvalidOperationException("The selected account needs to sign in again before this probe can launch it.");

    byte[] encrypted = LegacyWorkspaceMigration.SealSignIn(account, credential);
    string output = Path.Combine(snapshot, "probe.dpapi");
    await LegacyWorkspaceMigration.WriteNewAsync(output, encrypted, ct);
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

static async Task<int> MakeFixtureAsync(string root, bool passwordProtected)
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
        Username = username, RobloxUserId = userId, Notes = "Synthetic migration note",
        PreferredPlaceId = 1818, IsFavorite = true, Group = "Testing", AdditionalGroups = "Archive",
    }, ct);
    await vault.SaveAsync(new AuthorizationRecord(profile.Id, RobloxSessionIdentity.Provider, userId, 1,
        "", "", DateTimeOffset.UtcNow.AddHours(1), username, AuthorizationHealth.Linked,
        RobloxSessionIdentity.Issuer, "4242", "local-synthetic-fixture-only"), ct);
    var expired = await accounts.SaveAsync(AccountProfile.Create("Expired fixture") with
    {
        Username = "ExpiredFixture", RobloxUserId = 4243, Notes = "Keep this expired profile"
    }, ct);
    await vault.SaveAsync(new AuthorizationRecord(expired.Id, RobloxSessionIdentity.Provider, 4243, 1,
        "", "", DateTimeOffset.UtcNow.AddHours(-1), expired.Username, AuthorizationHealth.Linked,
        RobloxSessionIdentity.Issuer, "4243", "local-expired-fixture-only"), ct);
    await accounts.SaveAsync(AccountProfile.Create("Metadata fixture") with { Notes = "Needs sign-in" }, ct);
    using var favorites = new FavoriteDestinationsStore(root);
    await favorites.SaveAsync(new FavoriteDestination(Guid.NewGuid(), "Fixture game", 1818), ct);
    await using var assignments = new MacroAssignmentStore(root);
    var initial = await assignments.ReadAsync(new(), ct);
    await assignments.SaveAsync(new MacroAssignment(new MacroAssignmentTarget(MacroAssignmentTargetKind.Account, profile.Id),
        profile.Label, Guid.NewGuid(), "Fixture macro"), initial.Stamp, ct);
    if (passwordProtected)
    {
        byte[] fixturePassword = "synthetic-migration-password-1"u8.ToArray();
        try { await vault.EnableMasterPasswordAsync(fixturePassword, ct); }
        finally { CryptographicOperations.ZeroMemory(fixturePassword); }
    }
    await File.WriteAllBytesAsync(Path.Combine(root, "zenith-rust-probe-snapshot"), [], ct);
    await File.WriteAllBytesAsync(Path.Combine(root, "instance.lock"), [], ct);
    Console.WriteLine("Created a synthetic legacy account and vault fixture.");
    return 0;
}
