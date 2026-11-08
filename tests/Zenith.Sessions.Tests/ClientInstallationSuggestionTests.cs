using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-launch-18: after Refresh installation the hints may single out one present native Roblox player, which Settings
// preselects as "Suggested". These tests drive the real discovery over a fake inventory source and check that the
// suggestion follows the registry, protocol-handler and version-folder hints, skips what a trial could never use
// (the launcher, bootstrapper-managed players, Microsoft Store builds, missing or denied files) and stays silent
// when the hints disagree.
public sealed class ClientInstallationSuggestionTests
{
    private const string Local = @"C:\Synthetic\Local";
    private static readonly string Versions = Path.Combine(Local, "Roblox", "Versions");
    private static string Player(string folder) => Path.Combine(Versions, folder, "RobloxPlayerBeta.exe");
    private static string Launcher(string folder) => Path.Combine(Versions, folder, "RobloxPlayerLauncher.exe");
    private static readonly ClientInstallationFileMetadata Present = new(ClientInstallationFileState.PresentUnverified, "Roblox", "0.745.0.7451137");

    [Fact]
    public async Task ATypicalInstallSuggestsTheRegisteredPlayerAndNeverTheLauncherBesideIt()
    {
        var source = new FakeSource();
        source.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-abc" }));
        source.Files[Player("version-abc")] = Present; source.Files[Launcher("version-abc")] = Present with { ProductName = "Roblox Launcher" };
        source.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + Player("version-abc") + "\" \"%1\"";
        var snapshot = await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);

