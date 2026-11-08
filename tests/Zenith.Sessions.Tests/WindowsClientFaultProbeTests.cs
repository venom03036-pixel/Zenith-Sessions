using System.Diagnostics;
using System.Globalization;
using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class WindowsClientFaultProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithFaultProbeTests-" + Guid.NewGuid().ToString("N"));
    public WindowsClientFaultProbeTests() => Directory.CreateDirectory(_root);

    private static RecentClientHistoryEntry Ended(SessionProcessKey key, DateTimeOffset lastSeen, DateTimeOffset gone)
        => new(Guid.NewGuid(), key, lastSeen, lastSeen, gone);

    // Opt-in (ZENITH_RUN_NATIVE_CRASH_TESTS=1): it crashes a real process, so Windows writes an error report on the PC.
    // The default run keeps the record matching and crash-dump counting covered by the two synthetic tests below.
    [NativeCrashFact]
    [Trait(TestCategory.Name, TestCategory.NativeCrash)]
    public async Task FindsWindowsFaultRecordForTheExactCrashedProcessOnly()
    {
        // Test-owned helper: an inbox Windows PowerShell process that ends through Environment.FailFast.
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Environment]::FailFast('Zenith fault probe test')" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        // The same reading WindowsDesktopMonitor uses for a client's start time.
        long started = process.StartTime.ToUniversalTime().Ticks;
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(unchecked((int)0x80131623), process.ExitCode);
        var key = new SessionProcessKey(null, process.Id, started);
        var lastSeen = new DateTimeOffset(started, TimeSpan.Zero);
        var probe = new WindowsClientFaultProbe("Application", []);
        ClientFaultCheck check; var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            check = probe.Check(Ended(key, lastSeen, now), now, default);
            if (check.FaultRecordedUtc is not null || DateTime.UtcNow > deadline) break;
            await Task.Delay(250);
        }
        Assert.True(check.Complete);
        Assert.NotNull(check.FaultRecordedUtc); Assert.Equal(0x80131623u, check.ExceptionCode);
        Assert.InRange(check.FaultRecordedUtc!.Value, lastSeen, check.CheckedUtc);
        Assert.Equal(ClientEndCause.Crashed, (Ended(key, lastSeen, check.CheckedUtc) with { FaultCheck = check }).Validated(ended: true).EndCause);
        // A check made just before Windows wrote that record must not report it: a fault later than the check time would
        // make the result inconsistent with the end it was asked about.
        var before = check.FaultRecordedUtc!.Value.AddTicks(-1);
        var early = probe.Check(Ended(key, lastSeen, before), before, default);
        (Ended(key, lastSeen, before) with { FaultCheck = early }).Validated(ended: true);
        Assert.Null(early.FaultRecordedUtc); Assert.Null(early.ExceptionCode);
        // The same pid with any other creation time is a different process.
        var later = DateTimeOffset.UtcNow;
        var other = probe.Check(Ended(key with { ProcessStartUtcTicks = started + 1 }, lastSeen.AddTicks(1), later), later, default);
        Assert.Null(other.FaultRecordedUtc); Assert.Null(other.ExceptionCode); Assert.True(other.Complete);
    }

    [Fact]
    public void CountsOnlyCrashDumpsWrittenAsTheClientEndedAndNeverOpensThem()
    {
        var gone = DateTimeOffset.UtcNow.AddSeconds(-2); var lastSeen = gone.AddSeconds(-1);
        var key = new SessionProcessKey(null, 4242, lastSeen.AddHours(-1).UtcTicks);
        string reports = Directory.CreateDirectory(Path.Combine(_root, "reports")).FullName;
        string Write(string name, DateTimeOffset created)
        {
            string path = Path.Combine(reports, name); File.WriteAllText(path, "not a dump");
            File.SetCreationTimeUtc(path, created.UtcDateTime); return path;
        }
        Write("near-end.dmp", lastSeen.AddSeconds(-30));
        Write("UPPER.DMP", gone);
        Write("earlier.dmp", lastSeen - WindowsClientFaultProbe.DumpLead - TimeSpan.FromMinutes(1));
        Write("near-end.txt", gone);
        Directory.CreateDirectory(Path.Combine(reports, "folder.dmp"));
        using (File.Open(Write("locked.dmp", gone), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var probe = new WindowsClientFaultProbe("Application", [reports, Path.Combine(_root, "missing")]);
            var now = DateTimeOffset.UtcNow;
            var check = probe.Check(Ended(key, lastSeen, gone), now, default);
            Assert.Equal(3, check.CrashDumps); Assert.True(check.Complete); Assert.Null(check.FaultRecordedUtc);
            Assert.Equal(ClientEndCause.PossibleCrash, (Ended(key, lastSeen, gone) with { FaultCheck = check }).Validated(ended: true).EndCause);
        }
        // An unreadable fault log makes the check incomplete instead of throwing or claiming nothing happened.
        var missingLog = new WindowsClientFaultProbe("Zenith-Missing-Channel-" + Guid.NewGuid().ToString("N"), [reports]);
        var partial = missingLog.Check(Ended(key, lastSeen, gone), DateTimeOffset.UtcNow, default);
        Assert.False(partial.Complete); Assert.Equal(3, partial.CrashDumps);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => missingLog.Check(Ended(key, lastSeen, gone), DateTimeOffset.UtcNow, stop.Token));
    }

    [Fact]
    public void CrashDumpFoldersAndLogRootsComeFromOneSharedListOfRobloxFolders()
    {
        // One list of current-user Roblox folders, so the log and crash-record probes cannot drift apart.
        string local = Path.Combine(_root, "local");
        Assert.Equal(new[]
        {
            (Path.Combine(local, "Roblox", "logs"), ClientLogVariant.Win32),
            (Path.Combine(local, "Packages", RobloxClientFolders.PackageFamilyName, "LocalState", "logs"), ClientLogVariant.Uwp),
        }, RobloxClientFolders.LogRoots(local));
        Assert.Equal(RobloxClientFolders.LogRoots(local).Select(root => Path.Combine(root.Path, "crashes", "reports")),
            WindowsClientFaultProbe.DefaultDumpFolders(local));
        Assert.Empty(WindowsClientFaultProbe.DefaultDumpFolders(""));
        using var logs = new WindowsClientLogProbe();
        Assert.Equal(RobloxClientFolders.LogRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), logs.Roots);
    }

    private static string FaultEvent(string pid, string creation, string systemTime = "2026-11-07T20:13:17.6672502Z")
        => "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>"
            + "<Provider Name='Application Error'/><EventID>1000</EventID><TimeCreated SystemTime='" + systemTime + "'/></System><EventData>"
            + "<Data Name='AppName'>SYNTHETIC_APP.exe</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ProcessId'>" + pid + "</Data>"
            + "<Data Name='ProcessCreationTime'>" + creation + "</Data><Data Name='AppPath'>C:\\SYNTHETIC_SECRET_PATH\\x.exe</Data><Data Name='PackageFullName'></Data></EventData></Event>";

    [Fact]
    public void FaultQueryAndPlacementKeepRecordsToExactlyProcessStartThroughCheckTime()
    {
        // The default run never crashes a process, so the window M-L7-3 narrowed from one second either side to exactly
        // [process start, check time] is pinned here without the Event Log.
        var from = new DateTimeOffset(2026, 11, 7, 20, 13, 17, TimeSpan.Zero).AddTicks(1997408);
        var until = new DateTimeOffset(2026, 11, 7, 20, 15, 2, TimeSpan.Zero).AddTicks(6672502);
        const string Head = "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[@SystemTime>='";
        // Event Log bounds are whole milliseconds. They round outward, so a record in the last part-millisecond before the
        // check is still read, and no wider.
        Assert.Equal(Head + "2026-11-07T20:13:17.199Z' and @SystemTime<='2026-11-07T20:15:02.668Z']]]", WindowsClientFaultProbe.FaultQuery(from, until));
        var whole = new DateTimeOffset(2026, 11, 7, 20, 15, 2, 667, TimeSpan.Zero);
        Assert.Equal(Head + "2026-11-07T20:13:17.199Z' and @SystemTime<='2026-11-07T20:15:02.667Z']]]",
            WindowsClientFaultProbe.FaultQuery(from.ToOffset(TimeSpan.FromHours(-5)), whole.ToOffset(TimeSpan.FromHours(9))));

        // Each record the query returns is then placed against the exact window.
        string fileTime = "0x" + from.UtcDateTime.ToFileTimeUtc().ToString("x");
        var key = new SessionProcessKey(null, 0x1f6c, from.UtcTicks);
        string At(DateTimeOffset time) => FaultEvent("0x1f6c", fileTime, time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        WindowsClientFaultProbe.FaultRecordFit Place(string xml, out DateTimeOffset time, out uint? code)
            => WindowsClientFaultProbe.Place(xml, key, from, until, out time, out code);
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.InWindow, Place(At(from), out var first, out var code));
        Assert.Equal(from, first); Assert.Equal(0xC0000005u, code);
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.InWindow, Place(At(until), out var last, out _));
        Assert.Equal(until, last);
        // This process's record inside the rounded query bounds but outside the window cannot be placed, so the check is
        // incomplete rather than reporting a fault after the check time or claiming none.
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.CannotPlace, Place(At(from.AddTicks(-1)), out _, out _));
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.CannotPlace, Place(At(until.AddTicks(1)), out _, out _));
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.CannotPlace, Place(At(until.AddSeconds(1)), out _, out _));
        // Another process's record is skipped wherever it falls; an unreadable record cannot be placed.
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.OtherProcess, Place(FaultEvent("0x1f6d", fileTime, "2026-11-07T20:15:03.0000000Z"), out _, out _));
        Assert.Equal(WindowsClientFaultProbe.FaultRecordFit.CannotPlace, Place("<Event><EventData><Data Name='ProcessId'>0x1f6c", out _, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void FaultRecordMatchingNeedsTheExactPidAndCreationTime()
    {
        long created = new DateTime(2026, 11, 7, 20, 13, 17, DateTimeKind.Utc).AddTicks(1997408).Ticks;
        string fileTime = "0x" + new DateTime(created, DateTimeKind.Utc).ToFileTimeUtc().ToString("x");
        string Event(string pid, string creation) => FaultEvent(pid, creation);
        var key = new SessionProcessKey(null, 0x1f6c, created);
        Assert.True(WindowsClientFaultProbe.Matches(Event("0x1f6c", fileTime), key, out var time, out var code));
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 20, 13, 17, TimeSpan.Zero).AddTicks(6672502), time); Assert.Equal(0xC0000005u, code);
        Assert.False(WindowsClientFaultProbe.Matches(Event("0x1f6d", fileTime), key, out _, out _));
        Assert.False(WindowsClientFaultProbe.Matches(Event("0x1f6c", "0x1"), key, out _, out _));
        Assert.False(WindowsClientFaultProbe.Matches(Event("not-a-number", fileTime), key, out _, out _));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root), temporary = Path.GetFullPath(Path.GetTempPath());
        if (Path.GetDirectoryName(full) != temporary.TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("ZenithFaultProbeTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fault probe fixture root.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
