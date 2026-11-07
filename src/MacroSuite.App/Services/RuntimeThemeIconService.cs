using System.Windows;
using System.Windows.Media;
using MacroSuite.App.Themes;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Services;

/// <summary>Owns the running window and tray icons; the installed desktop icon stays stable.</summary>
public sealed class RuntimeThemeIconService : IDisposable
{
    private readonly Window _window;
    private readonly SystemTrayService? _tray;
    private ApplicationIconHandle? _icon;
    private bool _disposed;
    public RuntimeThemeIconService(Window window, SystemTrayService? tray = null) { _window = window; _tray = tray; }
    public void Refresh()
    {
        if (_disposed) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.Invoke(Refresh); return; }
        var settings = (_window.DataContext as ViewModels.MainViewModel)?.SettingsVM;
        var accent = (_window.TryFindResource("AccentPrimary") as SolidColorBrush)?.Color ?? Colors.Teal;
        var image = ThemeLogoRenderer.Get(accent, settings?.LogoColorMode ?? LogoColorMode.FollowTheme);
        _window.Icon = image;
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(32d / image.PixelWidth, 32d / image.PixelHeight));
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[32 * 32 * 4]; converted.CopyPixels(pixels, 32 * 4, 0);
        var next = ApplicationIconHandle.FromBgra32(pixels, 32, 32);
        if (!next.IsInvalid) { _tray?.UpdateIcon(next.DangerousGetHandle()); var old = _icon; _icon = next; old?.Dispose(); } else next.Dispose();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _tray?.RestoreDefaultIcon(); _icon?.Dispose(); _icon = null; }
}
