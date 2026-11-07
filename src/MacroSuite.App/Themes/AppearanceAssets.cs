using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacroSuite.App.Themes;

public sealed record AppearanceFrame(BitmapSource Image, TimeSpan Duration);
public sealed record AppearanceAsset(string Id, IReadOnlyList<AppearanceFrame> Frames, IReadOnlyList<string> Swatches, string? Notice);

/// <summary>Owned local files and bounded immutable frames; this service never captures the desktop.</summary>
public sealed class AppearanceAssetService
{
    private const int MaxBytes = 25 * 1024 * 1024, MaxCanvasPixels = 4_194_304, MaxFrames = 300;
    private const long MaxSourceFramePixels = 16_777_216, MaxFrameBytes = 48 * 1024 * 1024;
    private readonly string _directory;
    public AppearanceAssetService(string appDataRoot) => _directory = Path.Combine(appDataRoot, "Themes", "Assets");
    public static bool IsValidAssetId(string? id) => id is not null && Regex.IsMatch(id, "\\A[a-f0-9]{64}\\.(png|jpg|bmp|gif)\\z", RegexOptions.CultureInvariant);

    public Task<AppearanceAsset> ImportAsync(string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        byte[] bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        string extension = Signature(bytes);
        string id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
        AppearanceAsset asset = Decode(id, bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        string destination = Path.Combine(_directory, id);
        if (!File.Exists(destination))
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return asset;
    }, cancellationToken);

