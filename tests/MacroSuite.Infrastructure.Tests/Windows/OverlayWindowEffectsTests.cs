using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MacroSuite.App.Overlays;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Windows;

/// <summary>
/// Exercises real hidden HWNDs, including the production transparent WPF mini
/// window. No window is shown, no frame is captured, and no input is sent.
/// </summary>
[Collection("InteractiveDesktop")]
public sealed class OverlayWindowEffectsTests
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20;
    private const long NoActivate = 0x08000000;
    private const long Layered = 0x00080000;
    private const long AcceptFiles = 0x10;
    private const long Owned = Transparent | NoActivate;

    [Fact]
    public async Task ProductionMiniWindow_DefaultsStayOff_AndClickThroughPreservesUnownedFlags()
    {
        await WithProductionMini((window, handle) =>
        {
            long original = ReadStyles(handle);
            Assert.NotEqual(0, original & Layered);
            uint affinity = ReadAffinity(handle);
            Assert.Equal(0u, affinity);

            using var effects = new OverlayWindowEffects(handle);
            Assert.False(effects.ClickThroughApplied);
            Assert.False(effects.CaptureExclusionApplied);
            Assert.Equal(original, ReadStyles(handle));
            Assert.Equal(affinity, ReadAffinity(handle));

            // A flag changed after construction must survive both toggles.
            WriteStyles(handle, original | AcceptFiles);
            effects.SetClickThrough(true);
            Assert.True(effects.ClickThroughApplied);
            Assert.Equal((original | AcceptFiles) | Owned, ReadStyles(handle));
            effects.SetClickThrough(false);
            Assert.False(effects.ClickThroughApplied);
            Assert.Equal(original | AcceptFiles, ReadStyles(handle));
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public async Task ProductionMiniWindow_CaptureExclusionIsAppliedByWindows_AndRevertsToNone()
    {
        await WithProductionMini((window, handle) =>
        {
            Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
                "The native screenshot exclusion check requires Windows 10 2004 or later.");
            using var effects = new OverlayWindowEffects(handle);
            Assert.Equal(0u, ReadAffinity(handle));
            Assert.True(effects.TrySetCaptureExcluded(true, out string? error), error);
            Assert.Null(error);
            Assert.True(effects.CaptureExclusionApplied);
            Assert.Equal(0x11u, ReadAffinity(handle));
            Assert.True(effects.TrySetCaptureExcluded(false, out error), error);
            Assert.Null(error);
            Assert.False(effects.CaptureExclusionApplied);
            Assert.Equal(0u, ReadAffinity(handle));
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public async Task DisposeRestoresNativeFlagsAndAffinity_WithoutDestroyingTheWindow()
    {
        await WithProductionMini((_, handle) =>
        {
            long original = ReadStyles(handle);
            var effects = new OverlayWindowEffects(handle);
            WriteStyles(handle, original | AcceptFiles);
            effects.SetClickThrough(true);
            Assert.True(effects.TrySetCaptureExcluded(true, out string? error), error);
            effects.Dispose();
            effects.Dispose();

            Assert.True(IsWindow(handle));
            Assert.Equal(original | AcceptFiles, ReadStyles(handle));
            Assert.Equal(0u, ReadAffinity(handle));
            Assert.Throws<ObjectDisposedException>(() => effects.SetClickThrough(true));
            Assert.Throws<ObjectDisposedException>(() => effects.TrySetCaptureExcluded(true, out string? ignored));
        });
    }

    [Fact]
    public async Task ExistingOwnedStyleBitsAndDisplayAffinity_AreRestoredRatherThanCleared()
    {
        await WithProductionMini((_, handle) =>
        {
            long original = ReadStyles(handle);
            WriteStyles(handle, original | Owned);
            Assert.True(SetWindowDisplayAffinity(handle, 1), new Win32Exception(Marshal.GetLastWin32Error()).Message);
            using (var effects = new OverlayWindowEffects(handle))
            {
                effects.SetClickThrough(true);
                effects.SetClickThrough(false);
                Assert.Equal(original | Owned, ReadStyles(handle));
                Assert.True(effects.TrySetCaptureExcluded(true, out string? error), error);
                Assert.Equal(0x11u, ReadAffinity(handle));
                Assert.True(effects.TrySetCaptureExcluded(false, out error), error);
                Assert.Equal(1u, ReadAffinity(handle));
            }
            Assert.Equal(original | Owned, ReadStyles(handle));
            Assert.Equal(1u, ReadAffinity(handle));
        });
    }

    [Fact]
    public void InvalidConstructorHandles_ReportErrors()
    {
        Assert.Throws<ArgumentException>(() => new OverlayWindowEffects(0));
        Assert.Throws<Win32Exception>(() => new OverlayWindowEffects((nint)(-1)));
    }

    [Fact]
    public async Task DestroyedWindow_ReportsNativeFailures_AndDisposeRemainsIdempotent()
    {
        await WpfTestApplication.RunAsync(() =>
        {
            var source = new HwndSource(new HwndSourceParameters("Zenith hidden overlay lifetime test")
            {
                Width = 16, Height = 16,
                WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = (int)Layered
            });
            nint handle = source.Handle;
            Assert.False(IsWindowVisible(handle));
            var effects = new OverlayWindowEffects(handle);
            source.Dispose();
            Assert.False(IsWindow(handle));
            Assert.Throws<Win32Exception>(() => effects.SetClickThrough(true));
            Assert.False(effects.TrySetCaptureExcluded(true, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(effects.CaptureExclusionApplied);
            Assert.Throws<Win32Exception>(() => new OverlayWindowEffects(handle));
            effects.Dispose();
            effects.Dispose();
            Assert.Throws<ObjectDisposedException>(() => effects.SetClickThrough(false));
            return Task.CompletedTask;
        });
    }

    private static Task WithProductionMini(Action<MiniModeOverlay, nint> body) =>
        WpfTestApplication.RunAsync(() =>
        {
            var application = Application.Current;
            var resources = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml")
            };
            application.Resources.MergedDictionaries.Add(resources);
            MiniModeOverlay? window = null;
            try
            {
                window = new MiniModeOverlay();
                nint handle = new WindowInteropHelper(window).EnsureHandle();
                Assert.True(IsWindow(handle));
                Assert.False(IsWindowVisible(handle));
                Assert.False(window.IsVisible);
                body(window, handle);
                Assert.False(IsWindowVisible(handle));
            }
            finally
            {
                window?.Close();
                application.Resources.MergedDictionaries.Remove(resources);
            }
            return Task.CompletedTask;
        });

    private static long ReadStyles(nint handle)
    {
        Marshal.SetLastPInvokeError(0);
        nint value = nint.Size == 8 ? GetWindowLongPtrW(handle, ExtendedStyle) : GetWindowLongW(handle, ExtendedStyle);
        int error = Marshal.GetLastWin32Error();
        if (value == 0 && error != 0) throw new Win32Exception(error);
        return value.ToInt64();
    }

    private static void WriteStyles(nint handle, long value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = nint.Size == 8 ? SetWindowLongPtrW(handle, ExtendedStyle, (nint)value) : SetWindowLongW(handle, ExtendedStyle, (int)value);
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
    }

    private static uint ReadAffinity(nint handle)
    {
        if (!GetWindowDisplayAffinity(handle, out uint value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint handle, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint handle, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint handle, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint handle, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
}
