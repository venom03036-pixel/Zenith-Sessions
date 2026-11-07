using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.AI;

/// <summary>
/// The live AI Vision click test may only act after its target window really holds the
/// foreground. Windows' foreground lock can refuse SetForegroundWindow, so the wait must
/// retry for a bounded time and then report failure instead of letting the test run on.
/// </summary>
public sealed class LiveForegroundWaitTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShortInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Returns_true_without_requesting_when_the_window_already_has_the_foreground()
    {
        int requests = 0;

        bool acquired = await LiveForegroundWait.TryAcquireAsync(
            isForeground: () => true,
            requestForeground: () => requests++,
            ShortTimeout,
            ShortInterval,
            CancellationToken.None);

        acquired.Should().BeTrue();
        requests.Should().Be(0);
    }

    [Fact]
    public async Task Retries_the_request_until_the_window_takes_the_foreground()
    {
        int requests = 0;

        bool acquired = await LiveForegroundWait.TryAcquireAsync(
            isForeground: () => requests >= 3,
            requestForeground: () => requests++,
            TimeSpan.FromSeconds(5),
            ShortInterval,
            CancellationToken.None);

        acquired.Should().BeTrue();
        requests.Should().Be(3, "a refused request is retried, and retrying stops once the window is foreground");
    }

    [Fact]
    public async Task Returns_false_after_the_deadline_when_the_foreground_is_always_refused()
    {
        int requests = 0;
        var elapsed = Stopwatch.StartNew();

        bool acquired = await LiveForegroundWait.TryAcquireAsync(
            isForeground: () => false,
            requestForeground: () => requests++,
            ShortTimeout,
            ShortInterval,
            CancellationToken.None);

        elapsed.Stop();
        acquired.Should().BeFalse();
        requests.Should().BeGreaterThan(1, "the request is retried while the deadline has not passed");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(ShortTimeout - TimeSpan.FromMilliseconds(20));
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the wait is bounded");
    }

    [Fact]
    public async Task Stops_waiting_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        int requests = 0;

        Func<Task> wait = () => LiveForegroundWait.TryAcquireAsync(
            isForeground: () => false,
            requestForeground: () =>
            {
                requests++;
                cts.Cancel();
            },
            TimeSpan.FromSeconds(30),
            ShortInterval,
            cts.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(1);
    }
}
