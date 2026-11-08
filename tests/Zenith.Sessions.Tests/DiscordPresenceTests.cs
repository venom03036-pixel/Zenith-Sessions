using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Discord rich presence (R-api-17): an opt-in, off by default, that tells the Discord app running on this PC how many Roblox
// clients are running and, when chosen, which game, over Discord's own local named pipe (discord-ipc-0..9) with frames Zenith
// writes itself. It sends only when the content changes, stays silent when Discord is not running, and is cleared on Lock,
// Panic and exit. The publisher and the shell are driven through the real Windows pipe class against a test server that
// listens on a test-prefixed pipe; the Discord app itself is never run here.
public sealed class DiscordPresenceTests : IDisposable
{
    private const string AppId = "123456789012345678", OtherAppId = "987654321098765432";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-discord-presence-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Positive = TimeSpan.FromSeconds(30);

    [Fact]
    public Task AnEarlierPendingSettingsSaveCannotRestartDiscordPresenceAfterPanic() => OnDispatcherAsync(async () =>
    {
        await using var discord = new FakeDiscord();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(),
            discordPresencePipe: new WindowsDiscordPresencePipe(discord.Prefix));
        await vm.InitializeAsync();
        using (var held = new HeldPreferencesWrite(vm))
        {
            vm.DiscordPresence = true; vm.DiscordApplicationId = AppId; vm.SaveSettings.Execute(null);
            Assert.False(vm.SaveSettings.Execution.IsCompleted);
            vm.Panic.Execute(null);
        }
        await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordPresenceActive);
        Assert.Equal(0, discord.Connections);
    });

    [Fact]
    public void PresenceTextNamesTheCountAndTheGameFromLocalKnowledgeOnlyAndStaysBounded()
    {
        Assert.Equal("No clients running", PresenceText.Details(0));
        Assert.Equal("1 client running", PresenceText.Details(1));
        Assert.Equal("12 clients running", PresenceText.Details(12));
        Assert.Null(PresenceText.Game([], _ => "never"));
        Assert.Null(PresenceText.Game([0, -5], _ => "never"));
        // One place: its name when Zenith already knows one, else the place ID as elsewhere in Zenith; several places are counted.
        Assert.Equal("Playing Adopt Me!", PresenceText.Game([920587237, 920587237], place => place == 920587237 ? "Adopt Me!" : null));
        Assert.Equal("Playing Place 920587237", PresenceText.Game([920587237], _ => null));
        Assert.Equal("In 2 games", PresenceText.Game([1, 2, 1], _ => "x"));
        Assert.Equal("a b", PresenceText.NormalizeName(" a" + (char)7 + " b" + (char)0x2028 + " "));
        Assert.Null(PresenceText.NormalizeName("  "));
        Assert.Equal(PresenceText.MaximumGameNameLength + 1, PresenceText.NormalizeName(new string('x', 300))!.Length);
        Assert.InRange(PresenceText.Game([7], _ => new string('y', 300))!.Length, 1, PresenceText.MaximumLength);
        var activity = new PresenceActivity(2, "Playing X");
        Assert.Equal("2 clients running · Playing X", activity.Line);
        Assert.Equal("3 clients running", new PresenceActivity(3).Line);
        Assert.Equal(new PresenceActivity(2, "Playing X"), activity); // Value equality: the same content is not a change.
        Assert.NotEqual(new PresenceActivity(2), activity);
        Assert.Throws<ArgumentException>(() => new PresenceActivity(1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceActivity(-1));
        // The application ID is a 17 to 20 digit snowflake, or empty for none; the problem text never repeats the input.
        Assert.True(DiscordApplicationId.TryParse(" " + AppId + " ", out string id, out string problem)); Assert.Equal(AppId, id); Assert.Equal("", problem);
        Assert.True(DiscordApplicationId.TryParse(null, out id, out _)); Assert.Equal("", id);
        Assert.False(DiscordApplicationId.TryParse("secret-looking-value", out id, out problem)); Assert.Equal("", id);
        Assert.DoesNotContain("secret", problem, StringComparison.Ordinal); Assert.Contains("17 to 20 digits", problem, StringComparison.Ordinal);
        Assert.False(DiscordApplicationId.IsValid("1234567890123456")); Assert.False(DiscordApplicationId.IsValid("123456789012345678901"));
        Assert.True(DiscordApplicationId.IsValid("12345678901234567890"));
    }

    [Fact]
    public async Task FramesHaveLittleEndianHeadersBoundedPayloadsAndCarryOnlyTheCountAndGame()
    {
        byte[] frame = DiscordIpc.Encode(DiscordIpcOpcode.Handshake, "{}"u8);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, (byte)'{', (byte)'}' }, frame);
        Assert.True(DiscordIpc.TryDecodeHeader(frame, out var opcode, out int length)); Assert.Equal(DiscordIpcOpcode.Handshake, opcode); Assert.Equal(2, length);
        Assert.False(DiscordIpc.TryDecodeHeader([9, 0, 0, 0, 0, 0, 0, 0], out _, out _));   // An opcode Discord does not define.
        Assert.False(DiscordIpc.TryDecodeHeader([1, 0, 0, 0, 1, 0, 1, 0], out _, out _));   // 65,537 bytes: past the 64 KiB bound.
        Assert.False(DiscordIpc.TryDecodeHeader([1, 0, 0, 0, 0, 0, 0, 0xFF], out _, out _)); // A negative length.
        Assert.Throws<ArgumentException>(() => DiscordIpc.Encode(DiscordIpcOpcode.Frame, new byte[DiscordIpc.MaximumFrameLength + 1]));
        byte[] payload = DiscordIpc.SetActivityPayload(4242, "n-1", new PresenceActivity(2, "Playing Place 7"));
        var stream = new MemoryStream();
        await DiscordIpc.WriteFrameAsync(stream, DiscordIpcOpcode.Frame, payload, default);
        await DiscordIpc.WriteFrameAsync(stream, DiscordIpcOpcode.Ping, [], default);
        stream.Position = 0;
        var read = await DiscordIpc.ReadFrameAsync(stream, default);
        Assert.Equal(DiscordIpcOpcode.Frame, read.Opcode); Assert.Equal(payload, read.Payload);
        var ping = await DiscordIpc.ReadFrameAsync(stream, default);
        Assert.Equal(DiscordIpcOpcode.Ping, ping.Opcode); Assert.Empty(ping.Payload);
        await Assert.ThrowsAsync<EndOfStreamException>(() => DiscordIpc.ReadFrameAsync(stream, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscordIpc.ReadFrameAsync(new MemoryStream([1, 0, 0, 0, 1, 0, 1, 0, 1, 2, 3]), default));

        using (var json = JsonDocument.Parse(DiscordIpc.HandshakePayload(AppId)))
        {
            Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(AppId, json.RootElement.GetProperty("client_id").GetString());
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        }
        Assert.Throws<ArgumentException>(() => DiscordIpc.HandshakePayload("12"));
        using (var json = JsonDocument.Parse(payload))
        {
            Assert.Equal("SET_ACTIVITY", json.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("n-1", json.RootElement.GetProperty("nonce").GetString());
            var args = json.RootElement.GetProperty("args");
            Assert.Equal(4242, args.GetProperty("pid").GetInt32());
            var activity = args.GetProperty("activity");
            Assert.Equal("2 clients running", activity.GetProperty("details").GetString());
            Assert.Equal("Playing Place 7", activity.GetProperty("state").GetString());
            // Only the two lines: no assets, timestamps, party or anything Discord could show beyond the count and the game.
            Assert.Equal(2, activity.EnumerateObject().Count());
            Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        }
        using (var json = JsonDocument.Parse(DiscordIpc.SetActivityPayload(4242, "n-2", new PresenceActivity(0))))
            Assert.Single(json.RootElement.GetProperty("args").GetProperty("activity").EnumerateObject()); // No state line without a game.
        using (var json = JsonDocument.Parse(DiscordIpc.SetActivityPayload(4242, "n-3", null)))
            Assert.False(json.RootElement.GetProperty("args").TryGetProperty("activity", out _)); // A clear carries no activity.
        // Replies are read for cmd, evt, nonce and a code only: the READY payload's user and an error's message are dropped.
        var ready = DiscordIpc.ReadAnswer(Encoding.UTF8.GetBytes("{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1,\"user\":{\"username\":\"someone\"}},\"nonce\":null}"));
        Assert.Equal(new DiscordIpcAnswer("DISPATCH", "READY", null, null), ready);
        var refused = DiscordIpc.ReadAnswer(Encoding.UTF8.GetBytes("{\"code\":4000,\"message\":\"Invalid Client ID\"}"));
        Assert.Equal(new DiscordIpcAnswer(null, null, null, 4000), refused);
        var error = DiscordIpc.ReadAnswer(Encoding.UTF8.GetBytes("{\"cmd\":\"SET_ACTIVITY\",\"evt\":\"ERROR\",\"data\":{\"code\":4005,\"message\":\"bad\"},\"nonce\":\"n-1\"}"));
        Assert.Equal(new DiscordIpcAnswer("SET_ACTIVITY", "ERROR", "n-1", 4005), error);
        Assert.DoesNotContain("Invalid Client ID", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(new DiscordIpcAnswer(null, null, null, null), DiscordIpc.ReadAnswer([]));
        Assert.Throws<InvalidDataException>(() => DiscordIpc.ReadAnswer(Encoding.UTF8.GetBytes("{not json")));
        Assert.Throws<InvalidDataException>(() => DiscordIpc.ReadAnswer(Encoding.UTF8.GetBytes("[1]")));
    }

    [Fact]
    public async Task TheWindowsPipeListsOnlyOpenDiscordSlotsLowestFirstAndAnswersNullWhenNoneIsOpen()
    {
        string prefix = "zenith-test-" + Guid.NewGuid().ToString("N") + "-ipc-";
        var pipe = new WindowsDiscordPresencePipe(prefix);
        Assert.Empty(pipe.OpenSlots());
        Assert.Null(await pipe.ConnectAsync(default));
        using var seven = new NamedPipeServerStream(prefix + "7", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var three = new NamedPipeServerStream(prefix + "3", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.Equal([prefix + "3", prefix + "7"], pipe.OpenSlots());
        var accepted = three.WaitForConnectionAsync();
        await using (var stream = await pipe.ConnectAsync(default))
        {
            Assert.NotNull(stream);
            await accepted.WaitAsync(Positive);
            Assert.True(three.IsConnected, "The lowest open slot was not the one connected.");
        }
        Assert.Throws<ArgumentException>(() => new WindowsDiscordPresencePipe("bad prefix!"));
        Assert.Equal("discord-ipc-", WindowsDiscordPresencePipe.ProductionPrefix);
        Assert.Equal(10, WindowsDiscordPresencePipe.SlotCount);
    }

    [Fact]
    public async Task ThePublisherHandshakesPublishesOnChangeOnlyAnswersPingsAndClosesThePipeOnStop()
    {
        await using var discord = new FakeDiscord { PingAfterReady = true };
        var reports = new List<PresenceReport>();
        await using var publisher = new DiscordPresencePublisher(new WindowsDiscordPresencePipe(discord.Prefix), 4242, TimeSpan.FromMilliseconds(200));
        publisher.Reported += report => { lock (reports) reports.Add(report); };
        // Nothing runs, and nothing is kept, until Start (Rule 12).
        Assert.False(publisher.IsRunning); Assert.False(publisher.Publish(new(1)));
        publisher.Start(AppId); Assert.True(publisher.IsRunning); Assert.Equal(AppId, publisher.ApplicationId);
        await Task.Delay(150);
        Assert.Equal(0, discord.Connections); // No content yet: no connection.
        Assert.True(publisher.Publish(new(2, "Playing Place 77")));
        Assert.Equal("2 clients running · Playing Place 77", await discord.Shown(1));
        Assert.Equal(AppId, discord.ApplicationId); Assert.Equal(4242, discord.ProcessId);
        await WaitUntil(() => discord.Pongs >= 1); // Discord's ping after READY is answered.
        // The same content again sends nothing; a change sends once more.
        publisher.Publish(new(2, "Playing Place 77")); publisher.Publish(new(2, "Playing Place 77"));
        await Task.Delay(200);
        Assert.Single(discord.Activities);
        publisher.Publish(new(3));
        Assert.Equal("3 clients running", await discord.Shown(2));
        Assert.True(publisher.IsConnected);
        await WaitUntil(() => { lock (reports) return reports.Count(r => r.Status == PresenceStatus.Published) == 2; });
        lock (reports)
        {
            Assert.Equal(PresenceStatus.Connected, reports[0].Status);
            Assert.Equal(new PresenceActivity(2, "Playing Place 77"), reports[1].Activity);
            Assert.Equal(new PresenceActivity(3), reports[2].Activity);
        }
        // Stop closes the pipe: Discord sees the client go, which is how a status is cleared (Rule 11).
        publisher.Stop(); await publisher.Work.WaitAsync(Positive);
        Assert.False(publisher.IsRunning); Assert.False(publisher.IsConnected); Assert.Null(publisher.ApplicationId);
        await discord.WaitUntil(() => discord.Disconnections >= 1);
        Assert.Equal(1, discord.Connections);
        // Start again connects afresh and shows the current content at once; Dispose ends everything.
        publisher.Start(AppId); publisher.Publish(new(1));
        Assert.Equal("1 client running", await discord.Shown(3));
        Assert.Equal(2, discord.Connections);
        await publisher.DisposeAsync();
        Assert.True(publisher.Work.IsCompleted); Assert.False(publisher.IsRunning);
        await discord.WaitUntil(() => discord.Disconnections >= 2);
    }

    [Fact]
    public async Task WhenDiscordIsNotRunningNothingIsSentTheAbsenceIsReportedOnceAndTheStatusReturnsWhenItIsBack()
    {
        await using var discord = new FakeDiscord(listening: false);
        var reports = new List<PresenceReport>();
        var gap = TimeSpan.FromMilliseconds(300);
        await using var publisher = new DiscordPresencePublisher(new WindowsDiscordPresencePipe(discord.Prefix), 4242, gap);
        publisher.Reported += report => { lock (reports) reports.Add(report); };
        publisher.Start(AppId); publisher.Publish(new(1));
        await WaitUntil(() => { lock (reports) return reports.Any(r => r.Status == PresenceStatus.DiscordNotRunning); });
        await Task.Delay(gap * 3);
        lock (reports) { Assert.Single(reports); Assert.DoesNotContain(reports, r => r.Status == PresenceStatus.Failed); } // Once, not every gap.
        Assert.False(publisher.IsConnected);
        // Discord starts: within the next gap the status is shown without any change on Zenith's side.
        discord.Listen();
        Assert.Equal("1 client running", await discord.Shown(1));
        await WaitUntil(() => publisher.IsConnected);
        // Discord quits: the pending read sees the end of the stream at once; that is an absence, not a failure.
        await discord.QuitAsync();
        await WaitUntil(() => { lock (reports) return reports.Count(r => r.Status == PresenceStatus.DiscordNotRunning) == 2; });
        Assert.False(publisher.IsConnected);
        lock (reports) Assert.DoesNotContain(reports, r => r.Status == PresenceStatus.Failed);
        // Back again: the status returns on its own, and a change while it was away is what is shown.
        publisher.Publish(new(5));
        discord.Listen();
        Assert.Equal("5 clients running", await discord.Shown(2));
    }

    [Fact]
    public async Task ARefusedApplicationIdEndsThePublisherAndARefusedUpdateIsReportedWithoutAStorm()
    {
        await using var discord = new FakeDiscord { RefuseApplication = true };
        var reports = new List<PresenceReport>();
        await using var publisher = new DiscordPresencePublisher(new WindowsDiscordPresencePipe(discord.Prefix), 4242, TimeSpan.FromMilliseconds(200));
        publisher.Reported += report => { lock (reports) reports.Add(report); };
        publisher.Start(AppId); publisher.Publish(new(1));
        await WaitUntil(() => { lock (reports) return reports.Any(r => r.Status == PresenceStatus.HandshakeRefused); });
        lock (reports) Assert.Equal(DiscordIpcAnswer.InvalidApplicationCode, Assert.Single(reports, r => r.Status == PresenceStatus.HandshakeRefused).Code);
        await publisher.Work.WaitAsync(Positive);
        Assert.False(publisher.IsRunning); // Sending more would only repeat the refusal: the worker ends.
        Assert.Empty(discord.Activities);
        await Task.Delay(300);
        Assert.Equal(1, discord.Connections);
        // Discord accepts the application but refuses an update: reported with its code, the connection kept, nothing resent.
        discord.RefuseApplication = false; discord.RefuseUpdates = true;
        publisher.Start(AppId); publisher.Publish(new(1));
        await WaitUntil(() => { lock (reports) return reports.Any(r => r.Status == PresenceStatus.UpdateRefused); });
        lock (reports) Assert.Equal(4005, Assert.Single(reports, r => r.Status == PresenceStatus.UpdateRefused).Code);
        Assert.True(publisher.IsConnected);
        await Task.Delay(300);
        Assert.Single(discord.Activities);
        discord.RefuseUpdates = false; publisher.Publish(new(2));
        Assert.Equal("2 clients running", await discord.Shown(2));
        Assert.Equal(2, discord.Connections);
    }

    [Fact]
    public async Task PresenceIsOffByDefaultOlderFilesStayOffAndSavedChoicesSurviveLoadAndSave()
    {
        var defaults = new SessionsSettings();
        string json = JsonSerializer.Serialize(defaults);
        Assert.Contains("\"DiscordPresence\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"DiscordPresenceGame\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"DiscordApplicationId\":\"\"", json, StringComparison.Ordinal);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.DoesNotContain("\"DiscordPresence\":true", JsonSerializer.Serialize(loaded.Settings), StringComparison.Ordinal);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.DoesNotContain("\"DiscordPresence\":true", await File.ReadAllTextAsync(Primary), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DiscordPresence\":true,\"DiscordPresenceGame\":true,\"DiscordApplicationId\":\"" + AppId + "\"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            string saved = JsonSerializer.Serialize(loaded.Settings);
            Assert.Contains("\"DiscordPresence\":true", saved, StringComparison.Ordinal);
            Assert.Contains("\"DiscordPresenceGame\":true", saved, StringComparison.Ordinal);
            Assert.Contains("\"DiscordApplicationId\":\"" + AppId + "\"", saved, StringComparison.Ordinal);
            await store.SaveAsync(loaded.Settings, default);
        }
        string file = await File.ReadAllTextAsync(Primary);
        Assert.Contains("\"DiscordPresence\":true", file, StringComparison.Ordinal);
        Assert.Contains("\"DiscordApplicationId\":\"" + AppId + "\"", file, StringComparison.Ordinal);
        // An application ID is a Discord snowflake: 17 to 20 digits. Anything else is refused like every other invalid setting value.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"DiscordApplicationId\":\"not-a-snowflake\"}");
        File.Delete(Primary + ".bak");
        using (var store = new SettingsStore(_root))
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
    }

    [Fact]
    public void SettingsOffersThePresenceControlsAndTheGuideNamesThem()
    {
        var document = ProductionWindow();
        XElement? Named(string name) => document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        var option = Named("SettingsDiscordPresence");
        Assert.True(option is not null, "Settings has no control for the Discord status.");
        Assert.Equal("CheckBox", option!.Name.LocalName);
        Assert.Equal("{Binding DiscordPresence}", (string?)option.Attribute("IsChecked"));
        Assert.False(string.IsNullOrWhiteSpace((string?)option.Attribute("AutomationProperties.Name")));
        var game = Named("SettingsDiscordPresenceGame");
        Assert.True(game is not null, "Settings has no control for including the game in the Discord status.");
        Assert.Equal("CheckBox", game!.Name.LocalName);
        Assert.Equal("{Binding DiscordPresenceGame}", (string?)game.Attribute("IsChecked"));
        var id = Named("SettingsDiscordApplicationId");
        Assert.True(id is not null, "Settings has no field for the Discord application ID.");
        Assert.Equal("TextBox", id!.Name.LocalName);
        Assert.StartsWith("{Binding DiscordApplicationId", (string?)id.Attribute("Text"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace((string?)id.Attribute("AutomationProperties.Name")));
        var resume = Named("SettingsResumeDiscordPresence");
        Assert.Equal("{Binding ResumeDiscordPresence}", (string?)resume?.Attribute("Command"));
        Assert.Equal("{Binding CanResumeDiscordPresence}", (string?)resume?.Attribute("IsEnabled"));
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding DiscordPresenceStatus}");
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(option), appearance + 1, performance - 1);
        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Lock hides account controls", StringComparison.Ordinal) == true));
        Assert.Contains("Discord status", (string)guide.Attribute("Text")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rich presence")]
    [InlineData("discord status")]
    [InlineData("playing")]
    [InlineData("activity")]
    public void SettingsSearchFindsTheDiscordStatusInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "discord-presence");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the Discord status choice.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    [Fact]
    public Task NothingIsShownByDefaultAndASavedChoiceShowsTheClientCountLockClearsUnlockRestoresAndPanicClearsUntilResume() => OnDispatcherAsync(async () =>
    {
        await using var discord = new FakeDiscord();
        var monitor = new ClientMonitor();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, discordPresencePipe: new WindowsDiscordPresencePipe(discord.Prefix));
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        // Off by default: nothing runs, nothing connects, and the status says so.
        Assert.False(vm.DiscordPresence); Assert.False(vm.DiscordPresenceGame); Assert.Equal("", vm.DiscordApplicationId);
        Assert.False(vm.IsDiscordPresenceActive); Assert.Contains("Off", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        await RefreshFrame(vm);
        Assert.Equal(new PresenceActivity(1), vm.LatestPresence); // Computed from the scan even while off.
        Assert.Equal(0, discord.Connections);
        // A value that is not an application ID is refused at Save settings, without echoing it.
        vm.DiscordPresence = true; vm.DiscordApplicationId = "secret-looking-value";
        Assert.Equal("Unsaved settings", vm.SaveStatus);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.True(vm.HasErrorNotice, "An invalid application ID was accepted.");
        Assert.DoesNotContain("secret-looking-value", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.False(vm.IsDiscordPresenceActive);
        Assert.DoesNotContain("secret-looking-value", File.Exists(Primary) ? File.ReadAllText(Primary) : "", StringComparison.Ordinal);
        vm.DismissError.Execute(null);
        // On without an ID: saved, nothing shown, and the status says what to do.
        vm.DiscordApplicationId = ""; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.False(vm.IsDiscordPresenceActive); Assert.Contains("no Discord application ID", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        Assert.Equal(0, discord.Connections);
        // With an ID: the publisher starts and Discord shows the client count of the latest scan at once.
        vm.DiscordApplicationId = AppId; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.True(vm.IsDiscordPresenceActive, "Saving the Discord status on did not start the publisher.");
        Assert.Equal("1 client running", await discord.Shown(1));
        Assert.Equal(AppId, discord.ApplicationId); Assert.Equal(Environment.ProcessId, discord.ProcessId);
        await WaitUntilPumped(() => vm.DiscordPresenceStatus.Contains("Discord shows: 1 client running", StringComparison.Ordinal));
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.DiscordPresenceConnected);
        Assert.False(vm.HasErrorNotice);
        string file = File.ReadAllText(Primary);
        Assert.Contains("\"DiscordPresence\":true", file, StringComparison.Ordinal); Assert.Contains(AppId, file, StringComparison.Ordinal);
        // The same count on the next scans sends nothing; a second client sends once.
        await RefreshFrame(vm); await RefreshFrame(vm); await Task.Delay(200);
        Assert.Single(discord.Activities);
        monitor.Clients = 2; await RefreshFrame(vm);
        Assert.Equal("2 clients running", await discord.Shown(2));
        // Lock clears the status by closing the pipe; Unlock shows it again (monitoring continued meanwhile).
        vm.LockDashboard(); Assert.True(vm.IsLocked);
        Assert.False(vm.IsDiscordPresenceActive, "Lock left the Discord status running.");
        Assert.Contains("locked", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        await discord.WaitUntil(() => discord.Disconnections >= 1);
        vm.Unlock.Execute(null); Assert.False(vm.IsLocked);
        Assert.True(vm.IsDiscordPresenceActive, "Unlock did not show the Discord status again.");
        Assert.Equal("2 clients running", await discord.Shown(3));
        Assert.Equal(2, discord.Connections);
        // Panic clears it and keeps it cleared until Resume Discord status; the content keeps being computed meanwhile.
        vm.Panic.Execute(null);
        Assert.False(vm.IsDiscordPresenceActive, "Panic left the Discord status running.");
        Assert.True(vm.IsDiscordPresenceStopped); Assert.True(vm.CanResumeDiscordPresence);
        Assert.Contains("Panic", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        await discord.WaitUntil(() => discord.Disconnections >= 2);
        monitor.Clients = 3; await RefreshFrame(vm); await Task.Delay(200);
        Assert.Equal(3, discord.Activities.Count); // Nothing while stopped.
        Assert.Equal(new PresenceActivity(3), vm.LatestPresence);
        // Saving the choice unchanged does not lift a Panic stop; Resume does.
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordPresenceActive); Assert.True(vm.IsDiscordPresenceStopped);
        vm.ResumeDiscordPresence.Execute(null);
        Assert.True(vm.IsDiscordPresenceActive); Assert.False(vm.IsDiscordPresenceStopped);
        Assert.Equal("3 clients running", await discord.Shown(4));
        // Saved off: the pipe closes and nothing more is sent; the ID stays saved for next time.
        vm.DiscordPresence = false; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordPresenceActive); Assert.Contains("Off", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        Assert.Equal(AppId, vm.DiscordApplicationId);
        await discord.WaitUntil(() => discord.Disconnections >= 3);
        monitor.Clients = 4; await RefreshFrame(vm); await Task.Delay(200);
        Assert.Equal(4, discord.Activities.Count);
    });

    [Fact]
    public Task TheGameLineIsASavedChoiceAndNamesOnlyWhatZenithAlreadyKnows() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(), discordPresencePipe: new WindowsDiscordPresencePipe("zenith-test-" + Guid.NewGuid().ToString("N") + "-ipc-"));
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.Null(vm.PresenceGameName(920587237));
        vm.DestinationFavorites.Favorites.Add(new FavoriteDestination(Guid.NewGuid(), "Adopt Me!", 920587237));
        Assert.Equal("Adopt Me!", vm.PresenceGameName(920587237));
        Assert.Null(vm.PresenceGameName(1)); // Never a lookup: an unknown place stays unnamed and is labelled by its ID.
        Assert.Equal("Playing Adopt Me!", PresenceText.Game([920587237], vm.PresenceGameName));
        Assert.Equal("Playing Place 1", PresenceText.Game([1], vm.PresenceGameName));
        // Without the saved choice the scan computes no game line, whatever Zenith knows.
        await RefreshFrame(vm);
        Assert.Null(vm.LatestPresence.Game);
    });

    [Fact]
    public Task ARefusedApplicationIdStopsTheStatusUntilANewIdIsSavedAndAbsenceIsAStatusNotAnError() => OnDispatcherAsync(async () =>
    {
        await using var discord = new FakeDiscord(listening: false);
        var monitor = new ClientMonitor();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"DiscordPresence\":true,\"DiscordApplicationId\":\"" + AppId + "\"}");
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, discordPresencePipe: new WindowsDiscordPresencePipe(discord.Prefix));
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.True(vm.DiscordPresence); Assert.Equal(AppId, vm.DiscordApplicationId);
        Assert.True(vm.IsDiscordPresenceActive, "A saved Discord status choice did not start the publisher at startup.");
        await RefreshFrame(vm);
        // Discord is not running: the status says so, no error is shown and nothing is recorded.
        await WaitUntilPumped(() => vm.DiscordPresenceStatus.Contains("not running", StringComparison.Ordinal));
        Assert.False(vm.HasErrorNotice);
        Assert.DoesNotContain(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.OperationFailed);
        Assert.Equal(0, discord.Connections);
        // Discord starts, but does not know the application: the status stops, the error names the code, the ID is never echoed.
        discord.RefuseApplication = true; discord.Listen();
        monitor.Clients = 2; await RefreshFrame(vm); // A change is the retry.
        await WaitUntilPumped(() => vm.IsDiscordPresenceStopped);
        Assert.False(vm.IsDiscordPresenceActive);
        Assert.True(vm.HasErrorNotice, "A refused application ID was not shown as an error.");
        Assert.Contains("4000", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.DoesNotContain(AppId, vm.ErrorNotice, StringComparison.Ordinal);
        Assert.Contains("Stopped", vm.DiscordPresenceStatus, StringComparison.Ordinal);
        Assert.Equal("InvalidOperationException", Assert.Single(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordPresence).ErrorType);
        Assert.True(vm.CanResumeDiscordPresence);
        vm.DismissError.Execute(null);
        // Saving the same ID again does not retry on its own; a new ID answers the refusal.
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordPresenceActive); Assert.True(vm.IsDiscordPresenceStopped);
        discord.RefuseApplication = false;
        vm.DiscordApplicationId = OtherAppId; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.True(vm.IsDiscordPresenceActive, vm.ErrorNotice); Assert.False(vm.IsDiscordPresenceStopped);
        Assert.Equal("2 clients running", await discord.Shown(1));
        Assert.Equal(OtherAppId, discord.ApplicationId);
        await WaitUntilPumped(() => vm.DiscordPresenceStatus.Contains("Discord shows: 2 clients running", StringComparison.Ordinal));
        Assert.False(vm.HasErrorNotice);
    });

    [Fact]
    public Task ExitClosesThePipeAndSampleModeNeverConnects() => OnDispatcherAsync(async () =>
    {
        await using var discord = new FakeDiscord();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"DiscordPresence\":true,\"DiscordApplicationId\":\"" + AppId + "\"}");
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(), discordPresencePipe: new WindowsDiscordPresencePipe(discord.Prefix));
        try
        {
            await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice); Assert.True(vm.IsDiscordPresenceActive);
            await RefreshFrame(vm);
            Assert.Equal("1 client running", await discord.Shown(1));
        }
        finally { await vm.DisposeAsync(); }
        Assert.False(vm.IsDiscordPresenceActive);
        await discord.WaitUntil(() => discord.Disconnections >= 1);
        Assert.Equal(1, discord.Connections);

        await using var sample = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new ClientMonitor());
        await sample.InitializeAsync(); Assert.True(sample.IsReady, sample.Notice);
        Assert.Contains("sample", sample.DiscordPresenceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(sample.IsDiscordPresenceActive); Assert.False(sample.CanResumeDiscordPresence);
        sample.DiscordPresence = true; sample.DiscordApplicationId = AppId; sample.SaveSettings.Execute(null); await sample.SaveSettings.Execution;
        Assert.Equal("Settings saved", sample.SaveStatus); Assert.False(sample.IsDiscordPresenceActive);
        await Task.Delay(200);
        Assert.Equal(1, discord.Connections);
    });

    private static ManagerDiagnosticSnapshot Diagnostics(ShellViewModel shell)
        => ((ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics", Private)!.GetValue(shell)!).GetSnapshot();
    private static async Task RefreshFrame(ShellViewModel vm)
    {
        vm.Refresh.Execute(null); await vm.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Pump();
    }
    private static async Task Pump()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Positive) throw new TimeoutException("The awaited condition did not happen within " + Positive.TotalSeconds + " s.");
            await Task.Delay(20);
        }
    }
    /// <summary>Dispatcher thread: pumps the dispatcher while waiting, so reports hopped from the worker are applied.</summary>
    private static async Task WaitUntilPumped(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await Pump();
            if (condition()) return;
            if (watch.Elapsed > Positive) throw new TimeoutException("The awaited condition did not happen within " + Positive.TotalSeconds + " s.");
            await Task.Delay(20);
        }
    }
    private static XDocument ProductionWindow()
    {
        using var stream = typeof(DiscordPresenceTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    /// <summary>
    /// A stand-in for the Discord app's IPC server on a test-prefixed pipe: answers the handshake with READY (or a 4000 close when
    /// refusing the application), records every SET_ACTIVITY it is sent and answers it (or refuses it with 4005), answers pings
    /// and can ping first, and counts connections and disconnections. Listen and QuitAsync play Discord starting and quitting.
    /// </summary>
    internal sealed class FakeDiscord : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<string?> _activities = [];
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();
        private CancellationTokenSource? _listening;
        private Task _accepting = Task.CompletedTask;
        private int _connections, _disconnections, _pongs;
        public string Prefix { get; } = "zenith-test-" + Guid.NewGuid().ToString("N") + "-ipc-";
        public bool RefuseApplication { get; set; }
        public bool RefuseUpdates { get; set; }
        public bool PingAfterReady { get; set; }
        public string? ApplicationId { get; private set; }
        public int ProcessId { get; private set; }
        public int Connections => Volatile.Read(ref _connections);
        public int Disconnections => Volatile.Read(ref _disconnections);
        public int Pongs => Volatile.Read(ref _pongs);
        public IReadOnlyList<string?> Activities { get { lock (_gate) return [.. _activities]; } }

        public FakeDiscord(bool listening = true) { if (listening) Listen(); }

        /// <summary>Discord starts: the slot exists before this returns, so a listing made right after sees it.</summary>
        public void Listen()
        {
            if (_listening is not null) return;
            var stop = new CancellationTokenSource();
            var first = NewServer();
            _listening = stop;
            _accepting = Task.Run(() => AcceptLoopAsync(first, stop.Token));
        }
        /// <summary>Discord quits: the open connection and the slot go away.</summary>
        public async Task QuitAsync()
        {
            var stop = _listening; _listening = null;
            if (stop is null) return;
            stop.Cancel();
            await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            stop.Dispose();
        }
        // Real buffers, as Discord's own pipe has (libuv reserves 64 KiB each way): with the default zero sizes a pipe write waits
        // until the peer reads it, so a server pinging while the client writes would block both sides, which Discord never does.
        private NamedPipeServerStream NewServer()
            => new(Prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, DiscordIpc.MaximumFrameLength, DiscordIpc.MaximumFrameLength);

        private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken ct)
        {
            var server = first;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await server.WaitForConnectionAsync(ct); }
                    catch (OperationCanceledException) { return; }
                    Interlocked.Increment(ref _connections); _log.Enqueue("connected");
                    try { await ServeAsync(server, ct); _log.Enqueue("client left"); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error) { _log.Enqueue("serve ended: " + error.GetType().Name + " " + error.Message); }
                    finally { Interlocked.Increment(ref _disconnections); }
                    await server.DisposeAsync();
                    server = NewServer();
                }
            }
            finally { await server.DisposeAsync(); }
        }

        private async Task ServeAsync(Stream pipe, CancellationToken ct)
        {
            while (true)
            {
                DiscordIpcFrame frame;
                try { frame = await DiscordIpc.ReadFrameAsync(pipe, ct); }
                catch (EndOfStreamException) { _log.Enqueue("end of stream"); return; }
                _log.Enqueue("read " + frame.Opcode + " " + frame.Payload.Length + " bytes");
                switch (frame.Opcode)
                {
                    case DiscordIpcOpcode.Handshake:
                        using (var json = JsonDocument.Parse(frame.Payload)) ApplicationId = json.RootElement.GetProperty("client_id").GetString();
                        if (RefuseApplication)
                        {
                            await DiscordIpc.WriteFrameAsync(pipe, DiscordIpcOpcode.Close, Encoding.UTF8.GetBytes("{\"code\":4000,\"message\":\"Invalid Client ID\"}"), ct);
                            return;
                        }
                        await DiscordIpc.WriteFrameAsync(pipe, DiscordIpcOpcode.Frame, Encoding.UTF8.GetBytes("{\"cmd\":\"DISPATCH\",\"data\":{\"v\":1,\"user\":{\"username\":\"tester\"}},\"evt\":\"READY\",\"nonce\":null}"), ct);
                        if (PingAfterReady) await DiscordIpc.WriteFrameAsync(pipe, DiscordIpcOpcode.Ping, Encoding.UTF8.GetBytes("{\"nonce\":\"ping-1\"}"), ct);
                        break;
                    case DiscordIpcOpcode.Frame:
                        string nonce;
                        using (var json = JsonDocument.Parse(frame.Payload))
                        {
                            var root = json.RootElement;
                            if (root.GetProperty("cmd").GetString() != "SET_ACTIVITY") throw new InvalidDataException("Unexpected command.");
                            nonce = root.GetProperty("nonce").GetString() ?? "";
                            var args = root.GetProperty("args");
                            ProcessId = args.GetProperty("pid").GetInt32();
                            string? shown = null;
                            if (args.TryGetProperty("activity", out var activity))
                            {
                                shown = activity.GetProperty("details").GetString();
                                if (activity.TryGetProperty("state", out var state)) shown += " · " + state.GetString();
                            }
                            lock (_gate) _activities.Add(shown);
                        }
                        string reply = RefuseUpdates
                            ? "{\"cmd\":\"SET_ACTIVITY\",\"data\":{\"code\":4005,\"message\":\"bad activity\"},\"evt\":\"ERROR\",\"nonce\":\"" + nonce + "\"}"
                            : "{\"cmd\":\"SET_ACTIVITY\",\"data\":{\"name\":\"Zenith Sessions\"},\"evt\":null,\"nonce\":\"" + nonce + "\"}";
                        await DiscordIpc.WriteFrameAsync(pipe, DiscordIpcOpcode.Frame, Encoding.UTF8.GetBytes(reply), ct);
                        break;
                    case DiscordIpcOpcode.Ping:
                        await DiscordIpc.WriteFrameAsync(pipe, DiscordIpcOpcode.Pong, frame.Payload, ct);
                        break;
                    case DiscordIpcOpcode.Pong:
                        Interlocked.Increment(ref _pongs);
                        break;
                    case DiscordIpcOpcode.Close:
                        return;
                }
            }
        }

        public async Task<string?> Shown(int count)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                lock (_gate) if (_activities.Count >= count) return _activities[count - 1];
                if (watch.Elapsed > Positive) throw new TimeoutException($"Only {Activities.Count} of {count} expected status updates reached the test Discord within {Positive.TotalSeconds} s. Server log: {Log}");
                await Task.Delay(10);
            }
        }
        /// <summary>What the server saw, oldest first, for a failing test's message.</summary>
        public string Log => string.Join(" | ", _log);
        public Task WaitUntil(Func<bool> condition) => DiscordPresenceTests.WaitUntil(condition);
        public async ValueTask DisposeAsync() => await QuitAsync();
    }

    /// <summary>Synthetic clients: as many as <see cref="Clients"/> says, each its own process (the real monitor's shape).</summary>
    private sealed class ClientMonitor : IDesktopMonitor
    {
        private readonly long _started = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        private int _clients = 1;
        private long _revision = 1;
        public int Clients { get => _clients; set { _clients = value; _revision++; } }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            var sessions = Enumerable.Range(0, _clients)
                .Select(i => new SessionObservation(10001 + i, _started, "Synthetic", 1, "Synthetic", false, true, 1, 1, null, now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20),
                sessions, ProcessObservedUtc: now, ProcessRevision: _revision, ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-discord-presence-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
