using System.Collections.Concurrent;
using FluentAssertions;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class OwnedEmergencyHotkeyListenerTests
{
    private sealed class Pump : IEmergencyMessagePump, IDisposable
    {
        private readonly BlockingCollection<Action> _messages = new();
        public int ThreadId { get; private set; }
        public void Prepare() => ThreadId = Environment.CurrentManagedThreadId;
        public bool ReadAndDispatch()
        {
            try { var action = _messages.Take(); action(); return true; }
            catch (InvalidOperationException) { return false; }
        }
        public void Post(Action action) => _messages.Add(action);
        public void RequestStop() => _messages.CompleteAdding();
        public void Dispose() => _messages.Dispose();
    }
    private sealed class Hook(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    /// <summary>
    /// How long the test waits for the owner thread to start, and for a signal posted from the
    /// thread pool to reach it. Both normally take milliseconds, but a busy machine can delay a
    /// new thread or a pool thread past a one-second wait although nothing is wrong. A success
    /// still ends the wait at once, so the longer bound does not weaken what the test proves.
    /// </summary>
    private static readonly TimeSpan ThreadBound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task EmergencyOwner_DeliversSignalWhileWpfDispatcherIsBlocked_AndRemovesHookOnItsOwnThread()
    {
        await WpfTestApplication.RunAsync(async () =>
        {
            using var pump = new Pump(); using var signal = new ManualResetEventSlim();
            int uiThread = Environment.CurrentManagedThreadId, installThread = 0, callbackThread = 0, removalThread = 0;
            using var owner = new OwnedEmergencyHotkeyListener(() =>
            { installThread = Environment.CurrentManagedThreadId; return new Hook(() => removalThread = Environment.CurrentManagedThreadId); }, pump, ThreadBound);
            var producer = Task.Run(() => pump.Post(() => { callbackThread = Environment.CurrentManagedThreadId; signal.Set(); }));
            // Deliberately block only this private test dispatcher. No system hook or input is installed.
            signal.Wait(ThreadBound).Should().BeTrue();
            callbackThread.Should().NotBe(uiThread); callbackThread.Should().Be(installThread);
            owner.Dispose(); await producer;
            owner.Completion.IsCompletedSuccessfully.Should().BeTrue(); removalThread.Should().Be(installThread);
        });
    }

    [Fact]
    public void FailedHookStartup_IsReported_AndOwnerThreadReachesCompletionWithoutInstallingAnythingElse()
    {
        using var pump = new Pump(); int factories = 0;
        Action construct = () => new OwnedEmergencyHotkeyListener(() => { factories++; throw new InvalidOperationException("Fixture hook refused"); }, pump, TimeSpan.FromSeconds(1));
        construct.Should().Throw<InvalidOperationException>().WithMessage("Fixture hook refused"); factories.Should().Be(1);
    }
}
