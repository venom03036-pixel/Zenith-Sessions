using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class InputBackendResolverTests
{
    [Fact]
    public void Resolve_HealthyPreferredBackend_SelectsIt()
    {
        var preferred = new StubBackend("virtual-hid", InputBackendCapabilities.All);
        var fallback = new StubBackend("win32", InputBackendCapabilities.All);

        using InputBackendResolution resolution =
            new InputBackendResolver([preferred], fallback).Resolve("virtual-hid");

        resolution.BackendId.Should().Be("virtual-hid");
        resolution.UsedFallback.Should().BeFalse();
        resolution.Diagnostics.Should().ContainSingle(d => d.Code == "BackendSelected");
        fallback.CreateCalls.Should().Be(0);
    }

    [Fact]
    public void Resolve_UnhealthyPreferredBackend_FallsBackToWin32()
    {
        var preferred = new StubBackend(
            "virtual-hid",
            InputBackendCapabilities.All,
            InputBackendHealth.Unhealthy("Device is not connected."));
        var fallback = new StubBackend("win32", InputBackendCapabilities.All);

        using InputBackendResolution resolution =
            new InputBackendResolver([preferred], fallback).Resolve("virtual-hid");

        resolution.BackendId.Should().Be("win32");
        resolution.UsedFallback.Should().BeTrue();
        resolution.Diagnostics.Should().Contain(d => d.Code == "BackendUnhealthy");
        resolution.Diagnostics.Should().Contain(d => d.Code == "FallbackSelected");
    }

    [Fact]
    public void Resolve_PreferredMissingCapabilities_FallsBackToWin32()
    {
        var preferred = new StubBackend("virtual-hid", InputBackendCapabilities.RelativePointer);
        var fallback = new StubBackend("win32", InputBackendCapabilities.All);

        using InputBackendResolution resolution = new InputBackendResolver([preferred], fallback).Resolve(
            "virtual-hid",
            InputBackendCapabilities.Keyboard | InputBackendCapabilities.EmergencyRelease);

        resolution.BackendId.Should().Be("win32");
        resolution.Diagnostics.Should().Contain(d => d.Code == "MissingCapabilities");
    }

    [Fact]
    public void Resolve_PreferredCreationFailure_FallsBackAndReportsFailure()
    {
        var preferred = new StubBackend("virtual-hid", InputBackendCapabilities.All)
        {
            CreateException = new InvalidOperationException("Adapter initialization failed.")
        };
        var fallback = new StubBackend("win32", InputBackendCapabilities.All);

        using InputBackendResolution resolution =
            new InputBackendResolver([preferred], fallback).Resolve("virtual-hid");

        resolution.BackendId.Should().Be("win32");
        resolution.Diagnostics.Should().Contain(d =>
            d.Code == "BackendCreationFailed" && d.Exception == preferred.CreateException);
    }

    [Fact]
    public void Resolution_DisposesSelectedPlayerExactlyOnce()
    {
        var fallback = new StubBackend("win32", InputBackendCapabilities.All);
        InputBackendResolution resolution = new InputBackendResolver(win32Fallback: fallback).Resolve();
        var player = (TrackingPlayer)resolution.Player;

        resolution.Dispose();
        resolution.Dispose();

        player.DisposeCalls.Should().Be(1);
        var access = () => resolution.Player;
        access.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Resolve_UnimplementedVirtualHidPreference_FallsBackToWin32()
    {
        using InputBackendResolution resolution = new InputBackendResolver().Resolve("virtual-hid");

        resolution.BackendId.Should().Be("win32");
        resolution.UsedFallback.Should().BeTrue();
        resolution.Diagnostics.Should().Contain(d => d.Code == "BackendUnhealthy" && d.BackendId == "virtual-hid");
        resolution.Diagnostics.Should().Contain(d => d.Code == "FallbackSelected");
    }

    private sealed class StubBackend : IInputBackend
    {
        private readonly InputBackendHealth _health;

        public string Id { get; }
        public InputBackendCapabilities Capabilities { get; }
        public Exception? CreateException { get; init; }
        public int CreateCalls { get; private set; }

        public StubBackend(
            string id,
            InputBackendCapabilities capabilities,
            InputBackendHealth? health = null)
        {
            Id = id;
            Capabilities = capabilities;
            _health = health ?? InputBackendHealth.Healthy();
        }

        public InputBackendHealth CheckHealth() => _health;

        public IInputPlayer CreatePlayer()
        {
            CreateCalls++;
            if (CreateException is not null)
                throw CreateException;
            return new TrackingPlayer();
        }
    }

    private sealed class TrackingPlayer : IInputPlayer
    {
        public int DisposeCalls { get; private set; }
        public bool IsPlaying => false;

        public Task PlayAsync(
            IReadOnlyList<InputEvent> events,
            PlaybackOptions? options = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() => DisposeCalls++;
    }
}
