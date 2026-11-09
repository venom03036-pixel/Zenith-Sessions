using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-launch-0 (owner decision 2026-11-08): ordinary single and group launches run end to end, with no per-account review, once one
// explicit trial has been recorded as verified for the saved native launch mode and the selected installation. Until then the
// native provider stays Unverified and ordinary requests are recorded blocked, as before. Synthetic queue rows, evidence views,
// settings files and fake native children only: no account, client, browser or endpoint.
public sealed class NativeLaunchVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-launch-verification-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Start = Now.AddMinutes(-2).UtcTicks;
    private static readonly Guid Operation = Guid.Parse("7d1f1c2e-5b7a-4c3d-9e8f-0123456789ab");
    private static readonly NativeLaunchExecutableIdentity Identity = new(new('A', 64), new('B', 64), new('C', 64));
    private static NativeLaunchVerification Entry(string mode = "official-page", string product = "Roblox")
        => new(mode, product, Now, Operation, 730001, Start, 456, 42, Identity);

    [Theory]
    [InlineData("official-page", "Roblox", 730001, 456, 42, true)]
    [InlineData(" Direct-Ticket ", "bloxstrap", 1, 1, 1, true)]
    [InlineData("browser", "Roblox", 730001, 456, 42, false)]
    [InlineData("official-page", "RobloxPlayerBeta.exe", 730001, 456, 42, false)]
    [InlineData("official-page", "1", 730001, 456, 42, false)]
    [InlineData("official-page", "Roblox", 0, 456, 42, false)]
    [InlineData("official-page", "Roblox", 730001, 0, 42, false)]
    [InlineData("official-page", "Roblox", 730001, 456, 0, false)]
    public void AVerificationNamesOneModeOneProductAndTheTrialsReceiptPlayerAndPlace(string mode, string product, int pid, long user, long place, bool valid)
    {
        var entry = new NativeLaunchVerification(mode, product, Now, Operation, pid, Start, user, place);
        if (!valid) { Assert.Throws<InvalidDataException>(() => entry.Validated()); return; }
        var normalized = entry.Validated();
        Assert.True(NativeLaunchModes.TryParse(mode, out var parsedMode)); Assert.Equal(NativeLaunchModes.ToSettingsValue(parsedMode), normalized.Mode);
        Assert.True(NativeLaunchVerification.TryParseProduct(product, out var parsedProduct)); Assert.Equal(parsedProduct.ToString(), normalized.Product);
        Assert.True(normalized.IsFor(parsedMode, parsedProduct));
        Assert.False(normalized.IsFor(parsedMode == NativeLaunchMode.OfficialPage ? NativeLaunchMode.DirectTicket : NativeLaunchMode.OfficialPage, parsedProduct));
        Assert.False(normalized.IsFor(parsedMode, parsedProduct == ClientInstallationProduct.Roblox ? ClientInstallationProduct.Fishstrap : ClientInstallationProduct.Roblox));
        Assert.Contains(NativeLaunchModes.Label(parsedMode), normalized.Describe(), StringComparison.Ordinal);
        Assert.Contains(parsedProduct.ToString(), normalized.Describe(), StringComparison.Ordinal);
        // The entry holds IDs, a time and a receipt only; nothing about it is a secret, and the explanation claims no proof.
        Assert.Contains("still", normalized.ExplainSupported(), StringComparison.Ordinal);
        Assert.Contains("log", normalized.ExplainSupported(), StringComparison.Ordinal);
    }

    [Fact]
    public void AVerificationIsRefusedWhenItsTimeOrRequestIsInvalid()
    {
        Assert.Throws<InvalidDataException>(() => (Entry() with { VerifiedUtc = default }).Validated());
        Assert.Throws<InvalidDataException>(() => (Entry() with { VerifiedUtc = new DateTimeOffset(Start - 1, TimeSpan.Zero) }).Validated()); // Verified before the client started.
        Assert.Throws<InvalidDataException>(() => (Entry() with { OperationId = Guid.Empty }).Validated());
        Assert.Throws<InvalidDataException>(() => (Entry() with { ProcessStartUtcTicks = 0 }).Validated());
        Assert.Throws<InvalidDataException>(() => (Entry() with { Mode = null! }).Validated());
        Assert.Throws<InvalidDataException>(() => (Entry() with { Product = null! }).Validated());
        Assert.Equal(Entry(), Entry().Validated());
    }

    [Fact]
    public void TheListHoldsAtMostOneVerificationPerModeAndProductAndFindsIt()
    {
        var page = Entry(); var ticket = Entry("direct-ticket"); var bloxstrap = Entry(product: "Bloxstrap");
        var list = NativeLaunchVerification.Validate([page, ticket, bloxstrap]);
        Assert.Equal(3, list.Count); Assert.Throws<NotSupportedException>(() => ((IList<NativeLaunchVerification>)list).Clear());
        Assert.Same(list, NativeLaunchVerification.Validate(list));
        Assert.Equal(page, NativeLaunchVerification.For(list, NativeLaunchMode.OfficialPage, ClientInstallationProduct.Roblox));
        Assert.Equal(ticket, NativeLaunchVerification.For(list, NativeLaunchMode.DirectTicket, ClientInstallationProduct.Roblox));
        Assert.Equal(bloxstrap, NativeLaunchVerification.For(list, NativeLaunchMode.OfficialPage, ClientInstallationProduct.Bloxstrap));
        Assert.Null(NativeLaunchVerification.For(list, NativeLaunchMode.DirectTicket, ClientInstallationProduct.Bloxstrap));
        Assert.Null(NativeLaunchVerification.For(null, NativeLaunchMode.OfficialPage, ClientInstallationProduct.Roblox));
        Assert.Empty(NativeLaunchVerification.Validate(null)); Assert.Empty(NativeLaunchVerification.Validate([]));
        Assert.Throws<InvalidDataException>(() => NativeLaunchVerification.Validate([page, page with { ProcessId = 2 }]));
        Assert.Throws<InvalidDataException>(() => NativeLaunchVerification.Validate([page, Entry("Official-Page", "roblox") with { UserId = 9 }])); // Same mode and product in another spelling.
        Assert.Throws<InvalidDataException>(() => NativeLaunchVerification.Validate([page, null!]));
        Assert.Throws<InvalidDataException>(() => NativeLaunchVerification.Validate(Enumerable.Repeat(page, NativeLaunchVerification.MaximumEntries + 1).ToArray()));
    }

    [Fact]
    public async Task TheSettingIsOptionalWithinSchemaOneRoundTripsAndRefusesInvalidEntries()
    {
        Assert.Empty(new SessionsSettings().Validated().NativeLaunchVerifications!);
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.Empty(loaded.Settings.NativeLaunchVerifications!); Assert.Equal("ocean-blue", loaded.Settings.Theme);
            await store.SaveAsync(loaded.Settings with { NativeLaunchVerifications = [Entry("Official-Page", "roblox")] }, default);
        }
        string saved = await File.ReadAllTextAsync(path);
        Assert.Contains("\"NativeLaunchVerifications\":[" + JsonSerializer.Serialize(Entry()) + "]", saved, StringComparison.Ordinal);
        using (var store = new SettingsStore(_root))
        {
            var reloaded = await store.LoadAsync(default);
            Assert.Null(reloaded.Notice);
            Assert.Equal(Entry(), Assert.Single(reloaded.Settings.NativeLaunchVerifications!));
            Assert.Equal(NativeLaunchModes.OfficialPageValue, reloaded.Settings.NativeLaunchMode); // The mode itself is a separate choice.
        }
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(NativeLaunchVerifications: [Entry("browser")]).Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(NativeLaunchVerifications: [Entry() with { UserId = 0 }]).Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(NativeLaunchVerifications: [Entry(), Entry() with { PlaceId = 7 }]).Validated());
    }

    private static LaunchOperation StartedRow(LaunchState state = LaunchState.ClientStartedUnverified, LaunchProcessReceipt? receipt = null) => new(
        new(Operation, Guid.NewGuid(), 1, 456, 42, 0, Now.AddMinutes(-3), 7, null, RobloxServerKind.Unknown, true), state,
        state == LaunchState.ClientStartedUnverified ? LaunchProblem.ClientStartedUnverified : LaunchProblem.None, Now, receipt ?? new(730001, Start, Now.AddMinutes(-1)));

    [Fact]
    public async Task APreIdentitySettingsRecordIsPreservedAsHistoryWithoutQualifyingThePlayer()
    {
        Directory.CreateDirectory(_root);
        var legacy = Entry() with { Executable = null };
        string json = JsonSerializer.Serialize(new SessionsSettings(NativeLaunchVerifications: [legacy], Theme: "ocean-blue"))
            .Replace(",\"Executable\":null", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), json);
        using var store = new SettingsStore(_root);
        var read = await store.LoadAsync(default);
        Assert.Null(read.Notice); Assert.Equal("ocean-blue", read.Settings.Theme);
        Assert.Null(Assert.Single(read.Settings.NativeLaunchVerifications!).Executable);
        var source = new Source { Saved = read.Settings.NativeLaunchVerifications };
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => new Child(1), new TrustOwner(), source, (_, _) => true);
        provider.Configure(RobloxCandidate()); Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        await store.SaveAsync(read.Settings with { NativeLaunchVerifications = [Entry()] }, default);
        Assert.Equal(Identity, Assert.Single((await store.LoadAsync(default)).Settings.NativeLaunchVerifications!).Executable);
    }

    [Fact]
    public void ANewVerificationRequiresTheTrialExecutableAndRejectsMalformedDigests()
    {
        var row = StartedRow(); var view = MatchingView(row);
        Assert.Throws<ArgumentException>(() => NativeLaunchVerification.Record(NativeLaunchMode.OfficialPage, ClientInstallationProduct.Roblox, row, view, Now));
        Assert.Throws<ArgumentException>(() => NativeLaunchVerification.Record(NativeLaunchMode.OfficialPage, ClientInstallationProduct.Bloxstrap, row, view, Now, Identity));
        Assert.Throws<InvalidDataException>(() => (Entry() with { Executable = Identity with { FileSha256 = "short" } }).Validated());
        Assert.Throws<InvalidDataException>(() => (Entry() with { Executable = Identity with { PathSha256 = new('z', 64) } }).Validated());
    }
    private static NativeTrialEvidenceView MatchingView(LaunchOperation row) => new(row.Intent.OperationId, 456, 42, null, row.ProcessReceipt,
        NativeTrialProcessPresence.Observed, Now, Now.AddMilliseconds(-100),
        new(456, Now.AddSeconds(-1), NativeTrialReportedStatus.ReportedMatchUnverified),
        new(42, Guid.NewGuid(), Now.AddSeconds(-1), NativeTrialReportedStatus.ReportedMatchUnverified, NativeTrialReportedStatus.NotRequested),
        new(456, Now.AddSeconds(-2), NativeTrialReportedStatus.LastKnownOnly), new(42, Guid.NewGuid(), Now.AddSeconds(-2), NativeTrialReportedStatus.LastKnownOnly, NativeTrialReportedStatus.LastKnownOnly),
        NativeTrialEvidenceOutcome.ReportedMatchUnverified, "Reported values are unverified diagnostics.");

    [Theory]
    [InlineData("matching")]
    [InlineData("not-started")]
    [InlineData("no-receipt")]
    [InlineData("other-operation")]
    [InlineData("other-process")]
    [InlineData("not-observed")]
    [InlineData("stale-process")]
    [InlineData("outcome-unknown")]
    [InlineData("outcome-mismatch")]
    [InlineData("other-player")]
    [InlineData("other-place")]
    [InlineData("missing")]
    public void RecordingNeedsAStartedRowWhoseFreshEvidenceReportsTheExpectedPlayerInTheRequestedPlace(string change)
    {
        var row = StartedRow(); var view = MatchingView(row);
        LaunchOperation? candidateRow = row; NativeTrialEvidenceView? candidateView = view;
        switch (change)
        {
            case "not-started": candidateRow = StartedRow(LaunchState.Uncertain); candidateView = MatchingView(candidateRow); break;
            case "no-receipt": candidateRow = row with { ProcessReceipt = null, State = LaunchState.Queued, Problem = LaunchProblem.None }; candidateView = view with { ProcessReceipt = null }; break;
            case "other-operation": candidateView = view with { OperationId = Guid.NewGuid() }; break;
            case "other-process": candidateView = view with { ProcessReceipt = row.ProcessReceipt! with { ProcessId = 730002 } }; break;
            case "not-observed": candidateView = view with { Presence = NativeTrialProcessPresence.NotObserved }; break;
            case "stale-process": candidateView = view with { Presence = NativeTrialProcessPresence.Stale }; break;
            case "outcome-unknown": candidateView = view with { Outcome = NativeTrialEvidenceOutcome.Unknown, ReportedUser = new(null, null, NativeTrialReportedStatus.Uncorrelated) }; break;
            case "outcome-mismatch": candidateView = view with { Outcome = NativeTrialEvidenceOutcome.ReportedMismatchUnverified, ReportedUser = new(457, Now, NativeTrialReportedStatus.ReportedMismatchUnverified) }; break;
            case "other-player": candidateView = view with { ReportedUser = view.ReportedUser with { UserId = 457 } }; break;
            case "other-place": candidateView = view with { ReportedDestination = view.ReportedDestination with { PlaceId = 43 } }; break;
            case "missing": candidateView = null; break;
        }
        bool can = NativeLaunchVerification.CanRecord(candidateRow, candidateView);
        Assert.Equal(change == "matching", can);
        if (!can) { Assert.Throws<ArgumentException>(() => NativeLaunchVerification.Record(NativeLaunchMode.OfficialPage, ClientInstallationProduct.Roblox, candidateRow, candidateView, Now)); return; }
        var recorded = NativeLaunchVerification.Record(NativeLaunchMode.DirectTicket, ClientInstallationProduct.Roblox, row, view, Now, Identity);
        Assert.Equal(new NativeLaunchVerification("direct-ticket", "Roblox", Now, Operation, 730001, Start, 456, 42, Identity), recorded);
        Assert.False(NativeLaunchVerification.CanRecord(null, view));
    }

    [Fact]
    public async Task TheConfiguredProviderIsSupportedOnlyForASelectionWhoseProductTheSavedModeHasAVerificationFor()
    {
        var source = new Source(); var children = new List<Child>();
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => { var child = new Child(730001); children.Add(child); return child; }, new TrustOwner(), source, (_, identity) => identity == Identity);
        // No selection: nothing can be verified, whatever is saved.
        source.Saved = [Entry()];
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State); Assert.Equal(0, source.Reads);
        provider.Configure(RobloxCandidate());
        var supported = provider.AccountLaunch;
        Assert.Equal(CapabilityState.Supported, supported.State);
        Assert.Contains("Official page", supported.Explanation, StringComparison.Ordinal); Assert.Contains("Roblox", supported.Explanation, StringComparison.Ordinal);
        Assert.Contains("still", supported.Explanation, StringComparison.Ordinal);
        Assert.Equal(1, source.Reads); Assert.Equal(ClientInstallationProduct.Roblox, Assert.Single(source.Products));
        // A verification for another product does not cover this selection; neither does a source that fails or reports none.
        source.Saved = [Entry(product: "Bloxstrap")];
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        source.Saved = [Entry()]; source.Throw = true;
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        source.Throw = false; source.Saved = null;
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        Assert.Contains("recorded as verified", provider.AccountLaunch.Explanation, StringComparison.Ordinal);
        source.Saved = [Entry()];
        Assert.Equal(CapabilityState.Supported, provider.AccountLaunch.State);
        // Clearing the selection, and disposal, turn it off again.
        provider.Configure(null); Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        provider.Configure(RobloxCandidate()); Assert.Equal(CapabilityState.Supported, provider.AccountLaunch.State);
        await provider.DisposeAsync(); Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        Assert.Empty(children); // Capability reads never create a preparation.
        Assert.DoesNotContain("unverified", provider.ToString(), StringComparison.Ordinal);
        // A provider without a source stays Unverified, and a source can be attached exactly once (the Shell attaches its saved settings to an injected provider).
        await using var bare = new WindowsConfiguredNativeLaunchProvider(_ => new Child(1), new TrustOwner(), matchesExecutable: (_, identity) => identity == Identity);
        bare.Configure(RobloxCandidate()); Assert.Equal(CapabilityState.Unverified, bare.AccountLaunch.State);
        Assert.True(bare.TryUseVerificationSource(source)); Assert.False(bare.TryUseVerificationSource(new Source()));
        Assert.Equal(CapabilityState.Supported, bare.AccountLaunch.State);
    }

    [Fact]
    public async Task AVerifiedProviderQueuesABatchAndStartsItOneAtATimeThroughPreparationUntilTheVerificationIsForgotten()
    {
        var source = new Source { Saved = [Entry()] }; var children = new ConcurrentQueue<Child>(); int next = 730000, active = 0, overlap = 0;
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ =>
        {
            var child = new Child(Interlocked.Increment(ref next), () => { if (Interlocked.Increment(ref active) > 1) Interlocked.Increment(ref overlap); }, () => Interlocked.Decrement(ref active));
            children.Enqueue(child); return child;
        }, new TrustOwner(), source, (_, identity) => identity == Identity);
        provider.Configure(RobloxCandidate());
        var store = new Store();
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => Task.FromResult(true), _ => new(true, "Synthetic capacity"),
            startupDeadline: TimeSpan.FromSeconds(5), stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero, preparationDeadline: TimeSpan.FromSeconds(5),
            // Every started client counts as freshly observed, so the single-start barrier clears and the next request may start.
            observedReservations: rows => rows.Where(row => row.ProcessReceipt is { IsValid: true }).Select(row => row.Intent.OperationId).ToHashSet());
        await queue.InitializeAsync(default);
        var profiles = new[] { Profile("One"), Profile("Two"), Profile("Three") };
        var review = queue.CaptureBatchReview(new(42), profiles.Select(profile => (profile, (LaunchAccountBinding?)new LaunchAccountBinding(456, 7))).ToArray());
        var rows = await queue.EnqueueBatchAsync(review, default);
        // Supported: every row is recorded Queued, with no trial consent and no review per account.
        Assert.All(rows, row => { Assert.Equal(LaunchBatchDisposition.Recorded, row.Disposition); Assert.Equal(LaunchState.Queued, row.Operation!.State); Assert.False(queue.IsTrialOperation(row.Operation.Intent.OperationId)); });
        Assert.True(await queue.TryResumeAsync(default));
        await Until(() => rows.All(row => store.Row(row.Operation!.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified));
        Assert.Equal(3, children.Count); Assert.All(children, child => Assert.Equal(1, child.Handle?.Dispatches));
        Assert.Equal(0, overlap); // One preparation and start at a time, as for a trial.
        var receipts = rows.Select(row => store.Row(row.Operation!.Intent.OperationId)!.ProcessReceipt!).ToArray();
        Assert.Equal(3, receipts.Select(receipt => receipt.ProcessId).Distinct().Count()); Assert.All(receipts, receipt => Assert.True(receipt.IsValid));
        // A fourth ordinary request while paused is Queued; a trial needs an Unverified provider, so it is refused while ordinary launching is on.
        await queue.SetPausedAsync(true, default);
        var fourth = await queue.EnqueueAsync(Profile("Four"), default, new(456, 7), new(42));
        Assert.Equal(LaunchState.Queued, fourth.State); Assert.False(queue.IsTrialOperation(fourth.Intent.OperationId));
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueTrialAsync(Profile("Trial"), default, new(456, 7), new(42)));
        // Forgetting the verification (the source reports none) stops ordinary launching: the still Queued request is
        // recorded blocked at its turn, and no preparation is created for it.
        source.Saved = null;
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        int before = children.Count;
        Assert.True(await queue.TryResumeAsync(default));
        await Until(() => store.Row(fourth.Intent.OperationId)?.Problem == LaunchProblem.ProviderUnavailable);
        Assert.Equal(LaunchState.Blocked, store.Row(fourth.Intent.OperationId)!.State); Assert.Equal(before, children.Count);
        Assert.Contains("recorded as verified", LaunchDescriptions.Explain(store.Row(fourth.Intent.OperationId)!), StringComparison.Ordinal);
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
    }

    [Fact]
    public void TheBlockedRowExplanationSaysHowToTurnOrdinaryLaunchingOn()
    {
        string text = LaunchDescriptions.Explain(LaunchProblem.ProviderUnavailable);
        Assert.Contains("no client was started", text, StringComparison.Ordinal);
        Assert.Contains("recorded as verified", text, StringComparison.Ordinal);
        Assert.Contains("Settings → Roblox installation", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("verified trial")]
    [InlineData("ordinary launching")]
    [InlineData("group launch")]
    [InlineData("record this trial")]
    public void SettingsSearchFindsOrdinaryLaunchingAndTheControlsAndGuideExist(string query)
    {
        var entry = Assert.Single(SettingsSearchCatalog.Search(query), entry => entry.Id == "verified-launch");
        Assert.Equal(SettingsSearchTarget.Installations, entry.Target); Assert.Equal("Settings", SettingsSearchCatalog.PageOf(entry.Target));
        Assert.Contains("Off by default", entry.Description, StringComparison.Ordinal);
        Assert.Contains("Record this trial as verified", entry.Description, StringComparison.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(stream);
        var document = XDocument.Load(stream!);
        var installations = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "StackPanel"
            && e.Elements().Any(child => (string?)child.Attribute(X + "Name") == "SettingsInstallationsAnchor")));
        var status = Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "SettingsNativeLaunchVerification"));
        Assert.Equal("{Binding NativeLaunchVerificationStatus}", (string?)status.Attribute("Text")); Assert.Equal("True", (string?)status.Attribute("Focusable"));
        var forget = Assert.Single(installations.Descendants().Where(e => (string?)e.Attribute("Command") == "{Binding ForgetNativeLaunchVerification}"));
        Assert.Equal("{Binding CanForgetNativeLaunchVerification}", (string?)forget.Attribute("IsEnabled")); Assert.Equal("Forget verified trial", (string?)forget.Attribute("Content"));
        // The settings text no longer says the selected row is never used by ordinary launching.
        Assert.DoesNotContain(installations.Descendants().Where(e => e.Name.LocalName == "TextBlock"), e => ((string?)e.Attribute("Text") ?? "").Contains("Ordinary launching never uses the selected row", StringComparison.Ordinal));
        var record = Assert.Single(document.Descendants().Where(e => (string?)e.Attribute("Command") == "{Binding RecordNativeLaunchVerification}"));
        Assert.Equal("{Binding CanRecordNativeLaunchVerification}", (string?)record.Attribute("IsEnabled")); Assert.Equal("Record this trial as verified", (string?)record.Attribute("Content"));
        // The Accounts page note follows the gate instead of always saying launch is still being verified.
        Assert.Single(document.Descendants().Where(e => (string?)e.Attribute("Text") == "{Binding AccountLaunchAvailability}"));
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute("Text") == "Account-selecting launch is still being verified.");

        var guide = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"
            && ((string?)e.Attribute("Visibility"))?.Contains("IsGuide", StringComparison.Ordinal) == true));
        var texts = guide.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => ProductionWindowStrings.Resolve((string?)e.Attribute("Text"))).ToList();
        Assert.Contains(texts, text => text == "Ordinary launching after a verified trial");
        string section = Assert.Single(texts, text => text.StartsWith("Ordinary and group launch requests", StringComparison.Ordinal));
        foreach (string expected in new[] { "recorded blocked", "Record this trial as verified", "Forget verified trial", "settings.json", "no sign-in material",
            "one start at a time", "pauses the queue", "still confirmed only by its own log reports", "queue them again", "Off by default" })
            Assert.Contains(expected, section, StringComparison.Ordinal);
    }

    private static AccountProfile Profile(string name) => AccountProfile.Create(name) with { Revision = 1, PreferredPlaceId = 42 };
    private static ClientInstallationCandidate RobloxCandidate() => new(ClientInstallationProduct.Roblox, @"C:\Synthetic\Roblox\RobloxPlayerBeta.exe", "Roblox", "1",
        ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Roblox)]);
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, stop.Token);
    }

    private sealed class Source : INativeLaunchVerificationSource
    {
        public IReadOnlyList<NativeLaunchVerification>? Saved; public bool Throw; public int Reads; public readonly ConcurrentBag<ClientInstallationProduct> Products = [];
        public NativeLaunchVerification? Find(ClientInstallationProduct product)
        {
            Reads++; Products.Add(product);
            if (Throw) throw new IOException("Synthetic settings read failure: no secret here.");
            return NativeLaunchVerification.For(Saved, NativeLaunchMode.OfficialPage, product);
        }
    }
    private sealed class TrustOwner : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Child(int pid, Action? entered = null, Action? left = null) : IConfiguredNativeLaunchChild
    {
        public NativeLaunchMode? PreparedMode => NativeLaunchMode.OfficialPage;
        public Handle? Handle;
        public NativeLaunchExecutableIdentity? IdentityValue = Identity;
        public NativeLaunchExecutableIdentity? ExecutableIdentity => IdentityValue;
        public bool CleanupHealthy => true;
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); entered?.Invoke(); Handle = new(context, pid, left); return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.None, Handle)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handle(LaunchPreparationContext context, int pid, Action? left) : IPreparedAccountLaunch
    {
        private bool _closed; public int Dispatches;
        public LaunchPreparationContext Context => context;
        public LaunchIntent DispatchIntent => context.OriginalIntent;
        public bool IsExpired => _closed;
        public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        {
            Dispatches++;
            if (!boundary.IsCurrent || !await boundary.ValidateAsync(DispatchIntent, ct) || !boundary.ReadAdmission().Allowed) return new(ProviderLaunchStatus.RejectedBeforeStart);
            return new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: pid, ProcessStartUtcTicks: DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
        }
        public ValueTask DisposeAsync() { _closed = true; left?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class Store : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty;
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last);
        public LaunchOperation? Row(Guid id) => Last.Operations.FirstOrDefault(row => row.Intent.OperationId == id);
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Last);
        public Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct) { Volatile.Write(ref _last, snapshot); return Task.CompletedTask; }
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-launch-verification-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

