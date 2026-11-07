using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PublicAccountPresentationTests
{
    [Fact]
    public async Task CompletePngBecomesFrozenDetachedBounded32BitPixels()
    {
        var directory = new SyntheticDirectory(Png());
        await using var presenter = new PublicAccountPresentation(directory);
        Task<PublicDirectoryResult<PublicAccountData>> task = presenter.FetchAsync(42, default);
        Assert.Same(task, presenter.ActiveOperation);
        var result = await task;
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.NotNull(result.Value);
        var data = result.Value;
        Assert.Equal(42, data.UserId);
        Assert.Equal("Synthetic_42", data.Username);
        Assert.Equal("Public Display", data.DisplayName);
        Assert.Contains("Public profile information", data.EvidenceLabel);
        Assert.Equal(PublicDirectoryState.Success, data.AvatarState);
        var image = data.Avatar;
        Assert.NotNull(image);
        Assert.True(image.IsFrozen);
        Assert.Equal(150, image.PixelWidth);
        Assert.Equal(150, image.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, image.Format);
        Assert.Equal(96, image.DpiX);
        byte[] pixels = new byte[PublicAccountPresentation.DecodedBytesPerAvatar];
        image.CopyPixels(pixels, 150 * 4, 0);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[^4..]);
        Assert.True(presenter.IsCurrent(data));
        Assert.Equal(90_000, presenter.CachedDecodedPixelBytes);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("bad-crc")]
    [InlineData("trailing")]
    [InlineData("bad-zlib")]
    [InlineData("animated")]
    [InlineData("wrong-size")]
    [InlineData("wrong-format")]
    [InlineData("header-only")]
    [InlineData("oversized")]
    [InlineData("compressed-metadata")]
    [InlineData("bad-adler")]
    [InlineData("short-scanlines")]
    [InlineData("extra-scanlines")]
    [InlineData("illegal-filter")]
    [InlineData("truncated-zlib")]
    [InlineData("trailing-zlib")]
    [InlineData("second-zlib")]
    public async Task MalformedOrUnsafeImagesKeepThePlaceholderAndPublicName(string kind)
    {
        byte[] png = kind switch
        {
            "truncated" => Png()[..^1],
            "trailing" => [.. Png(), 0],
            "bad-zlib" => Png(invalidZlib: true),
            "animated" => Png(extraChunk: "acTL"),
            "wrong-size" => Png(width: 10000),
            "header-only" => Png()[..33],
            "oversized" => new byte[PublicAccountPresentation.MaximumEncodedBytes + 1],
            "compressed-metadata" => Png(extraChunk: "iCCP"),
            "bad-adler" or "short-scanlines" or "extra-scanlines" or "illegal-filter" or "truncated-zlib" or "trailing-zlib" or "second-zlib" => Png(streamFault: kind),
            _ => Png()
        };
        if (kind == "bad-crc") png[^1] ^= 1;
        var directory = new SyntheticDirectory(png) { MediaType = kind == "wrong-format" ? "image/jpeg" : "image/png" };
        await using var presenter = new PublicAccountPresentation(directory);
        var result = await presenter.FetchAsync(42, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.NotNull(result.Value);
        Assert.Equal("Public Display", result.Value.DisplayName);
        Assert.Null(result.Value.Avatar);
        Assert.Equal(PublicDirectoryState.Failure, result.Value.AvatarState);
        Assert.Contains("placeholder", result.Message);
        Assert.Equal(0, presenter.CachedDecodedPixelBytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    [InlineData(4, 16)]
    [InlineData(6, 8)]
    [InlineData(6, 16)]
    public async Task LegalPngColorAndBitDepthDecodeWithinTheSameFixedOutputBudget(int colorType, int bitDepth)
    {
        var directory = new SyntheticDirectory(Png(colorType: colorType, bitDepth: bitDepth, splitIdat: true));
        await using var presenter = new PublicAccountPresentation(directory);
        var result = await presenter.FetchAsync(42, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.NotNull(result.Value!.Avatar);
        Assert.Equal(PublicDirectoryState.Success, result.Value.AvatarState);
        Assert.Equal(PixelFormats.Bgra32, result.Value.Avatar.Format);
        Assert.Equal(90_000, presenter.CachedDecodedPixelBytes);
    }

    [Fact]
    public async Task Adam7AndSplitIdatAreValidatedAndProduceAllExpectedPixels()
    {
        var directory = new SyntheticDirectory(Png(interlaced: true, splitIdat: true));
        await using var presenter = new PublicAccountPresentation(directory);
        var result = await presenter.FetchAsync(42, default);
        var image = result.Value!.Avatar;
        Assert.NotNull(image);
        byte[] pixels = new byte[PublicAccountPresentation.DecodedBytesPerAvatar];
        image.CopyPixels(pixels, 600, 0);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { Assert.Equal(30, pixels[offset]); Assert.Equal(20, pixels[offset + 1]); Assert.Equal(10, pixels[offset + 2]); Assert.Equal(255, pixels[offset + 3]); }
    }

    [Fact]
    public async Task RestorationAndCacheReadsNeverStartNetworkAndExplicitRefreshCanBypassCache()
    {
        var directory = new SyntheticDirectory(Png());
        var clock = new FakeTime();
        await using var presenter = new PublicAccountPresentation(directory, clock);
        Assert.False(presenter.TryGetCached(42, out _));
        Assert.Equal(0, directory.LookupCalls);
        var first = await presenter.FetchAsync(42, default);
        Assert.True(presenter.TryGetCached(42, out var restored));
        Assert.Same(first.Value, restored);
        Assert.Same(first.Value, (await presenter.FetchAsync(42, default)).Value);
        Assert.Equal(1, directory.LookupCalls);
        Assert.Equal(1, directory.DescriptorCalls);
        Assert.Equal(1, directory.EncodedCalls);
        var refreshed = await presenter.FetchAsync(42, default, forceRefresh: true);
        Assert.NotSame(first.Value!.Avatar, refreshed.Value!.Avatar);
        Assert.Equal(2, directory.LookupCalls);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(presenter.TryGetCached(42, out _));
        Assert.Equal(2, directory.LookupCalls);
        Assert.Equal(PublicDirectoryState.Success, (await presenter.FetchAsync(42, default)).State);
        Assert.Equal(3, directory.LookupCalls);
    }

    [Fact]
    public async Task DecodedRetentionIsBoundedAndEvictsOldEntries()
    {
        var directory = new SyntheticDirectory(Png());
        await using var presenter = new PublicAccountPresentation(directory);
        for (int userId = 1; userId <= PublicAccountPresentation.MaximumDecodedEntries + 1; userId++)
            Assert.Equal(PublicDirectoryState.Success, (await presenter.FetchAsync(userId, default)).State);
        Assert.Equal(PublicAccountPresentation.MaximumDecodedEntries, presenter.CachedEntryCount);
        Assert.Equal((long)PublicAccountPresentation.MaximumDecodedEntries * PublicAccountPresentation.DecodedBytesPerAvatar, presenter.CachedDecodedPixelBytes);
        Assert.False(presenter.TryGetCached(1, out _));
        Assert.True(presenter.TryGetCached(2, out _));
        int calls = directory.LookupCalls;
        presenter.Clear();
        Assert.Equal(0, presenter.CachedEntryCount);
        Assert.Equal(0, presenter.CachedDecodedPixelBytes);
        Assert.Equal(calls, directory.LookupCalls);
        Assert.Equal(1, directory.ClearCalls);
    }

    [Fact]
    public async Task ActiveRequestsDeferAdditionalWorkWithoutCreatingQueuedRequests()
    {
        var started = Signal();
        var release = Signal();
        var directory = new SyntheticDirectory(Png())
        {
            Lookup = async (id, ct) => { started.TrySetResult(true); await release.Task.WaitAsync(ct); return User(id); }
        };
        await using var presenter = new PublicAccountPresentation(directory);
        var first = presenter.FetchAsync(42, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await presenter.FetchAsync(43, default);
        Assert.Equal(PublicDirectoryState.Deferred, busy.State);
        Assert.Null(busy.Value);
        Assert.Equal(1, directory.LookupCalls);
        release.SetResult(true);
        Assert.Equal(PublicDirectoryState.Success, (await first).State);
        Assert.Equal(1, directory.LookupCalls);
        directory.Lookup = null;
        Assert.Equal(PublicDirectoryState.Success, (await presenter.FetchAsync(43, default)).State);
        Assert.Equal(2, directory.LookupCalls);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndTheOwnedSlotBecomesAvailable()
    {
        var started = Signal();
        var directory = new SyntheticDirectory(Png())
        {
            Lookup = async (id, ct) => { started.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return User(id); }
        };
        await using var presenter = new PublicAccountPresentation(directory);
        using var stop = new CancellationTokenSource();
        var task = presenter.FetchAsync(42, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(presenter.ActiveOperation.IsCompleted);
        Assert.Equal(0, presenter.CachedEntryCount);
        directory.Lookup = null;
        Assert.Equal(PublicDirectoryState.Success, (await presenter.FetchAsync(42, default)).State);
    }

    [Fact]
    public async Task ClearFencesLateProvidersAndCachedPresentationGenerations()
    {
        var started = Signal();
        var release = Signal();
        var directory = new SyntheticDirectory(Png())
        {
            // Deliberately ignores cancellation to simulate a late provider result.
            Lookup = async (id, _) => { started.TrySetResult(true); await release.Task; return User(id); }
        };
        await using var presenter = new PublicAccountPresentation(directory);
        var pending = presenter.FetchAsync(42, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            long previousGeneration = presenter.Generation;
            presenter.Clear();
            Assert.True(presenter.Generation > previousGeneration);
            Assert.Equal(PublicDirectoryState.Deferred, (await presenter.FetchAsync(43, default)).State);
        }
        finally { release.TrySetResult(true); }
        var late = await pending;
        Assert.Equal(PublicDirectoryState.Deferred, late.State);
        Assert.Null(late.Value);
        Assert.Equal(0, presenter.CachedEntryCount);
        Assert.Equal(0, directory.DescriptorCalls);
        directory.Lookup = null;
        var current = (await presenter.FetchAsync(42, default)).Value!;
        Assert.True(presenter.IsCurrent(current));
        presenter.Clear();
        Assert.False(presenter.IsCurrent(current));
        Assert.False(presenter.TryGetCached(42, out _));
    }

    [Fact]
    public async Task ShutdownCancelsAndAwaitsTheOwnedTaskAndDoesNotRestartWork()
    {
        var started = Signal();
        var directory = new SyntheticDirectory(Png())
        {
            Lookup = async (id, ct) => { started.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return User(id); }
        };
        await using var presenter = new PublicAccountPresentation(directory);
        var pending = presenter.FetchAsync(42, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await presenter.DisposeAsync();
        Assert.True(presenter.ActiveOperation.IsCompleted);
        Assert.Equal(PublicDirectoryState.Deferred, (await pending).State);
        Assert.Null((await pending).Value);
        Assert.Equal(PublicDirectoryState.Failure, (await presenter.FetchAsync(43, default)).State);
        Assert.False(presenter.TryGetCached(42, out _));
        Assert.Equal(1, directory.LookupCalls);
        await presenter.DisposeAsync();
        Assert.Equal(1, directory.ClearCalls);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(7200)]
    public async Task AvatarDeferralKeepsPublicNamesAndNeverClaimsVerifiedLogin(int delaySeconds)
    {
        var directory = new SyntheticDirectory(Png())
        {
            Descriptor = (_, _) => Task.FromResult(new PublicDirectoryResult<PublicRobloxAvatar>(PublicDirectoryState.Deferred, null,
                "untrusted-provider-body", TimeSpan.FromSeconds(delaySeconds)))
        };
        await using var presenter = new PublicAccountPresentation(directory);
        var result = await presenter.FetchAsync(42, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal("Synthetic_42", result.Value!.Username);
        Assert.Equal(PublicDirectoryState.Deferred, result.Value.AvatarState);
        Assert.Null(result.Value.Avatar);
        Assert.Equal(TimeSpan.FromSeconds(delaySeconds), result.RetryAfter);
        Assert.Contains("not sign-in", result.Value.EvidenceLabel);
        Assert.DoesNotContain("untrusted-provider-body", result.Message);
        Assert.Equal(0, directory.EncodedCalls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-id")]
    [InlineData("bad-username")]
    [InlineData("bad-display")]
    public async Task InvalidPublicIdentityNeverBecomesAVisiblePresentation(string kind)
    {
        var directory = new SyntheticDirectory(Png())
        {
            Lookup = (id, _) => Task.FromResult(kind == "unknown"
                ? new PublicDirectoryResult<PublicRobloxUser>(PublicDirectoryState.Unknown, null, "untrusted-provider-body")
                : User(id) with { Value = User(id).Value! with
                    { UserId = kind == "wrong-id" ? id + 1 : id, Username = kind == "bad-username" ? "Bad Name" : "Synthetic", DisplayName = kind == "bad-display" ? "bad\nname" : "Display" } })
        };
        await using var presenter = new PublicAccountPresentation(directory);
        var result = await presenter.FetchAsync(42, default);
        Assert.NotEqual(PublicDirectoryState.Success, result.State);
        Assert.Null(result.Value);
        Assert.Equal(0, presenter.CachedEntryCount);
        Assert.Equal(0, directory.DescriptorCalls);
        Assert.DoesNotContain("untrusted-provider-body", result.Message);
    }

    [Fact]
    public async Task InvalidInputNeverCallsTheDirectory()
    {
        var directory = new SyntheticDirectory(Png());
        await using var presenter = new PublicAccountPresentation(directory);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await presenter.FetchAsync(0, default)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await presenter.FetchAsync(-1, default)).State);
        Assert.Equal(0, directory.LookupCalls);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PublicDirectoryResult<PublicRobloxUser> User(long id) => new(PublicDirectoryState.Success,
        new(id, "Synthetic_" + id, "Public Display", DateTimeOffset.UtcNow), "Synthetic public profile");
    private sealed class SyntheticDirectory(byte[] bytes) : IPublicRobloxDirectory
    {
        private int _lookupCalls, _descriptorCalls, _encodedCalls, _clearCalls;
        public int LookupCalls => Volatile.Read(ref _lookupCalls);
        public int DescriptorCalls => Volatile.Read(ref _descriptorCalls);
        public int EncodedCalls => Volatile.Read(ref _encodedCalls);
        public int ClearCalls => Volatile.Read(ref _clearCalls);
        public string MediaType = "image/png";
        public Func<long, CancellationToken, Task<PublicDirectoryResult<PublicRobloxUser>>>? Lookup;
        public Func<long, CancellationToken, Task<PublicDirectoryResult<PublicRobloxAvatar>>>? Descriptor;
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id, CancellationToken ct)
        { Interlocked.Increment(ref _lookupCalls); return Lookup?.Invoke(id, ct) ?? Task.FromResult(User(id)); }
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id, CancellationToken ct)
        {
            Interlocked.Increment(ref _descriptorCalls);
            return Descriptor?.Invoke(id, ct) ?? Task.FromResult(new PublicDirectoryResult<PublicRobloxAvatar>(PublicDirectoryState.Success,
                new(id, new Uri("https://tr.rbxcdn.com/synthetic.png"), 150, 150, DateTimeOffset.UtcNow), "Synthetic descriptor"));
        }
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct)
        {
            Interlocked.Increment(ref _encodedCalls);
            return Task.FromResult(new PublicDirectoryResult<EncodedPublicAvatar>(PublicDirectoryState.Success,
                new(bytes, MediaType, DateTimeOffset.UtcNow), "Synthetic bytes"));
        }
        public void ClearMemoryCache() => Interlocked.Increment(ref _clearCalls);
    }
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // Pure managed, synthetic RGBA PNG fixtures. No network, account data, files, WPF windows or capture.
    internal static byte[] Png(int width = 150, bool invalidZlib = false, string? extraChunk = null,
        string? streamFault = null, bool splitIdat = false, int colorType = 6, int bitDepth = 8, bool interlaced = false, int height = 150)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = interlaced ? (byte)1 : (byte)0;
        Chunk(output, "IHDR", header);
        if (colorType == 3) Chunk(output, "PLTE", new byte[] { 10, 20, 30 });
        if (extraChunk is not null) Chunk(output, extraChunk, new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 });
        using var rawOutput = new MemoryStream();
        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        void Pass(int passWidth, int passHeight)
        {
            byte[] row = new byte[(passWidth * channels * bitDepth + 7) / 8];
            if (bitDepth >= 8 && colorType != 3)
                for (int column = 0; column < passWidth; column++)
                    for (int channel = 0; channel < channels; channel++)
                    {
                        byte sample = colorType is 0 or 4 ? channel == 0 ? (byte)10 : (byte)255
                            : channel == 0 ? (byte)10 : channel == 1 ? (byte)20 : channel == 2 ? (byte)30 : (byte)255;
                        int offset = (column * channels + channel) * (bitDepth / 8);
                        row[offset] = sample;
                        if (bitDepth == 16) row[offset + 1] = sample;
                    }
            for (int y = 0; y < passHeight; y++) { rawOutput.WriteByte(0); rawOutput.Write(row); }
        }
        if (interlaced)
        {
            int[] startX = [0, 4, 0, 2, 0, 1, 0], startY = [0, 0, 4, 0, 2, 0, 1];
            int[] stepX = [8, 8, 4, 4, 2, 2, 1], stepY = [8, 8, 8, 4, 4, 2, 2];
            for (int pass = 0; pass < 7; pass++) Pass((width - startX[pass] + stepX[pass] - 1) / stepX[pass], (height - startY[pass] + stepY[pass] - 1) / stepY[pass]);
        }
        else Pass(width, height);
        byte[] raw = rawOutput.ToArray();
        if (streamFault == "short-scanlines") raw = raw[..^1];
        if (streamFault == "extra-scanlines") raw = [.. raw, 0];
        if (streamFault == "illegal-filter") raw[0] = 5;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        byte[] packed = invalidZlib ? new byte[] { 1, 2, 3, 4 } : compressed.ToArray();
        if (streamFault == "bad-adler") packed[^1] ^= 1;
        if (streamFault == "truncated-zlib") packed = packed[..^1];
        if (streamFault == "trailing-zlib") packed = [.. packed, 42, .. packed[^4..]];
        if (streamFault == "second-zlib") packed = [.. packed, .. packed];
        if (splitIdat)
        {
            Chunk(output, "IDAT", []);
            Chunk(output, "IDAT", packed[..1]);
            Chunk(output, "IDAT", packed[1..^1]);
            Chunk(output, "IDAT", packed[^1..]);
            Chunk(output, "IDAT", []);
        }
        else Chunk(output, "IDAT", packed);
        Chunk(output, "IEND", []);
        return output.ToArray();
    }
    private static void Chunk(Stream output, string name, byte[] bytes)
    {
        byte[] type = Encoding.ASCII.GetBytes(name), length = new byte[4], checksum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        output.Write(length);
        output.Write(type);
        output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte value in type.Concat(bytes))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        output.Write(checksum);
    }
}
