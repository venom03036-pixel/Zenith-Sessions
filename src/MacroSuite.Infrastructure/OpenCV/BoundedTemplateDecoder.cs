using System.Buffers.Binary;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>Checks encoded length and declared dimensions before allocating native image pixels.</summary>
public static class BoundedTemplateDecoder
{
    public const int MaxEncodedBytes = 32 * 1024 * 1024;
    public const int MaxDimension = 4096;

    public static Mat Decode(string? path, byte[]? suppliedBytes = null, long maxDecodedBytes = (long)MaxDimension * MaxDimension)
    {
        byte[] bytes;
        if (suppliedBytes is not null) bytes = suppliedBytes;
        else
        {
            if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("The target image is missing.", path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
            ValidateEncodedLength(stream.Length);
            bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        }
        ValidateEncodedLength(bytes.Length);
        var size = ReadSize(bytes);
        if (size.Width < 1 || size.Height < 1 || size.Width > MaxDimension || size.Height > MaxDimension)
            throw new InvalidDataException("Target image dimensions must be between 1 and 4096 pixels per side.");
        if ((long)size.Width * size.Height > maxDecodedBytes)
            throw new InvalidDataException("The image groups exceed the 128 MB prepared-image budget. Use smaller templates or fewer groups.");
        Mat template = Cv2.ImDecode(bytes, ImreadModes.Grayscale | ImreadModes.IgnoreOrientation);
        if (template.Empty() || template.Width != size.Width || template.Height != size.Height)
        {
            template.Dispose();
            throw new InvalidDataException("The target image pixels do not match its header.");
        }
        return template;
    }

    private static void ValidateEncodedLength(long size)
    {
        if (size is < 1 or > MaxEncodedBytes) throw new InvalidDataException("Target image must be between 1 byte and 32 MB.");
    }
    private static Size ReadSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The PNG target header is invalid.");
            uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]), height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
            if (width > MaxDimension || height > MaxDimension) return new(MaxDimension + 1, MaxDimension + 1);
            return new((int)width, (int)height);
        }
        if (bytes.Length >= 54 && bytes[0] == 'B' && bytes[1] == 'M')
        {
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..18]);
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes[30..34]);
            if (header < 40 || header > bytes.Length - 14 || compression is not (0 or 1 or 2 or 3 or 6))
                throw new InvalidDataException("This BMP target format is unsupported. Save it as PNG instead.");
            int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..22]), height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]);
            if (height == int.MinValue) throw new InvalidDataException("The BMP target dimensions are invalid.");
            return new(width, Math.Abs(height));
        }
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216) return ReadJpegSize(bytes);
        throw new InvalidDataException("Target images must be PNG, JPEG, or BMP. Capture a new template or convert this file.");
    }
    private static Size ReadJpegSize(ReadOnlySpan<byte> bytes)
    {
        int offset = 2; Size? result = null;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 255) throw new InvalidDataException("The JPEG target header is invalid.");
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) break;
            byte marker = bytes[offset++];
            if (marker is 0xDA or 0xD9) break; // Header must be complete before scan data.
            if (marker is 0x01 or >= 0xD0 and <= 0xD8) continue;
            if (offset > bytes.Length - 2) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset > bytes.Length - length) throw new InvalidDataException("The JPEG target header is incomplete.");
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (marker is not (0xC0 or 0xC1 or 0xC2) || length < 8)
                    throw new InvalidDataException("This JPEG target format is unsupported. Save it as PNG instead.");
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                var next = new Size(width, height);
                if (result.HasValue && result.Value != next) throw new InvalidDataException("The JPEG target has inconsistent dimensions.");
                result = next;
            }
            offset += length;
        }
        return result ?? throw new InvalidDataException("The JPEG target has no supported size header.");
    }
}
