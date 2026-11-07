using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The real Shell/settings/queue composition with isolated local data and a synthetic monitor.
public sealed class ShellResourceBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-shell-budget-" + Guid.NewGuid().ToString("N"));
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ulong G(double value) => checked((ulong)Math.Ceiling(value * CapacityPolicy.GiB));
    private static LaunchOperation Candidate(LaunchResourceBudget? budget) => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0,
        DateTimeOffset.UtcNow, 7), LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow, ResourceBudget: budget);

    [Fact]
    public Task ActualShellPressureCacheAllowsAPinnedSmallCandidateDespiteAnUnaffordableCurrentDefault() => OnUi(async () =>
    {
        await using var shell = await Ready(new(NewClientPhysicalGiB: 1024, NewClientCommitGiB: 1024), new Monitor(8, 55));
        Assert.True(Admission(shell, Candidate(new(G(.1), G(.1)))).Allowed);
        var denied = Admission(shell, Candidate(new(G(4), G(5))));
        Assert.False(denied.Allowed); Assert.Contains("requires", denied.Explanation);
        Assert.False(Admission(shell, Candidate(null)).Allowed);
    });

    [Fact]
    public Task ActualQueuePinsSavedSettingsAndLaterExplicitSavesAffectOnlyNewRequests() => OnUi(async () =>
    {
        await using var shell = await Ready(new(NewClientPhysicalGiB: 4, NewClientCommitGiB: 5), new Monitor());
        var queue = Field<LaunchCoordinator>(shell, "_launchQueue");
        var first = await queue.EnqueueAsync(AccountProfile.Create("Synthetic first") with { PreferredPlaceId = 42 }, default, new(123, 7));
        Assert.Equal(new LaunchResourceBudget(G(4), G(5)), first.ResourceBudget);
        Assert.Equal(LaunchState.Blocked, first.State); Assert.Equal(LaunchProblem.ProviderUnavailable, first.Problem);
        shell.PhysicalPerClient = .1.ToString(CultureInfo.CurrentCulture); shell.CommitPerClient = .2.ToString(CultureInfo.CurrentCulture);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Contains("saved", shell.Notice, StringComparison.OrdinalIgnoreCase);
        var second = await queue.EnqueueAsync(AccountProfile.Create("Synthetic second") with { PreferredPlaceId = 42 }, default, new(456, 7));
        Assert.Equal(new LaunchResourceBudget(G(.1), G(.2)), second.ResourceBudget);
        using var store = new LaunchQueueStore(_root); var saved = await store.LoadAsync(default);
        Assert.Equal(first.ResourceBudget, saved.Operations.Single(row => row.Intent.OperationId == first.Intent.OperationId).ResourceBudget);
        Assert.Equal(second.ResourceBudget, saved.Operations.Single(row => row.Intent.OperationId == second.Intent.OperationId).ResourceBudget);
        Assert.True(saved.Paused); Assert.Empty(shell.Sessions); // No native process was started.
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LegacyUnknownBudgetBlocksActualAdmissionEvenWithAnExactFreshProcessObservation(bool observed) => OnUi(async () =>
    {
        var monitor = new Monitor(); await using var shell = await Ready(new(), monitor);
        var at = DateTimeOffset.UtcNow; var legacy = Candidate(null) with
        {
            State = LaunchState.ClientStartedUnverified, Problem = LaunchProblem.ClientStartedUnverified,
            ProcessReceipt = new(12345, at.AddMinutes(-3).UtcTicks, at.AddMinutes(-2))
        };
        var desktop = monitor.Snapshot(legacy.ProcessReceipt, observed);
        typeof(ShellViewModel).GetField("_latestDesktop", Private)!.SetValue(shell, desktop);
        PublishAdmission(shell, desktop, new(true, "Coherent synthetic resource health"));
        var candidate = Candidate(new(G(.1), G(.1))); var denied = Admission(shell, candidate, legacy);
        Assert.False(denied.Allowed); Assert.Contains("no recorded memory budget", denied.Explanation);
        var released = legacy with { State = LaunchState.Cancelled, Problem = LaunchProblem.ClientClosureAcknowledged };
        Assert.True(Admission(shell, candidate, released).Allowed);
        Assert.Null(legacy.ResourceBudget); Assert.Equal(LaunchState.ClientStartedUnverified, legacy.State);
    });

    [Fact]
    public Task PublishedResourceDecisionKeepsItsOwnSampleDespiteSeparatePresentationUpdates() => OnUi(async () =>
    {
        var policy = new CapacitySettings(SparePhysicalPercent: 0, SparePhysicalGiB: 0, SpareCommitPercent: 0, SpareCommitGiB: 0);
        var monitor = new Monitor(); await using var shell = await Ready(policy, monitor);
        var healthy = monitor.Snapshot(); var critical = healthy with { Resources = healthy.Resources with { AvailablePhysical = G(.5) } };
        var candidate = Candidate(new(G(.1), G(.1)));
        Assert.True(CapacityPolicy.Evaluate(critical.Resources, policy, [], 0, DateTimeOffset.UtcNow, candidate.ResourceBudget).MemoryOnlySlots > 0);
        var health = new ResourcePressureTracker().Observe(critical.Resources,
            CapacityPolicy.EvaluateSystemPressure(critical.Resources, policy, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        Assert.True(health.Held);
        // Publish the critical sample and held result as one snapshot. A separate UI/preview
        // publication cannot lend its different desktop or an earlier healthy decision.
        PublishAdmission(shell, critical, new(false, health.Explanation));
        typeof(ShellViewModel).GetField("_latestDesktop", Private)!.SetValue(shell, healthy);
        var denied = Admission(shell, candidate); Assert.False(denied.Allowed); Assert.Contains("Critical memory", denied.Explanation);
        Assert.Equal(critical, Field<object>(shell, "_resourceAdmission").GetType().GetProperty("Desktop")!.GetValue(Field<object>(shell, "_resourceAdmission")));
    });

    [Fact]
    public Task LocksStillDenyCandidatesAndLeaveStopAvailable() => OnUi(async () =>
    {
        await using var shell = await Ready(new(), new Monitor());
        var candidate = Candidate(new(1, 1)); Assert.True(Admission(shell, candidate).Allowed);
        shell.Lock.Execute(null); Assert.False(Admission(shell, candidate).Allowed);
        shell.Panic.Execute(null); Assert.True(shell.IsLocked); Assert.Contains("Panic", shell.Notice);
    });

    [Fact]
    public void QueuePresentationShowsExactRequestEstimateAndNotAChangedDefault()
    {
        var row = new LaunchRow(Candidate(new(G(4), G(5))));
        Assert.Contains("4.00", row.ResourceEstimate); Assert.Contains("5.00", row.ResourceEstimate);
        var changes = new List<string?>(); row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        row.Update(row.Operation with { ResourceBudget = new(G(3), G(6)) });
        Assert.Contains(nameof(LaunchRow.ResourceEstimate), changes); Assert.Contains("6.00", row.ResourceEstimate);
        row.Update(row.Operation with { ResourceBudget = null }); Assert.Contains("not recorded", row.ResourceEstimate);
    }

    private async Task<ShellViewModel> Ready(CapacitySettings policy, Monitor monitor)
    {
        using (var settings = new SettingsStore(_root)) await settings.SaveAsync(new(Capacity: policy), default);
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor);
        try
        {
            await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (typeof(ShellViewModel).GetField("_latestDesktop", Private)!.GetValue(shell) is null) await Task.Delay(5, stop.Token);
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static T Field<T>(ShellViewModel shell, string name) => (T)typeof(ShellViewModel).GetField(name, Private)!.GetValue(shell)!;
    private static void PublishAdmission(ShellViewModel shell, DesktopSnapshot desktop, LaunchAdmission admission)
    {
        var field = typeof(ShellViewModel).GetField("_resourceAdmission", Private)!;
        field.SetValue(shell, Activator.CreateInstance(field.FieldType, desktop, admission));
    }
    private static LaunchAdmission Admission(ShellViewModel shell, LaunchOperation candidate, params LaunchOperation[] reservations)
        => (LaunchAdmission)typeof(ShellViewModel).GetMethod("ReadCandidateLaunchAdmission", Private)!.Invoke(shell, [candidate, reservations])!;
    private sealed class Monitor(double physical = 20, double commit = 10) : IDesktopMonitor
    {
        private long _revision;
        public DesktopSnapshot Snapshot(LaunchProcessReceipt? receipt = null, bool observe = false)
        {
            var now = DateTimeOffset.UtcNow; var rows = observe && receipt is not null ? new[]
            {
                new SessionObservation(receipt.ProcessId, receipt.ProcessStartUtcTicks, "Fixture", (nint)1, "Synthetic process", false, true,
                    (long)G(1), (long)G(1), 10, now.AddMilliseconds(-1), PrivateWorkingSetBytes: (long)G(.5))
            } : [];
            return new(new(now, G(32), G(physical), G(64), G(commit), 10), rows,
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
            || !Path.GetFileName(full).StartsWith("zenith-shell-budget-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
