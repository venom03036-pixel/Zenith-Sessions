using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-launch-17 (owner decision 2026-11-08): Zenith may start Roblox through an installed bootstrapper (Bloxstrap, Fishstrap,
// Froststrap or Voidstrap) when the user selects that bootstrapper's own installation row and has accepted its publisher once
// for that product. These tests use synthetic files, injected signature policies and fake process starters only.
public sealed class BootstrapperLaunchSelectionTests
{
    private static ClientInstallationCandidate Own(ClientInstallationProduct product, ClientInstallationHintSource source = ClientInstallationHintSource.UninstallRegistry)
        => new(product, @"C:\Synthetic\" + product + @"\" + product + ".exe", product.ToString(), "2.10.0.0", ClientInstallationFileState.PresentUnverified, [new(source, product)]);

    [Fact]
    public void TheTrialProviderAcceptsABootstrappersOwnRowButNeverAManagedPlayerOrForeignEvidence()
    {
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Bloxstrap)), "Bloxstrap's own executable with Bloxstrap evidence");
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Fishstrap, ClientInstallationHintSource.StandardInstallPath)));
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Froststrap)));
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Voidstrap)));
        // A bootstrapper row whose evidence names another product, or a Roblox player under a bootstrapper's folder, is still refused.
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Bloxstrap) with
            { Evidence = [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Roblox)] }));
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(new(ClientInstallationProduct.Roblox, @"C:\Synthetic\Bloxstrap\Versions\version-x\RobloxPlayerBeta.exe",
            "Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Bloxstrap)])));
        // The product's row must be its own executable, not a player or another bootstrapper's file.
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Bloxstrap) with { ExecutablePath = @"C:\Synthetic\Bloxstrap\RobloxPlayerBeta.exe" }));
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Bloxstrap) with { ExecutablePath = @"C:\Synthetic\Bloxstrap\Fishstrap.exe" }));
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(Own(ClientInstallationProduct.Bloxstrap) with { FileState = ClientInstallationFileState.Missing }));
    }

    [Fact]
    public void DiscoveryKnowsVoidstrapsOwnExecutableLikeTheOtherThreeBootstrappers()
    {
        Assert.True(WindowsClientInstallationDiscovery.TryNormalizeExecutablePath(@"C:\Tools\Voidstrap.exe", out var normalized, out var product));
        Assert.Equal(@"C:\Tools\Voidstrap.exe", normalized);
        Assert.Equal(ClientInstallationProduct.Voidstrap, product);
    }

    [Fact]
    public void TheFourBootstrappersAreKnownByTheirOwnExecutableAndRobloxIsNotOne()
    {
        Assert.Equal(new[] { ClientInstallationProduct.Bloxstrap, ClientInstallationProduct.Fishstrap, ClientInstallationProduct.Froststrap, ClientInstallationProduct.Voidstrap }, BootstrapperProducts.All);
        Assert.All(BootstrapperProducts.All, product =>
        {
            Assert.True(BootstrapperProducts.IsBootstrapper(product)); Assert.Equal(product + ".exe", BootstrapperProducts.ExecutableName(product));
            Assert.True(BootstrapperProducts.IsOwnExecutable(product, @"C:\Synthetic\" + product + @"\" + product.ToString().ToUpperInvariant() + ".EXE"));
            Assert.False(BootstrapperProducts.IsOwnExecutable(product, @"C:\Synthetic\" + product + @"\Versions\version-x\RobloxPlayerBeta.exe"));
            Assert.True(BootstrapperProducts.TryParse(product.ToString().ToLowerInvariant(), out var parsed)); Assert.Equal(product, parsed);
        });
        Assert.False(BootstrapperProducts.IsBootstrapper(ClientInstallationProduct.Roblox)); Assert.False(BootstrapperProducts.IsBootstrapper((ClientInstallationProduct)99));
        Assert.False(BootstrapperProducts.IsOwnExecutable(ClientInstallationProduct.Roblox, @"C:\Synthetic\Roblox.exe"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BootstrapperProducts.ExecutableName(ClientInstallationProduct.Roblox));
        Assert.False(BootstrapperProducts.TryParse("Roblox", out _)); Assert.False(BootstrapperProducts.TryParse("", out _)); Assert.False(BootstrapperProducts.TryParse(null, out _));
    }

    [Fact]
    public async Task ABootstrappersOwnExecutableIsNeverSuggestedEvenWhenItIsTheOnlyRowAndVoidstrapIsDiscovered()
    {
        const string local = @"C:\Synthetic\Local";
        var source = new InventorySource { Local = local };
        source.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation] = Path.Combine(local, "Bloxstrap");
        source.Files[Path.Combine(local, "Bloxstrap", "Bloxstrap.exe")] = new(ClientInstallationFileState.PresentUnverified, "Bloxstrap", "2.10.0.0");
        source.Registry[ClientInstallationRegistryHint.VoidstrapInstallLocation] = @"D:\Custom\Voidstrap";
        source.Files[@"D:\Custom\Voidstrap\Voidstrap.exe"] = new(ClientInstallationFileState.PresentUnverified, "Voidstrap", "1.0.0.0");
        source.Files[Path.Combine(local, "Voidstrap", "Voidstrap.exe")] = new(ClientInstallationFileState.PresentUnverified, "Voidstrap", "1.0.0.0");
        var snapshot = await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        var bloxstrap = Assert.Single(snapshot.Candidates, candidate => candidate.Product == ClientInstallationProduct.Bloxstrap);
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(bloxstrap), "The row is selectable by hand");
        Assert.Null(ClientInstallationSuggestion.Suggest(snapshot)); // but never preselected: launching through it needs the user's own acceptance.
        var voidstrap = snapshot.Candidates.Where(candidate => candidate.Product == ClientInstallationProduct.Voidstrap).ToArray();
        Assert.Equal(2, voidstrap.Length);
        Assert.Contains(voidstrap, candidate => candidate.ExecutablePath == @"D:\Custom\Voidstrap\Voidstrap.exe" && candidate.Evidence.Any(item => item.Source == ClientInstallationHintSource.UninstallRegistry));
        Assert.Contains(voidstrap, candidate => candidate.ExecutablePath == Path.Combine(local, "Voidstrap", "Voidstrap.exe") && candidate.Evidence.Any(item => item.Source == ClientInstallationHintSource.StandardInstallPath));
        Assert.Contains(snapshot.Issues, issue => issue.Code == ClientInstallationIssueCode.MultipleCandidates && issue.Product == ClientInstallationProduct.Voidstrap);
    }

    private sealed class InventorySource : IClientInstallationInventorySource
    {
        public string? Local;
        public Dictionary<ClientInstallationRegistryHint, string?> Registry = [];
        public Dictionary<string, ClientInstallationFileMetadata> Files = new(StringComparer.OrdinalIgnoreCase);
        public string? GetLocalAppData(CancellationToken ct) => Local;
        public string? ReadRegistryValue(ClientInstallationRegistryHint hint, CancellationToken ct) => Registry.GetValueOrDefault(hint);
        public ClientInstallationVersionDirectories ReadVersionDirectories(string root, int maximum, CancellationToken ct) => new([]);
        public ClientInstallationFileMetadata InspectExecutable(string path, CancellationToken ct) => Files.GetValueOrDefault(path) ?? new(ClientInstallationFileState.Missing);
    }
}

public sealed class BootstrapperLaunchPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-bootstrapper-launch-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    internal const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("Bloxstrap", Thumbprint, "Example Publisher", true)]
    [InlineData("voidstrap", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", " Example Publisher ", true)]
    [InlineData("Roblox", Thumbprint, "Roblox Corporation", false)]
    [InlineData("Unknownstrap", Thumbprint, "Example Publisher", false)]
    [InlineData("Bloxstrap", "ABC", "Example Publisher", false)]
    [InlineData("Bloxstrap", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "Example Publisher", false)]
    [InlineData("Bloxstrap", Thumbprint, "", false)]
    [InlineData("Bloxstrap", Thumbprint, "line\nbreak", false)]
    public void AnAcceptedPublisherNamesOneBootstrapperAWholeThumbprintAndAPlainPublisherName(string product, string thumbprint, string publisher, bool valid)
    {
        var entry = new AcceptedBootstrapperPublisher(product, thumbprint, publisher);
        if (!valid) { Assert.Throws<InvalidDataException>(() => entry.Validated()); return; }
        var normalized = entry.Validated();
        Assert.True(BootstrapperProducts.TryParse(product, out var parsed)); Assert.Equal(parsed.ToString(), normalized.Product);
        Assert.Equal(thumbprint.ToUpperInvariant(), normalized.CertificateSha256); Assert.Equal(publisher.Trim(), normalized.Publisher);
        Assert.True(normalized.IsFor(parsed)); Assert.False(normalized.IsFor(ClientInstallationProduct.Roblox));
        var signer = new ClientExecutableSigner("Example", "Example Publisher", Thumbprint, new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(normalized.Matches(signer)); Assert.False(normalized.Matches(signer with { CertificateSha256 = new string('F', 64) })); Assert.False(normalized.Matches(null));
    }

    [Fact]
    public void TheListHoldsAtMostOnePublisherPerBootstrapperAndFindsIt()
    {
        var bloxstrap = new AcceptedBootstrapperPublisher("Bloxstrap", Thumbprint, "Example Publisher");
        var fishstrap = new AcceptedBootstrapperPublisher("Fishstrap", new string('A', 64), "Other Publisher");
        var list = AcceptedBootstrapperPublisher.Validate([bloxstrap, fishstrap]);
        Assert.Equal(2, list.Count); Assert.Throws<NotSupportedException>(() => ((IList<AcceptedBootstrapperPublisher>)list).Clear());
        Assert.Equal(Thumbprint, AcceptedBootstrapperPublisher.For(list, ClientInstallationProduct.Bloxstrap)?.CertificateSha256);
        Assert.Null(AcceptedBootstrapperPublisher.For(list, ClientInstallationProduct.Froststrap)); Assert.Null(AcceptedBootstrapperPublisher.For(null, ClientInstallationProduct.Bloxstrap));
        Assert.Empty(AcceptedBootstrapperPublisher.Validate(null));
        Assert.Throws<InvalidDataException>(() => AcceptedBootstrapperPublisher.Validate([bloxstrap, bloxstrap with { CertificateSha256 = new string('B', 64) }]));
        Assert.Throws<InvalidDataException>(() => AcceptedBootstrapperPublisher.Validate([bloxstrap, null!]));
    }

    [Fact]
    public async Task TheSettingIsOptionalWithinSchemaOneRoundTripsAndRefusesInvalidEntries()
    {
        Assert.Empty(new SessionsSettings().Validated().AcceptedBootstrapperPublishers!);
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.Empty(loaded.Settings.AcceptedBootstrapperPublishers!); Assert.Equal("ocean-blue", loaded.Settings.Theme);
            await store.SaveAsync(loaded.Settings with { AcceptedBootstrapperPublishers = [new("bloxstrap", Thumbprint.ToLowerInvariant(), "Example Publisher")] }, default);
        }
        string saved = await File.ReadAllTextAsync(path);
        Assert.Contains("\"AcceptedBootstrapperPublishers\":[{\"Product\":\"Bloxstrap\",\"CertificateSha256\":\"" + Thumbprint + "\",\"Publisher\":\"Example Publisher\"}]", saved, StringComparison.Ordinal);
        using (var store = new SettingsStore(_root))
        {
            var reloaded = await store.LoadAsync(default);
            Assert.Null(reloaded.Notice);
            var entry = Assert.Single(reloaded.Settings.AcceptedBootstrapperPublishers!);
            Assert.Equal("Bloxstrap", entry.Product); Assert.Equal(Thumbprint, entry.CertificateSha256);
            Assert.NotNull(AcceptedBootstrapperPublisher.For(reloaded.Settings.AcceptedBootstrapperPublishers, ClientInstallationProduct.Bloxstrap));
            Assert.Null(AcceptedBootstrapperPublisher.For(reloaded.Settings.AcceptedBootstrapperPublishers, ClientInstallationProduct.Fishstrap));
        }
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(AcceptedBootstrapperPublishers: [new("Roblox", Thumbprint, "Roblox Corporation")]).Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(AcceptedBootstrapperPublishers: [new("Bloxstrap", "short", "x")]).Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(AcceptedBootstrapperPublishers:
            [new("Bloxstrap", Thumbprint, "x"), new("Bloxstrap", new string('A', 64), "y")]).Validated());
        // The thumbprint identifies a public certificate; it is not a secret, so ToString may name the product but never a path.
        Assert.Contains("Bloxstrap", new AcceptedBootstrapperPublisher("Bloxstrap", Thumbprint, "Example Publisher").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrustStatusesSummariesAndDiagnosticsNameTheAcceptedPublisherOutcome()
    {
        var at = DateTimeOffset.UtcNow; const string path = @"C:\Synthetic\Bloxstrap\Bloxstrap.exe";
        var signer = new ClientExecutableSigner("Example", "Example Publisher", Thumbprint, at.AddDays(-1), at.AddDays(1));
        var verified = new ClientExecutableTrustResult(ClientInstallationProduct.Bloxstrap, path, ClientExecutableTrustStatus.VerifiedAcceptedPublisher, ClientExecutableTrustReason.Verified, at, Signer: signer);
        Assert.Contains("Bloxstrap", verified.Summary, StringComparison.Ordinal); Assert.Contains("accepted", verified.Summary, StringComparison.Ordinal);
        Assert.Contains("does not write client files", verified.Summary, StringComparison.Ordinal); Assert.DoesNotContain("Roblox publisher", verified.Summary, StringComparison.Ordinal);
        Assert.False(verified.NativeLaunchAuthorized); Assert.DoesNotContain(path, verified.ToString());
        var notAccepted = verified with { Status = ClientExecutableTrustStatus.Unknown, Reason = ClientExecutableTrustReason.PublisherNotAccepted };
        Assert.Contains("Accept publisher", notAccepted.Summary, StringComparison.Ordinal); Assert.Contains("Bloxstrap", notAccepted.Summary, StringComparison.Ordinal);
        var changed = verified with { Status = ClientExecutableTrustStatus.Unknown, Reason = ClientExecutableTrustReason.AcceptedPublisherChanged };
        Assert.Contains("not the one you accepted", changed.Summary, StringComparison.Ordinal); Assert.Contains("Bloxstrap", changed.Summary, StringComparison.Ordinal);
        // Publisher review is still available, but no trust outcome bypasses the standard-player-first launch gate.
        foreach (var result in new[] { verified, notAccepted, changed })
        {
            Assert.Contains("Bootstrapper launches and trials are deferred", result.Summary, StringComparison.Ordinal);
            Assert.Contains("standard Roblox player", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("to allow launching", result.Summary, StringComparison.Ordinal);
            Assert.False(result.NativeLaunchAuthorized);
        }
        // The Roblox player's own verified summary is unchanged.
        Assert.Contains("Roblox publisher", (verified with { Product = ClientInstallationProduct.Roblox, Status = ClientExecutableTrustStatus.VerifiedRobloxPublisher }).Summary, StringComparison.Ordinal);

        var snapshot = new NativeLaunchDiagnosticSnapshot(Guid.NewGuid(), 1, at, at, NativeLaunchDiagnosticStage.VerifyingExecutable, NativeLaunchCleanupStatus.Pending,
            TrustStatus: ClientExecutableTrustStatus.VerifiedAcceptedPublisher, TrustReason: ClientExecutableTrustReason.Verified);
        Assert.Same(snapshot, snapshot.Validated());
        Assert.Throws<ArgumentException>(() => (snapshot with { TrustReason = ClientExecutableTrustReason.RevocationUnavailable }).Validated());
        Assert.Throws<ArgumentException>(() => (snapshot with { TrustStatus = ClientExecutableTrustStatus.Unknown }).Validated());
        var view = NativeLaunchDiagnosticPresentation.Project(snapshot.OperationId, snapshot);
        Assert.StartsWith("Publisher: verified · ", view.Trust, StringComparison.Ordinal); Assert.Contains("accepted", view.Trust, StringComparison.Ordinal);
        Assert.Contains("no launch authority", view.Trust, StringComparison.Ordinal); Assert.DoesNotContain("Roblox publisher", view.Trust, StringComparison.Ordinal);
        Assert.Contains("not accepted", NativeLaunchDiagnosticPresentation.Project(snapshot.OperationId, snapshot with { TrustStatus = ClientExecutableTrustStatus.Unknown, TrustReason = ClientExecutableTrustReason.PublisherNotAccepted }).Trust, StringComparison.Ordinal);
        Assert.Contains("not the one you accepted", NativeLaunchDiagnosticPresentation.Project(snapshot.OperationId, snapshot with { TrustStatus = ClientExecutableTrustStatus.Unknown, TrustReason = ClientExecutableTrustReason.AcceptedPublisherChanged }).Trust, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Voidstrap")]
    [InlineData("FFlags")]
    [InlineData("launch through a bootstrapper")]
    [InlineData("accept publisher")]
    public void SettingsSearchFindsLaunchingThroughABootstrapperAndTheControlsAndGuideExist(string query)
    {
        var entry = Assert.Single(SettingsSearchCatalog.Search(query), entry => entry.Id == "bootstrapper-launch");
        Assert.Equal(SettingsSearchTarget.Installations, entry.Target);
        Assert.Contains("Bloxstrap, Fishstrap, Froststrap or Voidstrap", entry.Description, StringComparison.Ordinal);
        Assert.Contains("Off by default", entry.Description, StringComparison.Ordinal);
        Assert.Contains(SettingsSearchCatalog.Search("Voidstrap"), entry => entry.Id == "installations");
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!);
        var installations = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "StackPanel"
            && e.Elements().Any(child => (string?)child.Attribute(X + "Name") == "SettingsInstallationsAnchor")));
        var accept = Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "SettingsAcceptBootstrapperPublisher"));
        Assert.Equal("Button", accept.Name.LocalName); Assert.Equal("{Binding AcceptBootstrapperPublisher}", (string?)accept.Attribute("Command"));
        Assert.Equal("{Binding CanAcceptBootstrapperPublisher}", (string?)accept.Attribute("IsEnabled")); Assert.Equal("Accept publisher", (string?)accept.Attribute("Content"));
        var forget = Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute("Command") == "{Binding ForgetBootstrapperPublisher}"));
        Assert.Equal("{Binding CanForgetBootstrapperPublisher}", (string?)forget.Attribute("IsEnabled"));
        var status = Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "SettingsBootstrapperLaunch"));
        Assert.Equal("{Binding BootstrapperLaunchStatus}", (string?)status.Attribute("Text")); Assert.Equal("True", (string?)status.Attribute("Focusable"));
        Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute("Command") == "{Binding VerifyInstallationPublisher}" && (string?)e.Attribute("Content") == "Verify publisher"));
        Assert.Contains(installations.Descendants().Where(e => e.Name.LocalName == "TextBlock"), e => ((string?)e.Attribute("Text") ?? "").Contains("Voidstrap", StringComparison.Ordinal));

        var guide = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"
            && ((string?)e.Attribute("Visibility"))?.Contains("IsGuide", StringComparison.Ordinal) == true));
        var texts = guide.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => ProductionWindowStrings.Resolve((string?)e.Attribute("Text") ?? "")).ToList();
        Assert.Contains(texts, text => text == "Launching through a bootstrapper");
        string section = Assert.Single(texts, text => text.StartsWith("Settings → Roblox installation lists Bloxstrap", StringComparison.Ordinal));
        foreach (string expected in new[] { "Bloxstrap, Fishstrap, Froststrap or Voidstrap", "Verify publisher", "Accept publisher", "thumbprint", "applies its own client settings and FFlags",
            "Zenith never writes those files", "deferred", "exact Roblox player", "does not enable trials or ordinary launching", "standard Roblox player" })
            Assert.Contains(expected, section, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-bootstrapper-launch-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

/// <summary>Synthetic files named like a bootstrapper, an injected signature policy and a fake starter; nothing is executed.</summary>
public sealed class BootstrapperExecutableTrustTests : IDisposable
{
    private readonly string _name = "zenith-bootstrapper-trust-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private const string Handoff = "roblox-player:1+launchmode:play+gameinfo:synthetic-opaque-one-use-value";
    private static readonly ClientExecutableSigner Example = new("Example Publisher", "Example Publisher", BootstrapperLaunchPolicyTests.Thumbprint,
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private string Executable(ClientInstallationProduct product) => Path.Combine(Root, product + ".exe");
    private ClientInstallationCandidate Candidate(ClientInstallationProduct product, string? path = null)
        => new(product, path ?? Executable(product), product.ToString(), "2.10.0.0", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, product)]);
    private byte[] CreateFile(ClientInstallationProduct product)
    { Directory.CreateDirectory(Root); byte[] bytes = Encoding.ASCII.GetBytes("Synthetic bootstrapper bytes, never executed: " + product); File.WriteAllBytes(Executable(product), bytes); return bytes; }

    [Theory]
    [InlineData(ClientInstallationProduct.Bloxstrap)]
    [InlineData(ClientInstallationProduct.Fishstrap)]
    [InlineData(ClientInstallationProduct.Froststrap)]
    [InlineData(ClientInstallationProduct.Voidstrap)]
    public async Task AWellSignedBootstrapperIsUnknownUntilItsPublisherIsAcceptedThenVerifiedAndHeldForOneStart(ClientInstallationProduct product)
    {
        byte[] bytes = CreateFile(product); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        // Default: no publisher accepted for any bootstrapper, so the file is evaluated but stays unknown, with the signer shown.
        var policy = new Policy(); var starter = new Starter();
        await using (var verifier = new WindowsClientExecutableTrust(policy, starter: starter))
        {
            var result = await verifier.VerifyAsync(Candidate(product), default);
            Assert.Equal(ClientExecutableTrustStatus.Unknown, result.Status); Assert.Equal(ClientExecutableTrustReason.PublisherNotAccepted, result.Reason);
            Assert.Equal(Example, result.Signer); Assert.Equal(hash, result.Sha256); Assert.NotNull(result.File); Assert.Equal(1, policy.Calls);
            using var preparation = await verifier.PrepareAsync(Candidate(product), default);
            Assert.Null(preparation.Prepared); Assert.Equal(ClientExecutableTrustReason.PublisherNotAccepted, preparation.Trust.Reason); AssertReleased(product);
        }
        // Accepted for another product only: still not accepted for this one.
        await using (var verifier = new WindowsClientExecutableTrust(policy, starter: starter,
            acceptedPublishers: new Accepted(product == ClientInstallationProduct.Bloxstrap ? ClientInstallationProduct.Fishstrap : ClientInstallationProduct.Bloxstrap, Example.CertificateSha256)))
            Assert.Equal(ClientExecutableTrustReason.PublisherNotAccepted, (await verifier.VerifyAsync(Candidate(product), default)).Reason);
        // Accepted with a different certificate: the file's signer is not the one accepted.
        await using (var verifier = new WindowsClientExecutableTrust(policy, starter: starter, acceptedPublishers: new Accepted(product, new string('F', 64))))
        {
            var changed = await verifier.VerifyAsync(Candidate(product), default);
            Assert.Equal(ClientExecutableTrustStatus.Unknown, changed.Status); Assert.Equal(ClientExecutableTrustReason.AcceptedPublisherChanged, changed.Reason); Assert.Equal(Example, changed.Signer);
            using var preparation = await verifier.PrepareAsync(Candidate(product), default); Assert.Null(preparation.Prepared); AssertReleased(product);
        }
        // Accepted for this product with this certificate: verified, held, and started with exactly the official request.
        starter = new Starter(start =>
        {
            Assert.Equal(Executable(product), start.FileName); Assert.Equal(Root, start.WorkingDirectory); Assert.False(start.UseShellExecute);
            Assert.Equal(Handoff, Assert.Single(start.ArgumentList)); AssertPinned(product); return new FakeProcess();
        });
        await using (var verifier = new WindowsClientExecutableTrust(policy, starter: starter, acceptedPublishers: new Accepted(product, Example.CertificateSha256.ToLowerInvariant())))
        {
            var accepted = await verifier.VerifyAsync(Candidate(product), default);
            Assert.Equal(ClientExecutableTrustStatus.VerifiedAcceptedPublisher, accepted.Status); Assert.Equal(ClientExecutableTrustReason.Verified, accepted.Reason);
            Assert.Equal(hash, accepted.Sha256); Assert.False(accepted.NativeLaunchAuthorized);
            using var preparation = await verifier.PrepareAsync(Candidate(product), default);
            Assert.NotNull(preparation.Prepared); Assert.Equal(ClientExecutableTrustStatus.VerifiedAcceptedPublisher, preparation.Trust.Status); AssertPinned(product);
            var started = preparation.Prepared!.Start(Handoff);
            Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, started.Outcome); Assert.True(started.ProcessReceipt!.IsValid);
            Assert.Equal(1, starter.Calls); Assert.Empty(starter.LastStart!.ArgumentList); AssertReleased(product);
            Assert.DoesNotContain(Handoff, preparation.ToString() + preparation.Prepared + JsonSerializer.Serialize(started));
        }
    }

    [Fact]
    public async Task AnUnsignedRenamedOrMissingBootstrapperIsRefusedWhateverIsAccepted()
    {
        CreateFile(ClientInstallationProduct.Bloxstrap); var starter = new Starter();
        var unsigned = new Policy(new(NativeClientTrustOutcome.Unsigned));
        await using (var verifier = new WindowsClientExecutableTrust(unsigned, starter: starter, acceptedPublishers: new Accepted(ClientInstallationProduct.Bloxstrap, Example.CertificateSha256)))
        {
            var result = await verifier.VerifyAsync(Candidate(ClientInstallationProduct.Bloxstrap), default);
            Assert.Equal(ClientExecutableTrustStatus.Rejected, result.Status); Assert.Equal(ClientExecutableTrustReason.Unsigned, result.Reason); Assert.Equal(1, unsigned.Calls);
            using var preparation = await verifier.PrepareAsync(Candidate(ClientInstallationProduct.Bloxstrap), default); Assert.Null(preparation.Prepared);
        }
        var policy = new Policy();
        await using (var verifier = new WindowsClientExecutableTrust(policy, starter: starter, acceptedPublishers: new Accepted(ClientInstallationProduct.Bloxstrap, Example.CertificateSha256)))
        {
            // Only the product's own executable is evaluated under its policy: a player or another bootstrapper's file is an invalid row.
            string player = Path.Combine(Root, "RobloxPlayerBeta.exe"); File.WriteAllBytes(player, [1, 2, 3]);
            var renamed = await verifier.VerifyAsync(Candidate(ClientInstallationProduct.Bloxstrap, player), default);
            Assert.Equal(ClientExecutableTrustStatus.Rejected, renamed.Status); Assert.Equal(ClientExecutableTrustReason.InvalidCandidate, renamed.Reason);
            var other = await verifier.VerifyAsync(Candidate(ClientInstallationProduct.Bloxstrap, Executable(ClientInstallationProduct.Fishstrap)), default);
            Assert.Equal(ClientExecutableTrustReason.InvalidCandidate, other.Reason); Assert.Equal(0, policy.Calls);
            File.Delete(Executable(ClientInstallationProduct.Bloxstrap));
            var missing = await verifier.VerifyAsync(Candidate(ClientInstallationProduct.Bloxstrap), default);
            Assert.Equal(ClientExecutableTrustStatus.Rejected, missing.Status); Assert.Equal(ClientExecutableTrustReason.MissingFile, missing.Reason);
            // The Roblox launcher keeps its own "not evaluated" answer; it is neither the player nor a bootstrapper.
            string launcher = Path.Combine(Root, RobloxNativePlayerFiles.LauncherName); File.WriteAllBytes(launcher, [1, 2, 3]);
            Assert.Equal(ClientExecutableTrustReason.BootstrapperNotEvaluated, (await verifier.VerifyAsync(new(ClientInstallationProduct.Roblox, launcher, null, null, ClientInstallationFileState.PresentUnverified, []), default)).Reason);
        }
        Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public async Task ExtraClientArgumentsNeverReachAnAcceptedBootstrapper()
    {
        CreateFile(ClientInstallationProduct.Bloxstrap);
        var starter = new Starter(start => { Assert.Equal(Handoff, Assert.Single(start.ArgumentList)); return new FakeProcess(); });
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter, acceptedPublishers: new Accepted(ClientInstallationProduct.Bloxstrap, Example.CertificateSha256));
        using var preparation = await verifier.PrepareAsync(Candidate(ClientInstallationProduct.Bloxstrap), ["--synthetic-flag", "--value=1"], default);
        Assert.NotNull(preparation.Prepared);
        Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, preparation.Prepared!.Start(Handoff).Outcome); Assert.Equal(1, starter.Calls);
        Assert.DoesNotContain("synthetic-flag", preparation.Prepared.ToString());
    }

    [Fact]
    public async Task AThrowingAcceptedPublisherSourceLeavesTrustUnknownWithoutAHeldStart()
    {
        CreateFile(ClientInstallationProduct.Bloxstrap); var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter, acceptedPublishers: new Accepted(ClientInstallationProduct.Bloxstrap, Example.CertificateSha256) { Throw = true });
        var result = await verifier.VerifyAsync(Candidate(ClientInstallationProduct.Bloxstrap), default);
        Assert.Equal(ClientExecutableTrustStatus.Unknown, result.Status); Assert.Equal(ClientExecutableTrustReason.VerificationUnavailable, result.Reason);
        using var preparation = await verifier.PrepareAsync(Candidate(ClientInstallationProduct.Bloxstrap), default);
        Assert.Null(preparation.Prepared); Assert.Equal(0, starter.Calls); AssertReleased(ClientInstallationProduct.Bloxstrap);
    }

    private void AssertPinned(ClientInstallationProduct product)
    {
        Assert.Throws<IOException>(() => File.Open(Executable(product), FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Delete(Executable(product)));
    }
    private void AssertReleased(ClientInstallationProduct product) { using var file = File.Open(Executable(product), FileMode.Open, FileAccess.Write, FileShare.None); }

    internal sealed class Accepted(ClientInstallationProduct product, string thumbprint) : IAcceptedBootstrapperPublisherSource
    {
        public bool Throw;
        public AcceptedBootstrapperPublisher? Find(ClientInstallationProduct requested)
        {
            if (Throw) throw new IOException("Synthetic saved-state read failure");
            return requested == product ? new AcceptedBootstrapperPublisher(product.ToString(), thumbprint, "Example Publisher").Validated() : null;
        }
    }
    private sealed class Policy(NativeClientTrustEvidence? evidence = null) : IClientAuthenticodePolicy
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Assert.False(handle.IsInvalid); Interlocked.Increment(ref _calls); return evidence ?? new(NativeClientTrustOutcome.VerifiedCached, Example); }
    }
    private sealed class Starter(Func<ProcessStartInfo, INativeHandoffStartedProcess?>? action = null) : INativeHandoffProcessStarter
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls); public ProcessStartInfo? LastStart;
        public INativeHandoffStartedProcess? Start(ProcessStartInfo start)
        { LastStart = start; Interlocked.Increment(ref _calls); return action is null ? new FakeProcess() : action(start); }
    }
    private sealed class FakeProcess : INativeHandoffStartedProcess
    {
        public LaunchProcessReceipt? CaptureReceipt(DateTimeOffset at) => new(24680, at.AddMinutes(-1).UtcTicks, at);
        public void Dispose() { }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected owned bootstrapper fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

public sealed partial class NativeAccountLaunchProviderTests
{
    [Fact]
    public async Task AnAcceptedBootstrapperIsStartedWithOnlyTheOfficialRequestAndTheArgumentsSourceIsNeverRead()
    {
        var source = new ArgumentsSource { Arguments = ["--synthetic-flag"] };
        await using var f = new Fixture(_root, arguments: source, product: ClientInstallationProduct.Bloxstrap,
            acceptedPublishers: new BootstrapperExecutableTrustTests.Accepted(ClientInstallationProduct.Bloxstrap, new string('A', 64)));
        Assert.EndsWith("Bloxstrap.exe", f.Executable, StringComparison.Ordinal);
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.None, result.Failure); await using var handle = result.Prepared!;
        Assert.Equal(0, source.Calls); // R-launch-16: extra arguments are for Zenith's own direct player start only.
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId);
        Assert.Equal(ClientExecutableTrustStatus.VerifiedAcceptedPublisher, diagnostic?.TrustStatus); Assert.Equal(ClientExecutableTrustReason.Verified, diagnostic?.TrustReason);
        using var boundary = Boundary(handle.DispatchIntent);
        var launched = await handle.DispatchAsync(boundary, default);
        Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, launched.Status); Assert.Equal(13579, launched.ProcessId);
        Assert.Equal(f.Executable, f.Starter.LastStart!.FileName); Assert.Single(f.Starter.ObservedArguments!); Assert.Equal(0, source.Calls); f.AssertReleased();
    }

    [Fact]
    public async Task ABootstrapperWhosePublisherIsNotAcceptedIsRejectedBeforeAnyAccountWork()
    {
        await using var f = new Fixture(_root, product: ClientInstallationProduct.Froststrap);
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.Equal(0, f.Preparer.Calls); Assert.Equal(0, f.Capture.Calls); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId);
        Assert.Equal(ClientExecutableTrustStatus.Unknown, diagnostic?.TrustStatus); Assert.Equal(ClientExecutableTrustReason.PublisherNotAccepted, diagnostic?.TrustReason);
    }
}

