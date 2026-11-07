using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Exercises the real log probe against synthetic files in a temporary trusted anchor. It never reads real
/// client logs, never opens another process beyond this test host, and never writes outside its own folder.
/// </summary>
public sealed class WindowsClientLogProbeTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithClientLogProbe-" + Guid.NewGuid().ToString("N"));
    private string Anchor => Path.Combine(_root, "anchor");

    public WindowsClientLogProbeTests() => Directory.CreateDirectory(Anchor);
    public void Dispose() => Directory.Delete(_root, true);

    private static string Synthetic(string folder, string name, TimeSpan age)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "synthetic", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private WindowsClientLogProbe Probe(params (string Path, ClientLogVariant Variant)[] roots) => new(roots, Anchor, null);

    private static IReadOnlyList<ClientLogCandidate> Drain(WindowsClientLogProbe probe, int maximum)
    {
        // The probe stops after 256 entries or 50 ms; repeated passes resume from its cursor until one finishes every root.
        for (int pass = 0; pass < 64; pass++)
        {
            var result = probe.Discover(maximum, CancellationToken.None);
            Assert.False(result.Unavailable);
            if (!result.BudgetLimited) return result.Files;
        }
        Assert.Fail("Discovery stayed budget-limited for 64 passes over a small synthetic folder.");
        return [];
    }

    private static (string, ClientLogVariant)[] Shape(IReadOnlyList<ClientLogCandidate> files) => files.Select(file => (file.Path, file.Variant)).ToArray();

    [Fact]
    public void DiscoverKeepsOnlyRecentPlayerLastLogsNewestFirstAndEvictsTheOldest()
    {
        string win32 = Path.Combine(Anchor, "Roblox", "logs"), uwp = Path.Combine(Anchor, "Packages", "synthetic", "LocalState", "logs");
        string a = Synthetic(win32, "a_Player_1_last.log", TimeSpan.FromMinutes(10));
        string b = Synthetic(win32, "b_Player_2_last.log", TimeSpan.FromHours(23)); // Oldest eligible, enumerated between newer files.
        string c = Synthetic(win32, "c_Player_3_last.log", TimeSpan.FromMinutes(1));
        string h = Synthetic(uwp, "h_Player_8_last.log", TimeSpan.FromMinutes(5));
        // Each of these is newer than b except g, so any that slipped through would show up in the roomy result.
        Synthetic(win32, "d_Player_CrashHandler_4_last.log", TimeSpan.FromMinutes(1));
        Synthetic(win32, "e_Studio_5_last.log", TimeSpan.FromMinutes(1));
        Synthetic(win32, "f_Player_6.log", TimeSpan.FromMinutes(1));
        Synthetic(win32, "g_Player_7_last.log", TimeSpan.FromDays(2));
        var roots = new[] { (win32, ClientLogVariant.Win32), (uwp, ClientLogVariant.Uwp) };

        using (var roomy = Probe(roots))
            Assert.Equal(new[] { (c, ClientLogVariant.Win32), (h, ClientLogVariant.Uwp), (a, ClientLogVariant.Win32), (b, ClientLogVariant.Win32) },
                Shape(Drain(roomy, 8)));

        // With room for three, the oldest (b) goes; first-in eviction would drop a, and refusing newcomers would drop h.
        using var bounded = Probe(roots);
        Assert.Equal(new[] { (c, ClientLogVariant.Win32), (h, ClientLogVariant.Uwp), (a, ClientLogVariant.Win32) }, Shape(Drain(bounded, 3)));
    }

    [Fact]
    public void DiscoverResumesFromItsCursorSoALargeFolderCannotHideLaterEntries()
    {
        string logs = Path.Combine(Anchor, "Roblox", "logs");
        for (int index = 0; index < 300; index++) Synthetic(logs, $"a{index:D3}_Player_{index}_last.log", TimeSpan.FromHours(2));
        string newest = Synthetic(logs, "z_Player_9999_last.log", TimeSpan.FromMinutes(1)); // Enumerated after the 300 older files.
        using var probe = Probe((logs, ClientLogVariant.Win32));

        var first = probe.Discover(8, CancellationToken.None);
        Assert.True(first.BudgetLimited);
        Assert.DoesNotContain(first.Files, file => file.Path == newest);

        var files = Drain(probe, 8);
        Assert.Equal(8, files.Count);
        Assert.Equal(newest, files[0].Path);
    }

    [Fact]
    public void DiscoverIgnoresRootsOutsideTheAnchorOrNamedByUncOrTraversalPaths()
    {
        string inside = Path.Combine(Anchor, "logs"), outside = Path.Combine(_root, "outside", "logs");
        string kept = Synthetic(inside, "a_Player_1_last.log", TimeSpan.FromMinutes(1));
        Synthetic(outside, "b_Player_2_last.log", TimeSpan.FromMinutes(1));
        string device = @"\\?\" + inside;
        var rejected = new[]
        {
            (Root: outside, Anchor),
            (Root: Path.Combine(Anchor, "..", "outside", "logs"), Anchor), // Passes a prefix check; only the parent walk refuses it.
            (Root: device, Anchor),
            (Root: device, @"\\?\" + Anchor), // Even under a matching device-path anchor, a \\ root is never scanned.
        };
        foreach (var (root, anchor) in rejected)
        {
            Assert.True(Directory.Exists(root));
            using var probe = new WindowsClientLogProbe([(root, ClientLogVariant.Win32)], anchor, null);
            Assert.Empty(Drain(probe, 8));
        }

        using var control = Probe((inside, ClientLogVariant.Win32));
        Assert.Equal(kept, Assert.Single(Drain(control, 8)).Path);
    }

    [Fact]
    public async Task DiscoverIgnoresRootsReachedThroughAJunctionInsideTheAnchor()
    {
        string real = Path.Combine(Anchor, "real"), alias = Path.Combine(Anchor, "alias");
        string kept = Synthetic(Path.Combine(real, "logs"), "a_Player_1_last.log", TimeSpan.FromMinutes(1));
        Synthetic(real, "b_Player_2_last.log", TimeSpan.FromMinutes(1));
        await Junction(alias, real);
        try
        {
            foreach (string root in new[] { alias, Path.Combine(alias, "logs") })
            {
                Assert.True(Directory.Exists(root));
                using var probe = Probe((root, ClientLogVariant.Win32));
                Assert.Empty(Drain(probe, 8));
            }
            using var control = Probe((Path.Combine(real, "logs"), ClientLogVariant.Win32));
            Assert.Equal(kept, Assert.Single(Drain(control, 8)).Path);
        }
        finally { if (Directory.Exists(alias)) Directory.Delete(alias, false); }
    }

    [Fact]
    public void DefaultProbeWatchesOnlyTheTwoDocumentedRootsInsideLocalApplicationData()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var probe = new WindowsClientLogProbe();
        Assert.Equal(local, probe.TrustedAnchor);
        Assert.Equal(new[]
        {
            (Path.Combine(local, "Roblox", "logs"), ClientLogVariant.Win32),
            (Path.Combine(local, "Packages", "ROBLOXCorporation.ROBLOX_55nm5eh3cm0pr", "LocalState", "logs"), ClientLogVariant.Uwp),
        }, probe.Roots);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\RobloxPlayerBeta.exe", "Win32", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\RobloxPlayerBeta.exe", "Uwp", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\robloxplayerbeta.EXE", "Win32", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\RobloxPlayer.exe", "Win32", true)]
    [InlineData(@"C:\Program Files\WindowsApps\pkg\RobloxPlayerBeta.exe", "Uwp", true)]
    [InlineData(@"C:\Program Files\WindowsApps\pkg\RobloxPlayerBeta.exe", "Win32", false)]
    [InlineData(@"C:\Program Files\windowsapps\pkg\Roblox.exe", "Uwp", true)]
    [InlineData(@"C:\WindowsAppsBackup\RobloxPlayerBeta.exe", "Win32", true)] // A folder merely starting with the name is not packaged.
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\NotRobloxPlayerBeta.exe", "Win32", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Roblox\Versions\v\notepad.exe", "Win32", false)]
    [InlineData(@"C:\Program Files\WindowsApps\pkg\notepad.exe", "Uwp", false)]
    public void IsClientImageEnforcesNameAndVariant(string image, string variant, bool expected) // ClientLogVariant is internal, so the theory names it.
        => Assert.Equal(expected, WindowsClientLogProbe.IsClientImage(image, Enum.Parse<ClientLogVariant>(variant)));

    [Fact]
    public void IsCurrentClientRejectsTheTestHostWrongStartTicksInvalidKeysAndAnUnknownUser()
    {
        using var self = Process.GetCurrentProcess();
        long ticks = self.StartTime.ToUniversalTime().Ticks;
        var host = new SessionProcessKey(null, self.Id, ticks);
        using var identity = WindowsIdentity.GetCurrent();
        using var probe = new WindowsClientLogProbe([(Path.Combine(Anchor, "logs"), ClientLogVariant.Win32)], Anchor, identity.User);

        // The test host is ours and its ticks are real; only the client image allow-list refuses it.
        Assert.False(WindowsClientLogProbe.IsClientImage(Environment.ProcessPath!, ClientLogVariant.Win32));
        Assert.False(probe.IsCurrentClient(host, ClientLogVariant.Win32));
        Assert.False(probe.IsCurrentClient(host, ClientLogVariant.Uwp));
        Assert.False(probe.IsCurrentClient(host with { ProcessStartUtcTicks = ticks + 1 }, ClientLogVariant.Win32));
        Assert.False(probe.IsCurrentClient(default, ClientLogVariant.Win32));
        using var anonymous = Probe((Path.Combine(Anchor, "logs"), ClientLogVariant.Win32));
        Assert.False(anonymous.IsCurrentClient(host, ClientLogVariant.Win32));
    }

    private static async Task Junction(string alias, string target)
    {
        // A directory junction needs no symbolic-link privilege or developer mode; same pattern as MacroAssignmentStoreTests.
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(alias) + " -Target " + Quote(target) + " | Out-Null";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!; using var deadline = new CancellationTokenSource(Deadline);
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(Deadline); } }
        Assert.NotEqual((FileAttributes)0, File.GetAttributes(alias) & FileAttributes.ReparsePoint);
    }
}
