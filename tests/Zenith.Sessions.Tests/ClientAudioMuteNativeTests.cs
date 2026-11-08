using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Real Core Audio calls on a test-owned Windows PowerShell helper that loops a second of silence through System.Media.SoundPlayer
/// (winmm over WASAPI), never a Roblox client. Only the accepted executable name is the helper's; rights, identity checks, the
/// session match by PID, readback, Unmute and Exit restoration are the shipped path. Each step is read back separately through the
/// adapter's own Core Audio enumeration, beneath the control and its ownership map. Skipped where Windows reports no active audio
/// output device (<see cref="AudioOutputFactAttribute"/>).
/// </summary>
public sealed class ClientAudioMuteNativeTests
{
    [AudioOutputFact]
    public async Task MuteChangesOnlyTheTestOwnedHelpersOwnSoundAndRestoreAndExitUnmuteIt()
    {
        // Windows remembers the executable's previous mute. Establish this test-owned helper's baseline explicitly.
        await using var helper = await SilentHelper.StartAsync(unmutedBaseline: true);
        var control = new WindowsClientAudioMuteControl(new NativeClientAudioMuteAdapter("powershell.exe"));
        bool[] restoredOnExit = [];
        try
        {
            Assert.All(helper.Sessions(), muted => Assert.False(muted));

            // An observation of the same PID with a different start is another process instance: refused, nothing changes.
            var reused = helper.Session with { ProcessStartUtcTicks = helper.Session.ProcessStartUtcTicks - TimeSpan.TicksPerSecond };
            var refused = Assert.Single((await control.ChangeAsync([reused], ClientAudioMuteAction.Mute, default)).Results);
            Assert.Equal(ClientAudioMuteStatus.InvalidClient, refused.Status);
            Assert.All(helper.Sessions(), muted => Assert.False(muted));

            var muted = Assert.Single((await control.ChangeAsync([helper.Session], ClientAudioMuteAction.Mute, default)).Results);
            Assert.True(muted.Status == ClientAudioMuteStatus.Applied, muted.Explanation);
            Assert.All(helper.Sessions(), state => Assert.True(state));
            Assert.Equal(1, control.PendingRestoreCount);

            var restored = Assert.Single((await control.RestoreAllAsync(default)).Results);
            Assert.True(restored.Status == ClientAudioMuteStatus.Restored, restored.Explanation);
            Assert.All(helper.Sessions(), state => Assert.False(state));
            Assert.Equal(0, control.PendingRestoreCount);

            // Exit restoration: mute again, then dispose the control without an explicit restore.
            var again = Assert.Single((await control.ChangeAsync([helper.Session], ClientAudioMuteAction.Mute, default)).Results);
            Assert.True(again.Status == ClientAudioMuteStatus.Applied, again.Explanation);
            Assert.All(helper.Sessions(), state => Assert.True(state));
        }
        finally
        {
            await control.DisposeAsync();
            restoredOnExit = helper.Sessions(); // Observe restoration before the fixture's last-resort cleanup can change it.
            helper.UnmuteIfLeftMuted(); // Windows may remember a mute for powershell.exe; never leave one behind.
        }
        Assert.Equal(0, control.PendingRestoreCount);
        Assert.NotEmpty(restoredOnExit);
        Assert.All(restoredOnExit, state => Assert.False(state));
    }

