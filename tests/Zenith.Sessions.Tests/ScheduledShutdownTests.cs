using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Scheduled Windows shutdown (R-shell-21): an explicit action on Settings that asks Windows' own shutdown tool to turn the PC
// off after a chosen time, with a visible countdown and Cancel. Nothing is scheduled unless the user asks; Panic and Exit
// cancel a shutdown Zenith scheduled, Lock leaves it in place, and a Windows session end never aborts the shutdown it is part of.
public sealed partial class ScheduledShutdownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-shutdown-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SettingsOffersTheShutdownControlsTheFooterShowsTheCountdownAndTheGuideNamesIt()
    {
        var document = ProductionWindow();
        XElement? Named(string name) => document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        var delay = Named("SettingsShutdownDelay");
        Assert.True(delay is not null, "Settings has no control for choosing when Windows should shut down this PC.");
        Assert.Equal("ComboBox", delay!.Name.LocalName);
        Assert.Equal("{Binding ShutdownDelayChoices}", (string?)delay.Attribute("ItemsSource"));
        Assert.Equal("Minutes", (string?)delay.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding ShutdownMinutes}", (string?)delay.Attribute("SelectedValue"));
        Assert.False(string.IsNullOrWhiteSpace((string?)delay.Attribute("AutomationProperties.Name")));

        var schedule = Named("SettingsScheduleShutdown");
        Assert.True(schedule is not null, "Settings has no Schedule shutdown button.");
        Assert.Equal("Button", schedule!.Name.LocalName);
        Assert.Equal("{Binding ScheduleShutdown}", (string?)schedule.Attribute("Command"));
        Assert.Equal("{Binding CanScheduleShutdown}", (string?)schedule.Attribute("IsEnabled"));
        var cancel = Named("SettingsCancelShutdown");
        Assert.True(cancel is not null, "Settings has no Cancel shutdown button.");
        Assert.Equal("{Binding CancelShutdown}", (string?)cancel!.Attribute("Command"));
        Assert.Equal("{Binding CanCancelShutdown}", (string?)cancel.Attribute("IsEnabled"));
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding ShutdownStatus}");

        // The section sits with the other manager-behaviour choices under Appearance, before Performance, where a search result lands.
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(delay), appearance + 1, performance - 1);

        // The countdown and Cancel are visible on every page through the footer, only while a shutdown is scheduled.
        var footer = Named("FooterShutdownCountdown");
        Assert.True(footer is not null, "The footer shows no shutdown countdown.");
        Assert.Contains(Named("FooterNotices")!, footer!.Ancestors());
        Assert.Equal("{Binding IsShutdownScheduled, Converter={StaticResource VisibleIf}}", (string?)footer.Attribute("Visibility"));
        Assert.Contains(footer.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding ShutdownCountdown}");
        var footerCancel = Assert.Single(footer.Descendants().Where(e => e.Name.LocalName == "Button"));
        Assert.Equal("{Binding CancelShutdown}", (string?)footerCancel.Attribute("Command"));
        Assert.Equal("{Binding CanCancelShutdown}", (string?)footerCancel.Attribute("IsEnabled"));

        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Lock hides account controls", StringComparison.Ordinal) == true));
        Assert.Contains("Scheduled shutdown", (string)guide.Attribute("Text")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("power off")]
    [InlineData("turn off")]
    [InlineData("timer")]
    public void SettingsSearchFindsTheScheduledShutdown(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "scheduled-shutdown");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the scheduled shutdown.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    [Fact]
    public void ThePolicyOffersPlainDelaysAndReadsTheCountdownNaturally()
    {
        Assert.Equal([5, 10, 15, 30, 45, 60, 90, 120, 180, 240], ScheduledShutdownPolicy.Choices.Select(c => c.Minutes));
        Assert.Equal("After 5 minutes", ScheduledShutdownPolicy.Choices[0].Label);
        Assert.Equal("After 1 hour", ScheduledShutdownPolicy.Choices.Single(c => c.Minutes == 60).Label);
        Assert.Equal("After 1 hour 30 minutes", ScheduledShutdownPolicy.Choices.Single(c => c.Minutes == 90).Label);
        Assert.Equal("After 2 hours", ScheduledShutdownPolicy.Choices.Single(c => c.Minutes == 120).Label);
        Assert.Equal(30, ScheduledShutdownPolicy.DefaultMinutes);
        Assert.Contains(ScheduledShutdownPolicy.Choices, c => c.Minutes == ScheduledShutdownPolicy.DefaultMinutes);

        Assert.False(ScheduledShutdownPolicy.IsValidMinutes(0));
        Assert.True(ScheduledShutdownPolicy.IsValidMinutes(1));
        Assert.True(ScheduledShutdownPolicy.IsValidMinutes(ScheduledShutdownPolicy.MaximumMinutes));
        Assert.False(ScheduledShutdownPolicy.IsValidMinutes(ScheduledShutdownPolicy.MaximumMinutes + 1));

        Assert.Equal("29 min 58 s", ScheduledShutdownPolicy.Countdown(new TimeSpan(0, 29, 58)));
        Assert.Equal("1 h 0 min 5 s", ScheduledShutdownPolicy.Countdown(new TimeSpan(1, 0, 5)));
        Assert.Equal("45 s", ScheduledShutdownPolicy.Countdown(TimeSpan.FromSeconds(45)));
        // Part seconds round up, so the display never shows 0 s while time is left.
        Assert.Equal("1 s", ScheduledShutdownPolicy.Countdown(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("now", ScheduledShutdownPolicy.Countdown(TimeSpan.Zero));
        Assert.Equal("now", ScheduledShutdownPolicy.Countdown(TimeSpan.FromSeconds(-1)));

        Assert.True(new ShutdownScheduleResult(ShutdownScheduleStatus.Scheduled).Succeeded);
        Assert.True(new ShutdownScheduleResult(ShutdownScheduleStatus.Cancelled).Succeeded);
        Assert.False(new ShutdownScheduleResult(ShutdownScheduleStatus.AlreadyScheduled).Succeeded);
        Assert.Contains("exit code 42", new ShutdownScheduleResult(ShutdownScheduleStatus.NotConfirmed, 42).Explanation, StringComparison.Ordinal);
        Assert.Contains("did not return a confirmed result", new ShutdownScheduleResult(ShutdownScheduleStatus.NotConfirmed).Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSchedulerRunsWindowsOwnShutdownToolFromSystem32AndReadsItsExitCodes()
    {
        var runner = new ToolRunnerProbe();
        await using var scheduler = new WindowsShutdownScheduler(runner);
        string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");

        runner.NextExitCode = 0;
        var scheduled = await scheduler.ScheduleAsync(TimeSpan.FromMinutes(30), default);
        Assert.True(scheduled.Status == ShutdownScheduleStatus.Scheduled, "A zero exit code from the shutdown tool was not read as a scheduled shutdown.");
        var call = Assert.Single(runner.Calls);
        Assert.Equal(tool, call.FileName);
        Assert.Equal(["/s", "/t", "1800", "/c", WindowsShutdownScheduler.Comment], call.Arguments);
        Assert.Equal(WindowsShutdownScheduler.ToolTimeout, call.Timeout);

        runner.NextExitCode = 1190; Assert.Equal(ShutdownScheduleStatus.AlreadyScheduled, (await scheduler.ScheduleAsync(TimeSpan.FromMinutes(1), default)).Status);
        runner.NextExitCode = 1115; Assert.Equal(ShutdownScheduleStatus.AlreadyScheduled, (await scheduler.ScheduleAsync(TimeSpan.FromMinutes(1), default)).Status);
        runner.NextExitCode = 5; Assert.Equal(ShutdownScheduleStatus.AccessDenied, (await scheduler.ScheduleAsync(TimeSpan.FromMinutes(1), default)).Status);
        runner.NextExitCode = 42;
        var odd = await scheduler.ScheduleAsync(TimeSpan.FromMinutes(1), default);
        Assert.Equal(ShutdownScheduleStatus.NotConfirmed, odd.Status); Assert.Equal(42, odd.ErrorCode);
        runner.NextExitCode = null;
        var late = await scheduler.ScheduleAsync(TimeSpan.FromMinutes(1), default);
        Assert.Equal(ShutdownScheduleStatus.NotConfirmed, late.Status); Assert.Null(late.ErrorCode);
        // Part seconds round up, so a shutdown is never scheduled earlier than asked.
        runner.NextExitCode = 0; await scheduler.ScheduleAsync(TimeSpan.FromSeconds(90.2), default);
        Assert.Equal("91", runner.Calls[^1].Arguments[2]);

        runner.NextExitCode = 0;
        Assert.Equal(ShutdownScheduleStatus.Cancelled, (await scheduler.CancelAsync(default)).Status);
        Assert.Equal(["/a"], runner.Calls[^1].Arguments);
        runner.NextExitCode = 1116; Assert.Equal(ShutdownScheduleStatus.NothingScheduled, (await scheduler.CancelAsync(default)).Status);
        runner.NextExitCode = 5; Assert.Equal(ShutdownScheduleStatus.AccessDenied, (await scheduler.CancelAsync(default)).Status);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scheduler.ScheduleAsync(TimeSpan.Zero, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scheduler.ScheduleAsync(TimeSpan.FromMinutes(ScheduledShutdownPolicy.MaximumMinutes) + TimeSpan.FromSeconds(1), default));
        Assert.Equal(10, runner.Calls.Count);

        // A tool that cannot start is reported to the caller, never swallowed.
        runner.Fault = new System.ComponentModel.Win32Exception(2);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => scheduler.CancelAsync(default));
    }

    [Fact]
    public async Task OneRequestRunsAtATimeAndDisposalWaitsForItThenRefusesMore()
    {
        var runner = new ToolRunnerProbe { Defer = true, NextExitCode = 0 };
        var scheduler = new WindowsShutdownScheduler(runner);
        var first = scheduler.ScheduleAsync(TimeSpan.FromMinutes(5), default);
        Assert.False(first.IsCompleted);
        var second = await scheduler.CancelAsync(default);
        Assert.True(second.Status == ShutdownScheduleStatus.Busy, "A second request ran while the first was still with the shutdown tool.");
        Assert.Single(runner.Calls);

        var disposal = scheduler.DisposeAsync();
        Assert.False(disposal.IsCompleted);
        runner.Release();
        await disposal;
        Assert.Equal(ShutdownScheduleStatus.Scheduled, (await first).Status);
        Assert.Equal(ShutdownScheduleStatus.Closed, (await scheduler.CancelAsync(default)).Status);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task TheProcessRunnerReturnsTheExitCodeStopsAToolThatOutlivesItsTimeoutAndReportsAMissingTool()
    {
        var runner = new ShutdownToolProcessRunner();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.Equal(7, await runner.RunAsync(Path.Combine(system, "cmd.exe"), ["/c", "exit", "7"], TimeSpan.FromSeconds(30), default));

        // ping -n 30 would run for about half a minute; the runner stops it at the timeout and reports no exit code.
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await runner.RunAsync(Path.Combine(system, "PING.EXE"), ["-n", "30", "127.0.0.1"], TimeSpan.FromSeconds(1), default));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), "The runner waited for the tool past its timeout.");

        Directory.CreateDirectory(_root);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => runner.RunAsync(Path.Combine(_root, "missing-shutdown.exe"), ["/a"], TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public Task NothingIsScheduledByDefaultAndSchedulingShowsACountdownThatCancelCallsOff() => OnDispatcherAsync(async () =>
    {
        var clock = new ManualClock(); var probe = new SchedulerProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: clock, shutdownScheduler: probe);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.False(vm.IsShutdownScheduled); Assert.Null(vm.ShutdownRemaining); Assert.False(vm.ShutdownCountdownArmed);
        Assert.Equal("No shutdown is scheduled.", vm.ShutdownStatus);
        Assert.Equal(ScheduledShutdownPolicy.DefaultMinutes, vm.ShutdownMinutes);
        Assert.Same(ScheduledShutdownPolicy.Choices, vm.ShutdownDelayChoices);
        Assert.True(vm.CanScheduleShutdown); Assert.False(vm.CanCancelShutdown);

        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.IsShutdownScheduled, "Scheduling a shutdown did not mark one as scheduled.");
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(probe.ScheduleCalls));
        Assert.Equal(TimeSpan.FromMinutes(30), vm.ShutdownRemaining);
        Assert.True(vm.ShutdownCountdownArmed);
        Assert.Contains("in 30 min 0 s", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.Contains("Roblox clients included", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.Contains("Shutdown scheduled", vm.Notice, StringComparison.Ordinal);
        Assert.Contains(vm.Events, e => e.Contains("shutdown scheduled", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.CanScheduleShutdown); Assert.True(vm.CanCancelShutdown);

        // The countdown follows the clock, in the Settings status and in the footer.
        clock.Advance(TimeSpan.FromSeconds(62)); vm.TickShutdownCountdown();
        Assert.Contains("in 28 min 58 s", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.Contains("28 min 58 s", vm.ShutdownCountdown, StringComparison.Ordinal);

        vm.CancelShutdown.Execute(null); await vm.CancelShutdown.Execution;
        Assert.Equal(1, probe.CancelCalls);
        Assert.False(vm.IsShutdownScheduled); Assert.False(vm.ShutdownCountdownArmed); Assert.Null(vm.ShutdownRemaining);
        Assert.Contains("cancelled", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.Contains("Shutdown cancelled", vm.Notice, StringComparison.Ordinal);
        Assert.True(vm.CanScheduleShutdown); Assert.False(vm.CanCancelShutdown);

        // When the time runs out the countdown stops ticking and says so; Windows is doing the rest.
        vm.ShutdownMinutes = 5;
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.Equal(TimeSpan.FromMinutes(5), probe.ScheduleCalls[^1]);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); vm.TickShutdownCountdown();
        Assert.Equal("Windows is shutting down this PC now.", vm.ShutdownStatus);
        Assert.False(vm.ShutdownCountdownArmed); Assert.True(vm.IsShutdownScheduled);
    });

    [Fact]
    public Task PanicCancelsAShutdownZenithScheduledAndLockLeavesItInPlace() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);

        // Panic with nothing scheduled asks Windows for nothing.
        vm.Panic.Execute(null); await vm.ShutdownWork;
        Assert.Equal(0, probe.CancelCalls);

        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.IsShutdownScheduled);

        // Lock is the "away" state: the shutdown the user asked for stays, and its countdown keeps counting behind the lock.
        vm.LockDashboard(); await vm.ShutdownWork;
        Assert.True(vm.IsLocked);
        Assert.Equal(0, probe.CancelCalls);
        Assert.True(vm.IsShutdownScheduled, "Lock cancelled the scheduled shutdown; it must leave it in place.");
        Assert.True(vm.ShutdownCountdownArmed);
        Assert.False(vm.CanCancelShutdown); // Unlock first, like every other control.
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        Assert.True(vm.CanCancelShutdown);

        // Panic promises that clients remain open, so it calls the shutdown off and says so, without replacing its own notice.
        vm.Panic.Execute(null); await vm.ShutdownWork;
        Assert.True(probe.CancelCalls == 1, "Panic did not ask Windows to cancel the scheduled shutdown.");
        Assert.False(vm.IsShutdownScheduled); Assert.False(vm.ShutdownCountdownArmed);
        Assert.Equal("Panic stop: automation paused. Roblox clients remain open.", vm.Notice);
        Assert.Contains("Panic cancelled the scheduled shutdown", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.Contains(vm.Events, e => e.Contains("Panic cancelled the scheduled shutdown", StringComparison.Ordinal));
        Assert.False(vm.HasErrorNotice);
    });

    [Fact]
    public Task AScheduleStillWithWindowsWhenPanicArrivesIsCancelledAsSoonAsWindowsAccepts() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { Defer = true };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        vm.ScheduleShutdown.Execute(null);
        Assert.False(vm.ScheduleShutdown.Execution.IsCompleted); Assert.False(vm.CanScheduleShutdown); Assert.False(vm.CanCancelShutdown);
        vm.Panic.Execute(null);
        probe.Release(); await vm.ScheduleShutdown.Execution; await vm.ShutdownWork;
        Assert.True(probe.CancelCalls == 1, "A shutdown Windows accepted after Panic was left scheduled.");
        Assert.False(vm.IsShutdownScheduled);
        Assert.Contains("Panic cancelled the scheduled shutdown", vm.ShutdownStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task ExitCancelsTheScheduledShutdownButAWindowsSessionEndLeavesItAlone() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        try
        {
            await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
            vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
            Assert.True(vm.IsShutdownScheduled);
        }
        finally { await vm.DisposeAsync(); }
        Assert.True(probe.CancelCalls == 1, "Exit left the shutdown Zenith scheduled in place with no countdown or Cancel to show for it.");
        Assert.True(probe.Disposed);
        Assert.False(vm.ShutdownCountdownArmed);

        // Windows ending the session may be that very shutdown arriving: Zenith must not abort it.
        var ending = new SchedulerProbe();
        var second = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: ending);
        try
        {
            await second.InitializeAsync(); Assert.True(second.IsReady, second.Notice);
            second.ScheduleShutdown.Execute(null); await second.ScheduleShutdown.Execution;
            Assert.True(second.IsShutdownScheduled);
            second.PrepareForSessionEnd(TimeSpan.FromSeconds(2));
            Assert.False(second.ShutdownCountdownArmed);
        }
        finally { await second.DisposeAsync(); }
        Assert.Equal(0, ending.CancelCalls);
        Assert.True(ending.Disposed);
    });

    [Fact]
    public Task AShutdownWindowsAlreadyHoldsIsReportedNotReplacedAndCancelClearsIt() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { NextScheduleResult = new(ShutdownScheduleStatus.AlreadyScheduled) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.False(vm.IsShutdownScheduled);
        Assert.Contains("already has a shutdown scheduled", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.False(vm.HasErrorNotice);
        Assert.True(vm.CanCancelShutdown, "Cancel shutdown must be offered for a shutdown another program scheduled.");
        Assert.True(vm.CanScheduleShutdown);

        vm.CancelShutdown.Execute(null); await vm.CancelShutdown.Execution;
        Assert.Equal(1, probe.CancelCalls);
        Assert.False(vm.CanCancelShutdown);
        Assert.Contains("cancelled", vm.ShutdownStatus, StringComparison.Ordinal);

        // Nothing to cancel is not an error either.
        probe.NextScheduleResult = new(ShutdownScheduleStatus.AlreadyScheduled); probe.NextCancelResult = new(ShutdownScheduleStatus.NothingScheduled);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        vm.CancelShutdown.Execute(null); await vm.CancelShutdown.Execution;
        Assert.False(vm.CanCancelShutdown);
        Assert.Contains("nothing to cancel", vm.ShutdownStatus, StringComparison.Ordinal);
        Assert.False(vm.HasErrorNotice);
    });

    [Fact]
    public Task RefusedAndUnconfirmedAnswersAreErrorsThatScheduleNothingAndAToolFailureIsReported() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { NextScheduleResult = new(ShutdownScheduleStatus.AccessDenied) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.HasErrorNotice, "A refused shutdown request was not shown as an error.");
        Assert.Contains("access denied", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.False(vm.IsShutdownScheduled); Assert.False(vm.CanCancelShutdown);
        vm.DismissError.Execute(null);

        // Not confirmed: Windows may or may not hold a shutdown, so Cancel is offered.
        probe.NextScheduleResult = new(ShutdownScheduleStatus.NotConfirmed, 42);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.HasErrorNotice);
        Assert.Contains("exit code 42", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.False(vm.IsShutdownScheduled); Assert.True(vm.CanCancelShutdown);
        vm.DismissError.Execute(null);

        // A cancel Windows refuses keeps the schedule and the countdown, and says what to do.
        probe.NextScheduleResult = new(ShutdownScheduleStatus.Scheduled);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.IsShutdownScheduled);
        probe.NextCancelResult = new(ShutdownScheduleStatus.AccessDenied);
        vm.CancelShutdown.Execute(null); await vm.CancelShutdown.Execution;
        Assert.True(vm.IsShutdownScheduled); Assert.True(vm.ShutdownCountdownArmed);
        Assert.Contains("still scheduled", vm.ErrorNotice, StringComparison.Ordinal);
        vm.DismissError.Execute(null);

        // The shutdown tool could not be run at all: reported by type, never swallowed, and nothing is marked scheduled.
        probe.NextCancelResult = new(ShutdownScheduleStatus.Cancelled);
        vm.CancelShutdown.Execute(null); await vm.CancelShutdown.Execution;
        Assert.False(vm.IsShutdownScheduled);
        probe.Fault = new System.ComponentModel.Win32Exception(2);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.True(vm.HasErrorNotice);
        Assert.Contains("shutdown tool could not be run", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.False(vm.IsShutdownScheduled);
        Assert.Contains(vm.Events, e => e.Contains("Win32Exception", StringComparison.Ordinal));
    });

    [Fact]
    public Task SampleModeNeverAsksWindowsToShutDown() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock());
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.False(vm.CanScheduleShutdown, "Sample mode offered to shut down the PC.");
        Assert.Contains("sample", vm.ShutdownStatus, StringComparison.OrdinalIgnoreCase);
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.False(vm.IsShutdownScheduled);
    });

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(ScheduledShutdownTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    private sealed record ToolCall(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);

    private sealed class ToolRunnerProbe : IShutdownToolRunner
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<ToolCall> Calls = [];
        internal int? NextExitCode;
        internal bool Defer;
        internal Exception? Fault;
        internal void Release() { _gate.TrySetResult(); _gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<int?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(new(fileName, arguments.ToArray(), timeout));
            if (Fault is { } fault) throw fault;
            if (Defer) await _gate.Task.WaitAsync(ct);
            return NextExitCode;
        }
    }

    private sealed class SchedulerProbe : IShutdownScheduler
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<TimeSpan> ScheduleCalls = [];
        internal int CancelCalls;
        internal bool Defer, Disposed;
        internal Exception? Fault;
        internal ShutdownScheduleResult NextScheduleResult = new(ShutdownScheduleStatus.Scheduled);
        internal ShutdownScheduleResult NextCancelResult = new(ShutdownScheduleStatus.Cancelled);
        internal void Release() { _gate.TrySetResult(); _gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<ShutdownScheduleResult> ScheduleAsync(TimeSpan delay, CancellationToken ct)
        {
            ScheduleCalls.Add(delay);
            if (Fault is { } fault) { Fault = null; throw fault; }
            if (Defer) await _gate.Task.WaitAsync(ct);
            return NextScheduleResult;
        }
        public Task<ShutdownScheduleResult> CancelAsync(CancellationToken ct)
        {
            CancelCalls++;
            return Task.FromResult(NextCancelResult);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // A tracked delegate owns the async work; no window, tray icon or desktop input is created.
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-shutdown-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
