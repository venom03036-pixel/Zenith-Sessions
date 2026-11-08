using System.Diagnostics;
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
    public void FaultRecordMatchingNeedsTheExactPidAndCreationTime()
    {
        long created = new DateTime(2026, 11, 7, 20, 13, 17, DateTimeKind.Utc).AddTicks(1997408).Ticks;
        string fileTime = "0x" + new DateTime(created, DateTimeKind.Utc).ToFileTimeUtc().ToString("x");
        string Event(string pid, string creation) => "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>"
            + "<Provider Name='Application Error'/><EventID>1000</EventID><TimeCreated SystemTime='2026-11-07T20:13:17.6672502Z'/></System><EventData>"
            + "<Data Name='AppName'>SYNTHETIC_APP.exe</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ProcessId'>" + pid + "</Data>"
            + "<Data Name='ProcessCreationTime'>" + creation + "</Data><Data Name='AppPath'>C:\\SYNTHETIC_SECRET_PATH\\x.exe</Data><Data Name='PackageFullName'></Data></EventData></Event>";
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
