using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// R-instances-12 against real Windows calls on a test-owned helper, never a Roblox client. The helper is a Windows PowerShell
/// child showing one WinForms window that cancels its first N close requests, so the first test sees a window that ignores the
/// first request (as Roblox's leave prompt does) and exits on the second, and the second test sees one that never closes. Only
/// the accepted executable name is the helper's; the identity checks, the posts, the waits and the termination are the
/// shipped path.
/// </summary>
public sealed class ClientCloseNativeTests(ITestOutputHelper output)
{
    private static readonly string[] HelperExecutable = ["powershell.exe"];

    [Fact]
    public async Task AWindowThatIgnoresTheFirstRequestExitsOnTheSecondAndAStaleStartIsRefused()
    {
        await using var helper = await WindowHelper.StartAsync(ignoreCloses: 1);
        var operations = new ClientCloseOperations(new NativeClientCloseAdapter(HelperExecutable), TimeProvider.System);
        // A reused PID has a different start time: nothing is sent and nothing is ended.
        var stale = helper.Session with { ProcessStartUtcTicks = helper.Session.ProcessStartUtcTicks - 1 };
        Assert.Equal(ClientCloseStatus.NotCurrent, (await operations.CloseAsync(stale, default)).Status);
        Assert.Equal(ClientForceCloseStatus.NotCurrent, (await operations.ForceCloseAsync(stale, default)).Status);
        Assert.False(helper.HasExited);
        var watch = Stopwatch.StartNew();
        var result = await operations.CloseAsync(helper.Session, default);
        output.WriteLine($"{result.Status} after {result.CloseRequests} request(s) in {watch.ElapsedMilliseconds} ms: {result.Explanation}");
        Assert.Equal(ClientCloseStatus.Exited, result.Status);
        Assert.Equal(2, result.CloseRequests);
        Assert.True(await helper.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AWindowThatNeverClosesIsStillOpenAfterTheWaitAndOnlyTheConfirmedForceCloseEndsIt()
    {
        await using var helper = await WindowHelper.StartAsync(ignoreCloses: int.MaxValue);
        var operations = new ClientCloseOperations(new NativeClientCloseAdapter(HelperExecutable), TimeProvider.System, TimeSpan.FromSeconds(1.5));
        var result = await operations.CloseAsync(helper.Session, default);
        output.WriteLine($"{result.Status} after {result.CloseRequests} request(s): {result.Explanation}");
        Assert.Equal(ClientCloseStatus.StillOpen, result.Status);
        Assert.Equal(2, result.CloseRequests);
        Assert.True(result.OffersForceClose);
        Assert.False(helper.HasExited);
        // The terminate handle checks the executable name too: the Roblox-only production list refuses the helper.
        var production = new ClientCloseOperations(new NativeClientCloseAdapter(), TimeProvider.System);
        Assert.Equal(ClientForceCloseStatus.NotCurrent, (await production.ForceCloseAsync(helper.Session, default)).Status);
        Assert.False(helper.HasExited);
        var forced = await operations.ForceCloseAsync(helper.Session, default);
        output.WriteLine($"{forced.Status}: {forced.Explanation}");
        Assert.Equal(ClientForceCloseStatus.Ended, forced.Status);
        Assert.NotNull(forced.EndedUtc);
        Assert.True(await helper.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        // The exact instance is gone, so a repeat ends nothing.
        var again = await operations.ForceCloseAsync(helper.Session, default);
        Assert.Equal(ClientForceCloseStatus.NotCurrent, again.Status);
        Assert.Null(again.EndedUtc);
    }

    private sealed class WindowHelper : IAsyncDisposable
    {
        private readonly Process _process;
        private WindowHelper(Process process, nint window)
        {
            _process = process;
            Session = new(process.Id, process.StartTime.ToUniversalTime().Ticks, "Synthetic", window, "Zenith close helper", false, true, 0, 0, null, DateTimeOffset.UtcNow);
        }
        public SessionObservation Session { get; }
        public bool HasExited { get { _process.Refresh(); return _process.HasExited; } }
        public async Task<bool> WaitForExitAsync(TimeSpan limit)
        {
            using var stop = new CancellationTokenSource(limit);
            try { await _process.WaitForExitAsync(stop.Token); return true; } catch (OperationCanceledException) { return false; }
        }

        /// <param name="ignoreCloses">How many close requests the window answers by staying open before it closes.</param>
        public static async Task<WindowHelper> StartAsync(int ignoreCloses)
        {
            string script = "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; "
                + "$f = New-Object System.Windows.Forms.Form; $f.Text = 'Zenith close helper'; $f.StartPosition = 'Manual'; "
                + "$f.Location = New-Object System.Drawing.Point(-30000, -30000); "
                + $"$global:remaining = {ignoreCloses}; "
                + "$f.add_FormClosing({ param($s, $e) if ($global:remaining -gt 0) { $global:remaining--; $e.Cancel = $true } }); "
                + "$f.add_Shown({ [Console]::Out.WriteLine('shown ' + $f.Handle.ToInt64()) }); "
                + "[System.Windows.Forms.Application]::Run($f)";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-STA");
            start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(start) ?? throw new InvalidOperationException("The test-owned window helper did not start.");
            try
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.NotNull(line); Assert.StartsWith("shown ", line);
                nint window = (nint)long.Parse(line.AsSpan(6), System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotEqual(0, window);
                return new(process, window);
            }
            catch { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (!HasExited) { _process.Kill(); await _process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
            finally { _process.Dispose(); }
        }
    }
}
