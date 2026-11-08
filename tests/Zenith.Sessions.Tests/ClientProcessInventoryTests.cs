using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Synthetic process snapshot and per-client reads. Counts every snapshot and every handle open.</summary>
internal sealed class FakeClientProcessSource : IClientProcessSource
{
    public static readonly long Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    public readonly List<ClientProcessEntry> Clients = [];
    public readonly List<int> Opened = [];
    public int Snapshots;
    public Exception? SnapshotFailure;
    public Func<int, CancellationToken, ClientProcessReading>? Reader;

    public void Snapshot(List<ClientProcessEntry> clients, CancellationToken ct)
    {
        Snapshots++;
        if (SnapshotFailure is not null) throw SnapshotFailure;
        clients.AddRange(Clients);
    }

    public ClientProcessReading Read(int processId, CancellationToken ct)
    {
        Opened.Add(processId);
        return Reader?.Invoke(processId, ct) ?? new(ClientProcessReadOutcome.Read, Start, TimeSpan.FromSeconds(processId),
            new(WorkingSetBytes: 700, PrivateCommitBytes: 800, PrivateWorkingSetBytes: 600, PageFaultCount: 5), new(1, 2, 3, 4, 5, 6));
    }
}

public sealed class ClientProcessInventoryTests
{
    private sealed class CountingWindows : ITopLevelWindows
    {
        public int Enumerations;
        public void Enumerate(ITopLevelWindowVisitor visitor) => Enumerations++;
        public bool IsUnownedAndVisible(nint window) => false;
        public bool IsVisible(nint window) => false;
        public bool IsHung(nint window) => false;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(128)]
    public void OneSampleTakesOneSnapshotOneWindowPassAndOneHandlePerClient(int count)
    {
        var source = new FakeClientProcessSource();
        for (int client = 0; client < count; client++)
            source.Clients.Add(new(1000 + client, client % 3, VirtualBytes: 4_000_000 + client, WorkingSetBytes: 1, PrivateBytes: 2));
        var windows = new CountingWindows();
        using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, source, windows);

        var inventory = monitor.SampleProcesses(default);

        Assert.Equal(1, source.Snapshots);
        Assert.Equal(count, source.Opened.Count);
        Assert.Equal(count, source.Opened.Distinct().Count());
        Assert.Equal(1, windows.Enumerations);
        Assert.Equal(count, inventory.Sessions.Count);
        Assert.All(inventory.Sessions, row =>
        {
            Assert.Null(row.Problem);
            Assert.Equal(FakeClientProcessSource.Start, row.ProcessStartUtcTicks);
            // Memory comes from the client's handle; the virtual size from the same snapshot that found it.
            Assert.Equal(700, row.WorkingSetBytes); Assert.Equal(800, row.PrivateCommitBytes); Assert.Equal(600, row.PrivateWorkingSetBytes);
            Assert.Equal(4_000_000 + row.ProcessId - 1000, row.VirtualAddressBytes);
        });
        // Reaching the 128-client scan budget is not proof that every client was visited.
        Assert.Equal(count < DesktopSamplingCadence.MaximumProcessSamples, inventory.Complete);

