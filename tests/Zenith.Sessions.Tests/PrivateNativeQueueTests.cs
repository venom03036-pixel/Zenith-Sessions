using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class NativeTrialQueueTests
{
    [Theory]
    [InlineData(CapabilityState.Unverified)] [InlineData(CapabilityState.Supported)]
    public async Task PrivateStartsRequireOneExplicitTrialEvenForAnOrdinarilyQualifiedProvider(CapabilityState state)
    {
        var store = new Store(); var provider = new Provider(state);
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var profile = Profile(); var destination = new RobloxRequestedDestination(1818, null, RobloxServerKind.Private, new(Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueAsync(profile, default, Binding, destination));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchReview(destination, [(profile, Binding)]));
        var trial = await queue.EnqueueTrialAsync(profile, default, Binding, destination);
        Assert.Equal(destination.PrivateServer, trial.Intent.PrivateServer);
        Assert.True(await queue.ResumeTrialAsync(trial.Intent.OperationId, default));
        await Until(() => store.Row(trial.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(1, provider.Starts);
        Assert.Equal(destination.PrivateServer, Assert.Single(provider.Contexts).OriginalIntent.PrivateServer);
    }

    [Fact]
    public async Task PrivateTrialConsentDoesNotSurviveReloadWithQualifiedProvider()
    {
        var store = new Store(); var provider = new Provider(CapabilityState.Supported);
        var profile = Profile(); var target = new RobloxRequestedDestination(1818, null, RobloxServerKind.Private, new(Guid.NewGuid(), 4));
        await using (var queue = Queue(store, provider))
        {
            await queue.InitializeAsync(default);
            await queue.EnqueueTrialAsync(profile, default, Binding, target);
        }
        await using (var reloaded = Queue(store, provider))
        {
            await reloaded.InitializeAsync(default); await reloaded.TryResumeAsync(default);
            Assert.False(reloaded.IsTrialOperation(Assert.Single(store.Last.Operations).Intent.OperationId));
            Assert.Equal(0, provider.Preparations); Assert.Equal(0, provider.Starts);
        }
    }
}
