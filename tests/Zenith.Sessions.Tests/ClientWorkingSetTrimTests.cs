using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientWorkingSetTrimTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;
    private const long Touched = 200 * MiB;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        0, "", false, false, 0, 0, null, Now);

    [Fact]
    public async Task TrimNowDropsATestOwnedHelpersPrivateWorkingSetKeepsItsCommitAndRefusesAReusedPid()
    {
        await using var helper = await TouchedMemoryHelper.StartAsync();
        var exact = new SessionObservation(helper.ProcessId, helper.StartUtcTicks, "Synthetic", 0, "", false, false, 0, 0, null, DateTimeOffset.UtcNow);
        await using var control = new WindowsClientWorkingSetTrimControl(new NativeClientWorkingSetTrimApi(), "powershell.exe",
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        // Windows' own working-set manager may page the idle helper out at any time on a busy, low-memory PC.
        // The test does that itself at each idle point so the worst case runs every time, not by chance, and the
        // helper touches its buffer again just before each measured step so system paging is never blamed on the
        // product. The touch always comes before the product call, never between that call and its reading.
        helper.EmptyAsTheSystemMight();
        await helper.TouchAgainAsync();

        // Same PID, different start: the identity a recycled PID would present. It must not be trimmed.
        var reused =await control.TrimAsync([exact with { ProcessStartUtcTicks = exact.ProcessStartUtcTicks - 1 }], default);
        var refused = Assert.Single(reused.Results);
        Assert.Equal(ClientWorkingSetTrimStatus.InvalidClient, refused.Status);
        Assert.Null(refused.Before);
        var untouched = ProcessMemoryReader.Read(helper.ProcessId, helper.StartUtcTicks);
        Assert.True(untouched?.PrivateWorkingSetBytes >= Touched, $"The refused trim still changed residency: {untouched?.PrivateWorkingSetBytes}");

        helper.EmptyAsTheSystemMight();
        await helper.TouchAgainAsync();
        var trimmed =await control.TrimAsync([exact], default);
        var row = Assert.Single(trimmed.Results);
        Assert.Equal(ClientWorkingSetTrimStatus.Trimmed, row.Status);
        Assert.False(trimmed.Cancelled);
        Assert.NotNull(row.Before);
        Assert.NotNull(row.After);
        long before = row.Before.PrivateWorkingSetBytes!.Value, after = row.After.PrivateWorkingSetBytes!.Value;
        output.WriteLine($"Private working set {before / MiB} MiB -> {after / MiB} MiB; private commit {row.Before.PrivateCommitBytes / MiB} MiB -> {row.After.PrivateCommitBytes / MiB} MiB.");
        Assert.True(before >= Touched, $"Private working set before the trim was {before / MiB} MiB.");
        Assert.True(after <= before - 150 * MiB, $"Private working set went from {before / MiB} MiB to {after / MiB} MiB.");
        Assert.True(row.After.PrivateCommitBytes >= Touched, $"Private commit after the trim was {row.After.PrivateCommitBytes / MiB} MiB.");
        Assert.Contains("does not free committed memory", row.Explanation);
        Assert.Contains("unmeasured", row.Explanation);
    }

    [Fact]
    public async Task TrimsAreSpacedOneSampleTickApartAndEachAfterReadingUsesTheSameExactHandle()
    {
        await using var fixture = new Fixture(1, 2, 3);
        var result = await fixture.Trim(1, 2, 3);

        Assert.Equal(new[]
        {
            "read 1", "trim 1", "wait 1", "read 2", "trim 2", "wait 1", "read 3", "trim 3",
            "wait 10", "read 1", "wait 10", "read 2", "wait 10", "read 3"
        }, fixture.Native.Order.ToArray());
        Assert.All(fixture.Delays.Take(2), wait => Assert.InRange(wait, TimeSpan.FromSeconds(.5), ClientWorkingSetTrimPolicy.TrimSpacing));
        Assert.All(fixture.Delays.Skip(2), wait => Assert.InRange(wait, TimeSpan.FromSeconds(9), ClientWorkingSetTrimPolicy.AfterReadingDelay));
        Assert.False(result.Cancelled);
        Assert.All(result.Results, row =>
        {
            Assert.Equal(ClientWorkingSetTrimStatus.Trimmed, row.Status);
            Assert.Equal(new ClientMemoryReading(800 * MiB, 1000 * MiB), row.Before);
            Assert.Equal(new ClientMemoryReading(40 * MiB, 1000 * MiB), row.After);
        });
        Assert.Equal(3, fixture.Native.Handles.Count);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        Assert.Equal("3 of 3 clients trimmed. Commit is unchanged by a trim, and any benefit is unmeasured.", result.Explanation);
    }

    [Theory]
    [InlineData("reused", ClientWorkingSetTrimStatus.InvalidClient)]
    [InlineData("exited", ClientWorkingSetTrimStatus.InvalidClient)]
    [InlineData("image", ClientWorkingSetTrimStatus.InvalidClient)]
    [InlineData("user", ClientWorkingSetTrimStatus.AccessDenied)]
    [InlineData("absent", ClientWorkingSetTrimStatus.InvalidClient)]
    [InlineData("denied", ClientWorkingSetTrimStatus.AccessDenied)]
    [InlineData("trim-denied", ClientWorkingSetTrimStatus.AccessDenied)]
    public async Task AnUnrelatedInaccessibleOrReusedProcessIsNotTrimmed(string changed, ClientWorkingSetTrimStatus expected)
    {
        await using var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        switch (changed)
        {
            case "reused": client.Start++; break;
            case "exited": client.Running = false; break;
            case "image": client.Image = @"C:\owned-fixture\Other.exe"; break;
            case "user": client.User = "S-1-5-21-other"; break;
            case "absent": fixture.Native.OpenError = 87; break;
            case "denied": fixture.Native.OpenError = 5; break;
            default: fixture.Native.DenyTrim = true; break;
        }
        var row = Assert.Single((await fixture.Trim(1)).Results);
        Assert.Equal(expected, row.Status);
        Assert.Null(row.After);
        Assert.Equal(800 * MiB, client.PrivateWorkingSet);
        Assert.Empty(fixture.Delays);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task DuplicatePidsOrUnhealthyRowsInOneBatchAreRefusedBeforeAnyHandleOpens()
    {
        await using var fixture = new Fixture(1, 2);
        var sessions = new[] { Session(1), Session(1) with { ProcessStartUtcTicks = Session(1).ProcessStartUtcTicks - 1 },
            Session(2) with { Problem = "Synthetic incomplete" } };
        var result = await fixture.Control.TrimAsync(sessions, default);
        Assert.All(result.Results, row => Assert.Equal(ClientWorkingSetTrimStatus.InvalidClient, row.Status));
        Assert.Empty(fixture.Native.Handles);
        Assert.Empty(fixture.Native.Trims);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringTheSpacingWaitPreventsLaterTrimsAndSkipsAfterReadings(bool callerToken)
    {
        using var caller = new CancellationTokenSource();
        await using var fixture = new Fixture(1, 2);
        fixture.OnDelay = (_, ct) =>
        {
            if (callerToken) caller.Cancel(); else fixture.Control.CancelOperations();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var result = await fixture.Control.TrimAsync([Session(1), Session(2)], caller.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Cancelled);
        Assert.Equal(ClientWorkingSetTrimStatus.Trimmed, result.Results[0].Status);
        Assert.NotNull(result.Results[0].Before);
        Assert.Null(result.Results[0].After);
        Assert.Contains("No after reading", result.Results[0].Explanation);
        Assert.Equal(ClientWorkingSetTrimStatus.Cancelled, result.Results[1].Status);
        Assert.Equal(new[] { 1 }, fixture.Native.Trims);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));

        // A stop belongs to the work it stopped. The next explicit request runs normally.
        fixture.OnDelay = null;
        Assert.Equal(ClientWorkingSetTrimStatus.Trimmed, Assert.Single((await fixture.Trim(2)).Results).Status);
    }

    [Fact]
    public async Task ActiveTrimWorkRefusesAnotherRequestAndDisposeJoinsItBeforeClosing()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture(1, 2);
        fixture.OnDelay = (_, ct) => { waiting.TrySetResult(); return Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var active = fixture.Trim(1);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var busy = await fixture.Trim(2);
        Assert.Equal(ClientWorkingSetTrimStatus.Busy, busy.Rejection);
        Assert.Equal(ClientWorkingSetTrimStatus.Busy, Assert.Single(busy.Results).Status);
        Assert.Equal(new[] { 1 }, fixture.Native.Trims);

        await fixture.Control.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(active.IsCompleted);
        var stopped = await active;
        Assert.True(stopped.Cancelled);
        Assert.Null(Assert.Single(stopped.Results).After);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        var closed = await fixture.Trim(2);
        Assert.Equal(ClientWorkingSetTrimStatus.Closed, closed.Rejection);
        Assert.Equal(new[] { 1 }, fixture.Native.Trims);
    }

    [Fact]
    public async Task BatchesAreBoundedAndOwnTheirResultList()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.TrimAsync(
            Enumerable.Range(1, ClientWorkingSetTrimPolicy.MaximumProcesses + 1).Select(Session).ToArray(), default));
        var rows = new List<ClientWorkingSetTrimResult> { new(Session(1), ClientWorkingSetTrimStatus.Trimmed) };
        var batch = new ClientWorkingSetTrimBatchResult(rows, false);
        rows.Clear();
        Assert.Single(batch.Results);
    }

    private sealed class Client(int pid)
    {
        public long Start = Session(pid).ProcessStartUtcTicks;
        public bool Running = true;
        public string Image = @"C:\owned-fixture\RobloxPlayerBeta.exe", User = "S-1-5-21-current";
        public long PrivateWorkingSet = 800 * MiB;
        public readonly long Commit = 1000 * MiB;
    }

    private sealed class Native : IClientTrimNativeApi
    {
        public string? CurrentUserSid => "S-1-5-21-current";
        public readonly Dictionary<int, Client> Clients = [];
        public readonly List<SafeProcessHandle> Handles = [];
        public readonly List<int> Trims = [];
        public readonly ConcurrentQueue<string> Order = new();
        private readonly Dictionary<SafeProcessHandle, int> _handles = [];
        public int OpenError;
        public bool DenyTrim;
        public SafeProcessHandle Open(int pid, out int error)
        {
            error = OpenError;
            var handle = new SafeProcessHandle(OpenError != 0 ? 0 : (nint)(Handles.Count + 1000), ownsHandle: false);
            lock (Handles) Handles.Add(handle);
            if (!handle.IsInvalid) _handles.Add(handle, pid);
            return handle;
        }
        public bool Read(SafeProcessHandle handle, out ClientTrimNativeSnapshot? snapshot)
        {
            Assert.False(handle.IsClosed);
            int pid = _handles[handle];
            var client = Clients[pid];
            Order.Enqueue($"read {pid}");
            snapshot = new(pid, client.Start, client.Running, client.Image, client.User,
                client.Running ? new(client.PrivateWorkingSet, client.Commit) : null);
            return true;
        }
        public bool Trim(SafeProcessHandle handle, out int error)
        {
            Assert.False(handle.IsClosed);
            int pid = _handles[handle];
            Order.Enqueue($"trim {pid}");
            error = DenyTrim ? 5 : 0;
            if (DenyTrim) return false;
            Trims.Add(pid);
            Clients[pid].PrivateWorkingSet = 40 * MiB;
            return true;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Native Native { get; } = new();
        public List<TimeSpan> Delays { get; } = [];
        public Func<TimeSpan, CancellationToken, Task>? OnDelay;
        public WindowsClientWorkingSetTrimControl Control { get; }
        public Fixture(params int[] pids)
        {
            foreach (int pid in pids) Native.Clients.Add(pid, new(pid));
            Control = new(Native, "RobloxPlayerBeta.exe", ClientWorkingSetTrimPolicy.TrimSpacing,
                ClientWorkingSetTrimPolicy.AfterReadingDelay, (wait, ct) =>
                {
                    lock (Delays) Delays.Add(wait);
                    Native.Order.Enqueue($"wait {wait.TotalSeconds:0}");
                    return OnDelay?.Invoke(wait, ct) ?? Task.CompletedTask;
                });
        }
        public Task<ClientWorkingSetTrimBatchResult> Trim(params int[] pids)
            => Control.TrimAsync(pids.Select(Session).ToArray(), default);
        public ValueTask DisposeAsync() => Control.DisposeAsync();
    }

    /// <summary>
    /// A test-owned Windows PowerShell child that allocates and touches 200 MiB of its own memory, reports
    /// ready, then idles. Each "touch" line on its stdin makes it write one byte per page of its own buffer
    /// again and answer "touched"; any other line, or a closed stdin, makes it exit. It is never a Roblox
    /// process and receives no other input.
    /// </summary>
    private sealed class TouchedMemoryHelper : IAsyncDisposable
    {
        private readonly Process _process;
        private TouchedMemoryHelper(Process process, long start) { _process = process; StartUtcTicks = start; }
        public int ProcessId => _process.Id;
        public long StartUtcTicks { get; }

        public static async Task<TouchedMemoryHelper> StartAsync()
        {
            const string script = "$b=[byte[]]::new(200MB); function Touch-Buffer { for($i=0;$i -lt $b.Length;$i+=4096){$b[$i]=1} }; "
                + "Touch-Buffer; [Console]::Out.WriteLine('ready'); "
                + "while([Console]::In.ReadLine() -eq 'touch'){ Touch-Buffer; [Console]::Out.WriteLine('touched') }; $b[0]=2";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(start) ?? throw new InvalidOperationException("The test-owned memory helper did not start.");
            var helper = new TouchedMemoryHelper(process, process.StartTime.ToUniversalTime().Ticks);
            try
            {
                Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)));
                return helper;
            }
            catch { await helper.DisposeAsync(); throw; }
        }

        /// <summary>Makes every page of the helper's buffer resident again and waits for it to confirm.</summary>
        public async Task TouchAgainAsync()
        {
            await _process.StandardInput.WriteLineAsync("touch");
            Assert.Equal("touched", await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        }

        /// <summary>
        /// Empties the helper's working set from outside, the way Windows' memory manager may under pressure.
        /// This goes straight to the native call, not through the product's trim control.
        /// </summary>
        public void EmptyAsTheSystemMight()
        {
            var native = new NativeClientWorkingSetTrimApi();
            using var handle = native.Open(ProcessId, out int error);
            Assert.False(handle.IsInvalid, $"The test could not open its own helper (error {error}).");
            Assert.True(native.Trim(handle, out error), $"The test could not empty its own helper's working set (error {error}).");
            var paged = ProcessMemoryReader.Read(ProcessId, StartUtcTicks);
            Assert.True(paged?.PrivateWorkingSetBytes < Touched, $"The simulated system trim left {paged?.PrivateWorkingSetBytes} bytes resident.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync();
                    _process.StandardInput.Close();
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException) { _process.Kill(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            finally { _process.Dispose(); }
        }
    }
}
