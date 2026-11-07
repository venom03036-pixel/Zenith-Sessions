using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// SANITIZED historical syntax, not an untouched capture or a real-process identity fixture.
// Source version 0.745.0.7451137; all source values/attributes were discarded by the bounded sanitizer.
public sealed class SanitizedClientLogFixtureTests : IDisposable
{
    private const string FixtureHash = "53E83F0F6DD5E5AF43A0F06FAD8FD0F7C9AAEDE217D16493DFF7265E1DA776C5";
    private const long SyntheticPlace = 900_000_000_000_000_001, SyntheticUser = 900_000_000_000_000_002;
    private static readonly DateTimeOffset SyntheticTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithSanitizedLogTests-" + Guid.NewGuid().ToString("N"));
    private string Log => Path.Combine(_root, "sanitized-player.log");
    public SanitizedClientLogFixtureTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FixtureFingerprintAndClosedSyntaxExcludeAllUnapprovedValues()
    {
        byte[] bytes = Fixture();
        Assert.Equal(13_974, bytes.Length);
        Assert.Equal(FixtureHash, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.All(bytes, value => Assert.True(value == 10 || value is >= 32 and <= 126));
        string text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("\n", text);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(134, lines.Length);
        var safeLine = new Regex(@"\A2026-01-01T00:00:00\.\d{3}Z,0\.000000,0000,6,Info \[(?:FLog::Output\] ! Joining game '11111111-2222-4333-8444-\d{12}' place 900000000000000001|FLog::GameJoinLoadTime\] Report game_join_loadtime: userid:900000000000000002|FLog::Network\] (?:Replicator created|Sending disconnect with reason)|FLog::UgcExperienceController\] UgcExperienceController: doTeleport)\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        Assert.All(lines, line => Assert.Matches(safeLine, line));
        // Exact closed templates are stronger than searching for a finite list of known secrets:
        // no original timestamp, CSV field, real ID/job, URL, path, address or trailing body attribute can fit.
    }

    [Fact]
    public async Task HistoricalSanitizedShapesReplayAllFiveKindsWithoutUpgradingEvidence()
    {
        await File.WriteAllBytesAsync(Log, Fixture());
        using var reader = new IncrementalClientLogReader(Log);
        var facts = await ReadAllAsync(reader, 64 * 1024, 128);
        AssertCounts(facts);
        for (int index = 0; index < facts.Count; index++)
        {
            var fact = facts[index];
            Assert.Equal(SyntheticTime.AddMilliseconds(index), fact.LoggedUtc);
            Assert.Equal(ClientLogFormatSupport.Unverified, fact.FormatSupport);
            if (fact.Fact == ClientLogFact.Joining)
            {
                Assert.Equal(SyntheticPlace, fact.PlaceId);
                Assert.StartsWith("11111111-2222-4333-8444-", fact.JobId!.Value.ToString("D"));
            }
            else Assert.Null(fact.PlaceId);
            if (fact.Fact == ClientLogFact.UserIdentityObserved) Assert.Equal(SyntheticUser, fact.UserId);
            else Assert.Null(fact.UserId);
        }
        Assert.Empty((await reader.ReadAsync(default)).Observations);
        string typedOutput = JsonSerializer.Serialize(facts);
        Assert.DoesNotContain(Log, typedOutput);
        Assert.DoesNotMatch(@"(?i)https?://|roblox(?:-player)?://|[a-z]:\\|\\\\|\.ROBLOSECURITY|authentication[-_ ]?ticket|rbx-authentication", typedOutput);
        Assert.Equal(ClientLogFormatSupport.Unverified, IncrementalClientLogReader.FormatSupport);
    }

    [Theory]
    [InlineData(17, 3)]
    [InlineData(4096, 7)]
    public async Task SanitizedTraceResumesAtBothByteAndObservationBounds(int bytesPerRead, int observationsPerRead)
    {
        await File.WriteAllBytesAsync(Log, Fixture());
        using var reader = new IncrementalClientLogReader(Log,
            new(MaximumBytesPerRead: bytesPerRead, MaximumObservationsPerRead: observationsPerRead));
        var facts = await ReadAllAsync(reader, bytesPerRead, observationsPerRead);
        AssertCounts(facts);
        Assert.Equal(0, facts[0].StartOffset);
        for (int index = 1; index < facts.Count; index++) Assert.Equal(facts[index - 1].EndOffset, facts[index].StartOffset);
        Assert.Equal(Fixture().Length, facts[^1].EndOffset);
    }

    [Fact]
    public async Task ReplacingPartlyConsumedSanitizedTraceStartsACompleteIndependentGeneration()
    {
        byte[] fixture = Fixture();
        await File.WriteAllBytesAsync(Log, fixture);
        using var reader = new IncrementalClientLogReader(Log, new(MaximumObservationsPerRead: 7));
        var previous = await reader.ReadAsync(default);
        Assert.Equal(7, previous.Observations.Count);
        Assert.True(previous.HasMore);
        File.Move(Log, Path.Combine(_root, "sanitized-rotated.log"));
        await File.WriteAllBytesAsync(Log, fixture);
        var replacement = await reader.ReadAsync(default);
        Assert.Equal(ClientLogDiscontinuity.Replaced, replacement.Discontinuity);
        Assert.True(replacement.Stream.Generation > previous.Stream.Generation);
        Assert.Equal(0, replacement.Observations[0].StartOffset);
        var facts = replacement.Observations.ToList();
        facts.AddRange(await ReadAllAsync(reader, 64 * 1024, 7, replacement.ReadOffset));
        AssertCounts(facts);
        Assert.All(facts, fact => Assert.Equal(replacement.Stream, fact.Stream));
    }

    private static byte[] Fixture()
    {
        using var stream = typeof(SanitizedClientLogFixtureTests).Assembly.GetManifestResourceStream("RobloxPlayer0745.sanitized.log");
        Assert.NotNull(stream);
        Assert.Equal(13_974, stream.Length);
        byte[] bytes = new byte[13_974];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task<List<ClientLogObservation>> ReadAllAsync(IncrementalClientLogReader reader,
        int bytesPerRead, int observationsPerRead, long previousOffset = 0)
    {
        var facts = new List<ClientLogObservation>();
        for (int pass = 0; pass < 4096; pass++)
        {
            var batch = await reader.ReadAsync(default);
            Assert.Equal(ClientLogReadStatus.Ready, batch.Status);
            Assert.Equal(0, batch.RejectedLines);
            Assert.InRange(batch.ReadOffset - previousOffset, 0L, bytesPerRead);
            Assert.InRange(batch.Observations.Count, 0, observationsPerRead);
            previousOffset = batch.ReadOffset;
            facts.AddRange(batch.Observations);
            if (!batch.HasMore)
            {
                Assert.Equal(13_974, batch.CompleteLineOffset);
                return facts;
            }
        }
        throw new InvalidOperationException("The bounded sanitized-fixture replay did not complete.");
    }

    private static void AssertCounts(IReadOnlyList<ClientLogObservation> facts)
    {
        Assert.Equal(134, facts.Count);
        Assert.Equal(14, facts.Count(fact => fact.Fact == ClientLogFact.Joining));
        Assert.Equal(14, facts.Count(fact => fact.Fact == ClientLogFact.UserIdentityObserved));
        Assert.Equal(28, facts.Count(fact => fact.Fact == ClientLogFact.Connected));
        Assert.Equal(42, facts.Count(fact => fact.Fact == ClientLogFact.Disconnected));
        Assert.Equal(36, facts.Count(fact => fact.Fact == ClientLogFact.Teleporting));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
