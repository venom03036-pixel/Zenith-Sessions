using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ClientCoordinateResolverTests
{
    private sealed class StubWindows : IWindowCoordinateService
    {
        public IntPtr Window { get; set; } = (IntPtr)0x10;
        public bool Valid { get; set; } = true;
        public (int width, int height) Size { get; set; } = (200, 100);
        public int ClientToScreenCalls { get; private set; }

        public IntPtr GetWindowAt(int screenX, int screenY) => Window;
        public IntPtr GetForegroundWindow() => Window;
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY) => (screenX, screenY);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY)
        {
            ClientToScreenCalls++;
            return (clientX + 40, clientY + 80);
        }
        public (int width, int height) GetClientSize(IntPtr hWnd) => Size;
        public bool IsWindowValid(IntPtr hWnd) => Valid && hWnd != IntPtr.Zero;
        public IntPtr FindWindowByTitle(string? titleOrProcessName)
            => string.Equals(titleOrProcessName, "Roblox", StringComparison.OrdinalIgnoreCase) ? Window : IntPtr.Zero;
    }

    [Fact]
    public void Resolve_ConvertsClientToScreen()
    {
        var windows = new StubWindows();
        var screen = ClientCoordinateResolver.ResolveScreenPoint(windows, new Point(10, 20), "Roblox");
        screen.Should().Be(new Point(50, 100));
        windows.ClientToScreenCalls.Should().Be(1);
    }

    [Fact]
    public void Resolve_MissingWindow_Throws()
    {
        var windows = new StubWindows { Window = IntPtr.Zero, Valid = false };
        var act = () => ClientCoordinateResolver.ResolveScreenPoint(windows, new Point(1, 1), "Missing");
        act.Should().Throw<InvalidOperationException>().WithMessage("*could not be resolved*");
    }

    [Fact]
    public void Resolve_ScalesRecordedClientSize()
    {
        var windows = new StubWindows { Size = (400, 200) };
        var screen = ClientCoordinateResolver.ResolveScreenPoint(
            windows,
            new Point(10, 20),
            "Roblox",
            recordedClientWidth: 200,
            recordedClientHeight: 100);
        screen.Should().Be(new Point(20 + 40, 40 + 80));
    }
}
