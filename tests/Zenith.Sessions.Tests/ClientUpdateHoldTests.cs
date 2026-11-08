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
// public release source. No process, network, Roblox client or account is touched.
public sealed class ClientUpdateHoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-update-hold-" + Guid.NewGuid().ToString("N"));
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PlayerFolder = "version-00000000000000aa";
    private const string Player = "C:\\Synthetic\\Roblox\\Versions\\" + PlayerFolder + "\\RobloxPlayerBeta.exe";
    private static ulong G(double value) => checked((ulong)Math.Ceiling(value * CapacityPolicy.GiB));
    private static PublicRobloxClientRelease Release(string upload = "version-1111111111111111", DateTimeOffset? at = null)
        => new("0.746.0.7461200", upload, at ?? DateTimeOffset.UtcNow);
    private static LaunchOperation Candidate() => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, DateTimeOffset.UtcNow, 7),
        LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow, ResourceBudget: new(G(.1), G(.1)));

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
        else Assert.DoesNotContain("would close", decision.Reason, StringComparison.Ordinal);
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
        Assert.StartsWith("Roblox has an update that would close running clients.", held.Explanation, StringComparison.Ordinal);
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
        // A release only moves forward, so a player older than the last answer is still older than Roblox's current one.
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, Candidate()).Hold);
        Assert.DoesNotContain("synthetic unreachable service", shell.Notice + shell.ErrorNotice + shell.NativeTrialStatus, StringComparison.Ordinal);
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
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, installationDiscovery: new Inventory(), clientReleases: releases);
        try
        {
            await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (typeof(ShellViewModel).GetField("_latestDesktop", Private)!.GetValue(shell) is null) await Task.Delay(5, stop.Token);
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static async Task<InstallationCandidateRow> SelectPlayerAsync(ShellViewModel shell)
    {
        shell.RefreshInstallations.Execute(null); await shell.RefreshInstallations.Execution;
        var player = Assert.Single(shell.InstallationCandidates);
        shell.SelectedInstallation = player;
        Assert.Equal(Player, shell.NativeTrialInstallation);
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

    private sealed class Releases : IPublicRobloxClientReleaseDirectory
    {
        public int Calls; public PublicRobloxClientRelease? Answer; public Exception? Throw;
        public Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Answer is null ? new PublicDirectoryResult<PublicRobloxClientRelease>(PublicDirectoryState.Failure, null, "Synthetic failure.")
                : new(PublicDirectoryState.Success, Answer, "Synthetic public release."));
        }
    }
    private sealed class Inventory : IClientInstallationDiscovery
    {
        public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct) => Task.FromResult(new ClientInstallationSnapshot(DateTimeOffset.UtcNow,
            ClientInstallationSnapshotStatus.Complete,
            [new(ClientInstallationProduct.Roblox, Player, "Synthetic Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified,
                [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)])], []));
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