public sealed partial class ClientWindowIntegrationTests
{
    private static ClientInstallationSnapshot BootstrapperSnapshot() => new(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Complete,
        [new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\Bloxstrap\\Versions\\version-abc\\RobloxPlayerBeta.exe", "Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Bloxstrap)]),
         new(ClientInstallationProduct.Bloxstrap, "C:\\Synthetic\\Bloxstrap\\Bloxstrap.exe", "Bloxstrap", "2.10.0.0", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Bloxstrap)])],
        []);

    [Fact]
    public Task AcceptingABootstrapperPublisherCannotSelectItForNativeTrials() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = BootstrapperSnapshot() };
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(),
            installationDiscovery: discovery, executableTrust: new BootstrapperTrustProbe());
        await shell.InitializeAsync();
        shell.RefreshInstallations.Execute(null); await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation = Assert.Single(shell.InstallationCandidates, row => row.Candidate.Product == ClientInstallationProduct.Bloxstrap);
        shell.VerifyInstallationPublisher.Execute(null); await shell.VerifyInstallationPublisher.Execution;
        Assert.True(shell.CanAcceptBootstrapperPublisher);
        shell.AcceptBootstrapperPublisher.Execute(null); await shell.AcceptBootstrapperPublisher.Execution;
        Assert.True(shell.CanForgetBootstrapperPublisher);
        var provider = Assert.IsType<WindowsConfiguredNativeLaunchProvider>(ReportedField(shell, "_nativeLaunch").GetValue(shell));
        Assert.False(provider.Selected); // Actual launch eligibility, independent of deferred-status wording.
        Assert.False(shell.CanRunNativeTrial);
    });

    [Fact]
    public Task BootstrapperPublisherAcceptanceStaysInspectionOnlyUntilExactPlayerAssociationIsImplemented() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = BootstrapperSnapshot() }; var trust = new BootstrapperTrustProbe();
        var monitor = new Monitor(); string settingsPath = Path.Combine(_root, "settings.json");
        await using (var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, installationDiscovery: discovery, executableTrust: trust))
        {
            await shell.InitializeAsync();
            shell.RefreshInstallations.Execute(null); await shell.RefreshInstallations.Execution;
            Assert.Null(shell.SelectedInstallation); // Neither the managed player nor the bootstrapper's own row is ever preselected.
            var provider = Assert.IsType<WindowsConfiguredNativeLaunchProvider>(ReportedField(shell, "_nativeLaunch").GetValue(shell));
            var managed = Assert.Single(shell.InstallationCandidates, row => row.ManagedBy == ClientInstallationProduct.Bloxstrap);
            var row = Assert.Single(shell.InstallationCandidates, row => row.Candidate.Product == ClientInstallationProduct.Bloxstrap);
            Assert.Equal("Bloxstrap", row.Product);

            // Off by default: the bootstrapper's own row is selectable but not the trial installation, and the status says what to do.
            shell.SelectedInstallation = row;
            Assert.False(provider.Selected); Assert.NotEqual(row.ExecutablePath, shell.NativeTrialInstallation); Assert.False(shell.CanRunNativeTrial);
            Assert.Contains("Bloxstrap", shell.NativeTrialStatus, StringComparison.Ordinal); Assert.Contains("deferred", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.Contains("deferred", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.Contains("off", shell.BootstrapperLaunchStatus, StringComparison.Ordinal); Assert.Contains("Bloxstrap", shell.BootstrapperLaunchStatus, StringComparison.Ordinal);
            Assert.True(shell.CanVerifyInstallationPublisher); Assert.False(shell.CanAcceptBootstrapperPublisher); Assert.False(shell.CanForgetBootstrapperPublisher);
            Assert.Equal("Select a native Roblox installation, then explicitly verify its publisher.", shell.PublisherVerificationStatus);
            shell.AcceptBootstrapperPublisher.Execute(null); await shell.AcceptBootstrapperPublisher.Execution; // Nothing to accept yet.
            Assert.False(File.Exists(settingsPath) && (await File.ReadAllTextAsync(settingsPath)).Contains("Bloxstrap", StringComparison.Ordinal));

            // Verify: the signature is valid but no publisher is accepted, so the signer is shown and may be accepted.
            shell.VerifyInstallationPublisher.Execute(null); await shell.VerifyInstallationPublisher.Execution;
            Assert.Equal(1, trust.Calls);
            Assert.Contains(BootstrapperTrustProbe.Thumbprint, shell.PublisherVerificationStatus, StringComparison.Ordinal);
            Assert.Contains("Example Publisher", shell.PublisherVerificationStatus, StringComparison.Ordinal);
            Assert.Contains("Accept publisher", shell.PublisherVerificationStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("SHA-256 " + new string('C', 64), shell.PublisherVerificationStatus, StringComparison.Ordinal);
            Assert.True(shell.CanAcceptBootstrapperPublisher); Assert.False(provider.Selected);

            // Accept once for Bloxstrap: saved inspection choice, with launching still deferred.
            shell.AcceptBootstrapperPublisher.Execute(null); await shell.AcceptBootstrapperPublisher.Execution;
            string saved = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("\"Product\":\"Bloxstrap\"", saved, StringComparison.Ordinal); Assert.Contains(BootstrapperTrustProbe.Thumbprint, saved, StringComparison.Ordinal);
            Assert.Contains("\"Publisher\":\"Example Publisher\"", saved, StringComparison.Ordinal); Assert.Equal("Settings saved", shell.SaveStatus);
            Assert.False(provider.Selected); Assert.NotEqual(row.ExecutablePath, shell.NativeTrialInstallation);
            Assert.Contains("Bloxstrap", shell.NativeTrialStatus, StringComparison.Ordinal); Assert.Contains("standard Roblox player", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.Contains("Publisher inspection remains available", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.False(shell.CanAcceptBootstrapperPublisher); Assert.True(shell.CanForgetBootstrapperPublisher);
            Assert.Contains("Example Publisher", shell.BootstrapperLaunchStatus, StringComparison.Ordinal); Assert.Contains(BootstrapperTrustProbe.Thumbprint[..16], shell.BootstrapperLaunchStatus, StringComparison.Ordinal);
            Assert.Contains("Example Publisher", shell.PublisherVerificationStatus, StringComparison.Ordinal); Assert.Contains("Accepted", shell.PublisherVerificationStatus, StringComparison.Ordinal);

            // A verified result for the accepted signer is accepted by the Shell's consistency checks.
            trust.Accepted = true; shell.VerifyInstallationPublisher.Execute(null); await shell.VerifyInstallationPublisher.Execution;
            Assert.Equal(2, trust.Calls); Assert.Contains("SHA-256 " + new string('C', 64), shell.PublisherVerificationStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("failed", shell.PublisherVerificationStatus, StringComparison.OrdinalIgnoreCase);
            // A result for a different signer than the accepted one is reported as such and offers acceptance again.
            trust.Accepted = false; trust.Changed = true; shell.VerifyInstallationPublisher.Execute(null); await shell.VerifyInstallationPublisher.Execution;
            Assert.Contains("not the one you accepted", shell.PublisherVerificationStatus, StringComparison.Ordinal); Assert.True(shell.CanAcceptBootstrapperPublisher);

            // The managed player under Bloxstrap's folder is still inspection-only and points at the bootstrapper's own row.
            shell.SelectedInstallation = managed;
            Assert.False(provider.Selected); Assert.Contains("managed by Bloxstrap", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.Contains("exact player", shell.NativeTrialStatus, StringComparison.Ordinal); Assert.False(shell.CanAcceptBootstrapperPublisher);
            shell.SelectedInstallation = row; Assert.False(provider.Selected);

            // Forget: the acceptance leaves settings; the row remains inspection-only.
            shell.ForgetBootstrapperPublisher.Execute(null); await shell.ForgetBootstrapperPublisher.Execution;
            Assert.DoesNotContain("\"Product\":\"Bloxstrap\"", await File.ReadAllTextAsync(settingsPath), StringComparison.Ordinal);
            Assert.False(provider.Selected); Assert.False(shell.CanForgetBootstrapperPublisher); Assert.Contains("deferred", shell.NativeTrialStatus, StringComparison.Ordinal);
            Assert.Empty(shell.Launches); Assert.DoesNotContain(shell.Events, line => line.Contains("Operation failed", StringComparison.Ordinal));

            // Accept again so the reopened shell below can show that the saved acceptance is read back.
            trust.Changed = false; shell.VerifyInstallationPublisher.Execute(null); await shell.VerifyInstallationPublisher.Execution;
            shell.AcceptBootstrapperPublisher.Execute(null); await shell.AcceptBootstrapperPublisher.Execution; Assert.False(provider.Selected);
        }
        await using (var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, installationDiscovery: discovery, executableTrust: trust))
        {
            await reopened.InitializeAsync();
            reopened.RefreshInstallations.Execute(null); await reopened.RefreshInstallations.Execution;
            var provider = Assert.IsType<WindowsConfiguredNativeLaunchProvider>(ReportedField(reopened, "_nativeLaunch").GetValue(reopened));
            var row = Assert.Single(reopened.InstallationCandidates, row => row.Candidate.Product == ClientInstallationProduct.Bloxstrap);
            Assert.Null(reopened.SelectedInstallation); // An accepted publisher never makes the bootstrapper's row a preselection.
            reopened.SelectedInstallation = row;
            Assert.False(provider.Selected); Assert.NotEqual(row.ExecutablePath, reopened.NativeTrialInstallation); Assert.True(reopened.CanForgetBootstrapperPublisher);
        }
    });

    private sealed class BootstrapperTrustProbe : IClientExecutableTrustVerifier
    {
        public const string Thumbprint = BootstrapperLaunchPolicyTests.Thumbprint;
        public int Calls; public bool Accepted, Changed;
        public Task<ClientExecutableTrustResult> VerifyAsync(ClientInstallationCandidate candidate, CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested(); var at = DateTimeOffset.UtcNow;
            var signer = new ClientExecutableSigner("Example Publisher", "Example Publisher", Changed ? new string('D', 64) : Thumbprint, at.AddDays(-1), at.AddDays(1));
            var file = new ClientExecutableFileSnapshot(candidate.ExecutablePath, 4096, at.AddDays(-1), at.AddDays(-1), 1, new string('B', 32));
            var (status, reason) = Accepted ? (ClientExecutableTrustStatus.VerifiedAcceptedPublisher, ClientExecutableTrustReason.Verified)
                : Changed ? (ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.AcceptedPublisherChanged)
                : (ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.PublisherNotAccepted);
            return Task.FromResult(new ClientExecutableTrustResult(candidate.Product, candidate.ExecutablePath, status, reason, at, file, new string('C', 64), signer));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
