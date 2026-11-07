using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class DraftTemplatePreviewConverterTests
{
    [Theory]
    [InlineData(1, 1200, 1, 60)]
    [InlineData(1200, 1, 96, 1)]
    [InlineData(640, 360, 96, 54)]
    [InlineData(12, 8, 12, 8)]
    public void GeneratedCropPreviewIsBoundedOnBothAxesAndNeverUpscaled(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var preview = Assert.IsType<BitmapImage>(new DraftTemplatePreviewConverter().Convert(
            stream.ToArray(), typeof(BitmapSource), null!, CultureInfo.InvariantCulture));

        Assert.Equal(expectedWidth, preview.PixelWidth);
        Assert.Equal(expectedHeight, preview.PixelHeight);
        Assert.True(preview.PixelWidth <= 96 && preview.PixelHeight <= 60);
        Assert.True(preview.IsFrozen);
    }

    [Fact]
    public void InvalidHeaderDoesNotAttemptDecode()
    {
        var converter = new DraftTemplatePreviewConverter();
        Assert.Null(converter.Convert(new byte[5], typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert(new byte[33], typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
    }
}
