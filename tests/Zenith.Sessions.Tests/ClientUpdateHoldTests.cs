using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The real Shell/queue composition with isolated local data, a synthetic monitor, a fake installation inventory and a fake
// public release source. No process, network, Roblox client or account is touched; the "player" is an empty file in the
// fixture folder, because a hold re-checks that the selected player still exists.
public sealed class ClientUpdateHoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-update-hold-" + Guid.NewGuid().ToString("N"));
    private readonly Inventory _inventory;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PlayerFolder = "version-00000000000000aa", OlderFolder = "version-1111111111111111";
    private static ulong G(double value) => checked((ulong)Math.Ceiling(value * CapacityPolicy.GiB));
    public ClientUpdateHoldTests() => _inventory = new(_root);
    private static PublicRobloxClientRelease Release(string upload = OlderFolder, DateTimeOffset? at = null)
        => new("0.746.0.7461200", upload, at ?? DateTimeOffset.UtcNow);
    private static LaunchOperation Candidate() => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, DateTimeOffset.UtcNow, 7),
        LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow, ResourceBudget: new(G(.1), G(.1)));
    // A trial whose provider started a Roblox process: the queue recorded its process-start receipt.
    private static LaunchOperation Started() => Candidate() with { State = LaunchState.ClientStartedUnverified, Problem = LaunchProblem.ClientStartedUnverified,
        ProcessReceipt = new(4242, DateTime.UtcNow.AddSeconds(-2).Ticks, DateTimeOffset.UtcNow) };

    [Theory]
    [InlineData(ClientReleaseMatch.NotCurrent, 1, true)]
    [InlineData(ClientReleaseMatch.NotCurrent, 12, true)]
    [InlineData(ClientReleaseMatch.NotCurrent, 0, false)]
    [InlineData(ClientReleaseMatch.Current, 3, false)]
    [InlineData(ClientReleaseMatch.NotCompared, 3, false)]
    [InlineData(ClientReleaseMatch.NotChecked, 3, false)]
    [InlineData((ClientReleaseMatch)99, 3, false)]
    public void OnlyAKnownOutdatedPlayerWithAClientRunningIsHeld(ClientReleaseMatch selected, int running, bool hold)
    {
        var decision = ClientUpdateHold.Evaluate(selected, running);
        Assert.Equal(hold, decision.Hold);
        if (hold) Assert.Equal(ClientUpdateHold.Explanation, decision.Reason);
        else Assert.DoesNotContain("could close", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeClientCountIsAProgrammingError()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ClientUpdateHold.Evaluate(ClientReleaseMatch.NotCurrent, -1));

    [Fact]
    public Task ARunningClientHoldsAStartFromAnOutdatedSelectedPlayerUntilNoClientRuns() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        var player = await SelectPlayerAsync(shell);
        Assert.Equal(ClientReleaseMatch.NotCurrent, player.Release);

        Publish(shell, monitor.Snapshot(running: 1));
        var held = Admission(shell, Candidate());
        Assert.False(held.Allowed, "A start from an outdated player must wait while a client runs: Roblox would install the update and close it.");
        // Hedged: only the default live channel is read, and a staged rollout or another channel may not update this player.
        Assert.StartsWith("Roblox may have an update that could close running clients:", held.Explanation, StringComparison.Ordinal);
        Assert.Contains("default live channel", held.Explanation, StringComparison.Ordinal);
        Assert.Equal(LaunchHoldReason.ClientUpdate, held.Hold);

        Publish(shell, monitor.Snapshot(running: 0));
        Assert.True(Admission(shell, Candidate()).Allowed, "With no client running there is nothing to close.");

        // Only the accepted trial installation is compared: a cleared selection (Refresh, Cancel check, Panic, Lock) holds nothing.
        shell.SelectedInstallation = null;
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.True(Admission(shell, Candidate()).Allowed);
    });

    [Theory]
    [InlineData("unanswered")]
    [InlineData("unreachable")]
    [InlineData("no release source")]
    [InlineData("current")]
    public Task AnUnknownReleaseOrACurrentPlayerNeverHoldsAStart(string release) => OnUi(async () =>
    {
        var monitor = new Monitor();
        var releases = release switch
        {
            "unanswered" => new Releases(),
            "unreachable" => new Releases { Throw = new HttpRequestException("synthetic unreachable service") },
            "current" => new Releases { Answer = Release(PlayerFolder) },
            _ => null
        };
        await using var shell = await Ready(monitor, releases);
        var player = await SelectPlayerAsync(shell);
        Assert.Equal(release == "current" ? ClientReleaseMatch.Current : ClientReleaseMatch.NotChecked, player.Release);
        Publish(shell, monitor.Snapshot(running: 2));
        var admission = Admission(shell, Candidate());
        Assert.True(admission.Allowed, admission.Explanation);
    });

    [Fact]
    public Task AStartAsksRobloxAgainOnlyWhenItsLastAnswerIsTenMinutesOld() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release(at: DateTimeOffset.UtcNow.AddMinutes(-9)) };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell); Assert.Equal(1, releases.Calls);
        await ReadForStart(shell);
        Assert.Equal(1, releases.Calls); // Read nine minutes ago: reused, no request.
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.False(Admission(shell, Candidate()).Allowed);
    });

    [Fact]
    public Task AStaleAnswerIsAskedForOnceAndANewerReleaseDecidesTheHold() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release(at: DateTimeOffset.UtcNow.AddMinutes(-11)) };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.False(Admission(shell, Candidate()).Allowed);
        // Roblox now names the selected player's own folder as its release: the player is current and nothing is held.
        releases.Answer = Release(PlayerFolder);
        await ReadForStart(shell); Assert.Equal(2, releases.Calls);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.True(Admission(shell, Candidate()).Allowed);
        await ReadForStart(shell); Assert.Equal(2, releases.Calls);
    });

    [Fact]
    public Task AFailedReReadKeepsTheLastValidReleaseAndNeverFailsTheStart() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release(at: DateTimeOffset.UtcNow.AddMinutes(-30)) };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell);
        releases.Throw = new HttpRequestException("synthetic unreachable service");
        await ReadForStart(shell); Assert.Equal(2, releases.Calls);
        // The kept answer was read after this player was observed (the same refresh), and releases only move forward, so a
        // player that differed from it then is still older than Roblox's current release now.
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, Candidate()).Hold);
        Assert.DoesNotContain("synthetic unreachable service", shell.Notice + shell.ErrorNotice + shell.NativeTrialStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task AClientStartedFromTheOutdatedPlayerEndsItsHoldForThatRelease() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        Assert.Equal(ClientReleaseMatch.NotCurrent, (await SelectPlayerAsync(shell)).Release);
        // The first account's trial: no client runs, so nothing can close and it starts.
        Publish(shell, monitor.Snapshot(running: 0));
        Assert.True(Admission(shell, Candidate()).Allowed);
        // The queue records that trial's process receipt: Roblox ran the selected player, and updates an outdated one as it starts.
        Observe(shell, new(false, [Started()]));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        // The next account's trial while that client runs is not held with the update claim: the start already let Roblox
        // update this player, or showed that it does not update it for this release (another channel, a staged rollout).
        Publish(shell, monitor.Snapshot(running: 1));
        var next = Admission(shell, Candidate());
        Assert.True(next.Allowed, next.Explanation); Assert.Equal(LaunchHoldReason.ResourceHeadroom, next.Hold);
        Assert.DoesNotContain("could close", next.Explanation, StringComparison.Ordinal);
        Assert.Contains("refresh installations", shell.InstallationReleaseStatus, StringComparison.OrdinalIgnoreCase);

        // A refresh that still finds that player and the same release: the start did not replace it, so it is never held for that release.
        Assert.Equal(ClientReleaseMatch.NotCurrent, (await SelectPlayerAsync(shell)).Release);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.True(Admission(shell, Candidate()).Allowed);
        // A newer release is a new update that may close clients, so it holds again.
        releases.Answer = Release("version-2222222222222222");
        await SelectPlayerAsync(shell);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, Candidate()).Hold);
    });

    [Fact]
    public Task ASelectedPlayerThatNoLongerExistsIsNeverHeld() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, Candidate()).Hold);
        // Roblox's updater replaced that version folder (for example when Roblox was started outside Zenith).
        File.Delete(_inventory.Player);
        Publish(shell, monitor.Snapshot(running: 1));
        var admission = Admission(shell, Candidate());
        Assert.True(admission.Allowed, admission.Explanation);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task ANewerPlayerIsNeverHeldOnAnOlderKeptReleaseItWasNotComparedWith(bool staleKeptRelease, bool stopped) => OnUi(async () =>
    {
        // Release A is read while this PC still has A's folder.
        var monitor = new Monitor(); var releases = new Releases { Answer = Release(OlderFolder, staleKeptRelease ? DateTimeOffset.UtcNow.AddMinutes(-30) : null) };
        _inventory.Folder = OlderFolder;
        await using var shell = await Ready(monitor, releases);
        Assert.Equal(ClientReleaseMatch.Current, (await SelectPlayerAsync(shell)).Release);
        // Roblox then ships B and this PC updates: the next refresh finds only B's folder, and Roblox's answer fails or Cancel check stops it.
        _inventory.Folder = PlayerFolder;
        InstallationCandidateRow player;
        if (stopped)
        {
            releases.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.RefreshInstallations.Execute(null);
            for (int wait = 0; wait < 100 && releases.Calls < 2; wait++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, releases.Calls);
            player = Assert.Single(shell.InstallationCandidates); shell.SelectedInstallation = player;
            shell.CancelInstallationDiscovery.Execute(null);
            await shell.RefreshInstallations.Execution;
            releases.Gate = null;
        }
        else
        {
            releases.Throw = new HttpRequestException("synthetic unreachable service");
            player = await SelectPlayerAsync(shell);
        }
        Assert.Equal(_inventory.Player, shell.NativeTrialInstallation);
        Assert.Equal("Not checked", player.ReleaseStatus);
        Publish(shell, monitor.Snapshot(running: 1));
        var admission = Admission(shell, Candidate());
        Assert.True(admission.Allowed, admission.Explanation);

        // The start's own read: a fresh kept A is reused, a stale one is asked for again and that read fails too. Either way
        // only A is known, which this row was never compared with, so the row still reads "Not checked" and nothing holds.
        releases.Throw = new HttpRequestException("synthetic unreachable service");
        await ReadForStart(shell);
        Assert.Equal(staleKeptRelease ? 3 : 2, releases.Calls);
        Publish(shell, monitor.Snapshot(running: 1));
        admission = Admission(shell, Candidate());
        Assert.True(admission.Allowed, admission.Explanation); Assert.Equal("Not checked", player.ReleaseStatus);
    });

    [Fact]
    public Task ANewerReleaseReadForAStartFillsTheRowsSoTheHoldMatchesWhatTheRowShows() => OnUi(async () =>
    {
        // The refresh's own answer failed: the row reads "Not checked" and nothing holds.
        var monitor = new Monitor(); var releases = new Releases { Throw = new HttpRequestException("synthetic unreachable service") };
        await using var shell = await Ready(monitor, releases);
        var player = await SelectPlayerAsync(shell);
        Assert.Equal("Not checked", player.ReleaseStatus);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.True(Admission(shell, Candidate()).Allowed);
        // The start asks again and Roblox answers: the row shows that comparison, and the hold follows the row.
        releases.Throw = null; releases.Answer = Release();
        await ReadForStart(shell); Assert.Equal(2, releases.Calls);
        Assert.Equal("Not current", player.ReleaseStatus);
        Assert.StartsWith("Update pending:", shell.InstallationReleaseStatus, StringComparison.Ordinal);
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, Candidate()).Hold);
    });

    [Fact]
    public void AWaitingRowSaysWhyItWaitsAndTheReasonIsNeverSaved()
    {
        var waiting = Candidate() with { State = LaunchState.Blocked, Problem = LaunchProblem.InsufficientCapacity };
        var row = new LaunchRow(waiting);
        Assert.Equal(LaunchDescriptions.Explain(LaunchProblem.InsufficientCapacity), row.Detail);
        var changes = new List<string?>(); row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        row.Update(waiting with { HoldReason = LaunchHoldReason.ClientUpdate });
        Assert.Contains(nameof(LaunchRow.Detail), changes); Assert.Equal(ClientUpdateHold.Explanation, row.Detail);
        Assert.True(LaunchDescriptions.AwaitsCapacity(row.Operation)); Assert.True(LaunchDescriptions.AwaitsClientUpdate(row.Operation));
        // Only a waiting row carries the reason; any other state explains its own problem.
        var queued = waiting with { State = LaunchState.Queued, Problem = LaunchProblem.None, HoldReason = LaunchHoldReason.ClientUpdate };
        Assert.False(LaunchDescriptions.AwaitsClientUpdate(queued)); Assert.Equal("", LaunchDescriptions.Explain(queued));
        Assert.DoesNotContain(nameof(LaunchOperation.HoldReason), JsonSerializer.Serialize(new LaunchQueueSnapshot(false, [row.Operation])), StringComparison.Ordinal);
    }

    private async Task<ShellViewModel> Ready(Monitor monitor, Releases? releases)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, installationDiscovery: _inventory, clientReleases: releases);
        try
        {
            await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (typeof(ShellViewModel).GetField("_latestDesktop", Private)!.GetValue(shell) is null) await Task.Delay(5, stop.Token);
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private async Task<InstallationCandidateRow> SelectPlayerAsync(ShellViewModel shell)
    {
        shell.RefreshInstallations.Execute(null); await shell.RefreshInstallations.Execution;
        var player = Assert.Single(shell.InstallationCandidates);
        shell.SelectedInstallation = player;
        Assert.Equal(_inventory.Player, shell.NativeTrialInstallation);
        return player;
    }
    private static Task ReadForStart(ShellViewModel shell)
        => (Task)typeof(ShellViewModel).GetMethod("ReadLaunchReleaseAsync", Private)!.Invoke(shell, [CancellationToken.None])!;
    // The sampler publishes every second from a monitor that reports no client; each check publishes its own sample first.
    private static void Publish(ShellViewModel shell, DesktopSnapshot desktop)
    {
        var field = typeof(ShellViewModel).GetField("_resourceAdmission", Private)!;
        field.SetValue(shell, Activator.CreateInstance(field.FieldType, desktop, new LaunchAdmission(true, "Coherent synthetic resource health")));
    }
    private static LaunchAdmission Admission(ShellViewModel shell, LaunchOperation candidate, params LaunchOperation[] reservations)
        => (LaunchAdmission)typeof(ShellViewModel).GetMethod("ReadCandidateLaunchAdmission", Private)!.Invoke(shell, [candidate, reservations])!;
    // The queue's Changed event, as the coordinator raises it after saving a snapshot.
    private static void Observe(ShellViewModel shell, LaunchQueueSnapshot snapshot)
        => typeof(ShellViewModel).GetMethod("QueueChanged", Private)!.Invoke(shell, [snapshot]);

    private sealed class Releases : IPublicRobloxClientReleaseDirectory
    {
        public int Calls; public PublicRobloxClientRelease? Answer; public Exception? Throw;
        // Set to hold the answer until the caller cancels it (Cancel check).
        public TaskCompletionSource? Gate;
        public async Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Throw is not null) throw Throw;
            return Answer is null ? new PublicDirectoryResult<PublicRobloxClientRelease>(PublicDirectoryState.Failure, null, "Synthetic failure.")
                : new(PublicDirectoryState.Success, Answer, "Synthetic public release.");
        }
    }
    private sealed class Inventory(string root) : IClientInstallationDiscovery
    {
        public string Folder = PlayerFolder;
        public string Player => Path.Combine(root, "Roblox", "Versions", Folder, "RobloxPlayerBeta.exe");
        public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct)
        {
            // An empty stand-in file, never a program: the hold only asks whether the selected path still exists.
            string player = Player; Directory.CreateDirectory(Path.GetDirectoryName(player)!);
            if (!File.Exists(player)) File.WriteAllBytes(player, []);
            return Task.FromResult(new ClientInstallationSnapshot(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Complete,
                [new(ClientInstallationProduct.Roblox, player, "Synthetic Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified,
                    [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)])], []));
        }
    }
    private sealed class Monitor : IDesktopMonitor
    {
        private long _revision;
        public DesktopSnapshot Snapshot(int running = 0)
        {
            var now = DateTimeOffset.UtcNow;
            var rows = Enumerable.Range(0, running).Select(index => new SessionObservation(70001 + index, now.AddMinutes(-5).UtcTicks, "Fixture", 0, "",
                false, false, (long)G(.5), (long)G(.5), 1, now.AddMilliseconds(-1), PrivateWorkingSetBytes: (long)G(.25))).ToArray();
            return new(new(now, G(32), G(20), G(64), G(10), 10), rows,
                ProcessObservedUtc: now, ProcessRevision: Interlocked.Increment(ref _revision), ProcessInventoryComplete: true);
        }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot()); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-update-hold-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