        // The launcher beside the player makes discovery report multiple Roblox candidates; that is not a conflict between players.
        Assert.Equal(2, snapshot.Candidates.Count);
        Assert.Contains(snapshot.Issues, issue => issue.Code == ClientInstallationIssueCode.MultipleCandidates && issue.Product == ClientInstallationProduct.Roblox);
        var suggested = ClientInstallationSuggestion.Suggest(snapshot);
        Assert.NotNull(suggested);
        Assert.Equal(Player("version-abc"), suggested!.ExecutablePath);
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(suggested), "A suggested row must be one the trial provider accepts.");
    }

    [Fact]
    public async Task APlayerFoundOnlyInItsVersionFolderIsSuggestedWhenItIsTheOnlyPresentPlayer()
    {
        var source = new FakeSource();
        source.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-only" }));
        source.Files[Player("version-only")] = Present;
        var snapshot = await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.Equal(Player("version-only"), ClientInstallationSuggestion.Suggest(snapshot)?.ExecutablePath);
    }

    [Fact]
    public async Task TwoPresentPlayersAreSuggestedOnlyWhenTheRegistrationNamesOneOfThem()
    {
        var source = new FakeSource();
        source.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-new", "version-old" }));
        source.Files[Player("version-new")] = Present; source.Files[Player("version-old")] = Present with { FileVersion = "0.700.0.7000000" };

        // No registration: the folders alone cannot say which player Roblox would start.
        Assert.Null(ClientInstallationSuggestion.Suggest(await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default)));

        // The roblox-player registration is what Roblox itself wrote at its last install or update, so it names the player.
        source.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + Player("version-new") + "\" \"%1\"";
        Assert.Equal(Player("version-new"), ClientInstallationSuggestion.Suggest(await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default))?.ExecutablePath);

        // Both registrations agreeing on the other folder names that one instead; a registration is followed, not second-guessed.
        source.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + Player("version-old") + "\" \"%1\"";
        source.Registry[ClientInstallationRegistryHint.RobloxProtocol] = "\"" + Player("version-old") + "\" \"%1\"";
        Assert.Equal(Player("version-old"), ClientInstallationSuggestion.Suggest(await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default))?.ExecutablePath);
    }

    [Fact]
    public async Task ConflictingProtocolRegistrationsSuggestNothingEvenWhenOnePlayerIsPresent()
    {
        var source = new FakeSource();
        source.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-abc" }));
        source.Files[Player("version-abc")] = Present;
        source.Registry[ClientInstallationRegistryHint.RobloxProtocol] = "\"" + Player("version-abc") + "\" \"%1\"";
        source.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + Path.Combine(Local, "Bloxstrap", "Bloxstrap.exe") + "\" \"%1\"";
        var snapshot = await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.Contains(snapshot.Issues, issue => issue.Code == ClientInstallationIssueCode.ConflictingProtocols);
        Assert.Null(ClientInstallationSuggestion.Suggest(snapshot));
    }

    [Fact]
    public async Task BootstrapperManagedAndMicrosoftStorePlayersAreNeverSuggested()
    {
        // Only a Bloxstrap-managed player: a trial rejects it by design, so nothing is suggested.
        var managed = new FakeSource();
        managed.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation] = Path.Combine(Local, "Bloxstrap");
        managed.Files[Path.Combine(Local, "Bloxstrap", "Bloxstrap.exe")] = new(ClientInstallationFileState.PresentUnverified, "Bloxstrap", "2.10.0.0");
        managed.Files[Path.Combine(Local, "Bloxstrap", "Roblox", "Player", "RobloxPlayerBeta.exe")] = Present;
        var managedSnapshot = await new WindowsClientInstallationDiscovery(managed).DiscoverAsync(default);
        Assert.Contains(managedSnapshot.Candidates, candidate => candidate.Product == ClientInstallationProduct.Roblox && candidate.FileState == ClientInstallationFileState.PresentUnverified);
        Assert.Null(ClientInstallationSuggestion.Suggest(managedSnapshot));

        // A Microsoft Store (packaged) build registered as the handler is skipped, even when it is the only present player.
        const string store = @"C:\Program Files\WindowsApps\ROBLOXCORPORATION.ROBLOX_2.600.0.0_x64__55nm5eh3cm0pr\RobloxPlayerBeta.exe";
        var packaged = new FakeSource();
        packaged.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + store + "\" \"%1\"";
        packaged.Files[store] = Present;
        var packagedSnapshot = await new WindowsClientInstallationDiscovery(packaged).DiscoverAsync(default);
        Assert.Contains(packagedSnapshot.Candidates, candidate => candidate.ExecutablePath == store && candidate.FileState == ClientInstallationFileState.PresentUnverified);
        Assert.Null(ClientInstallationSuggestion.Suggest(packagedSnapshot));

        // Beside a native player, the Store build and the managed player are not candidates for the suggestion at all.
        packaged.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-abc" })); packaged.Files[Player("version-abc")] = Present;
        packaged.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation] = Path.Combine(Local, "Bloxstrap");
        packaged.Files[Path.Combine(Local, "Bloxstrap", "Roblox", "Player", "RobloxPlayerBeta.exe")] = Present;
        Assert.Equal(Player("version-abc"), ClientInstallationSuggestion.Suggest(await new WindowsClientInstallationDiscovery(packaged).DiscoverAsync(default))?.ExecutablePath);
    }

    [Fact]
    public async Task MissingDeniedUnavailableAndDeferredInventoriesSuggestNothing()
    {
        var stale = new FakeSource();
        stale.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol] = "\"" + Player("version-gone") + "\" \"%1\"";
        var staleSnapshot = await new WindowsClientInstallationDiscovery(stale).DiscoverAsync(default);
        Assert.Equal(ClientInstallationFileState.Missing, Assert.Single(staleSnapshot.Candidates).FileState);
        Assert.Null(ClientInstallationSuggestion.Suggest(staleSnapshot));

        var denied = new FakeSource();
        denied.Versions[Versions] = new(Array.AsReadOnly(new[] { "version-abc" }));
        denied.Files[Player("version-abc")] = new(ClientInstallationFileState.AccessDenied);
        Assert.Null(ClientInstallationSuggestion.Suggest(await new WindowsClientInstallationDiscovery(denied).DiscoverAsync(default)));

        var broken = new FakeSource { RootFailure = new IOException("synthetic"), RegistryFailure = new IOException("synthetic") };
        var unavailable = await new WindowsClientInstallationDiscovery(broken).DiscoverAsync(default);
        Assert.Equal(ClientInstallationSnapshotStatus.Unavailable, unavailable.Status);
        Assert.Null(ClientInstallationSuggestion.Suggest(unavailable));

        var deferred = new ClientInstallationSnapshot(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Deferred,
            [new(ClientInstallationProduct.Roblox, Player("version-abc"), "Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified,
                [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)])], []);
        Assert.Null(ClientInstallationSuggestion.Suggest(deferred));
        Assert.Null(ClientInstallationSuggestion.Suggest(null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\ROBLOXCORPORATION.ROBLOX_2.600.0.0_x64__55nm5eh3cm0pr\RobloxPlayerBeta.exe", true)]
    [InlineData(@"C:\Program Files\windowsapps\Roblox\RobloxPlayerBeta.exe", true)]
    [InlineData(@"C:\Users\Someone\AppData\Local\Roblox\Versions\version-abc\RobloxPlayerBeta.exe", false)]
    [InlineData(@"C:\WindowsAppsBackup\RobloxPlayerBeta.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void APackagedPathIsRecognisedByItsWindowsAppsComponentOnly(string? path, bool packaged)
        => Assert.Equal(packaged, RobloxNativePlayerFiles.IsPackagedPath(path));

    private sealed class FakeSource : IClientInstallationInventorySource
    {
        public string? Local = ClientInstallationSuggestionTests.Local;
        public Dictionary<ClientInstallationRegistryHint, string?> Registry = [];
        public Dictionary<string, ClientInstallationFileMetadata> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ClientInstallationVersionDirectories> Versions = new(StringComparer.OrdinalIgnoreCase);
        public Exception? RootFailure, RegistryFailure;
        public string? GetLocalAppData(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (RootFailure is { } failure) throw failure; return Local; }
        public string? ReadRegistryValue(ClientInstallationRegistryHint hint, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (RegistryFailure is { } failure) throw failure; return Registry.GetValueOrDefault(hint); }
        public ClientInstallationVersionDirectories ReadVersionDirectories(string root, int maximum, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Versions.GetValueOrDefault(root) ?? new([]); }
        public ClientInstallationFileMetadata InspectExecutable(string path, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Files.GetValueOrDefault(path) ?? new(ClientInstallationFileState.Missing); }
    }
}
