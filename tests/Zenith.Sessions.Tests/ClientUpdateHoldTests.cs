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

    // A process sample older than three seconds, or one that missed a client, leaves the running count untrusted until the
    // next fresh one, and every start waits either way. A row held for an update keeps that reason through a short run of
    // them (the trusted count that held it is still recent); only the comparison, re-read each time, can end the hold early.
    [Fact]
    public Task AnUntrustedProcessSampleNeitherStartsNorEndsAnUpdateHold() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell); await monitor.ParkAsync();
        var held = Candidate() with { State = LaunchState.Blocked, Problem = LaunchProblem.InsufficientCapacity, HoldReason = LaunchHoldReason.ClientUpdate };
        // Held as every hold begins: on a fresh count of one running client.
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, held).Hold);
        foreach (var untrusted in new[] { monitor.Snapshot(running: 1) with { ProcessInventoryComplete = false }, monitor.Snapshot(running: 1, processAge: TimeSpan.FromSeconds(4)) })
        {
            Publish(shell, untrusted);
            var kept = Admission(shell, held);
            Assert.False(kept.Allowed, kept.Explanation);
            Assert.Equal(LaunchHoldReason.ClientUpdate, kept.Hold); Assert.Equal(ClientUpdateHold.Explanation, kept.Explanation);
            // A request that is not held yet is never newly held on a count that cannot be trusted.
            var queued = Admission(shell, Candidate());
            Assert.False(queued.Allowed); Assert.Equal(LaunchHoldReason.ResourceHeadroom, queued.Hold);
        }
        // Once the comparison stops holding (here the selection is cleared), the row waits only for a fresh sample.
        shell.SelectedInstallation = null;
        Publish(shell, monitor.Snapshot(running: 1, processAge: TimeSpan.FromSeconds(4)));
        var lifted = Admission(shell, held);
        Assert.False(lifted.Allowed); Assert.Equal(LaunchHoldReason.ResourceHeadroom, lifted.Hold);
    });

    // The grace a held row gets without a trusted count is measured from the newest trusted inventory's observation time and
    // is twice the three-second evidence limit: the 1 Hz sampler's alternating samples stay inside it, a lasting failure does not.
    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(6.001, false)]
    [InlineData(60, false)]
    [InlineData(-1, false)] // Dated in the future: not evidence of anything.
    public void AHeldRowKeepsItsReasonWithoutATrustedCountOnlyWhileTheNewestTrustedInventoryIsWithinTheGrace(double secondsAgo, bool keeps)
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(LaunchReservationProjection.MaximumEvidenceAge * 2, ClientUpdateHold.UntrustedSampleGrace);
        Assert.Equal(keeps, ClientUpdateHold.KeepsHoldWithoutTrustedCount(now.AddSeconds(-secondsAgo), now));
        // Before any trusted inventory there is nothing to keep the reason on.
        Assert.False(ClientUpdateHold.KeepsHoldWithoutTrustedCount(default, now));
    }

    // A monitoring failure that outlasts the grace (an incomplete inventory, one older than the limit, or a failed enumeration,
    // sample after sample): no running count supports the update claim any more, so the row stops saying it starts once no
    // client runs and waits, like every other start, for fresh complete process evidence. Starts stay refused throughout.
    [Fact]
    public Task AHeldRowStopsClaimingAClientRunsOnceUntrustedSamplesOutlastTheNewestTrustedOne() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell); await monitor.ParkAsync();
        var held = Candidate() with { State = LaunchState.Blocked, Problem = LaunchProblem.InsufficientCapacity, HoldReason = LaunchHoldReason.ClientUpdate };
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, held).Hold);
        DesktopSnapshot[] untrusted =
        [
            monitor.Snapshot(running: 1) with { ProcessInventoryComplete = false },
            monitor.Snapshot(running: 1, processAge: TimeSpan.FromSeconds(4)),
            monitor.Snapshot(running: 0) with { Problem = "Synthetic process enumeration failure." }
        ];
        // Within the grace each kind keeps the reason (the N-B8-4 rule).
        foreach (var sample in untrusted)
        {
            Publish(shell, sample);
            var kept = Admission(shell, held);
            Assert.False(kept.Allowed); Assert.Equal(LaunchHoldReason.ClientUpdate, kept.Hold);
        }
        // The failure has now lasted longer than the grace: the newest trusted inventory is older than it.
        AgeTrustedInventory(shell, ClientUpdateHold.UntrustedSampleGrace + TimeSpan.FromSeconds(1));
        foreach (var sample in untrusted)
        {
            Publish(shell, sample);
            var lapsed = Admission(shell, held);
            Assert.False(lapsed.Allowed, lapsed.Explanation);
            Assert.Equal(LaunchHoldReason.ResourceHeadroom, lapsed.Hold);
            Assert.Contains("fresh complete process evidence", lapsed.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("once no Roblox client is running", lapsed.Explanation, StringComparison.Ordinal);
        }
        // The next trusted sample decides again: a client running holds the row once more, none running starts it.
        Publish(shell, monitor.Snapshot(running: 1));
        Assert.Equal(LaunchHoldReason.ClientUpdate, Admission(shell, held).Hold);
        Publish(shell, monitor.Snapshot(running: 0));
        var started = Admission(shell, held);
        Assert.True(started.Allowed, started.Explanation);
    });

    // The queue saves (and fsyncs and republishes) a waiting row only when its reason changes. Samples that alternate between
    // fresh and untrusted, as on a PC busy with clients, must not save it or switch its text each time.
    [Fact]
    public Task AHeldRowIsNotSavedAgainWhileProcessSamplesAlternateBetweenFreshAndUntrusted() => OnUi(async () =>
    {
        var monitor = new Monitor(); var releases = new Releases { Answer = Release() };
        await using var shell = await Ready(monitor, releases);
        await SelectPlayerAsync(shell); await monitor.ParkAsync();
        var phase = new Phase(); var store = new MemoryQueue(); var provider = new NoStart();
        // The real Shell admission behind a memory queue. Each admission publishes its step's sample first and then reports
        // the step it read, so the test knows which sample each queue decision used.
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => Task.FromResult(true), _ => new(false, "Unused"),
            stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero, budgetResolver: _ => new(G(.1), G(.1)),
            candidateAdmission: (candidate, others) =>
            {
                int step = phase.Step; Publish(shell, Phase.Sample(monitor, step));
                var admission = (LaunchAdmission)typeof(ShellViewModel).GetMethod("ReadCandidateLaunchAdmission", Private)!.Invoke(shell, [candidate, others])!;
                phase.Evaluated = step; return admission;
            });
        await queue.InitializeAsync(default);
        var row = await queue.EnqueueAsync(AccountProfile.Create("Held for an update") with { Revision = 3, PreferredPlaceId = 42 }, default, new(456, 7));
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(row.Intent.OperationId) is { State: LaunchState.Blocked, HoldReason: LaunchHoldReason.ClientUpdate });
        int saves = Volatile.Read(ref store.Saves);
        // The queue decides one sample at a time, so once the last step is read every earlier decision's save has finished.
        for (int step = 1; step <= 8; step++)
        {
            phase.Step = step; queue.Signal();
            await Until(() => phase.Evaluated == step);
        }
        Assert.Equal(saves, Volatile.Read(ref store.Saves));
        Assert.Equal(LaunchHoldReason.ClientUpdate, store.Row(row.Intent.OperationId)!.HoldReason);
        Assert.Equal(0, provider.Calls);
    });

    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, stop.Token); }

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
    // Dates the newest trusted process inventory the Shell noted that far back, as a monitoring failure lasting that long leaves it.
    private static void AgeTrustedInventory(ShellViewModel shell, TimeSpan by)
        => typeof(ShellViewModel).GetField("_trustedInventoryUtcTicks", Private)!.SetValue(shell, (DateTimeOffset.UtcNow - by).UtcTicks);
    // The queue's Changed event, as the coordinator raises it after saving a snapshot.
    private static void Observe(ShellViewModel shell, LaunchQueueSnapshot snapshot)
        => typeof(ShellViewModel).GetMethod("QueueChanged", Private)!.Invoke(shell, [snapshot]);

    // Which sample the queue's next admission reads: even steps are fresh and complete with one client running; odd steps are
    // untrusted, alternately missing a client and older than the three-second limit.
    private sealed class Phase
    {
        public volatile int Step, Evaluated = -1;
        public static DesktopSnapshot Sample(Monitor monitor, int step) => (step % 4) switch
        {
            1 => monitor.Snapshot(running: 1) with { ProcessInventoryComplete = false },
            3 => monitor.Snapshot(running: 1, processAge: TimeSpan.FromSeconds(4)),
            _ => monitor.Snapshot(running: 1)
        };
    }
    private sealed class MemoryQueue : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty; public int Saves;
        public LaunchOperation? Row(Guid id) => Volatile.Read(ref _last).Operations.FirstOrDefault(row => row.Intent.OperationId == id);
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref _last));
        // Counted before the row is visible, so a test that sees a saved row also sees its save counted.
        public Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        { Interlocked.Increment(ref Saves); Volatile.Write(ref _last, snapshot with { Operations = snapshot.Operations.ToArray() }); return Task.CompletedTask; }
    }
    // Admission holds every start in these fixtures, so a call here is a failure the test reports.
    private sealed class NoStart : ILaunchProvider
    {
        public int Calls;
        public ProviderCapability AccountLaunch { get; } = new("Synthetic", CapabilityState.Supported, "No native capability proof");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart)); }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct) => Task.FromResult(false);
    }
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
        private long _revision; private volatile bool _park;
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // processAge dates the process inventory (not the system memory sample) that far back.
        public DesktopSnapshot Snapshot(int running = 0, TimeSpan processAge = default)
        {
            var now = DateTimeOffset.UtcNow; var observed = now - processAge;
            var rows = Enumerable.Range(0, running).Select(index => new SessionObservation(70001 + index, observed.AddMinutes(-5).UtcTicks, "Fixture", 0, "",
                false, false, (long)G(.5), (long)G(.5), 1, observed.AddMilliseconds(-1), PrivateWorkingSetBytes: (long)G(.25))).ToArray();
            return new(new(now, G(32), G(20), G(64), G(10), 10), rows,
                ProcessObservedUtc: observed, ProcessRevision: Interlocked.Increment(ref _revision), ProcessInventoryComplete: true);
        }
        // Parks the Shell's own 1 Hz sampler until disposal cancels it, so only the samples a test publishes reach admission.
        public Task ParkAsync() { _park = true; return _parked.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        public async Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_park) { _parked.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return Snapshot();
        }
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
