using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

internal static class LegacyWorkspaceMigration
{
    private const int MaximumRecords = 10_000;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> ExportAsync(string snapshot)
    {
        try
        {
            RequireSnapshot(snapshot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = timeout.Token;
            using var accounts = new AccountRepository(snapshot);
            using var vault = new ProtectedCredentialVault(snapshot);
            using var favorites = new FavoriteDestinationsStore(snapshot);
            using var bundles = new RecipientBundleStore(snapshot);
            using var settings = new SettingsStore(snapshot);
            await using var assignments = new MacroAssignmentStore(snapshot);

            string protection;
            try
            {
                protection = (await vault.GetPasswordStatusAsync(ct)).Mode == VaultPasswordMode.Disabled
                    ? "windows" : "password-protected";
            }
            catch (Exception error) when (IsUnreadable(error)) { protection = "unavailable"; }

            var profiles = new List<object>();
            var signIns = new List<object>();
            for (int page = 0; ; page++)
            {
                var batch = await accounts.ReadAsync(new(Page: page), ct);
                CheckCount(batch.Total, batch.Items.Count, profiles.Count);
                foreach (var profile in batch.Items)
                {
                    AuthorizationRecord? verified = null;
                    if (protection == "windows")
                    {
                        try
                        {
                            var credential = await vault.ReadAsync(profile.Id, ct);
                            if (credential is not null && Matches(profile, credential)) verified = credential;
                        }
                        catch (Exception error) when (IsUnreadable(error)) { }
                    }
                    if (verified is { Health: AuthorizationHealth.Linked } && verified.ExpiresUtc > DateTimeOffset.UtcNow)
                        signIns.Add(new { profileId = profile.Id, bundle = SealSignIn(profile, verified) });
                    profiles.Add(new
                    {
                        profile.Id, profile.Label, profile.Username, groups = profile.GroupNames, profile.Priority,
                        preferredPlaceId = profile.PreferredPlaceId?.ToString(CultureInfo.InvariantCulture),
                        legacyUserId = profile.RobloxUserId?.ToString(CultureInfo.InvariantCulture),
                        verifiedUserId = verified?.VerifiedUserId, profile.Notes, profile.IsFavorite, profile.UpdatedUtc, profile.Revision,
                    });
                }
                if (profiles.Count == batch.Total) break;
            }

            var groups = new List<AccountGroupDefinition>();
            for (int page = 0; ; page++)
            {
                var batch = await accounts.ReadGroupCatalogAsync(new(Page: page, PageSize: 1000), ct);
                CheckCount(batch.Total, batch.Items.Count, groups.Count);
                groups.AddRange(batch.Items);
                if (groups.Count == batch.Total) break;
            }

            var macroAssignments = new List<MacroAssignment>();
            var assignmentPage = await assignments.ReadAsync(new(), ct);
            macroAssignments.AddRange(assignmentPage.Items);
            while (assignmentPage.NextOffset is int offset)
            {
                assignmentPage = await assignments.ReadAsync(new(Offset: offset, Expected: assignmentPage.Stamp), ct);
                macroAssignments.AddRange(assignmentPage.Items);
            }

            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                workspace = new
                {
                    protection, profiles, groups,
                    favorites = (await favorites.LoadAsync(ct)).Destinations.Select(item => new
                    {
                        item.Id, item.Name, placeId = item.PlaceId.ToString(CultureInfo.InvariantCulture), item.JobId
                    }),
                    bundles = (await bundles.LoadAsync(ct)).Bundles.Select(item => new { item.Id, item.Name, item.ProfileIds }),
                    assignments = macroAssignments,
                    preferences = PortableBackupPreferences.From((await settings.LoadAsync(ct)).Settings)
                },
                signIns
            }, Json);
            try
            {
                if (clear.Length > MaximumBytes - 4096)
                    throw new InvalidDataException("Workspace exceeds the supported migration size.");
                var encrypted = ProtectedData.Protect(clear, "ZenithSessions/Rust/LegacyMigration/v1"u8.ToArray(), DataProtectionScope.CurrentUser);
                if (encrypted.Length > MaximumBytes) throw new InvalidDataException("Protected workspace exceeds the supported migration size.");
                await WriteNewAsync(Path.Combine(snapshot, "legacy-workspace.zenith-migration"), encrypted, ct);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
            Console.WriteLine($"Prepared {profiles.Count} profiles and {signIns.Count} usable sign-ins. Vault protection: {protection}.");
            return 0;
        }
        catch (Exception error) when (IsUnreadable(error) || error is OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Workspace staging failed ({error.GetType().Name}). The original library was left available.");
            return 1;
        }
    }

    internal static void RequireSnapshot(string snapshot)
    {
        if (!File.Exists(Path.Combine(snapshot, "zenith-rust-probe-snapshot"))
            || !File.Exists(Path.Combine(snapshot, "accounts.db"))
            || !Directory.Exists(Path.Combine(snapshot, "accounts.vault")))
            throw new InvalidOperationException("A separately staged probe snapshot is required.");
    }

    internal static bool Matches(AccountProfile profile, AuthorizationRecord credential)
        => credential.ProfileId == profile.Id && credential.VerifiedUserId == profile.RobloxUserId
        && credential.Provider == RobloxSessionIdentity.Provider
        && !string.IsNullOrEmpty(profile.Username)
        && RobloxSessionIdentity.HasVerifiedContext(credential.Issuer, credential.Subject, credential.VerifiedUserId)
        && RobloxSessionIdentity.IsValidCookie(credential.SessionCookie);

    internal static byte[] SealSignIn(AccountProfile profile, AuthorizationRecord credential)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, profileId = profile.Id, username = profile.Username, userId = credential.VerifiedUserId,
            generation = credential.Generation, expiresUnixSeconds = credential.ExpiresUtc.ToUnixTimeSeconds(),
            cookie = credential.SessionCookie
        });
        try { return ProtectedData.Protect(clear, "ZenithSessions/Rust/LaunchProbe/v1"u8.ToArray(), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static async Task WriteNewAsync(string output, byte[] ciphertext, CancellationToken ct)
    {
        string staged = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(ciphertext, ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(staged, output);
        }
        finally { File.Delete(staged); }
    }

    private static void CheckCount(int total, int batch, int read)
    {
        if (total > MaximumRecords || read + batch > total || batch == 0 && read < total)
            throw new InvalidDataException("Workspace collection is incomplete or exceeds the supported migration size.");
    }

    private static bool IsUnreadable(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or Microsoft.Data.Sqlite.SqliteException;
}
