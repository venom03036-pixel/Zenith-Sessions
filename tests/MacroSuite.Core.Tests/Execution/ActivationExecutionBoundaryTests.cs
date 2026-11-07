using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ActivationExecutionBoundaryTests
{
    [Fact]
    public async Task DirectExecutorRejectsInactiveActivationBeforeFirstInputAndStillReleasesHeldKeys()
    {
        using var input = new TrackingInput(); using var capture = new NeverCapture(); using var control = new MacroController(input);
        var activation = new RejectingActivation(); using var license = new PublicActivationLicenseService(activation);
        var context = new MacroContext(capture, control) { InputPlayer = input, ActivationService = activation, FeatureGate = new FeatureGate(license) };
        var executor = new MacroExecutor();
        await Assert.ThrowsAsync<ActivationRequiredException>(() => executor.ExecuteAsync(new MacroModel { Steps = [new KeyPressStep(77, 0)] }, context));
        Assert.Equal(ExecutionState.Faulted, executor.State); Assert.Equal(1, activation.Requires); Assert.Equal(0, input.Events); Assert.True(input.Releases > 0);
        await control.StopAsync(); control.Panic();
        Assert.True(control.StopToken.IsCancellationRequested); Assert.True(control.PanicToken.IsCancellationRequested); Assert.Equal(0, input.Events);
    }
    private sealed class RejectingActivation : IActivationService
    {
        public bool IsActive => false;
        public string StatusMessage => "Activation is required.";
        public VerifiedLicense? CurrentLicense => null;
        public int Requires { get; private set; }
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default) => Task.FromResult(new ActivationOperationResult(false, StatusMessage));
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => Task.FromResult(new ActivationRequestResult(false, StatusMessage));
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => Task.FromResult(new ActivationOperationResult(false, StatusMessage));
        public void RequireActive() { Requires++; throw new ActivationRequiredException(StatusMessage); }
    }
    private sealed class TrackingInput : IInputPlayer
    {
        public int Events { get; private set; }
        public int Releases { get; private set; }
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) { Events += events.Count; return Task.CompletedTask; }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => Events++;
        public void SendRelativeMove(int dx, int dy) => Events++;
        public void ReleaseAllHeldKeys() => Releases++;
        public void Dispose() { }
    }
    private sealed class NeverCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected capture before activation.");
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected capture before activation.");
        public void Dispose() { }
    }
}
