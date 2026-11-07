using System.IO;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class IncrementalClientLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithSyntheticLogTests-" + Guid.NewGuid().ToString("N"));
    private string Log => Path.Combine(_root, "synthetic-player.log");
    public IncrementalClientLogReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CompleteLinesHaveByteOffsetsLongIdsAndNoDuplicateNotificationReplay()
    {
        string text = SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity + SyntheticClientLogFixtures.Connected;
        await File.WriteAllTextAsync(Log, text, new UTF8Encoding(false));
        using var reader = new IncrementalClientLogReader(Log);
        var batch = await reader.ReadAsync(default);
        Assert.Equal(ClientLogReadStatus.Ready, batch.Status);
        Assert.Equal(ClientLogDiscontinuity.Initial, batch.Discontinuity);
        Assert.Equal(3, batch.Observations.Count);
        Assert.Equal(SyntheticClientLogFixtures.Place, batch.Observations[0].PlaceId);
        Assert.Equal(SyntheticClientLogFixtures.Job, batch.Observations[0].JobId);
        Assert.Equal(SyntheticClientLogFixtures.User, batch.Observations[1].UserId);
        Assert.Equal(SyntheticClientLogFixtures.Time, batch.Observations[0].LoggedUtc);
        Assert.All(batch.Observations, item => Assert.Equal(ClientLogFormatSupport.Unverified, item.FormatSupport));
        Assert.Equal(0, batch.Observations[0].StartOffset);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), batch.CompleteLineOffset);
        Assert.Equal(batch.Observations[0].EndOffset, batch.Observations[1].StartOffset);
        var duplicate = await reader.ReadAsync(default);
        Assert.Empty(duplicate.Observations); Assert.Equal(batch.ReadOffset, duplicate.ReadOffset);
    }

    [Fact]
    public async Task PartialUtf8AndCrLfAreRetainedUntilTheCompleteLineArrives()
    {
        string line = SyntheticClientLogFixtures.Joining.TrimEnd('\n') + " 😀\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        int split = Array.IndexOf(bytes, (byte)0xF0) + 2;
        await File.WriteAllBytesAsync(Log, bytes[..split]);
        using var reader = new IncrementalClientLogReader(Log);
        var first = await reader.ReadAsync(default);
        Assert.Empty(first.Observations); Assert.Equal(0, first.CompleteLineOffset);
        await AppendAsync(bytes[split..]);
        var second = await reader.ReadAsync(default);
        Assert.Single(second.Observations); Assert.Equal(0, second.RejectedLines);
        Assert.Equal(bytes.Length, second.CompleteLineOffset); Assert.Equal(first.Stream, second.Stream);
    }

    [Fact]
    public async Task ByteAndResultBudgetsResumeWithoutSkippingFetchedLines()
    {
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity, new UTF8Encoding(false));
        using var reader = new IncrementalClientLogReader(Log, new(MaximumBytesPerRead: 17, MaximumObservationsPerRead: 1));
        var found = new List<ClientLogObservation>(); long previous = 0; bool more;
        do
        {
            var batch = await reader.ReadAsync(default);
            Assert.InRange(batch.ReadOffset - previous, 0, 17); previous = batch.ReadOffset;
            Assert.InRange(batch.Observations.Count, 0, 1); found.AddRange(batch.Observations); more = batch.HasMore;
        } while (more);
        Assert.Equal(2, found.Count);
        using var capped = new IncrementalClientLogReader(Log, new(MaximumObservationsPerRead: 1));
        var first = await capped.ReadAsync(default); var second = await capped.ReadAsync(default);
        Assert.Single(first.Observations); Assert.Single(second.Observations);
        Assert.Equal(first.CompleteLineOffset, second.Observations[0].StartOffset);
    }

    [Fact]
    public async Task OversizedAndInvalidUtf8LinesAreDiscardedWithoutLosingTheNextCompleteLine()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 9000) + "\n").Concat(new byte[] { 0xC3, 0x28, 10 })
            .Concat(Encoding.UTF8.GetBytes(SyntheticClientLogFixtures.Joining)).ToArray();
        await File.WriteAllBytesAsync(Log, bytes);
        using var reader = new IncrementalClientLogReader(Log);
        var batch = await reader.ReadAsync(default);
        Assert.Equal(2, batch.RejectedLines); Assert.Single(batch.Observations);
        Assert.Equal(bytes.Length, batch.ReadOffset);
    }

    [Fact]
    public async Task TruncationSameLengthRewriteAndPathReplacementStartNewGenerations()
    {
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Joining, new UTF8Encoding(false));
        using var reader = new IncrementalClientLogReader(Log);
        var original = await reader.ReadAsync(default);
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Connected, new UTF8Encoding(false));
        var truncated = await reader.ReadAsync(default);
        Assert.Equal(ClientLogDiscontinuity.TruncatedOrRewritten, truncated.Discontinuity);
        Assert.True(truncated.Stream.Generation > original.Stream.Generation);
        string rewrite = SyntheticClientLogFixtures.Connected.Replace("synthetic", "different", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Log, rewrite, new UTF8Encoding(false));
        var rewritten = await reader.ReadAsync(default);
        Assert.Equal(ClientLogDiscontinuity.TruncatedOrRewritten, rewritten.Discontinuity);
        Assert.True(rewritten.Stream.Generation > truncated.Stream.Generation);
        File.Move(Log, Path.Combine(_root, "synthetic-rotated.log"));
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Identity, new UTF8Encoding(false));
        var replaced = await reader.ReadAsync(default);
        Assert.Equal(ClientLogDiscontinuity.Replaced, replaced.Discontinuity);
        Assert.True(replaced.Stream.Generation > rewritten.Stream.Generation);
        Assert.Equal(0, Assert.Single(replaced.Observations).StartOffset);
    }

    [Fact]
    public async Task DelayedCreationAndRotationDoNotJoinOldPartialBytesToNewFile()
    {
        using var reader = new IncrementalClientLogReader(Log);
        Assert.Equal(ClientLogReadStatus.Missing, (await reader.ReadAsync(default)).Status);
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Joining.TrimEnd('\n'), new UTF8Encoding(false));
        var partial = await reader.ReadAsync(default); Assert.Empty(partial.Observations);
        File.Delete(Log);
        var missing = await reader.ReadAsync(default); Assert.Equal(ClientLogReadStatus.Missing, missing.Status);
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Connected, new UTF8Encoding(false));
        var replacement = await reader.ReadAsync(default);
        Assert.Equal(ClientLogFact.Connected, Assert.Single(replacement.Observations).Fact);
        Assert.True(replacement.Stream.Generation > partial.Stream.Generation);
    }

    [Fact]
    public async Task PermissionSharingCancellationAndExpiredDeadlineHaveTypedBoundedOutcomes()
    {
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Identity);
        using var reader = new IncrementalClientLogReader(Log);
        using (var locked = new FileStream(Log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(ClientLogReadStatus.IoFailure, (await reader.ReadAsync(default)).Status);
        using var directoryReader = new IncrementalClientLogReader(_root);
        Assert.Equal(ClientLogReadStatus.AccessDenied, (await directoryReader.ReadAsync(default)).Status);
        using var expired = new IncrementalClientLogReader(Log, new(Deadline: TimeSpan.Zero));
        var deadline = await expired.ReadAsync(default);
        Assert.Equal(ClientLogReadStatus.DeadlineExceeded, deadline.Status); Assert.Equal(0, deadline.ReadOffset);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        Assert.Single((await reader.ReadAsync(default)).Observations);
    }

    [Fact]
    public async Task OnlyFirstAllowlistedTagsEmitTypedValuesAndSecretTextNeverEscapes()
    {
        const string secret = "SYNTHETIC_ONLY_SECRET_DO_NOT_EXPORT";
        string text = SyntheticClientLogFixtures.Line("FLog::Output", "[FLog::Network] Lost connection with reason " + secret)
            + SyntheticClientLogFixtures.Line("FLog::Warning", "[FLog::GameJoinLoadTime] Report game_join_loadtime: userid:999")
            + SyntheticClientLogFixtures.Line("FLog::Network", "gameinfo:ticket=" + secret)
            + SyntheticClientLogFixtures.Joining.TrimEnd('\n') + " ticket=" + secret + "\n"
            + SyntheticClientLogFixtures.Teleporting + SyntheticClientLogFixtures.Disconnected;
        await File.WriteAllTextAsync(Log, text, new UTF8Encoding(false));
        using var reader = new IncrementalClientLogReader(Log);
        var batch = await reader.ReadAsync(default);
        Assert.Equal(new[] { ClientLogFact.Joining, ClientLogFact.Teleporting, ClientLogFact.Disconnected }, batch.Observations.Select(o => o.Fact));
        string exported = JsonSerializer.Serialize(batch);
        Assert.DoesNotContain(secret, exported); Assert.DoesNotContain("ticket=", exported); Assert.DoesNotContain(Log, exported);
    }

    [Fact]
    public async Task DisconnectMarkersKeepOnlyABoundedReasonCodeAndHumanChecksIgnoreGameWritableTags()
    {
        const string secret = "SYNTHETIC_KICK_TEXT_DO_NOT_EXPORT";
        string[] lines =
        [
            SyntheticClientLogFixtures.Line("FLog::Network", "Disconnection Notification. Reason: 267"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Time to disconnect replication data: 0.250000"),
            SyntheticClientLogFixtures.Line("DFLog::RbxTransportDummyClient", "Disconnected from server for reason: Player: 285 (SyntheticName)"),
            SyntheticClientLogFixtures.Line("DFLog::RbxTransportDummyClient", "Connection closed by server for reason: RCC: 288 (SyntheticName)"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Sending disconnect with reason: 273"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Lost connection with reason : " + secret),
            SyntheticClientLogFixtures.Line("FLog::Network", "Disconnection Notification. Reason: 2851"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Sending disconnect with reason: 99999"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Sending disconnect with reason: 285x"),
            SyntheticClientLogFixtures.Line("FLog::SyntheticChannel", "join status: challengedByGcs"),
            // Spoofs: game-writable channels, a second tag inside the body, a marker under the wrong tag, a partial token.
            SyntheticClientLogFixtures.Line("FLog::Output", "Disconnected from server for reason: Player: 285"),
            SyntheticClientLogFixtures.Line("FLog::Output", "[FLog::Network] Disconnection Notification. Reason: 285"),
            SyntheticClientLogFixtures.Line("FLog::Warning", "[DFLog::RbxTransportDummyClient] Disconnected from server for reason: 285"),
            SyntheticClientLogFixtures.Line("FLog::Network", "Disconnected from server for reason: Player: 285"),
            SyntheticClientLogFixtures.Line("FLog::Output", "challengedByGcs"),
            SyntheticClientLogFixtures.Line("FLog::Warning", "status: challengedByGcs"),
            SyntheticClientLogFixtures.Line("FLog::Error", "status: challengedByGcs"),
            SyntheticClientLogFixtures.Line("FLog::SyntheticChannel", "status: notchallengedByGcsYet"),
        ];
        await File.WriteAllTextAsync(Log, string.Concat(lines), new UTF8Encoding(false));
        using var reader = new IncrementalClientLogReader(Log);
        var batch = await reader.ReadAsync(default);

        Assert.Equal(Enumerable.Repeat(ClientLogFact.Disconnected, 9).Append(ClientLogFact.HumanCheck), batch.Observations.Select(o => o.Fact));
        Assert.Equal(new int?[] { 267, null, 285, 288, 273, null, 2851, null, null, null }, batch.Observations.Select(o => o.DisconnectReason));
        Assert.Equal(new[] { false, false, true, false, true, false, false, false, false, false }, batch.Observations.Select(o => o.PlayerInitiated));
        Assert.All(batch.Observations, o => { Assert.Null(o.UserId); Assert.Null(o.PlaceId); Assert.Null(o.JobId); Assert.Equal(SyntheticClientLogFixtures.Time, o.LoggedUtc); });
        string exported = JsonSerializer.Serialize(batch);
        Assert.DoesNotContain(secret, exported); Assert.DoesNotContain("challengedByGcs", exported); Assert.DoesNotContain("Synthetic", exported);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("123evil")]
    public async Task InvalidIdsCannotBecomeObservedIdentity(string number)
    {
        await File.WriteAllTextAsync(Log, SyntheticClientLogFixtures.Line("FLog::GameJoinLoadTime", "Report game_join_loadtime: userid:" + number));
        using var reader = new IncrementalClientLogReader(Log);
        Assert.Empty((await reader.ReadAsync(default)).Observations);
    }

    private async Task AppendAsync(byte[] bytes)
    {
        await using var stream = new FileStream(Log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(bytes);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