    /// <summary>
    /// Two helpers of the same program, as with several Roblox clients. Observed on Windows 11 (build 26200): a helper started
    /// while another is muted starts with its sound on, but one started after a muted helper exited starts muted, because Windows
    /// remembered that mute. Either way, a later instance Zenith mutes or finds muted that way is unmuted on Exit.
    /// </summary>
    [AudioOutputFact]
    public async Task LaterHelpersOfTheSameProgramAreUnmutedOnExitEvenWhenWindowsStartsOneMuted()
    {
        // Reproduce Windows remembering a previous helper's mute without relying on test order or global volume state.
        // Only this newly owned helper's PID is seeded; concurrent and later instances below retain Windows' actual state.
        // Establish the first helper's precondition before this control exists. A mute already present at its first
        // request is correctly not owned by the control and cannot test restoration of a mute Zenith applied.
        var first = await SilentHelper.StartAsync(unmutedBaseline: true, seedMutedBaseline: true);
        SilentHelper? concurrent = null, later = null;
        var control = new WindowsClientAudioMuteControl(new NativeClientAudioMuteAdapter("powershell.exe"));
        try
        {
            var muted = Assert.Single((await control.ChangeAsync([first.Session], ClientAudioMuteAction.Mute, default)).Results);
            Assert.True(muted.Status == ClientAudioMuteStatus.Applied, muted.Explanation);

            concurrent = await SilentHelper.StartAsync();
            var second = Assert.Single((await control.ChangeAsync([concurrent.Session], ClientAudioMuteAction.Mute, default)).Results);
            Assert.True(second.RestorePending, second.Status + ": " + second.Explanation);
            Assert.All(concurrent.Sessions(), state => Assert.True(state));

            await first.DisposeAsync(); // Exits while muted, so Windows may remember the mute for the program.
            later = await SilentHelper.StartAsync();
            var third = Assert.Single((await control.ChangeAsync([later.Session], ClientAudioMuteAction.Mute, default)).Results);
            Assert.True(third.RestorePending, third.Status + ": " + third.Explanation);
            Assert.All(later.Sessions(), state => Assert.True(state));
        }
        finally
        {
            bool[] left = [];
            try
            {
                await control.DisposeAsync();
                // Observe the control's restoration before fixture cleanup can change the answer.
                left = [.. concurrent?.Sessions() ?? [], .. later?.Sessions() ?? []];
            }
            finally { await SilentHelper.CleanupAsync(first, concurrent, later); }
            Assert.All(left, state => Assert.False(state));
        }
        Assert.Equal(0, control.PendingRestoreCount);
    }

    private sealed class SilentHelper : IAsyncDisposable
    {
        private readonly Process _process;
        private bool _disposed;
        private SilentHelper(Process process)
        {
            _process = process;
            Session = new(process.Id, process.StartTime.ToUniversalTime().Ticks, "Synthetic", 0, "", false, false, 0, 0, null,
                DateTimeOffset.UtcNow);
        }
        public SessionObservation Session { get; }

