using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroSuite.App.Themes;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// What a view actually drew, rendered offscreen at twice the size (192 DPI) so text stems and 1.4 px strokes cover whole pixels
/// instead of only antialiased edges. No window is shown and nothing on the screen is read.
/// </summary>
internal static class RenderedInk
{
    private const double Scale = 2;

    /// <summary>Renders <paramref name="root"/>, which must already be laid out and have an opaque background.</summary>
    public static RenderTargetBitmap Render(FrameworkElement root)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * Scale), (int)Math.Ceiling(root.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        image.Render(root);
        return image;
    }

    /// <summary>The colour drawn at <paramref name="point"/> of <paramref name="element"/>.</summary>
    public static Color At(BitmapSource image, FrameworkElement root, FrameworkElement element, Point point)
    {
        Point pixel = element.TransformToAncestor(root).Transform(point); byte[] value = new byte[4];
        image.CopyPixels(new Int32Rect((int)Math.Floor(pixel.X * Scale), (int)Math.Floor(pixel.Y * Scale), 1, 1), value, 4, 0);
        return Color.FromRgb(value[2], value[1], value[0]);
    }

    /// <summary>
    /// Fails unless some pixel drawn inside <paramref name="element"/> reaches <paramref name="minimum"/>:1 against
    /// <paramref name="background"/>. Text drawn in a colour that does not read on that background never does, however it is antialiased.
    /// </summary>
    public static void AssertReadable(BitmapSource image, FrameworkElement root, FrameworkElement element, Color background, double minimum, string what)
    {
        // One device pixel in from every edge, so nothing drawn next to the element is counted.
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        int left = Math.Max(0, (int)Math.Floor(bounds.Left * Scale) + 1), top = Math.Max(0, (int)Math.Floor(bounds.Top * Scale) + 1);
        int right = Math.Min(image.PixelWidth, (int)Math.Ceiling(bounds.Right * Scale) - 1), bottom = Math.Min(image.PixelHeight, (int)Math.Ceiling(bounds.Bottom * Scale) - 1);
        Assert.True(right > left && bottom > top, $"{what} has no area to read.");
        int width = right - left, height = bottom - top; byte[] pixels = new byte[width * height * 4];
        image.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        double strongest = 1; Color ink = background;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            Color drawn = Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]); double contrast = AppearancePalette.Contrast(drawn, background);
            if (contrast > strongest) { strongest = contrast; ink = drawn; }
        }
        Assert.True(strongest >= minimum,
            $"{what} is drawn at {strongest:0.00}:1 at most (ink {AppearancePalette.Hex(ink)}) on {AppearancePalette.Hex(background)}, below {minimum}:1.");
    }
}
