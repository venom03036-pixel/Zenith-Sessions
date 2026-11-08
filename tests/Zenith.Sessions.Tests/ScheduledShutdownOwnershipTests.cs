using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ScheduledShutdownTests
{
    [Fact]
    public async Task CancellationAfterTheToolStartsReturnsUnconfirmedInsteadOfLosingThePossibleSchedule()
    {
        using var stop = new CancellationTokenSource();
        var process = new ShutdownProcessProbe();
        var runner = new ShutdownToolProcessRunner(_ => process);
        var pending = runner.RunAsync("synthetic-shutdown-never-started.exe", [], TimeSpan.FromSeconds(10), stop.Token);
        await process.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        Assert.Null(await pending);
        Assert.True(process.Stopped);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CancellationBeforeTheToolStartsDoesNotSubmitARequest()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        int starts = 0;
        var runner = new ShutdownToolProcessRunner(_ => { starts++; return new ShutdownProcessProbe(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("synthetic", [], TimeSpan.FromSeconds(10), stop.Token));
        Assert.Equal(0, starts);
    }

    [Fact]
    public Task ExitAbortsAnUnconfirmedScheduleThatMayHaveReachedWindows() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { NextScheduleResult = new(ShutdownScheduleStatus.NotConfirmed) };
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync();
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        Assert.False(vm.IsShutdownScheduled);
        Assert.True(vm.CanCancelShutdown);
        await vm.DisposeAsync();
        Assert.Equal(1, probe.CancelCalls);
    });

    [Fact]
    public Task PanicAbortsALateUnconfirmedScheduleAndDoesNotCallItConfirmed() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { Defer = true, NextScheduleResult = new(ShutdownScheduleStatus.NotConfirmed) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync();
        vm.ScheduleShutdown.Execute(null);
        vm.Panic.Execute(null);
        probe.Release(); await vm.ScheduleShutdown.Execution; await vm.ShutdownWork;
        Assert.Equal(1, probe.CancelCalls);
        Assert.False(vm.IsShutdownScheduled);
        Assert.False(vm.CanCancelShutdown);
    });

    [Fact]
    public Task ExitDoesNotAbortAKnownPreexistingSchedule() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { NextScheduleResult = new(ShutdownScheduleStatus.AlreadyScheduled) };
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync();
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        await vm.DisposeAsync();
        Assert.Equal(0, probe.CancelCalls);
    });

    [Fact]
    public Task ExitDuringTheStartedToolWaitAbortsItsPossibleScheduleWithAFreshToken() => OnDispatcherAsync(async () =>
    {
        var process = new ShutdownProcessProbe();
        int aborts = 0;
        var runner = new ShutdownToolProcessRunner(start =>
        {
            if (start.ArgumentList.Contains("/a")) { aborts++; return new ShutdownProcessProbe { CompletedCode = 0 }; }
            return process;
        });
        var scheduler = new WindowsShutdownScheduler(runner);
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: scheduler);
        await vm.InitializeAsync();
        vm.ScheduleShutdown.Execute(null);
        await process.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.DisposeAsync();
        Assert.Equal(1, aborts);
        Assert.True(process.Stopped);
        Assert.True(process.Disposed);
    });

    [Fact]
    public Task SessionEndDoesNotAbortAnUnconfirmedSubmission() => OnDispatcherAsync(async () =>
    {
        var probe = new SchedulerProbe { NextScheduleResult = new(ShutdownScheduleStatus.NotConfirmed) };
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), shutdownScheduler: probe);
        await vm.InitializeAsync();
        vm.ScheduleShutdown.Execute(null); await vm.ScheduleShutdown.Execution;
        vm.PrepareForSessionEnd(TimeSpan.FromSeconds(2));
        await vm.DisposeAsync();
        Assert.Equal(0, probe.CancelCalls);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1190)]
    public async Task CancelledWaitKeepsAKnownToolExitCode(int exitCode)
    {
        using var stop = new CancellationTokenSource();
        var process = new ShutdownProcessProbe();
        var runner = new ShutdownToolProcessRunner(_ => process);
        var pending = runner.RunAsync("synthetic", [], TimeSpan.FromSeconds(10), stop.Token);
        await process.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        process.CompletedCode = exitCode; stop.Cancel();
        Assert.Equal(exitCode, await pending);
        Assert.False(process.Stopped);
        Assert.True(process.Disposed);
    }

    private sealed class ShutdownProcessProbe : IShutdownToolProcess
    {
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Stopped, Disposed;
        internal int? CompletedCode;
        public int? ExitCode => CompletedCode;
        public async Task WaitForExitAsync(CancellationToken ct)
        { Waiting.TrySetResult(); if (CompletedCode is null) await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        public void Stop() => Stopped = true;
        public void Dispose() => Disposed = true;
    }
}
