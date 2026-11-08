using System.Security.Cryptography;
using System.Text;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record PortableImportResult(int ProfilesAdded, int ProfilesKept, int GroupsAdded, int FavoritesAdded,
    int SignInsImported, int ExistingSignInsKept, int SignInsRejected, bool PreferencesApplied,
    bool Stopped, string? FailureType = null)
{
    public bool Complete => !Stopped && FailureType is null;
}

/// <summary>Explicit backup operations. The caller owns consent, cancellation, settings publication and task drain.</summary>
public sealed class PortableBackupService(IAccountRepository profiles, ProtectedCredentialVault vault,
    IPortableSavedSessionImporter sessions, IFavoriteDestinationStore favorites, SettingsStore settings)
{
    public async Task<PortableBackupDocument> CaptureAsync(CancellationToken ct)
    {
        var capturedProfiles = await ReadProfilesAsync(ct).ConfigureAwait(false);
        var capturedGroups = await ReadGroupsAsync(ct).ConfigureAwait(false);
        var capturedFavorites = await favorites.LoadAsync(ct).ConfigureAwait(false);
        var capturedSettings = (await settings.LoadAsync(ct).ConfigureAwait(false)).Settings;
        var snapshots = new List<AuthorizationWriteSnapshot>(capturedProfiles.Length);
        var signIns = new List<PortableSavedSignIn>(); bool transferred = false;
        try
        {
            if ((await vault.GetPasswordStatusAsync(ct).ConfigureAwait(false)).Mode is VaultPasswordMode.Locked or VaultPasswordMode.MigrationRequired)
                throw new VaultPasswordRequiredException();
            foreach (var profile in capturedProfiles)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = await vault.ReadWriteSnapshotAsync(profile.Id, ct).ConfigureAwait(false); snapshots.Add(snapshot);
                if (snapshot.Record is { Provider: RobloxSessionIdentity.Provider, Health: AuthorizationHealth.Linked } record && record.ExpiresUtc > DateTimeOffset.UtcNow)
                    signIns.Add(new(profile.Id, record.VerifiedUserId, record.ExpiresUtc, Encoding.ASCII.GetBytes(record.SessionCookie)));
            }
            // Optimistic source-version validation: a change observed during capture requires a fresh export,
            // never mixing record generations or silently substituting a fallback. Later changes are a later library state.
            var currentProfiles = await ReadProfilesAsync(ct).ConfigureAwait(false);
            if (!capturedProfiles.SequenceEqual(currentProfiles)) throw Changed();
            var currentGroups = await ReadGroupsAsync(ct).ConfigureAwait(false);
            if (capturedGroups.Revision != currentGroups.Revision || !capturedGroups.Groups.SequenceEqual(currentGroups.Groups)) throw Changed();
            var currentFavorites = await favorites.LoadAsync(ct).ConfigureAwait(false);
            if (currentFavorites.Revision != capturedFavorites.Revision || !currentFavorites.Destinations.SequenceEqual(capturedFavorites.Destinations)) throw Changed();
            if (PortableBackupPreferences.From(capturedSettings) != PortableBackupPreferences.From((await settings.LoadAsync(ct).ConfigureAwait(false)).Settings)) throw Changed();
            foreach (var snapshot in snapshots)
                if (snapshot != await vault.ReadWriteSnapshotAsync(snapshot.ProfileId, ct).ConfigureAwait(false)) throw Changed();
            ct.ThrowIfCancellationRequested();
            var result = new PortableBackupDocument(1, capturedProfiles, capturedGroups.Groups, capturedFavorites.Destinations.ToArray(),
                PortableBackupPreferences.From(capturedSettings), signIns.ToArray()).Validated();
            transferred = true; return result;
        }
        finally { if (!transferred) foreach (var signIn in signIns) signIn.Dispose(); }
    }

    public async Task ExportAsync(string path, ReadOnlyMemory<byte> password, string dataRoot, string applicationRoot, CancellationToken ct)
    {
        ValidateDestination(path, dataRoot, applicationRoot);
        using var captured = await CaptureAsync(ct).ConfigureAwait(false);
        byte[] ciphertext = PortableBackupCodec.Seal(captured, password.Span, ct);
        try { await ExplicitReportFile.WriteAsync(path, ciphertext, candidate => ValidateDestination(candidate, dataRoot, applicationRoot), ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(ciphertext); }
    }

    public async Task<PortableImportResult> ImportAsync(PortableBackupDocument source, bool applyPreferences, CancellationToken ct)
    {
        source.Validated(); ct.ThrowIfCancellationRequested();
        if ((await vault.GetPasswordStatusAsync(ct).ConfigureAwait(false)).Mode is VaultPasswordMode.Locked or VaultPasswordMode.MigrationRequired)
            throw new VaultPasswordRequiredException();
        int added = 0, kept = 0, groups = 0, favoriteCount = 0, imported = 0, existingSignIns = 0, rejected = 0; bool preferencesApplied = false;
        PortableImportResult Result(bool stopped, string? failure = null) => new(added, kept, groups, favoriteCount, imported, existingSignIns, rejected, preferencesApplied, stopped, failure);
        try
        {
            var existing = await ReadProfilesAsync(ct).ConfigureAwait(false);
            var keys = new HashSet<string>(existing.Select(Key), StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<Guid, Guid>(); var created = new List<AccountProfile>();
            foreach (var profile in source.Profiles)
            {
                if (!keys.Add(Key(profile))) { kept++; continue; }
                Guid id = Guid.NewGuid(); map.Add(profile.Id, id);
                // Enforce the import allowlist here as well as the file codec; a direct model caller is not a local launch approval.
                created.Add((profile with { Id = id, RobloxUserId = null, Revision = 0, UpdatedUtc = DateTimeOffset.UtcNow, ExtraClientArguments = "" }).Validated());
            }
            if (profiles is not IAccountMetadataImport metadata) throw new InvalidOperationException("This library does not support atomic profile and group imports.");
            groups = await metadata.ImportMetadataAsync(source.Groups.OrderBy(group => group.SortOrder).ToArray(), created, ct).ConfigureAwait(false);
            added = created.Count;
            var destinations = (await favorites.LoadAsync(ct).ConfigureAwait(false)).Destinations.ToList();
            foreach (var favorite in source.Favorites)
            {
                ct.ThrowIfCancellationRequested();
                if (destinations.Any(item => item.PlaceId == favorite.PlaceId && item.JobId == favorite.JobId)) continue;
                var fresh = (favorite with { Id = Guid.NewGuid(), Revision = 0, ChangeId = Guid.Empty }).Validated();
                destinations.Add(await favorites.SaveAsync(fresh, ct).ConfigureAwait(false)); favoriteCount++;
            }
            foreach (var signIn in source.SignIns)
            {
                ct.ThrowIfCancellationRequested();
                if (!map.TryGetValue(signIn.SourceProfileId, out var destination)) { rejected++; continue; }
                var outcome = await sessions.ImportPortableSignInAsync(destination, signIn, ct).ConfigureAwait(false);
                if (outcome == PortableSignInOutcome.Imported) imported++;
                else if (outcome == PortableSignInOutcome.ExistingSignInPreserved) existingSignIns++;
                else rejected++;
            }
            if (applyPreferences)
            {
                ct.ThrowIfCancellationRequested();
                var local = (await settings.LoadAsync(ct).ConfigureAwait(false)).Settings;
                await settings.SaveAsync(source.Preferences.MergeInto(local, map), ct).ConfigureAwait(false); preferencesApplied = true;
            }
            ct.ThrowIfCancellationRequested(); return Result(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException or AccountLinkException or FavoriteDestinationCapacityException or FavoriteDestinationConflictException)
        { return Result(false, ex.GetType().Name); }
    }

    private async Task<AccountProfile[]> ReadProfilesAsync(CancellationToken ct)
    {
        var result = new List<AccountProfile>(); int total = -1;
        for (int page = 0; ; page++)
        {
            var batch = await profiles.ReadAsync(new(Page: page, PageSize: 100), ct).ConfigureAwait(false);
            if (batch.Total > PortableBackupDocument.MaximumProfiles || total >= 0 && total != batch.Total || batch.Items.Count == 0 && result.Count < batch.Total) throw Changed();
            total = batch.Total; result.AddRange(batch.Items);
            if (result.Count > PortableBackupDocument.MaximumProfiles) throw Changed();
            if (result.Count >= total) return result.ToArray();
        }
    }
    private async Task<(long Revision, ProfileMetadataGroup[] Groups)> ReadGroupsAsync(CancellationToken ct)
    {
        if (profiles is not IAccountGroupCatalog catalog) return (0, []);
        long revision = -1; int total = -1; var result = new List<ProfileMetadataGroup>();
        for (int page = 0; ; page++)
        {
            var batch = await catalog.ReadGroupCatalogAsync(new(Page: page, PageSize: 1000), ct).ConfigureAwait(false);
            if (batch.Total > PortableBackupDocument.MaximumGroups || revision >= 0 && revision != batch.CatalogRevision || total >= 0 && total != batch.Total
                || batch.Items.Count == 0 && result.Count < batch.Total) throw Changed();
            revision = batch.CatalogRevision; total = batch.Total;
            result.AddRange(batch.Items.Select(group => new ProfileMetadataGroup(group.Name, group.ColorHex, group.SortOrder)));
            if (result.Count >= total) return (revision, result.ToArray());
        }
    }
    private static string Key(AccountProfile profile) => profile.Label.Trim() + "\n" + profile.Username.Trim();
    private static InvalidDataException Changed() => new("The library changed during backup or exceeds the bounded portable operation size. Retry with a stable library; the existing destination was preserved.");
    private static string ValidateDestination(string path, string dataRoot, string applicationRoot)
    {
        string supplied = path.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(supplied) || DirectoryIdentity.IsDevicePath(supplied)) throw new ArgumentException("Choose a normal local backup path.");
        string full = Path.GetFullPath(supplied), name = Path.GetFileName(full);
        if (!name.EndsWith(".zenithbackup", StringComparison.OrdinalIgnoreCase) || name.Contains(':')) throw new ArgumentException("Save the protected backup as a .zenithbackup file.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Choose a backup folder without links or junctions.");
        string parent = Path.GetDirectoryName(full)!; if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        string resolved = Path.Combine(DirectoryIdentity.Canonical(parent), name);
        foreach (string root in new[] { dataRoot, applicationRoot })
        {
            string protectedRoot = Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(root));
            if (resolved.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase) || resolved.StartsWith(protectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Save the protected backup outside the app and its data folder.");
        }
        return full;
    }
}