    public Task<AppearanceAsset> LoadAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidAssetId(id)) throw new InvalidDataException("The saved wallpaper identifier is invalid.");
        return Task.Run(async () => Decode(id, await ReadBoundedAsync(Path.Combine(_directory, id), cancellationToken).ConfigureAwait(false), cancellationToken), cancellationToken);
    }

    public Task RemoveUnusedAsync(IReadOnlyCollection<string> keep, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(_directory)) return;
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unused backgrounds were kept because the asset folder is redirected.");
        string root = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var retained = new HashSet<string>(keep, StringComparer.Ordinal);
        // Top-level, generated hash files only. Never recurse or follow a link, and
        // bound the work even if an unrelated program populated this directory.
        int inspected = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > 4096) break;
            string name = Path.GetFileName(file), resolved = Path.GetFullPath(file);
            if (!IsValidAssetId(name) || retained.Contains(name) || !resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(resolved);
        }
    }, cancellationToken);

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length is < 12 or > MaxBytes) throw new InvalidDataException("Choose an image or GIF smaller than 25 MB.");
        byte[] bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static string Signature(byte[] bytes)
    {
        if (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) return ".gif";
        if (bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
        if (bytes[0] == 'B' && bytes[1] == 'M') return ".bmp";
        throw new InvalidDataException("Choose a PNG, JPEG, BMP, or GIF file.");
    }

    private static AppearanceAsset Decode(string id, byte[] bytes, CancellationToken ct)
    {
        string format = Signature(bytes);
        IReadOnlyList<AppearanceFrame> frames;
        string? notice = null;
        if (format == ".gif")
        {
            frames = DecodeGif(bytes, ct, out bool stillFallback);
            notice = stillFallback
                ? "This GIF exceeds the animation budget. Its first frame is used as a still image."
                : "GIF playback is limited to 15 frames/s and a 48 MB frame budget. It pauses during automation.";
        }
        else
        {
            using var metadataStream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 16_777_216)
                throw new InvalidDataException("Choose a still image with at most 16 million pixels.");
            double scale = Math.Min(1, Math.Min(1920d / frame.PixelWidth, 1080d / frame.PixelHeight));
            ct.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes, false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
            bitmap.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            frames = new[] { new AppearanceFrame(bitmap, TimeSpan.FromSeconds(1)) };
        }
        ct.ThrowIfCancellationRequested();
        return new(id, frames, ExtractSwatches(frames), notice);
    }

    private sealed record GifFrame(int X, int Y, int Width, int Height, int Delay, int Disposal, bool TransparentBackground);
    private sealed record GifInfo(int Width, int Height, byte[] Background, List<GifFrame> Frames,
        int FirstFrameEnd, bool StillFallback = false);

    private static GifInfo ReadGif(byte[] bytes)
    {
        int position = 6;
        int ReadWord() { Need(2); int n = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2)); position += 2; return n; }
        int ReadByte() { Need(1); return bytes[position++]; }
        void Need(int count) { if (count < 0 || position > bytes.Length - count) throw new InvalidDataException("The GIF is incomplete."); }
        void Skip(int count) { Need(count); position += count; }
        void Blocks() { int size; while ((size = ReadByte()) != 0) Skip(size); }
        int width = ReadWord(), height = ReadWord(); CheckCanvas(width, height);
        int flags = ReadByte(), backgroundIndex = ReadByte(); Skip(1);
        byte[] background = new byte[4];
        if ((flags & 128) != 0)
        {
            int tableSize = 3 * (1 << ((flags & 7) + 1)); Need(tableSize);
            int offset = position + backgroundIndex * 3;
            if (offset + 2 < position + tableSize) { background[0] = bytes[offset + 2]; background[1] = bytes[offset + 1]; background[2] = bytes[offset]; background[3] = 255; }
            Skip(tableSize);
        }
        var frames = new List<GifFrame>();
        int delay = 100, disposal = 0; bool transparent = false;
        long pixels = 0;
        int firstFrameEnd = 0;
        while (position < bytes.Length)
        {
            int marker = ReadByte(); if (marker == 59) break;
            if (marker == 33)
            {
                int label = ReadByte();
                if (label == 249)
                {
                    if (ReadByte() != 4) throw new InvalidDataException("The GIF control block is invalid.");
                    int control = ReadByte(); disposal = (control >> 2) & 7;
                    delay = Math.Max(20, ReadWord() * 10);
                    int transparentIndex = ReadByte(); transparent = (control & 1) != 0 && transparentIndex == backgroundIndex;
                    if (ReadByte() != 0 || disposal > 3) throw new InvalidDataException("The GIF disposal mode is unsupported.");
                }
                else Blocks();
            }
            else if (marker == 44)
            {
                int x = ReadWord(), y = ReadWord(), w = ReadWord(), h = ReadWord();
                if (w == 0 || h == 0 || x + w > width || y + h > height) throw new InvalidDataException("A GIF frame falls outside its canvas.");
                pixels += (long)w * h;
                if (frames.Count >= MaxFrames || pixels > MaxSourceFramePixels)
                    return new(width, height, background, [frames[0]], firstFrameEnd, StillFallback: true);
                int packed = ReadByte(); if ((packed & 128) != 0) Skip(3 * (1 << ((packed & 7) + 1)));
                Skip(1); Blocks();
                frames.Add(new(x, y, w, h, delay, disposal, transparent)); delay = 100; disposal = 0; transparent = false;
                if (frames.Count == 1) firstFrameEnd = position;
            }
            else throw new InvalidDataException("The GIF contains an invalid block.");
        }
        if (frames.Count == 0) throw new InvalidDataException("The GIF contains no image frames.");
        return new(width, height, background, frames, firstFrameEnd);
    }

    private static IReadOnlyList<AppearanceFrame> DecodeGif(byte[] bytes, CancellationToken ct, out bool stillFallback)
    {
        GifInfo info = ReadGif(bytes);
        stillFallback = info.StillFallback;
        byte[] decodeBytes = bytes;
        if (stillFallback)
        {
            // WPF builds a frame directory when opening a GIF. Give it only the
            // validated first frame, not an unsupported animation with unbounded
            // frame metadata. Keep its palette, extensions and original offset.
            decodeBytes = new byte[checked(info.FirstFrameEnd + 1)];
            bytes.AsSpan(0, info.FirstFrameEnd).CopyTo(decodeBytes);
            decodeBytes[^1] = 0x3B;
        }
        ct.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(decodeBytes, false);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder.Frames.Count != info.Frames.Count) throw new InvalidDataException("The GIF frame directory is inconsistent.");
        double scale = Math.Min(1, Math.Min(960d / info.Width, 540d / info.Height));
        scale = Math.Min(scale, Math.Sqrt(MaxFrameBytes / (4d * info.Width * info.Height * info.Frames.Count)));
        int outputWidth = Math.Max(1, (int)(info.Width * scale)), outputHeight = Math.Max(1, (int)(info.Height * scale));
        byte[] canvas = new byte[checked(info.Width * info.Height * 4)];
        if (!info.Frames[0].TransparentBackground) Fill(canvas, info.Width, new(0, 0, info.Width, info.Height, 0, 0, false), info.Background);
        byte[]? restore = null;
        var output = new List<AppearanceFrame>();
        for (int index = 0; index < info.Frames.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index > 0)
            {
                var previous = info.Frames[index - 1];
                if (previous.Disposal == 2) Fill(canvas, info.Width, previous, previous.TransparentBackground ? new byte[4] : info.Background);
                else if (previous.Disposal == 3 && restore is not null)
                    for (int row = 0; row < previous.Height; row++)
                        Buffer.BlockCopy(restore, row * previous.Width * 4, canvas,
                            ((previous.Y + row) * info.Width + previous.X) * 4, previous.Width * 4);
            }
            GifFrame frame = info.Frames[index];
            restore = null;
            if (frame.Disposal == 3)
            {
                // Restoring this frame can only affect its rectangle. Saving the
                // whole canvas for small overlays would create excessive copy work.
                restore = new byte[checked(frame.Width * frame.Height * 4)];
                for (int row = 0; row < frame.Height; row++)
                    Buffer.BlockCopy(canvas, ((frame.Y + row) * info.Width + frame.X) * 4,
                        restore, row * frame.Width * 4, frame.Width * 4);
            }
            var pixels = new FormatConvertedBitmap(decoder.Frames[index], PixelFormats.Bgra32, null, 0);
            if (pixels.PixelWidth != frame.Width || pixels.PixelHeight != frame.Height) throw new InvalidDataException("The GIF frame dimensions are inconsistent.");
            byte[] raw = new byte[checked(frame.Width * frame.Height * 4)]; pixels.CopyPixels(raw, frame.Width * 4, 0);
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    int src = (y * frame.Width + x) * 4, dst = ((y + frame.Y) * info.Width + frame.X + x) * 4;
                    if (raw[src + 3] != 0) Buffer.BlockCopy(raw, src, canvas, dst, 4);
                }
            byte[] reduced = new byte[checked(outputWidth * outputHeight * 4)];
            for (int y = 0; y < outputHeight; y++)
                for (int x = 0; x < outputWidth; x++)
                    Buffer.BlockCopy(canvas, ((y * info.Height / outputHeight) * info.Width + x * info.Width / outputWidth) * 4, reduced, (y * outputWidth + x) * 4, 4);
            var bitmap = BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, reduced, outputWidth * 4);
            bitmap.Freeze(); output.Add(new(bitmap, TimeSpan.FromMilliseconds(frame.Delay)));
        }
        return output.AsReadOnly();
    }

    private static void Fill(byte[] canvas, int width, GifFrame rectangle, byte[] color)
    {
        for (int y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
            for (int x = rectangle.X; x < rectangle.X + rectangle.Width; x++)
                Buffer.BlockCopy(color, 0, canvas, (y * width + x) * 4, 4);
    }

    private static void CheckCanvas(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxCanvasPixels)
            throw new InvalidDataException("Choose an image with at most 4 million pixels.");
    }

    private static IReadOnlyList<string> ExtractSwatches(IReadOnlyList<AppearanceFrame> frames)
    {
        var bins = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int n = 0; n < Math.Min(5, frames.Count); n++)
        {
            var source = frames[n * frames.Count / Math.Min(5, frames.Count)].Image;
            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (int y = 0; y < bitmap.PixelHeight; y += Math.Max(1, bitmap.PixelHeight / 64))
                for (int x = 0; x < bitmap.PixelWidth; x += Math.Max(1, bitmap.PixelWidth / 64))
                {
                    int i = (y * bitmap.PixelWidth + x) * 4; if (pixels[i + 3] < 128) continue;
                    int r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
                    int key = (r >> 5) << 6 | (g >> 5) << 3 | (b >> 5);
                    bins.TryGetValue(key, out var value); bins[key] = (value.R + r, value.G + g, value.B + b, value.Count + 1);
                }
        }
        return bins.Values.OrderByDescending(v => v.Count).Take(5)
            .Select(v => $"#{v.R / v.Count:X2}{v.G / v.Count:X2}{v.B / v.Count:X2}").ToArray();
    }
}
