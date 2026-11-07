using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class SessionLogCorrelatorTests
{
    // 64 bytes of ordinary client output with no recognised marker: consumed without producing a fact.
    private static readonly string Chatter = "synthetic chatter " + new string('x', 45) + "\n";

    [Fact]
    public async Task SixteenChattyClientsStayCaughtUpWithinTheScaledPerPassReadBudget()
    {
        // Sixteen bound clients each write 8 KiB of ordinary output per second, behind 24 unowned history logs.
        // A fixed 64 KiB per-pass budget reads only about 4 KiB/s per client here, so every log falls further
        // behind and the evidence registry hides each client's state as an unread tail (AwaitingLogTail).
        var clock = new Clock();
        var keys = Enumerable.Range(100, 16).Select(pid => Key(pid) with { HostId = null }).ToArray();
        var owners = new Dictionary<string, SessionProcessKey>(StringComparer.OrdinalIgnoreCase);
        var files = Enumerable.Range(0, 24).Select(i => Candidate($"quiet{i}")).ToList();
        foreach (var key in keys)
        {
            var file = Candidate($"chatty{key.ProcessId}",
                SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity + SyntheticClientLogFixtures.Connected);
            owners.Add(file.Path, key);
            files.Add(file);
        }
        var probe = new Probe(Log, keys[0])
        {
            Files = files,
            CurrentClient = key => owners.ContainsValue(key),
            HolderFor = path => owners.TryGetValue(path, out var key) ? HeldBy(key) : new(SessionLogCorrelationStatus.Unknown, [])
        };
        var options = new SessionLogCorrelatorOptions();
        await using var worker = new WindowsSessionLogCorrelator(probe, options, clock);
        var projection = new SessionLogProjection();
        var desktop = Desktop(keys);
        var cursors = new Dictionary<SessionProcessKey, (ClientLogStreamId Stream, long Offset)>();
        string secondOfOutput = string.Concat(Enumerable.Repeat(Chatter, 8192 / Chatter.Length));
        long perPassCap = keys.Length * (long)options.BytesPerReader;
        long revision = 0;
        int caughtUp = 0, pass = 0;
        string[] lagging = [];
        for (; pass < 90 && caughtUp < 20; pass++)
        {
            if (pass != 0)
            {
                clock.Advance(1000);
                foreach (var path in owners.Keys) File.AppendAllText(path, secondOfOutput);
            }
            int queriesBefore = probe.Queries;
            worker.Offer(desktop);
            var snapshot = await After(worker, revision); revision = snapshot.Revision;
            // Backlog reads never add holder queries beyond the regular turns.
            Assert.InRange(probe.Queries - queriesBefore, 0, options.ReadsPerPass);

            long readThisPass = 0;
            foreach (var row in snapshot.Sessions)
            {
                if (row.Batch is not { } batch) continue;
                readThisPass += cursors.TryGetValue(row.Process, out var prior) && prior.Stream == batch.Stream
                    ? batch.ReadOffset - prior.Offset : batch.ReadOffset;
                cursors[row.Process] = (batch.Stream, batch.ReadOffset);
            }
            Assert.True(readThisPass <= perPassCap,
                $"Pass {pass} read {readThisPass} log bytes; the per-pass cap for 16 bound readers is {perPassCap}.");

            projection.Update(desktop.Sessions, snapshot, clock.GetUtcNow());
            var evidence = projection.EvidenceSnapshot(clock.GetUtcNow());
            lagging = owners.Where(owner => evidence.SingleOrDefault(e => e.Process == owner.Value)
                    is not { AwaitingLogTail: false, Lifecycle: ObservedSessionLifecycle.Connected })
                .Select(owner => $"{owner.Value.ProcessId}:{new FileInfo(owner.Key).Length - (cursors.TryGetValue(owner.Value, out var c) ? c.Offset : 0)} bytes unread")
                .ToArray();
            if (lagging.Length == 0) caughtUp++;
            else Assert.True(caughtUp == 0,
                $"Pass {pass}: {lagging.Length} client(s) fell behind again after catching up: {string.Join(", ", lagging)}");
        }
        Assert.True(caughtUp == 20,
            $"After {pass} passes {lagging.Length} of 16 chatty clients were still hidden behind an unread log tail: {string.Join(", ", lagging)}");
    }
}
