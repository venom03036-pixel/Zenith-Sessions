using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Public presentation only. Never replace a protected login identity or associate a process from this data.</summary>
public sealed record PublicAccountData(long UserId, string Username, string DisplayName, BitmapSource? Avatar,
    PublicDirectoryState AvatarState, DateTimeOffset RetrievedUtc, long PresentationGeneration)
{
    public string EvidenceLabel => "Public profile information; not sign-in or running-client verification.";
}

/// <summary>
/// Explicit, caller-triggered public profile/avatar work. No row ownership, automatic network refresh,
/// credentials, disk cache, timers, or per-account workers. The caller owns the directory/HTTP lifetime
/// and must drop row image references when clearing, locking or shutting down.
/// </summary>
public sealed class PublicAccountPresentation : IAsyncDisposable
{
    public const int MaximumDecodedEntries = 128;
    public const int AvatarDimension = 150;
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;
    public const int DecodedBytesPerAvatar = AvatarDimension * AvatarDimension * 4;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly IPublicRobloxDirectory _directory;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<long, CacheEntry> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private Operation? _active;
    private Task _activeTask = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    private bool _disposed;

    public PublicAccountPresentation(IPublicRobloxDirectory directory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public Task ActiveOperation { get { lock (_gate) return _activeTask; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public int CachedEntryCount { get { lock (_gate) { RemoveExpired(); return _cache.Count; } } }
    public long CachedDecodedPixelBytes
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired();
                long bytes = 0;
                foreach (CacheEntry entry in _cache.Values) if (entry.Data.Avatar is not null) bytes += DecodedBytesPerAvatar;
                return bytes;
            }
        }
    }

    public bool IsCurrent(PublicAccountData data)
    {
        lock (_gate) return !_disposed && data.PresentationGeneration == _generation;
    }

    public bool TryGetCached(long userId, out PublicAccountData? data)
    {
        lock (_gate)
        {
            data = null;
            if (_disposed) return false;
            RemoveExpired();
            if (!_cache.TryGetValue(userId, out CacheEntry? entry)) return false;
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            data = entry.Data;
            return true;
        }
    }

    /// <summary>True when the directory can read many players' headshot descriptors in one request.</summary>
    public bool CanBatchAvatars => _directory is IPublicRobloxAvatarBatchDirectory;

    /// <summary>
    /// One caller-owned request for the headshot descriptors (picture addresses and states, no pictures) of 1 to
    /// <see cref="PublicRobloxAvatarBatchLimits.MaximumBatch"/> distinct players. It caches nothing and starts no other
    /// work; the caller awaits it and runs it one at a time with its other requests here. Pass each item to
    /// <see cref="FetchAsync(long, PublicRobloxAvatarBatchItem, CancellationToken, bool)"/>.
    /// </summary>
    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>> GetAvatarDescriptorsAsync(IReadOnlyList<long> userIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            if (_disposed) return Task.FromResult(new PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>(
                PublicDirectoryState.Failure, null, "Public profile presentation is closed."));
        return _directory is IPublicRobloxAvatarBatchDirectory batch ? batch.GetAvatarsAsync(userIds, ct)
            : Task.FromResult(new PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>(
                PublicDirectoryState.Failure, null, "Public avatar information for several players at once is unavailable."));
    }

    public Task<PublicDirectoryResult<PublicAccountData>> FetchAsync(long userId, CancellationToken ct, bool forceRefresh = false)
        => Start(userId, null, ct, forceRefresh);

    /// <summary>
    /// The same as <see cref="FetchAsync(long, CancellationToken, bool)"/>, using a headshot descriptor already read by
    /// <see cref="GetAvatarDescriptorsAsync"/> instead of requesting this player's descriptor again. A descriptor for
    /// another player is rejected before any request.
    /// </summary>
    public Task<PublicDirectoryResult<PublicAccountData>> FetchAsync(long userId, PublicRobloxAvatarBatchItem descriptor,
        CancellationToken ct, bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ct.ThrowIfCancellationRequested();
        if (descriptor.UserId != userId) return Task.FromResult(new PublicDirectoryResult<PublicAccountData>(PublicDirectoryState.InvalidInput, null,
            "The public avatar information belongs to another player."));
        return Start(userId, descriptor, ct, forceRefresh);
    }