        public static async Task<SilentHelper> StartAsync(bool unmutedBaseline = false, bool seedMutedBaseline = false)
        {
            // One second of 8 kHz 16-bit mono silence (all-zero samples), looped from memory until a line arrives on input.
            const string script = "$n=16000;$m=New-Object IO.MemoryStream;$w=New-Object IO.BinaryWriter($m);$a=[Text.Encoding]::ASCII;"
                + "$w.Write($a.GetBytes('RIFF'));$w.Write([int](36+$n));$w.Write($a.GetBytes('WAVEfmt '));$w.Write([int]16);"
                + "$w.Write([int16]1);$w.Write([int16]1);$w.Write([int]8000);$w.Write([int]16000);$w.Write([int16]2);$w.Write([int16]16);"
                + "$w.Write($a.GetBytes('data'));$w.Write([int]$n);$w.Write((New-Object byte[] $n));$w.Flush();$m.Position=0;"
                + "$p=New-Object Media.SoundPlayer($m);$p.Load();$p.PlayLooping();[Console]::Out.WriteLine('ready');"
                + "[void][Console]::In.ReadLine();$p.Stop()";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(start) ?? throw new InvalidOperationException("The test-owned sound helper did not start.");
            var helper = new SilentHelper(process);
            try
            {
                Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)));
                // The audio session opens asynchronously after playback starts; wait a bounded time for Windows to list it.
                var watch = Stopwatch.StartNew();
                while (helper.Sessions().Length == 0)
                {
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15),
                        "The test-owned helper never got a Windows audio session. This test needs an active audio output device.");
                    await Task.Delay(100);
                }
                if (seedMutedBaseline)
                {
                    var write = NativeApi().SetSessions(process.Id, null, true);
                    Assert.Equal(0, write.HResult);
                    Assert.True(write.Accepted > 0);
                    Assert.All(helper.Sessions(), state => Assert.True(state));
                }
                if (unmutedBaseline)
                {
                    helper.UnmuteIfLeftMuted();
                    Assert.All(helper.Sessions(), state => Assert.False(state));
                }
                return helper;
            }
            catch { await CleanupAsync(helper); throw; }
        }

        public static async Task CleanupAsync(params SilentHelper?[] helpers)
        {
            List<Exception>? failures = null;
            foreach (var helper in helpers)
            {
                if (helper is null) continue;
                // Even an assertion on the first mute must not leave this helper or its remembered mute behind.
                try { helper.UnmuteIfLeftMuted(); }
                catch (Exception error) { (failures ??= []).Add(error); }
                try { await helper.DisposeAsync(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Test-owned sound helper cleanup failed.", failures);
        }

        /// <summary>The helper's own sessions, read straight from the adapter's Core Audio enumeration.</summary>
        public bool[] Sessions()
        {
            var native = NativeApi();
            var states = native.ReadSessions(_process.Id, out int hresult);
            Assert.True(states is not null, $"Core Audio could not list the helper's sessions (0x{hresult:X8}).");
            return states!;
        }

        public void UnmuteIfLeftMuted()
        {
            if (_disposed || _process.HasExited) return;
            var states = Sessions();
            if (states.Length > 0 && states.Any(muted => muted))
            {
                var write = NativeApi().SetSessions(_process.Id, null, false);
                Assert.Equal(0, write.HResult);
                Assert.True(write.Accepted > 0);
                Assert.All(Sessions(), muted => Assert.False(muted));
            }
        }

        // The adapter's production Core Audio calls, without its identity checks or the control's ownership map.
        private static IClientAudioNativeApi NativeApi() => new NativeClientAudioApi();

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("stop");
                    _process.StandardInput.Close();
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException) { _process.Kill(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            finally { _process.Dispose(); }
        }
    }
}

/// <summary>
/// A real Core Audio test. Skipped only where Windows reports no active audio output device (for example a headless or remote
/// session, or a VM without one), because no program there can get a Windows audio session. When Core Audio cannot list the devices,
/// the test still runs, so it fails with that error.
/// </summary>
public sealed class AudioOutputFactAttribute : FactAttribute
{
    public AudioOutputFactAttribute() => Skip = SkipReasonFor(ActiveOutputDevices());

    /// <param name="activeOutputDevices">How many active output devices Windows reports; null when Core Audio could not list them.</param>
    internal static string? SkipReasonFor(uint? activeOutputDevices) => activeOutputDevices == 0
        ? "Needs an active audio output device. Windows reports none on this PC (for example in a headless or remote session, or a VM "
          + "without one), so no program here can get a Windows audio session to mute."
        : null;

    /// <summary>Counted through the adapter's own Core Audio declarations (eRender, DEVICE_STATE_ACTIVE); null on a Core Audio error.</summary>
    private static uint? ActiveOutputDevices()
    {
        object? enumerator = null;
        NativeClientAudioApi.CoreAudio.IMMDeviceCollection? devices = null;
        try
        {
            enumerator = new NativeClientAudioApi.CoreAudio.MMDeviceEnumerator();
            if (((NativeClientAudioApi.CoreAudio.IMMDeviceEnumerator)enumerator).EnumAudioEndpoints(0, 0x1, out devices) != 0) return null;
            return devices.GetCount(out uint count) == 0 ? count : null;
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return null; } // For example, no audio service.
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }
}