        monitor.SampleProcesses(default);
        Assert.Equal(2, source.Snapshots);
        Assert.Equal(2 * count, source.Opened.Count);
        Assert.Equal(2, windows.Enumerations);
    }

    [Fact]
    public void ClientsKeepNameOrderAndTheScanBudgetKeepsEarlierNamesFirst()
    {
        // Snapshot order is Roblox, then RobloxPlayer, then RobloxPlayerBeta: 130 clients for a budget of 128.
        var source = new FakeClientProcessSource();
        for (int client = 0; client < 60; client++) source.Clients.Add(new(3000 + client, 2, 1, 1, 1));
        for (int client = 0; client < 50; client++) source.Clients.Add(new(2000 + client, 1, 1, 1, 1));
        for (int client = 0; client < 20; client++) source.Clients.Add(new(1000 + client, 0, 1, 1, 1));
        using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, source, new CountingWindows());

        var inventory = monitor.SampleProcesses(default);

        int[] expected = [.. Enumerable.Range(1000, 20), .. Enumerable.Range(2000, 50), .. Enumerable.Range(3000, 58)];
        Assert.Equal(expected, source.Opened);
        Assert.Equal(expected, inventory.Sessions.Select(row => row.ProcessId));
        Assert.False(inventory.Complete);
    }

    [Fact]
    public void UnreadableEndedAndPartlyReadClientsAreReportedWithoutInventedValues()
    {
        var source = new FakeClientProcessSource
        {
            Reader = (processId, _) => processId switch
            {
                1 => ClientProcessReading.Unavailable,
                2 => new(ClientProcessReadOutcome.Exited, FakeClientProcessSource.Start),
                3 => new(ClientProcessReadOutcome.Read, FakeClientProcessSource.Start, TimeSpan.FromSeconds(1)),
                _ => throw new Win32Exception(5),
            },
        };
        source.Clients.AddRange([new(1, 0, 10, 11, 12), new(2, 0, 20, 21, 22), new(3, 0, 30, 31, 32), new(4, 0, 40, 41, 42)]);
        using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, source, new CountingWindows());

        var inventory = monitor.SampleProcesses(default);

        Assert.False(inventory.Complete);
        Assert.Equal(new[] { 1, 3, 4 }, inventory.Sessions.Select(row => row.ProcessId));
        foreach (var row in inventory.Sessions.Where(row => row.ProcessId != 3))
        {
            Assert.Equal("Process identity or access was unavailable during observation.", row.Problem);
            Assert.Equal(0, row.ProcessStartUtcTicks);
        }
        // Without the handle's memory counters the snapshot's own sizes stand in; unread I/O stays unknown.
        var partial = inventory.Sessions.Single(row => row.ProcessId == 3);
        Assert.Null(partial.Problem);
        Assert.Equal(31, partial.WorkingSetBytes); Assert.Equal(32, partial.PrivateCommitBytes); Assert.Equal(30, partial.VirtualAddressBytes);
        Assert.Null(partial.PrivateWorkingSetBytes); Assert.Null(partial.IoRates);
    }

    [Fact]
    public void AnUnavailableSnapshotOpensNoProcessAndScansNoWindows()
    {
        var source = new FakeClientProcessSource { SnapshotFailure = new Win32Exception(5) };
        source.Clients.Add(new(1, 0, 1, 1, 1));
        var windows = new CountingWindows();
        using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, source, windows);

        var error = Assert.Throws<InvalidOperationException>(() => monitor.SampleProcesses(default));

        Assert.Equal("Windows process inventory was unavailable.", error.Message);
        Assert.IsType<Win32Exception>(error.InnerException);
        Assert.Empty(source.Opened);
        Assert.Equal(0, windows.Enumerations);
    }

    [Fact]
    public async Task NativeSourceFindsClientsByNameInOneSnapshotAndReadsEachThroughOneHandle()
    {
        using var current = Process.GetCurrentProcess();
        long start = current.StartTime.ToUniversalTime().Ticks;
        using var child = StartIdleChild();
        // A 1 KiB first buffer is always too small, so the grow-and-retry path runs too.
        var source = new NativeClientProcessSource(1024, "zenith-no-such-client", current.ProcessName.ToUpperInvariant(), "cmd");
        try
        {
            var found = new List<ClientProcessEntry>();
            source.Snapshot(found, default);

            var self = Assert.Single(found, entry => entry.ProcessId == current.Id);
            Assert.Equal(1, self.NameIndex);
            Assert.True(self.VirtualBytes > 0); Assert.True(self.WorkingSetBytes > 0); Assert.True(self.PrivateBytes > 0);
            Assert.Contains(found, entry => entry.ProcessId == child.Id && entry.NameIndex == 2);
            Assert.DoesNotContain(found, entry => entry.NameIndex == 0);

            var reading = source.Read(current.Id, default);
            Assert.Equal(ClientProcessReadOutcome.Read, reading.Outcome);
            // The same instance key as Process.StartTime, so existing rows keep their identity.
            Assert.Equal(start, reading.StartUtcTicks);
            Assert.True(reading.ProcessorTime > TimeSpan.Zero);
            Assert.NotNull(reading.Memory); Assert.True(reading.Memory.WorkingSetBytes > 0);
            Assert.NotNull(reading.Io);
            Assert.Equal(ClientProcessReadOutcome.Unavailable, source.Read(0, default).Outcome);

            using var stop = new CancellationTokenSource(); stop.Cancel();
            Assert.Throws<OperationCanceledException>(() => source.Read(current.Id, stop.Token));
            Assert.Throws<OperationCanceledException>(() => source.Snapshot(found, stop.Token));
        }
        finally { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        // The child's Process object still holds its handle, so the ended instance stays queryable and reads as ended.
        Assert.Equal(ClientProcessReadOutcome.Exited, source.Read(child.Id, default).Outcome);
    }

    private static Process StartIdleChild()
    {
        // cmd.exe waits on its redirected input until the test kills it; it opens no window.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q /k")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        return Process.Start(start) ?? throw new InvalidOperationException("The idle child process did not start.");
    }
}
