using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class PublicNetworkGuardTests
{
    [Fact]
    public Task SavedPublicNetworkGuardIsAppliedBeforeAnyStartupUpdateAttempt() => OnDispatcherAsync(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"PublicNetworkGuard\":true}");
        string updatePath = Path.Combine(_root, "release-updates.json");
        await File.WriteAllTextAsync(updatePath, JsonSerializer.Serialize(new ReleaseUpdateState(new("https://updates.example.test/feed.json", new string('A',64), true))));
        var network = new UpdateStartupNetworkProbe(updatePath);
        var backend = new UpdateStartupBackend();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network, releaseUpdateBackend: backend);
        await vm.InitializeAsync(); await Pump();
        Assert.True(vm.IsReady, vm.Notice);
        Assert.True(network.Started);
        Assert.Null(network.AttemptBeforeNetworkGuard);
        Assert.True(vm.IsLocked); Assert.True(vm.IsPublicNetworkHeld);
        Assert.Equal(0, backend.Calls);
        vm.ReleaseUpdates.CheckNow.Execute(null); await vm.ReleaseUpdates.CheckNow.Execution;
        Assert.Equal(0, backend.Calls);
        Assert.Null(ReleaseUpdateSession.ReadState(updatePath).LastAttemptUtc);
    });

    private sealed class UpdateStartupNetworkProbe(string updatePath) : IPublicNetworkMonitor
    {
        public bool Started;
        public DateTimeOffset? AttemptBeforeNetworkGuard;
        public event Action<NetworkExposure>? Changed { add {} remove {} }
        public NetworkExposure Start() { Started=true; AttemptBeforeNetworkGuard=ReleaseUpdateSession.ReadState(updatePath).LastAttemptUtc; return NetworkExposure.Public; }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class UpdateStartupBackend : IReleaseUpdateBackend
    {
        public int Calls;
        public Task<StagedUpdate> StageAsync(ReleaseUpdateSettings settings, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new StagedUpdate("synthetic-stage", "2.0.0.0")); }
    }
}