// Shell integration: the record and forget commands over the real settings store, queue and evidence projection, with the same
// injected in-memory native child and monitor as the trial evidence tests. No executable, browser, process or real log is used.
public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task RecordingATrialAsVerifiedNeedsFreshMatchingEvidenceAndThenTurnsOrdinaryLaunchingOn() => OnUi(async () =>
    {
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, monitor);
        Assert.False(shell.IsNativeLaunchVerified); Assert.False(shell.CanRecordNativeLaunchVerification); Assert.False(shell.CanForgetNativeLaunchVerification);
        Assert.StartsWith("Ordinary launching: off", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        Assert.Equal("This Roblox installation needs a successful launch test. Choose a game and account, then use Test selected account launch on the launch review page. Signing in saves the account; it does not complete this test.", shell.AccountLaunchAvailability);
        Assert.False(shell.CanQueueTargetedLaunch);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        var evidence = await NativeEvidenceStartedAsync(shell);
        // A started client alone is not enough, and neither is a fresh process without a matching log report.
        Assert.False(shell.CanRecordNativeLaunchVerification);
        monitor.Observe(child.Receipt, NativeEvidenceMode.Fresh); shell.Refresh.Execute(null); await shell.Refresh.Execution;
        Assert.Equal(NativeTrialProcessPresence.Observed, shell.SelectedLaunchEvidence!.View!.Presence);
        Assert.False(shell.CanRecordNativeLaunchVerification);
        shell.RecordNativeLaunchVerification.Execute(null); await shell.RecordNativeLaunchVerification.Execution;
        Assert.False(shell.IsNativeLaunchVerified); Assert.DoesNotContain("NativeLaunchVerifications\":[{", await ReadSettingsAsync(), StringComparison.Ordinal);
        // The client's own log reports the expected player in the requested place: the evidence the user may accept.
        var now = DateTimeOffset.UtcNow;
        shell.RefreshLaunchEvidenceWithLogsForTest([new(Guid.NewGuid(), new(null, child.Receipt.ProcessId, child.Receipt.ProcessStartUtcTicks), (nint)100, ObservedSessionLifecycle.Connected,
            456, new(42, Guid.NewGuid(), null, now.AddMilliseconds(-200)), null, new(Guid.NewGuid(), 1), SessionEvidenceConfidence.UnverifiedDiagnostic,
            now.AddMilliseconds(-200), false, now.AddMilliseconds(-300), SessionLogCorrelationKind.ResourceHolderDiagnostic, now.AddSeconds(5))]);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, shell.SelectedLaunchEvidence!.View!.Outcome);
        Assert.True(shell.CanRecordNativeLaunchVerification);
        shell.RecordNativeLaunchVerification.Execute(null); await shell.RecordNativeLaunchVerification.Execution;
        Assert.True(shell.IsNativeLaunchVerified); Assert.False(shell.CanRecordNativeLaunchVerification); Assert.True(shell.CanForgetNativeLaunchVerification);
        Assert.True(shell.CanQueueTargetedLaunch);
        var selected=shell.SelectedAccount;selected!.IsChecked=false;shell.SelectedAccount=null;
        Assert.False(shell.CanQueueTargetedLaunch);
        selected.IsChecked=true;Assert.True(shell.CanQueueTargetedLaunch);selected.IsChecked=false;shell.SelectedAccount=selected;
        shell.ServerBrowser.LinkInput="pending destination";Assert.False(shell.CanQueueTargetedLaunch);
        shell.ServerBrowser.UsePreferredPlace(42);Assert.True(shell.CanQueueTargetedLaunch);
        Assert.False(shell.CanRunNativeTrial); // Ordinary launching replaces the trial while it is on.
        string settings = await ReadSettingsAsync();
        Assert.Contains("\"NativeLaunchVerifications\":[{\"Mode\":\"official-page\",\"Product\":\"Roblox\",", settings, StringComparison.Ordinal);
        Assert.Contains("\"OperationId\":\"" + evidence.Id + "\",\"ProcessId\":" + child.Receipt.ProcessId + ",\"ProcessStartUtcTicks\":" + child.Receipt.ProcessStartUtcTicks + ",\"UserId\":456,\"PlaceId\":42,\"Executable\":", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(EvidenceAlias, settings, StringComparison.Ordinal); Assert.DoesNotContain("synthetic-probe-original", settings, StringComparison.Ordinal);
        Assert.StartsWith("Ordinary launching: on", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        Assert.Contains("Official page", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal); Assert.Contains("Roblox", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        Assert.StartsWith("Account-selecting launch is on", shell.AccountLaunchAvailability, StringComparison.Ordinal);
        Assert.Contains("Ordinary launching is on", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.Contains("Recorded the trial as verified", shell.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Events, line => line.Contains("Operation failed", StringComparison.Ordinal));
        // The verification is for this mode: saving Direct ticket turns ordinary launching off until that mode is verified too.
        shell.SelectedNativeLaunchMode = ShellViewModel.NativeLaunchModeOptions.Single(option => option.Mode == NativeLaunchMode.DirectTicket);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.False(shell.IsNativeLaunchVerified); Assert.False(shell.CanForgetNativeLaunchVerification);
        Assert.False(shell.CanQueueTargetedLaunch);
        Assert.Contains("do not apply to this selection", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        shell.SelectedNativeLaunchMode = ShellViewModel.NativeLaunchModeOptions.Single(option => option.Mode == NativeLaunchMode.OfficialPage);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.True(shell.IsNativeLaunchVerified);
    });

    [Fact]
    public Task ASavedVerificationLetsACheckedAccountRequestStartThroughThePreparingProviderUntilItIsForgotten() => OnUi(async () =>
    {
        Directory.CreateDirectory(_root);
        var saved = new SessionsSettings(NativeLaunchVerifications: [new("official-page", "Roblox", DateTimeOffset.UtcNow, Guid.NewGuid(), 730009, DateTimeOffset.UtcNow.AddHours(-1).UtcTicks, 456, 42, NativeEvidenceIdentity)]).Validated();
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), JsonSerializer.Serialize(saved));
        var monitor = new NativeEvidenceMonitor(); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, monitor, trialAvailable: false);
        Assert.True(shell.IsNativeLaunchVerified); Assert.False(shell.CanRunNativeTrial); Assert.True(shell.CanForgetNativeLaunchVerification);
        Assert.StartsWith("Ordinary launching: on", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        // The group route: one review and one queue write, no per-account step, and the request is Queued rather than blocked.
        shell.SelectedAccount!.IsChecked = true;
        shell.QueueTargetedAccounts.Execute(null); await shell.QueueTargetedAccounts.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var launch = Assert.Single(shell.Launches);
        Assert.Contains("1 queued, 0 blocked", shell.Notice, StringComparison.Ordinal);
        Assert.NotEqual(LaunchProblem.ProviderUnavailable, launch.Operation.Problem);
        // The queue opens paused after a restart; Resume queue is the one explicit step, for the whole queue.
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
        shell.ToggleQueuePause.Execute(null); await shell.ToggleQueuePause.Execution;
        var evidence = await NativeEvidenceStartedAsync(shell);
        Assert.Equal(launch.Id, evidence.Id); Assert.Equal(1, child.Preparations); Assert.Equal(1, child.Starts);
        Assert.Equal(child.Receipt.ProcessId, evidence.Operation.ProcessReceipt!.ProcessId);
        Assert.False(evidence.View!.ArrivalVerified); Assert.Contains("unverified", evidence.Comparison, StringComparison.OrdinalIgnoreCase);
        // Forget: ordinary launching is off, the queue is paused and trials are available again.
        shell.ForgetNativeLaunchVerification.Execute(null); await shell.ForgetNativeLaunchVerification.Execution;
        Assert.False(shell.IsNativeLaunchVerified); Assert.False(shell.CanForgetNativeLaunchVerification); Assert.True(shell.CanRunNativeTrial);
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            while (shell.QueuePauseLabel != "Resume queue") await Task.Delay(5, stop.Token); // The queue's pause reaches the label on the next dispatcher pass.
        Assert.StartsWith("Ordinary launching: off", shell.NativeLaunchVerificationStatus, StringComparison.Ordinal);
        Assert.Contains("Ordinary launching is off", shell.Notice, StringComparison.Ordinal);
        Assert.Contains("\"NativeLaunchVerifications\":[]", await ReadSettingsAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Events, line => line.Contains("Operation failed", StringComparison.Ordinal));
    });

    private Task<string> ReadSettingsAsync() => File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
}
