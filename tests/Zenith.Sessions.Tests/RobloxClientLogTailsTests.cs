using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Exercises the real tail reader against synthetic Player logs in a temporary trusted anchor. It never touches the current
/// user's Roblox folders. The lines are the synthetic marker shapes the incremental reader is tested with, plus text no
/// support report may ever carry: a game's chat line with a URL and a token, a cookie-like print, an address after a join.
/// </summary>
public sealed class RobloxClientLogTailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithClientLogTails-" + Guid.NewGuid().ToString("N"));
    private string Anchor => Path.Combine(_root, "anchor");
    private string Win32 => Path.Combine(Anchor, "Roblox", "logs");
    private string Uwp => Path.Combine(Anchor, "Packages", "synthetic", "LocalState", "logs");
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public RobloxClientLogTailsTests() { Directory.CreateDirectory(Win32); Directory.CreateDirectory(Uwp); }
    public void Dispose() => Directory.Delete(_root, true);

    private static string Joining(long place, Guid? job = null) => SyntheticClientLogFixtures.Line("FLog::Output",
        $"! Joining game '{(job ?? SyntheticClientLogFixtures.Job):D}' place {place} at synthetic-address 10.0.0.1");

    private static string Synthetic(string folder, string name, string content, TimeSpan age)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private RobloxClientLogTails Tails() => new([(Win32, ClientLogVariant.Win32), (Uwp, ClientLogVariant.Uwp)], Anchor);

    [Fact]
    public async Task TailsKeepOnlyRecognisedShapesAsTypedFieldsAndNeverRawText()
    {
        const string secret = "SECRET-TOKEN-4711";
        string log = Joining(SyntheticClientLogFixtures.Place)
            + SyntheticClientLogFixtures.Identity
            + SyntheticClientLogFixtures.Line("FLog::Network", "Replicator created")
            + SyntheticClientLogFixtures.Line("FLog::Output", $"chat: visit https://example.invalid/claim?ticket={secret} now")
            + SyntheticClientLogFixtures.Line("FLog::Output", "print .ROBLOSECURITY=_|WARNING:-DO-NOT-SHARE|_synthetic")
            + "a line with no timestamp at all\n"
            + SyntheticClientLogFixtures.Line("FLog::Network", "Disconnection Notification. Reason: 267");
        Synthetic(Win32, "0.745_20260108T120000Z_Player_AAAAA_last.log", log, TimeSpan.FromMinutes(5));
        Synthetic(Win32, "0.745_20260108T120000Z_Studio_BBBBB_last.log", Joining(77), TimeSpan.FromMinutes(1));
        Synthetic(Uwp, "0.745_20260108T120000Z_Player_CCCCC_CrashHandler_last.log", Joining(78), TimeSpan.FromMinutes(1));
        Synthetic(Uwp, "0.745_20260106T120000Z_Player_DDDDD_last.log", Joining(79), TimeSpan.FromDays(2));

        var report = await Tails().ReadAsync(CancellationToken.None);

        Assert.Equal(2, report.FoldersChecked);
        Assert.Equal(0, report.FoldersUnreadable);
        Assert.Equal(1, report.LogsFound);
        Assert.False(report.BudgetLimited);
        var tail = Assert.Single(report.Logs);
        Assert.Equal(1, tail.Order);
        Assert.False(tail.StorePackage);
        Assert.Equal(ClientLogTailOutcome.Read, tail.Outcome);
        Assert.Equal(7, tail.LinesRead);
        Assert.Equal(0, tail.EntriesDropped);
        Assert.Equal(Encoding.UTF8.GetByteCount(log), tail.BytesRead);
        Assert.Equal(
            [ClientLogFact.Joining, ClientLogFact.UserIdentityObserved, ClientLogFact.Connected, ClientLogFact.Disconnected],
            tail.Entries.Select(entry => entry.Fact).ToArray());
        Assert.Equal(SyntheticClientLogFixtures.Place, tail.Entries[0].PlaceId);
        Assert.Equal(SyntheticClientLogFixtures.Job, tail.Entries[0].JobId);
        Assert.Equal(267, tail.Entries[3].DisconnectReason);
        Assert.All(tail.Entries, entry => Assert.Equal(SyntheticClientLogFixtures.Time, entry.LoggedUtc));

        // The whole report, serialized the way the support report serializes it, carries none of the text and no identity.
        string serialized = JsonSerializer.Serialize(report, Json);
        foreach (string forbidden in new[] { "synthetic-address", "10.0.0.1", secret, "https", "ROBLOSECURITY", "chat", "AAAAA", "Player_", _root,
                     SyntheticClientLogFixtures.User.ToString(System.Globalization.CultureInfo.InvariantCulture), "\"UserId\"" })
            Assert.DoesNotContain(forbidden, serialized);
    }

    [Fact]
    public async Task OnlyTheNewestLogsAreIncludedNewestFirst()
    {
        for (int index = 0; index < RobloxClientLogTails.MaximumLogs + 2; index++)
            Synthetic(index % 2 == 0 ? Win32 : Uwp, $"0.745_2026_Player_{index:D2}_last.log", Joining(1000 + index), TimeSpan.FromMinutes(index + 1));

        var report = await Tails().ReadAsync(CancellationToken.None);

        Assert.Equal(RobloxClientLogTails.MaximumLogs + 2, report.LogsFound);
        Assert.Equal(RobloxClientLogTails.MaximumLogs, report.Logs.Count);
        Assert.Equal(Enumerable.Range(1, RobloxClientLogTails.MaximumLogs), report.Logs.Select(log => log.Order));
        Assert.Equal(Enumerable.Range(0, RobloxClientLogTails.MaximumLogs).Select(index => 1000L + index), report.Logs.Select(log => log.Entries.Single().PlaceId!.Value));
        Assert.Equal(report.Logs.Select(log => log.ModifiedUtc).OrderByDescending(time => time), report.Logs.Select(log => log.ModifiedUtc));
        Assert.Equal([false, true, false, true, false, true, false, true], report.Logs.Select(log => log.StorePackage));
    }

    [Fact]
    public async Task OnlyTheLastBytesAreReadAndAPartialFirstLineIsSkipped()
    {
        var text = new StringBuilder(Joining(1));
        string filler = SyntheticClientLogFixtures.Line("FLog::Output", new string('f', 60));
        while (text.Length < RobloxClientLogTails.TailBytes + 4096) text.Append(filler);
        text.Append(Joining(2)).Append(Joining(3)).Append(Joining(4));
        Synthetic(Win32, "0.745_2026_Player_BIG_last.log", text.ToString(), TimeSpan.FromMinutes(1));

        var tail = Assert.Single((await Tails().ReadAsync(CancellationToken.None)).Logs);

        Assert.Equal(RobloxClientLogTails.TailBytes, tail.BytesRead);
        Assert.Equal(Encoding.UTF8.GetByteCount(text.ToString()), tail.Length);
        Assert.Equal([2L, 3L, 4L], tail.Entries.Select(entry => entry.PlaceId!.Value));
        Assert.InRange(tail.LinesRead, 3, RobloxClientLogTails.TailBytes / filler.Length + 3);
    }

    [Fact]
    public async Task OnlyTheNewestEntriesPerLogAreKeptAndTheDropCountSaysHowMany()
    {
        var text = new StringBuilder();
        for (int place = 1; place <= RobloxClientLogTails.MaximumEntriesPerLog + 50; place++) text.Append(Joining(place));
        Synthetic(Win32, "0.745_2026_Player_MANY_last.log", text.ToString(), TimeSpan.FromMinutes(1));

        var tail = Assert.Single((await Tails().ReadAsync(CancellationToken.None)).Logs);

        Assert.Equal(RobloxClientLogTails.MaximumEntriesPerLog, tail.Entries.Count);
        Assert.Equal(50, tail.EntriesDropped);
        Assert.Equal(51, tail.Entries[0].PlaceId);
        Assert.Equal(RobloxClientLogTails.MaximumEntriesPerLog + 50, tail.Entries[^1].PlaceId);
    }

    [Fact]
    public async Task AHeldLogIsListedWithItsOutcomeAndAStopEndsTheRead()
    {
        string held = Synthetic(Win32, "0.745_2026_Player_HELD_last.log", Joining(5), TimeSpan.FromMinutes(1));
        Synthetic(Win32, "0.745_2026_Player_FREE_last.log", Joining(6), TimeSpan.FromMinutes(2));
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await Tails().ReadAsync(CancellationToken.None);
            Assert.Equal(2, report.Logs.Count);
            Assert.Equal(ClientLogTailOutcome.ReadFailed, report.Logs[0].Outcome);
            Assert.Empty(report.Logs[0].Entries);
            Assert.Equal(0, report.Logs[0].BytesRead);
            Assert.Equal(ClientLogTailOutcome.Read, report.Logs[1].Outcome);
            Assert.Equal(6, report.Logs[1].Entries.Single().PlaceId);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tails().ReadAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task MissingFoldersAreCountedNotFailed()
    {
        var tails = new RobloxClientLogTails([(Path.Combine(Anchor, "absent", "logs"), ClientLogVariant.Win32)], Anchor);
        var report = await tails.ReadAsync(CancellationToken.None);
        Assert.Equal(0, report.FoldersChecked);
        Assert.Equal(0, report.LogsFound);
        Assert.Empty(report.Logs);
    }

    [Fact]
    public void RecogniseReducesALineToItsParsedFieldsAndDropsThePlayerId()
    {
        var now = DateTimeOffset.UtcNow;
        var joining = RobloxClientLogTails.Recognize(Joining(42).TrimEnd('\n'), now);
        Assert.NotNull(joining);
        Assert.Equal(ClientLogFact.Joining, joining.Fact);
        Assert.Equal(42, joining.PlaceId);
        Assert.Equal(SyntheticClientLogFixtures.Job, joining.JobId);
        Assert.Null(joining.DisconnectReason);
        var identity = RobloxClientLogTails.Recognize(SyntheticClientLogFixtures.Identity.TrimEnd('\n'), now);
        Assert.NotNull(identity);
        Assert.Equal(ClientLogFact.UserIdentityObserved, identity.Fact);
        Assert.Null(identity.PlaceId); Assert.Null(identity.JobId); Assert.Null(identity.DisconnectReason);
        Assert.Null(RobloxClientLogTails.Recognize(SyntheticClientLogFixtures.Line("FLog::Output", "visit https://example.invalid/?t=1").TrimEnd('\n'), now));
        Assert.Null(RobloxClientLogTails.Recognize("no timestamp [FLog::Network] Replicator created", now));
    }
}
