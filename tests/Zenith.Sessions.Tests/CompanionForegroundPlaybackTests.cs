using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;
using Zenith.MacroHost.Execution;

namespace Zenith.Sessions.Tests;

public sealed class CompanionForegroundPlaybackTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const int W = 0x57;

    [Fact]
    public async Task SavedKeysUseTheExistingEngineAndTheExactHostOwner()
    {
        await using var f = new Fixture([new KeyPressStep(W, 0)]);
        var run = f.Start();
        Assert.Equal(ForegroundPlaybackStatus.Completed, (await run.Completion.WaitAsync(Deadline)).Status);
        Assert.Equal([InputEventType.KeyDown, InputEventType.KeyUp], f.Player.Input.Select(e => e.Type));
        Assert.Equal([run.Id], f.Player.Releases);
        Assert.Null(f.Player.Session);
        Assert.False(f.Arbiter.Snapshot.IsBusy);
        Assert.False(await run.ResumeAsync());
    }

    [Theory]
    [InlineData("screen", TargetedMacroPlanProblem.UnsupportedTarget)]
    [InlineData("pointer", TargetedMacroPlanProblem.UnsupportedStep)]
    [InlineData("system-key", TargetedMacroPlanProblem.UnsupportedInput)]
    public async Task IncompatibleDocumentsCannotEmitAnEarlierStep(string test, TargetedMacroPlanProblem problem)
    {
        var steps = test switch
        {
            "pointer" => new MacroStep[] { new KeyPressStep(W, 0), new MouseMoveStep() },
            "system-key" => [new KeyPressStep(W, 0), new KeyPressStep(0x5b, 0)],
            _ => [new KeyPressStep(W, 0)]
        };
        await using var f = new Fixture(steps, screen: test == "screen");
        var result = await f.Start().Completion.WaitAsync(Deadline);
        Assert.Equal(ForegroundPlaybackStatus.PreparationRejected, result.Status);
        Assert.Equal(problem, result.DocumentProblem);
        Assert.Empty(f.Player.Input);
        Assert.Empty(f.Player.Releases);
    }

    [Fact]
    public async Task SameHandleWithDifferentProcessBirthCannotBecomeTheTarget()
    {
        await using var f = new Fixture([new KeyPressStep(W, 0)]);
        f.Targets.Replace(f.Window with { ProcessInstance = f.Window.ProcessInstance + 1 });
        Assert.Equal(ForegroundPlaybackStatus.TargetUnavailable, (await f.Start().Completion.WaitAsync(Deadline)).Status);
        Assert.Empty(f.Player.Input);
        Assert.False(f.Arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ARevisionChangeWhileWaitingForNeutralInputStopsBeforeAttachment()
    {
        await using var f = new Fixture([new KeyPressStep(W, 0)]);
        f.BeforeNeutral = () => f.Current = false;
        var result = await f.Start().Completion.WaitAsync(Deadline);
        Assert.Equal(ForegroundPlaybackStatus.PreparationRejected, result.Status);
        Assert.Equal(SavedMacroPreparationStatus.Changed, result.Preparation);
        Assert.Empty(f.Player.Input);
        Assert.Empty(f.Player.Releases);
    }

    [Fact]
    public async Task PanicDuringSavedReadKeepsTheLeaseUntilTheActualReadFinishes()
    {
        await using var f = new Fixture([new KeyPressStep(W, 0)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeRead = async () => { entered.TrySetResult(); await finish.Task; };
        var generation = f.Arbiter.StopGeneration;
        var run = f.Start();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            f.Arbiter.SignalStop();
            Assert.False(run.Completion.IsCompleted);
            Assert.Equal(run.Id, f.Arbiter.Snapshot.OperationId);
            Assert.Equal(ForegroundPlaybackStatus.Stopped, f.Host.Start(f.Reference, f.Window, generation).Rejection);
            Assert.Equal(ForegroundPlaybackStatus.Busy, f.Host.Start(f.Reference, f.Window, f.Arbiter.StopGeneration).Rejection);
        }
        finally { finish.TrySetResult(); }
        Assert.Equal(ForegroundPlaybackStatus.Stopped, (await run.Completion.WaitAsync(Deadline)).Status);
        Assert.Empty(f.Player.Input);
        Assert.False(f.Arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task FocusLossReleasesRunOwnedKeysAndOnlyExactNeutralFocusCanResume()
    {
        await using var f = new Fixture([
            new KeyPressStep(W, 0) { Stroke = "Down" },
            new DelayStep { Duration = TimeSpan.FromSeconds(30) },
            new KeyPressStep(W, 0) { Stroke = "Up" }
        ]);
        var run = f.Start();
        await f.Player.FirstDown.Task.WaitAsync(Deadline);
        f.Targets.Replace(f.Window with { Handle = f.Window.Handle + 1 });
        await f.Player.FirstRelease.Task.WaitAsync(Deadline);
        Assert.True(run.IsPaused);
        Assert.False(await run.ResumeAsync().WaitAsync(Deadline));
        f.Targets.Replace(f.Window);
        f.Neutral = false;
        Assert.False(await run.ResumeAsync().WaitAsync(Deadline));
        f.Neutral = true;
        await RetryResumeAsync(run);
        await f.Player.SecondDown.Task.WaitAsync(Deadline);
        f.Arbiter.SignalStop();
        Assert.Equal(ForegroundPlaybackStatus.Stopped, (await run.Completion.WaitAsync(Deadline)).Status);
        Assert.Equal([InputEventType.KeyDown, InputEventType.KeyUp, InputEventType.KeyDown, InputEventType.KeyUp],
            f.Player.Input.Select(e => e.Type));
        Assert.False(f.Arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ExistingEditorOwnershipAndFailedCleanupBlockNewPlayback()
    {
        await using var f = new Fixture([new KeyPressStep(W, 0)]);
        Assert.True(f.Arbiter.TryAcquire(HostInputOperationKind.Recording, out var editor, out _));
        Assert.Equal(ForegroundPlaybackStatus.Busy, f.Host.Start(f.Reference, f.Window, f.Arbiter.StopGeneration).Rejection);
        await editor!.CompleteAsync(true);
        f.Player.FailCleanup = true;
        var run = f.Start();
        Assert.Equal(ForegroundPlaybackStatus.CleanupFailed, (await run.Completion.WaitAsync(Deadline)).Status);
        Assert.True(f.Arbiter.Snapshot.IsQuarantined);
        Assert.Equal(run.Id, f.Arbiter.Snapshot.OperationId);
        Assert.Equal(ForegroundPlaybackStatus.Busy, f.Host.Start(f.Reference, f.Window, f.Arbiter.StopGeneration).Rejection);
    }

    private static async Task RetryResumeAsync(CompanionForegroundPlayback.Run run)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!await run.ResumeAsync(timeout.Token))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable, ISavedMacroDocumentSource
    {
        private readonly SavedMacroPlanPreparer _preparer;
        private readonly MacroModel _document;
        private CompanionForegroundPlayback.Run? _active;
        public SavedMacroReference Reference { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public TargetWindowInfo Window { get; } = new(1234, 4711, 666, "Fixture", "Fixture window", new Rectangle(0, 0, 200, 200), 1);
        public HostInputOperationArbiter Arbiter { get; } = new();
        public Player Player { get; } = new();
        public TargetService Targets { get; }
        public CompanionForegroundPlayback Host { get; }
        public volatile bool Current = true, Neutral = true;
        public Action? BeforeNeutral;
        public Func<Task>? BeforeRead;

        public Fixture(IReadOnlyList<MacroStep> steps, bool screen = false)
        {
            Targets = new(Window);
            _document = new()
            {
                Id = Reference.MacroId, Steps = steps.ToArray(),
                Settings = new() { Target = new() { Mode = screen ? MacroTargetMode.Screen : MacroTargetMode.ActiveExternalWindow } }
            };
            _preparer = new(this, _ => { });
            var license = new License();
            Host = new(Arbiter, _preparer, Targets, Player, license, license,
                _ => { BeforeNeutral?.Invoke(); return Task.FromResult(Neutral); }, _ => { });
        }

        public CompanionForegroundPlayback.Run Start() => _active = Assert.IsType<CompanionForegroundPlayback.Run>(
            Host.Start(Reference, Window, Arbiter.StopGeneration).Operation);

        public async Task<SavedMacroDocumentReadResult> ReadAsync(SavedMacroReference reference, CancellationToken ct = default)
        {
            Assert.Equal(Reference, reference);
            if (BeforeRead is { } wait) await wait();
            var pin = SavedMacroDocumentPin.Create(reference, new SavedMacroDocument(_document, new(_document.Id, new('A', 64), 128)),
                () => Current);
            return new(SavedMacroDocumentReadStatus.Ready, pin);
        }

        public async ValueTask DisposeAsync()
        {
            if (_active is not null)
            {
                _active.Stop();
                await _active.Completion.WaitAsync(Deadline);
            }
            await _preparer.DisposeAsync();
        }
    }

    private sealed class TargetService(TargetWindowInfo initial) : ITargetWindowService
    {
        private TargetWindowInfo _foreground = initial;
        public bool SupportsWindowTargeting => true;
        public TargetWindowSnapshot Snapshot => new(Volatile.Read(ref _foreground), null, 1, 1, true);
        public event EventHandler? Changed;
        public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null)
            => new(rule, Volatile.Read(ref _foreground));
        public TargetValidation Inspect(ResolvedMacroTarget target)
        {
            var foreground = Volatile.Read(ref _foreground);
            bool exact = target.Window is { } candidate && PlaybackSessionContext.SameIdentity(candidate, foreground);
            return new(true, exact, false, foreground.ClientBounds, foreground.Generation);
        }
        public void Replace(TargetWindowInfo next)
        {
            Volatile.Write(ref _foreground, next);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void RegisterOwnWindow(nint window) => throw new NotSupportedException();
        public bool TryActivate(ResolvedMacroTarget target) => throw new InvalidOperationException("Must not steal focus.");
        public void Dispose() { }
    }

    private sealed class Player : IInputPlayer, IHostInputOperationAwarePlayer, IPlaybackSessionAwarePlayer, IPlaybackSessionBoundSink
    {
        private PlaybackSessionContext? _session;
        private HostInputOperationLease? _lease;
        private int _downs;
        public ConcurrentQueue<InputEvent> Input { get; } = new();
        public ConcurrentQueue<Guid> Releases { get; } = new();
        public TaskCompletionSource FirstDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailCleanup;
        public bool IsPlaying => false;
        public PlaybackSessionContext? Session { get => _session; set { _session = value; value?.BindSink(this); } }

        public async Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            var session = Assert.IsType<PlaybackSessionContext>(options?.Session);
            foreach (var input in events)
                await session.EmitAsync(input, ct);
        }
        public ValueTask EmitSessionInputAsync(PlaybackSessionContext owner, InputEvent input, bool cleanup, CancellationToken ct)
        {
            Assert.Same(Session, owner);
            Assert.Same(_lease, owner.HostOperation);
            Assert.True(cleanup ? _lease!.OwnsCleanup : owner.InspectFinalInputAdmission(ct) == PlaybackInputAdmission.Allowed);
            Input.Enqueue(input);
            if (input.Type == InputEventType.KeyDown)
            {
                if (Interlocked.Increment(ref _downs) == 1) FirstDown.TrySetResult();
                else SecondDown.TrySetResult();
            }
            if (cleanup) FirstRelease.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
            => throw new InvalidOperationException("Must use the bound input sink.");
        public IDisposable AttachHostOperation(HostInputOperationLease lease)
        {
            Assert.Null(_lease);
            _lease = lease;
            return new Detach(this);
        }
        private sealed class Detach(Player player) : IDisposable { public void Dispose() => player._lease = null; }
        public void ReleaseHostOperationInputs(HostInputOperationLease lease)
        {
            Assert.Same(_lease, lease);
            Assert.True(lease.OwnsCleanup);
            Releases.Enqueue(lease.OperationId);
            if (FailCleanup) throw new IOException("Synthetic cleanup error");
        }
        public void ReleaseAllHeldKeys() => throw new InvalidOperationException("Must release only owned input.");
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => throw new NotSupportedException();
        public void SendRelativeMove(int dx, int dy) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class License : IActivationService, IFeatureGate
    {
        public bool IsActive => true;
        public string StatusMessage => "";
        public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public void RequireActive() { }
        public bool IsAvailable(FeatureId feature) => feature == FeatureId.BasicPlayback;
        public void Require(FeatureId feature) => Assert.True(IsAvailable(feature));
        public Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
