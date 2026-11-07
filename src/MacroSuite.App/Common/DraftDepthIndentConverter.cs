using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MacroSuite.App.Common;

/// <summary>Readable nesting for generated macro review without changing its editor data.</summary>
public sealed class DraftDepthIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness(Math.Clamp(value is int depth ? depth : 0, 0, 4) * 18, 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Small immutable review thumbnails from the same embedded template used by playback.</summary>
public sealed class DraftTemplatePreviewConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        try
        {
            // These templates are PNGs produced by the compiler. Read only IHDR before
            // decoding: width-only decoding can massively upscale a narrow/tall crop.
            ReadOnlySpan<byte> header = bytes;
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length is < 33 or > 16_000_000 || !header[..8].SequenceEqual(signature)
                || BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13
                || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The generated template was not a supported PNG.");
            uint width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
            if (width == 0 || height == 0 || (ulong)width * height > 4_000_000)
                throw new InvalidDataException("The generated template dimensions exceeded the preview limit.");
            double scale = Math.Min(1, Math.Min(96d / width, 60d / height));
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Max(1, (int)Math.Floor(width * scale));
            image.DecodePixelHeight = Math.Max(1, (int)Math.Floor(height * scale));
            image.StreamSource = stream;
            image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Generated template preview was unavailable ({0}).", ex.GetType().Name);
            return null;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