    private Task<PublicDirectoryResult<PublicAccountData>> Start(long userId, PublicRobloxAvatarBatchItem? descriptor, CancellationToken ct, bool forceRefresh)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(Failure("Public profile presentation is closed."));
            if (userId <= 0) return Task.FromResult(new PublicDirectoryResult<PublicAccountData>(PublicDirectoryState.InvalidInput, null, "Choose a positive Roblox user ID."));
            if (!forceRefresh && TryGetCached(userId, out PublicAccountData? cached))
                return Task.FromResult(Success(cached!, "Cached public profile information."));
            if (_active is not null) return Task.FromResult(Deferred("A public profile request is already active. Try again after it finishes."));
            var operation = new Operation(userId, _generation, ct, descriptor);
            _active = operation;
            Task<PublicDirectoryResult<PublicAccountData>> task = FetchCoreAsync(operation);
            _activeTask = task;
            return task;
        }
    }

    private async Task<PublicDirectoryResult<PublicAccountData>> FetchCoreAsync(Operation operation)
    {
        try
        {
            // There is at most one owned continuation; decoding never blocks the caller's UI context.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            operation.Token.ThrowIfCancellationRequested();
            var profile = await _directory.LookupUserAsync(operation.UserId, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (profile.State != PublicDirectoryState.Success) return ProviderFailure(profile.State, profile.RetryAfter, "Public profile");
            if (profile.Value is not { } user || user.UserId != operation.UserId || !ValidNames(user)) return Failure("The public profile response was invalid.");
            BitmapSource? image = null;
            PublicDirectoryState avatarState;
            TimeSpan? retry = null;
            var descriptor = operation.Descriptor is { } batched ? FromBatch(batched)
                : await _directory.GetAvatarAsync(operation.UserId, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (descriptor.State != PublicDirectoryState.Success)
            { avatarState = SafeState(descriptor.State); retry = SafeRetry(descriptor.RetryAfter); }
            else if (descriptor.Value is not { } avatar || avatar.UserId != operation.UserId
                || avatar.Width != AvatarDimension || avatar.Height != AvatarDimension)
                avatarState = PublicDirectoryState.Failure;
            else
            {
                var encoded = await _directory.FetchAvatarAsync(avatar, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                avatarState = SafeState(encoded.State);
                retry = SafeRetry(encoded.RetryAfter);
                if (encoded.State == PublicDirectoryState.Success)
                {
                    image = encoded.Value is { } value ? Decode(value, operation.Token) : null;
                    if (image is null) avatarState = PublicDirectoryState.Failure;
                }
            }
            operation.Token.ThrowIfCancellationRequested();
            var data = new PublicAccountData(user.UserId, user.Username, user.DisplayName, image, avatarState, _clock.GetUtcNow(), operation.Generation);
            lock (_gate)
            {
                if (_disposed || operation.Generation != _generation || operation.Token.IsCancellationRequested) return Superseded();
                RemoveExpired();
                if (_cache.TryGetValue(data.UserId, out CacheEntry? old)) Remove(old.Node);
                while (_cache.Count >= MaximumDecodedEntries) Remove(_lru.Last!);
                var node = _lru.AddFirst(data.UserId);
                _cache.Add(data.UserId, new(data, _clock.GetUtcNow().Add(CacheLifetime), node));
                return new(PublicDirectoryState.Success, data,
                    image is not null ? "Public profile and avatar loaded. This does not verify sign-in or a running client."
                        : "Public profile loaded; the public avatar is unavailable. Keep the placeholder.", retry);
            }
        }
        catch (OperationCanceledException) when (!operation.CallerToken.IsCancellationRequested) { return Superseded(); }
        catch (HttpRequestException) { return Failure("The public profile service could not be reached."); }
        catch (IOException) { return Failure("The public profile request could not be completed."); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_active, operation)) _active = null; }
            operation.Complete();
        }
    }

    /// <summary>Cancels/fences owned work and clears both retained decoded and encoded caches. It starts no network work.</summary>
    public void Clear()
    {
        Operation? operation;
        lock (_gate) { _generation++; _cache.Clear(); _lru.Clear(); operation = _active; }
        operation?.Cancel();
        _directory.ClearMemoryCache();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _generation++;
            _cache.Clear();
            _lru.Clear();
            _disposeTask = StopAsync(_active, _activeTask);
            return new(_disposeTask);
        }
    }
    private async Task StopAsync(Operation? operation, Task active)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        operation?.Cancel();
        _directory.ClearMemoryCache();
        try { await active.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Caller-requested cancellation has already fenced publication. */ }
    }

    private static BitmapSource? Decode(EncodedPublicAvatar encoded, CancellationToken ct)
        => DecodePublicPng(encoded.Bytes, encoded.MediaType, ct);

    // One strict 150px public-image decoder is shared by avatar and game-icon presentation.
    // It returns owned frozen pixels, never a decoder, metadata or an encoded stream.
    internal static BitmapSource? DecodePublicPng(ReadOnlyMemory<byte> bytes, string mediaType, CancellationToken ct)
        => DecodeBoundedPng(bytes, mediaType, AvatarDimension, AvatarDimension, ct);

    // Closed second policy: a gallery must never relax the original square-image contract.
    internal static BitmapSource? DecodeGalleryPng(ReadOnlyMemory<byte> bytes, string mediaType, CancellationToken ct)
        => DecodeBoundedPng(bytes, mediaType, PublicRobloxGameGalleryLimits.Width, PublicRobloxGameGalleryLimits.Height, ct);

    // Third closed policy for 16:9 card pictures; it relaxes neither the square nor the gallery contract.
    internal static BitmapSource? DecodeCardPng(ReadOnlyMemory<byte> bytes, string mediaType, CancellationToken ct)
        => DecodeBoundedPng(bytes, mediaType, PublicRobloxGameArtLimits.Width, PublicRobloxGameArtLimits.Height, ct);

    private static BitmapSource? DecodeBoundedPng(ReadOnlyMemory<byte> bytes, string mediaType, int width, int height, CancellationToken ct)
    {
        if (!string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            || bytes.Length is < 45 or > MaximumEncodedBytes || !ValidPngEnvelope(bytes, width, height, ct)) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) return null;
            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height) return null;
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            if (converted.PixelWidth != width || converted.PixelHeight != height || converted.Format.BitsPerPixel != 32) return null;
            byte[] pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0); // Force complete pixel decoding before returning success.
            ct.ThrowIfCancellationRequested();
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze(); // No decoder, encoded stream, metadata or thread-affine frame escapes this method.
            return image;
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException or InvalidOperationException or COMException or IOException)
        { return null; } // A malformed public image is a placeholder, never a raw decoder/provider error.
    }

    private static bool ValidPngEnvelope(ReadOnlyMemory<byte> encoded, int width, int height, CancellationToken ct)
    {
        ReadOnlySpan<byte> bytes = encoded.Span;
        if (!bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        int offset = 8, chunks = 0, metadataBytes = 0;
        bool header = false, data = false, endedData = false;
        byte depth = 0, color = 0, interlace = 0;
        var imageData = new List<ReadOnlyMemory<byte>>();
        while (offset <= bytes.Length - 12)
        {
            ct.ThrowIfCancellationRequested();
            if (++chunks > 1024) return false;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > (uint)(bytes.Length - offset - 12)) return false;
            int count = (int)length;
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4), content = bytes.Slice(offset + 8, count);
            foreach (byte c in type) if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return false;
            if (Crc(bytes.Slice(offset + 4, count + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4))) return false;
            bool isHeader = type.SequenceEqual("IHDR"u8), isData = type.SequenceEqual("IDAT"u8), isEnd = type.SequenceEqual("IEND"u8);
            if (!header && !isHeader) return false;
            if (isHeader)
            {
                if (header || count != 13 || BinaryPrimitives.ReadUInt32BigEndian(content[..4]) != width
                    || BinaryPrimitives.ReadUInt32BigEndian(content.Slice(4, 4)) != height
                    || !ValidBitDepth(content[8], content[9]) || content[10] != 0 || content[11] != 0 || content[12] > 1) return false;
                header = true;
                depth = content[8]; color = content[9]; interlace = content[12];
            }
            else if (isData)
            {
                if (endedData) return false;
                data = true;
                imageData.Add(encoded.Slice(offset + 8, count));
            }
            else if (isEnd) return count == 0 && data && offset + 12 == bytes.Length
                && ValidCompressedPixels(imageData, depth, color, interlace, width, height, ct);
            else
            {
                if (data) endedData = true;
                // Reject animation and compressed/unnecessary metadata before WIC can inflate it.
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)
                    || type.SequenceEqual("iCCP"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)) return false;
                if (type.SequenceEqual("PLTE"u8)) { if (data || count is < 3 or > 768 || count % 3 != 0) return false; }
                else if ((type[0] & 32) == 0) return false; // Unrecognized critical chunk.
                else { metadataBytes += count; if (count > 4096 || metadataBytes > 16384) return false; }
            }
            offset += count + 12;
        }
        return false; // Truncated chunks, missing IEND, and trailing bytes are never accepted.
    }
    private static bool ValidCompressedPixels(IReadOnlyList<ReadOnlyMemory<byte>> chunks, byte depth, byte color, byte interlace, int imageWidth, int imageHeight, CancellationToken ct)
    {
        try
        {
            // Logically concatenate IDAT without another encoded-image allocation. One-byte source
            // reads prevent inflater read-ahead from hiding trailing compressed streams/garbage.
            using var compressed = new PngCompressedInput(chunks, ct);
            if (compressed.Length < 6) return false;
            int cmf = compressed.ByteAt(0), flg = compressed.ByteAt(1);
            if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 32) != 0) return false;
            uint expectedAdler = 0;
            for (int i = 4; i > 0; i--) expectedAdler = (expectedAdler << 8) | compressed.ByteAt(compressed.Length - i);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            int channels = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0) return false;
            byte[] row = new byte[imageWidth * 8]; // Closed policies bound even RGBA16 source rows.
            uint sum1 = 1, sum2 = 0;
            void AddAdler(byte value)
            {
                sum1 += value;
                if (sum1 >= 65521) sum1 -= 65521;
                sum2 += sum1;
                if (sum2 >= 65521) sum2 -= 65521;
            }
            bool ReadPass(int width, int height)
            {
                if (width == 0 || height == 0) return true;
                int rowBytes = (width * channels * depth + 7) / 8;
                for (int y = 0; y < height; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    int filter = zlib.ReadByte();
                    if (filter is < 0 or > 4) return false;
                    AddAdler((byte)filter);
                    int filled = 0;
                    while (filled < rowBytes)
                    {
                        int read = zlib.Read(row.AsSpan(filled, rowBytes - filled));
                        if (read == 0) return false;
                        for (int i = filled; i < filled + read; i++) AddAdler(row[i]);
                        filled += read;
                    }
                }
                return true;
            }
            if (interlace == 0)
            {
                if (!ReadPass(imageWidth, imageHeight)) return false;
            }
            else
            {
                // Adam7 pass geometry; rows are independently filtered in each nonempty pass.
                ReadOnlySpan<int> startX = [0, 4, 0, 2, 0, 1, 0], startY = [0, 0, 4, 0, 2, 0, 1];
                ReadOnlySpan<int> stepX = [8, 8, 4, 4, 2, 2, 1], stepY = [8, 8, 8, 4, 4, 2, 2];
                for (int pass = 0; pass < 7; pass++)
                    if (!ReadPass((imageWidth - startX[pass] + stepX[pass] - 1) / stepX[pass],
                        (imageHeight - startY[pass] + stepY[pass] - 1) / stepY[pass])) return false;
            }
            // Exact output length, zlib EOF, independently checked Adler32, and exact compressed
            // consumption are all required: WIC can otherwise silently fill malformed images.
            return zlib.ReadByte() == -1 && compressed.Position == compressed.Length && ((sum2 << 16) | sum1) == expectedAdler;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return false; }
    }
    private static bool ValidBitDepth(byte depth, byte color) => color switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        _ => false
    };
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (int index = 0; index < table.Length; index++)
        {
            uint value = (uint)index;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
    private static bool ValidNames(PublicRobloxUser user)
    {
        if (user.Username is not { Length: > 0 and <= 20 } || user.DisplayName is not { Length: > 0 and <= 100 }
            || string.IsNullOrWhiteSpace(user.DisplayName)) return false;
        foreach (char c in user.Username) if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        foreach (char c in user.DisplayName) if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) return false;
        return true;
    }
    private void RemoveExpired()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        var node = _lru.Last;
        while (node is not null) { var previous = node.Previous; if (_cache[node.Value].ExpiresUtc <= now) Remove(node); node = previous; }
    }
    private void Remove(LinkedListNode<long> node) { _cache.Remove(node.Value); _lru.Remove(node); }
    // A batch item reads exactly as the single-player headshot reply would: still being prepared waits like that
    // reply's 30-second hint, and blocked or unavailable keeps the placeholder.
    private static PublicDirectoryResult<PublicRobloxAvatar> FromBatch(PublicRobloxAvatarBatchItem item) => item.State switch
    {
        PublicRobloxThumbnailState.Ready when item.Avatar is { } avatar => new(PublicDirectoryState.Success, avatar, "Public avatar information."),
        PublicRobloxThumbnailState.Ready => new(PublicDirectoryState.Failure, null, "The public avatar information was invalid."),
        PublicRobloxThumbnailState.Pending => new(PublicDirectoryState.Deferred, null, "The public avatar is still being prepared.", TimeSpan.FromSeconds(30)),
        _ => new(PublicDirectoryState.Unknown, null, "The public avatar is unavailable.")
    };
    private static PublicDirectoryState SafeState(PublicDirectoryState state) => Enum.IsDefined(state) ? state : PublicDirectoryState.Failure;
    private static TimeSpan? SafeRetry(TimeSpan? retry) => retry is { } delay && delay>TimeSpan.Zero?delay:null;
    private static PublicDirectoryResult<PublicAccountData> ProviderFailure(PublicDirectoryState state, TimeSpan? retry, string operation) => new(
        state is PublicDirectoryState.Unknown or PublicDirectoryState.Deferred ? state : PublicDirectoryState.Failure, null,
        state == PublicDirectoryState.Deferred ? operation + " is deferred. Try again later." : operation + " information is unavailable.", SafeRetry(retry));
    private static PublicDirectoryResult<PublicAccountData> Success(PublicAccountData data, string message) => new(PublicDirectoryState.Success, data, message);
    private static PublicDirectoryResult<PublicAccountData> Failure(string message) => new(PublicDirectoryState.Failure, null, message);
    private static PublicDirectoryResult<PublicAccountData> Deferred(string message) => new(PublicDirectoryState.Deferred, null, message);
    private static PublicDirectoryResult<PublicAccountData> Superseded() => Deferred("The public profile request was canceled or superseded. Refresh the selected profile when ready.");
    private sealed record CacheEntry(PublicAccountData Data, DateTimeOffset ExpiresUtc, LinkedListNode<long> Node);

    private sealed class PngCompressedInput : Stream
    {
        private readonly IReadOnlyList<ReadOnlyMemory<byte>> _chunks;
        private readonly long _length;
        private readonly CancellationToken _ct;
        private int _chunk, _offset;
        private long _position;
        public PngCompressedInput(IReadOnlyList<ReadOnlyMemory<byte>> chunks, CancellationToken ct)
        {
            _chunks = chunks; _ct = ct;
            foreach (ReadOnlyMemory<byte> chunk in chunks) _length += chunk.Length;
        }
        public byte ByteAt(long offset)
        {
            foreach (ReadOnlyMemory<byte> chunk in _chunks)
            { if (offset < chunk.Length) return chunk.Span[(int)offset]; offset -= chunk.Length; }
            throw new InvalidDataException("The public PNG compressed stream is incomplete.");
        }
        public override int ReadByte()
        {
            if ((_position & 4095) == 0) _ct.ThrowIfCancellationRequested();
            while (_chunk < _chunks.Count)
            {
                if (_offset == _chunks[_chunk].Length) { _chunk++; _offset = 0; continue; }
                _position++;
                return _chunks[_chunk].Span[_offset++];
            }
            return -1;
        }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            int value = ReadByte();
            if (value < 0) return 0;
            buffer[0] = (byte)value;
            return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Operation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private bool _complete;
        private int _cancelers;
        public long UserId { get; }
        public long Generation { get; }
        public CancellationToken CallerToken { get; }
        public CancellationToken Token { get; }
        /// <summary>Set when a batch already read this player's headshot descriptor; null requests it here.</summary>
        public PublicRobloxAvatarBatchItem? Descriptor { get; }
        public Operation(long userId, long generation, CancellationToken callerToken, PublicRobloxAvatarBatchItem? descriptor)
        {
            UserId = userId; Generation = generation; CallerToken = callerToken; Descriptor = descriptor;
            _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken); Token = _source.Token;
        }
        public void Cancel()
        {
            lock (_gate) { if (_complete) return; _cancelers++; }
            try { _source.Cancel(); }
            finally { lock (_gate) { if (--_cancelers == 0 && _complete) _source.Dispose(); } }
        }
        public void Complete() { lock (_gate) { _complete = true; if (_cancelers == 0) _source.Dispose(); } }
    }
}
