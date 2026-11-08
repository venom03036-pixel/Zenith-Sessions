using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class PublicNetworkGuardTests
{
    [Theory]
    [InlineData(NetworkExposure.NotPublic)]
    [InlineData(NetworkExposure.Unknown)]
    public Task AnOlderQueuedReadingCannotClearANewerImmediatePublicHold(NetworkExposure earlier) => OnDispatcherAsync(async () =>
    {
        var network = new NetworkProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network);
        await vm.InitializeAsync();
        vm.PublicNetworkGuard = true; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        network.Raise(earlier);
        bool? heldBetweenCallbacks = null;
        LaunchHoldReason? admissionBetweenCallbacks = null;
        // Same priority and FIFO ordering: read after the old notification applies, before the newest UI callback.
        var observation = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            heldBetweenCallbacks = vm.IsPublicNetworkHeld;
            admissionBetweenCallbacks = Admission(vm).Hold;
        });
        network.Raise(NetworkExposure.Public);
        Assert.True(vm.IsPublicNetworkHeld); // Immediate admission fence, before any dispatcher callback.
        await Pump(); await observation;
        Assert.True(heldBetweenCallbacks, "An older reading briefly cleared the newest Public hold.");
        Assert.Equal(LaunchHoldReason.PublicNetwork, admissionBetweenCallbacks);
        Assert.True(vm.IsPublicNetworkHeld);
        Assert.True(vm.IsLocked);
    });

    [Theory]
    [InlineData(NetworkExposure.NotPublic, NetworkExposure.Public)]
    [InlineData(NetworkExposure.Public, NetworkExposure.NotPublic)]
    public Task ACallbackFromAStoppedWatchCannotChangeTheRestartedWatch(NetworkExposure oldReading, NetworkExposure restartedReading) => OnDispatcherAsync(async () =>
    {
        var network = new NetworkProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network);
        await vm.InitializeAsync();
        vm.PublicNetworkGuard = true; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        var abandonedCallback = Assert.IsType<Action<NetworkExposure>>(network.CaptureCallback());
        vm.PublicNetworkGuard = false; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        network.Exposure = restartedReading;
        vm.PublicNetworkGuard = true; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal(2, network.StartCalls);
        Assert.Equal(restartedReading == NetworkExposure.Public, vm.IsPublicNetworkHeld);
        abandonedCallback(oldReading); // Monitor's documented in-flight callback can arrive after Stop.
        await Pump();
        Assert.Equal(restartedReading == NetworkExposure.Public, vm.IsPublicNetworkHeld);
        Assert.Equal(restartedReading == NetworkExposure.Public, vm.IsLocked);
    });
}
