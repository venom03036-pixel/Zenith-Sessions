using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Capture;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Performance;

public sealed class HotPathStressTests
{
    [Fact]
    public void VirtualDesktopGeometry_Query_ReusesCachedInstance()
    {
        Win32VirtualDesktopGeometry.InvalidateCache();
        var first = Win32VirtualDesktopGeometry.Query();
        var second = Win32VirtualDesktopGeometry.Query();
        second.Should().BeSameAs(first);
        first.VirtualDesktopBounds.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void VirtualDesktopGeometry_Query_ThousandHitsStayCached()
    {
        Win32VirtualDesktopGeometry.InvalidateCache();
        var sw = Stopwatch.StartNew();
        Win32VirtualDesktopGeometry last = Win32VirtualDesktopGeometry.Query();
        for (int i = 0; i < 1_000; i++)
            last = Win32VirtualDesktopGeometry.Query();
        sw.Stop();

        last.Monitors.Should().NotBeEmpty();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void ForegroundGuard_TenThousandChecks_StayCheap()
    {
        var guard = new Win32ForegroundInputGuard();
        var sw = Stopwatch.StartNew();
        bool allowed = true;
        for (int i = 0; i < 10_000; i++)
            allowed = guard.TryAllowSynthesizedInput(out _);
        sw.Stop();

        _ = allowed;
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SendRelativeMove_RefusedForeground_FiftyThousand_DoesNotInject()
    {
        var guard = new CountingGuard { Allow = false };
        var sink = new MacroSuite.Infrastructure.Tests.Input.RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink, foregroundGuard: guard);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50_000; i++)
        {
            try { player.SendRelativeMove(1, 1, dualBackend: false); }
            catch (UnsafeForegroundInputException) { }
        }
        sw.Stop();

        guard.Calls.Should().Be(50_000);
        sink.Inputs.Should().BeEmpty();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PlayAsync_EmptySequence_ThousandTimes()
    {
        using var player = new Win32InputPlayer();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
            await player.PlayAsync(Array.Empty<InputEvent>());
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Capture_ThirtySmallRegions_StayInteractive()
    {
        using var capture = new FallbackCaptureCoordinator();
        var sw = Stopwatch.StartNew();
        var region = new Rectangle(0, 0, 96, 96);
        for (int i = 0; i < 30; i++)
        {
            using CaptureFrame frame = await capture.CaptureRegionAsync(region);
            frame.Width.Should().BeGreaterThan(0);
            frame.Height.Should().BeGreaterThan(0);
            frame.Span.Length.Should().BeGreaterThan(0);
        }

        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void GeometryCache_InvalidateProducesNewInstance()
    {
        var first = Win32VirtualDesktopGeometry.Query();
        Win32VirtualDesktopGeometry.InvalidateCache();
        var second = Win32VirtualDesktopGeometry.Query();
        second.Should().NotBeSameAs(first);
        second.VirtualDesktopBounds.Should().Be(first.VirtualDesktopBounds);
    }

    private sealed class CountingGuard : IForegroundInputGuard
    {
        public bool Allow { get; init; }
        public int Calls;

        public bool TryAllowSynthesizedInput(out string? refusalReason)
        {
            Interlocked.Increment(ref Calls);
            refusalReason = Allow ? null : "stress";
            return Allow;
        }
    }
}
