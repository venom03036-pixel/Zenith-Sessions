using System.Diagnostics;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

internal static class PackagedWindowsOverviewCheck
{
    public static async Task VerifyAsync(CancellationToken ct)
    {
        var provider = new FixtureProvider();
        await using var owner = new ClientPreviewCoordinator(provider);
        long start = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        var targets = Enumerable.Range(0, 16).Select(index => new ClientPreviewTarget(new(null, 700000 + index, start + index), (nint)(800000 + index))).ToArray();
        owner.StartOverview(targets, DateTimeOffset.UtcNow.AddSeconds(30));
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = owner.GetSnapshot();
            if (state.Mode != ClientPreviewMode.Overview || state.Results.Count != 16)
                throw new InvalidDataException("The overview lost its full observed roster.");
            if (state.Results.All(item => item.Frame is not null)) break;
            if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("The synthetic overview did not visit every target.");
            await Task.Delay(20, ct);
        }
        if (provider.MaximumBatch > 4 || provider.Visited.Count != 16)
            throw new InvalidDataException("Overview capture exceeded its batch budget or omitted targets.");
        await owner.StopAsync(ct);
        if (owner.GetSnapshot().Results.Any(item => item.Frame is not null) || provider.Releases != 1)
            throw new InvalidDataException("Stopped overview pixels or capture ownership were retained.");
    }
    private sealed class FixtureProvider : IClientPreviewBatchProvider
    {
        public readonly HashSet<ClientPreviewTarget> Visited = [];
        public int MaximumBatch, Releases;
        private long _timestamp;
        public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => throw new InvalidOperationException("The overview must use the explicit progress capability.");
        public Task<ClientPreviewBatchResult> CaptureBatchAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); MaximumBatch = Math.Max(MaximumBatch, targets.Count);
            var results = targets.Select(target =>
            {
                Visited.Add(target);
                return new ClientPreviewResult(target, ClientPreviewStatus.Ready,
                    new(target, generation, 1, 1, 4, new byte[] { 160, 184, 64, 255 }, DateTimeOffset.UtcNow, 1, 1, ++_timestamp), "Synthetic packaged fixture");
            }).ToArray();
            return Task.FromResult(new ClientPreviewBatchResult(results.Length, Array.AsReadOnly(results)));
        }
        public Task ReleaseAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Releases++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
