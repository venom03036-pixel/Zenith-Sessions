using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacroSuite.App.Themes;

public enum LogoColorMode { FollowTheme, Original }

/// <summary>Reuses the supplied artwork and preserves its facet shading when tinted.</summary>
public sealed class ThemeAwareLogo : Image
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(LogoColorMode), typeof(ThemeAwareLogo), new PropertyMetadata(LogoColorMode.FollowTheme, Refresh));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(ThemeAwareLogo), new PropertyMetadata(Brushes.Teal, Refresh));
    public LogoColorMode Mode { get => (LogoColorMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public ThemeAwareLogo()
    {
        Stretch = Stretch.Uniform;
        SetResourceReference(AccentProperty, "AccentPrimary");
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Source = ThemeLogoRenderer.Get(Colors.Teal, LogoColorMode.FollowTheme);
    }
    private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var logo = (ThemeAwareLogo)sender;
        logo.Source = ThemeLogoRenderer.Get((logo.Accent as SolidColorBrush)?.Color ?? Colors.Teal, logo.Mode);
    }
}

public static class ThemeLogoRenderer
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(Color, LogoColorMode), BitmapSource> Cache = [];
    private static readonly Queue<(Color, LogoColorMode)> Order = [];
    private static readonly BitmapSource Original = LoadOriginal();
    private static BitmapSource LoadOriginal()
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri($"pack://application:,,,/{typeof(ThemeLogoRenderer).Assembly.GetName().Name};component/Assets/zenith-mark.png");
        bitmap.DecodePixelWidth = 256; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    public static BitmapSource Get(Color accent, LogoColorMode mode)
    {
        if (mode == LogoColorMode.Original) return Original;
        lock (Sync)
        {
            var key = (accent, mode);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var source = new FormatConvertedBitmap(Original, PixelFormats.Bgra32, null, 0);
            int stride = source.PixelWidth * 4; var pixels = new byte[stride * source.PixelHeight]; source.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // The lightness ratio retains the original bright and shaded facets.
                double light = Math.Clamp((.2126 * pixels[i + 2] + .7152 * pixels[i + 1] + .0722 * pixels[i]) / 153d, .25, 1.5);
                pixels[i] = (byte)Math.Clamp(accent.B * light, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(accent.G * light, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(accent.R * light, 0, 255);
            }
            var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); result.Freeze();
            while (Cache.Count >= 16) Cache.Remove(Order.Dequeue());
            Cache[key] = result; Order.Enqueue(key); return result;
        }
    }
}
