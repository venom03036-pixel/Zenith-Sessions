using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class PortableBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-portable-service-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Password = "synthetic backup integration password"u8.ToArray();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task IndependentLibrariesRoundTripProfilesGroupsFavoritesSettingsAndOnlyFreshVerifiedSignIns()
    {
        await using var source = await Library.Create(Path.Combine(_root, "source"));
        var account = await source.Accounts.SaveAsync(AccountProfile.Create("Exported alias") with { Group = "Fixture group", Notes = "metadata retained", IsFavorite = true }, default);
        await source.Accounts.CreateGroupAsync("Empty exported group", "#12AB34", (await source.Accounts.ReadGroupCatalogAsync(new(), default)).CatalogRevision, default);
        await source.Vault.SaveAsync(Session(account.Id, 123, 77), default);
        await source.Favorites.SaveAsync(new(Guid.NewGuid(), "Exported game", 456), default);
        await source.Settings.SaveAsync(new SessionsSettings(DenseAccounts: true, Performance: new(MainAccountProfileId: account.Id)), default);
        string output = Path.Combine(_root, "contains-sign-ins.zenithbackup");
        await source.Service.ExportAsync(output, Password, source.Root, source.Root, default);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.DoesNotContain("synthetic-backup-cookie", Encoding.UTF8.GetString(File.ReadAllBytes(output)));
        await using var destination = await Library.Create(Path.Combine(_root, "destination"));
        using var decoded = await PortableBackupCodec.ReadAsync(output, Password, default);
        var result = await destination.Service.ImportAsync(decoded, true, default);
        Assert.True(result.Complete); Assert.Equal(1, result.ProfilesAdded); Assert.Equal(1, result.SignInsImported); Assert.Equal(1, result.FavoritesAdded);
        Assert.True(result.PreferencesApplied); var imported = Assert.Single((await destination.Accounts.ReadAsync(new(), default)).Items);
        Assert.NotEqual(account.Id, imported.Id); Assert.Equal(account.Notes, imported.Notes); Assert.Equal(account.IsFavorite, imported.IsFavorite);
        Assert.NotNull(await destination.Accounts.FindGroupAsync("Empty exported group", default));
        var grant = await destination.Vault.ReadAsync(imported.Id, default); Assert.NotNull(grant); Assert.Equal(1, grant.Generation); Assert.Equal(123, grant.VerifiedUserId);
        var settings = (await destination.Settings.LoadAsync(default)).Settings;
        Assert.True(settings.DenseAccounts); Assert.Equal(imported.Id, settings.Performance!.MainAccountProfileId);
    }

    [Fact]
    public async Task ExistingPlayerIsKeptWithoutEvenValidatingTheOlderBackupCookie()
    {
        await using var destination = await Library.Create(Path.Combine(_root, "destination"));
        var existing = await destination.Accounts.SaveAsync(AccountProfile.Create("Existing local"), default);
        await destination.Vault.SaveAsync(Session(existing.Id, 123, 50), default);
        var snapshot = await destination.Vault.ReadWriteSnapshotAsync(existing.Id, default);
        using var document = Document();
        var result = await destination.Service.ImportAsync(document, false, default);
        Assert.Equal(1, result.ExistingSignInsKept); Assert.Equal(0, result.SignInsImported); Assert.Equal(0, destination.Client.Calls);
        Assert.Equal(snapshot, await destination.Vault.ReadWriteSnapshotAsync(existing.Id, default));
        Assert.Equal(2, (await destination.Accounts.ReadAsync(new(), default)).Total);
    }

    [Fact]
    public async Task IdentityMismatchLeavesNewMetadataButNoAuthorization()
    {
        await using var destination = await Library.Create(Path.Combine(_root, "destination")); destination.Client.User = 999;
        using var document = Document(); var result = await destination.Service.ImportAsync(document, false, default);
        Assert.True(result.Complete); Assert.Equal(1, result.SignInsRejected); Assert.Equal(0, result.SignInsImported);
        var imported = Assert.Single((await destination.Accounts.ReadAsync(new(), default)).Items);
        Assert.Null(await destination.Vault.ReadAsync(imported.Id, default)); Assert.NotEqual(document.Profiles[0].Id, imported.Id);
    }

    [Fact]
    public async Task CancellationDuringValidationLeavesMetadataAndCannotPublishALateGrant()
    {
        await using var destination = await Library.Create(Path.Combine(_root, "destination")); destination.Client.Hold = true;
        using var document = Document(); using var stop = new CancellationTokenSource();
        var import = destination.Service.ImportAsync(document, false, stop.Token);
        await destination.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        Assert.False(import.IsCompleted); destination.Client.Release.TrySetResult();
        var result = await import; Assert.True(result.Stopped); Assert.Equal(1, result.ProfilesAdded); Assert.Equal(0, result.SignInsImported);
        var imported = Assert.Single((await destination.Accounts.ReadAsync(new(), default)).Items);
        Assert.Null(await destination.Vault.ReadAsync(imported.Id, default));
    }

    [Fact]
    public async Task ConcurrentDestinationGenerationWinsOverTheBackupCandidate()
    {
        await using var destination = await Library.Create(Path.Combine(_root, "destination")); destination.Client.Hold = true;
        using var document = Document(); var import = destination.Service.ImportAsync(document, false, default);
        await destination.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var imported = Assert.Single((await destination.Accounts.ReadAsync(new(), default)).Items);
        await destination.Vault.SaveAsync(Session(imported.Id, 123, 8), default); destination.Client.Release.TrySetResult();
        var result = await import; Assert.Equal(1, result.SignInsRejected); Assert.Equal(0, result.SignInsImported);
        Assert.Equal(8, (await destination.Vault.ReadAsync(imported.Id, default))!.Generation);
    }

    [Fact]
    public async Task LockedVaultAndProtectedDestinationCannotCreateAnExport()
    {
        await using var source = await Library.Create(Path.Combine(_root, "source"));
        await source.Vault.EnableMasterPasswordAsync(Password, default); source.Vault.LockMasterPassword();
        string output = Path.Combine(_root, "locked.zenithbackup");
        await Assert.ThrowsAsync<VaultPasswordRequiredException>(() => source.Service.ExportAsync(output, Password, source.Root, source.Root, default));
        Assert.False(File.Exists(output));
        await source.Vault.UnlockMasterPasswordAsync(Password, default);
        await Assert.ThrowsAsync<ArgumentException>(() => source.Service.ExportAsync(Path.Combine(source.Root, "protected.zenithbackup"), Password, source.Root, source.Root, default));
    }

    private static PortableBackupDocument Document()
    {
        var profile = AccountProfile.Create("Incoming fixture");
        return new(1, [profile], [new("Imported group", null, 0)], [], PortableBackupPreferences.From(new SessionsSettings().Validated()),
            [new(profile.Id, 123, DateTimeOffset.MaxValue, "synthetic-backup-cookie"u8.ToArray())]);
    }
    private static AuthorizationRecord Session(Guid id, long user, long generation) => new(id, RobloxSessionIdentity.Provider, user, generation,
        "", "", DateTimeOffset.MaxValue, "Synthetic_User", Issuer: RobloxSessionIdentity.Issuer, Subject: user.ToString(CultureInfo.InvariantCulture), SessionCookie: "synthetic-backup-cookie");

    private sealed class Library : IAsyncDisposable
    {
        public string Root { get; }
        public AccountRepository Accounts { get; }
        public ProtectedCredentialVault Vault { get; }
        public FavoriteDestinationsStore Favorites { get; }
        public SettingsStore Settings { get; }
        public FixtureClient Client { get; } = new();
        public RobloxSessionLinkService Links { get; }
        public PortableBackupService Service { get; }
        private Library(string root)
        {
            Root = root; Accounts = new(root); Vault = new(root); Favorites = new(root); Settings = new(root);
            Links = new(Accounts, Vault, Client, new NoSignIn()); Service = new(Accounts, Vault, Links, Favorites, Settings);
        }
        public static async Task<Library> Create(string root) { var library = new Library(root); await library.Accounts.InitializeAsync(default); return library; }
        public async ValueTask DisposeAsync() { Client.Release.TrySetResult(); await Links.DisposeAsync(); Vault.Dispose(); Accounts.Dispose(); Favorites.Dispose(); Settings.Dispose(); }
    }
    private sealed class FixtureClient : IRobloxSessionClient
    {
        public int Calls; public long User = 123; public bool Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            Calls++; Entered.TrySetResult(); if (Hold) await Release.Task;
            return new(new(User, "Synthetic_User", RobloxSessionIdentity.Issuer, User.ToString(CultureInfo.InvariantCulture)));
        }
    }
    private sealed class NoSignIn : IRobloxSessionSignIn
    { public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct) => throw new InvalidOperationException("A backup must not open a browser."); }
}
