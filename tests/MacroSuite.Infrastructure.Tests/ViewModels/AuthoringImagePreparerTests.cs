using System.Buffers;
using System.Drawing;
using System.IO;
using MacroSuite.App.Authoring;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class AuthoringImagePreparerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly Rectangle TemplateRegion = new(-20, -13, 4, 6);

    [Fact]
    public async Task TemplateUsesExactPhysicalRegionAndOwnsTheValidatedPngBytes()
    {
        await using var operation = new Operation();
        var owner = new MemoryOwner(TemplateRegion.Height * 20);
        var frame = Frame(TemplateRegion, FramePixelFormat.Rgba32, 20, owner);
        var capture = new Capture(_ => Task.FromResult(frame));
        var png = EncodeFixture(".png", TemplateRegion.Width, TemplateRegion.Height);
        var encoderBytes = (byte[])png.Clone();
        var encoder = new Encoder(actual =>
        {
            Assert.Same(frame, actual);
            Assert.False(actual.IsDisposed);
            return encoderBytes;
        });
        var preparer = new AuthoringImagePreparer(capture, encoder);

        var result = await preparer.CaptureTemplateAsync(operation.Lease, TemplateRegion, .81, true).WaitAsync(Deadline);
        encoderBytes[0] = 0;
        Assert.Equal("Captured image", result.ImagePath);
        Assert.Equal(.81, result.Confidence);
        Assert.True(result.AutoClick);
        Assert.Equal((Point?)new Point(-18, -10), result.InitialCenter);
        Assert.Equal((Rectangle?)TemplateRegion, result.SourceRegion);
        Assert.Equal((Size?)TemplateRegion.Size, result.TemplateSize);
        Assert.Equal(png, result.TemplateBytes);
        Assert.Equal(TemplateRegion, capture.LastRegion);
        Assert.Equal(CancellationToken.None, capture.LastToken);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.True(frame.IsDisposed);
        Assert.True(operation.Lease.CanContinue);
        Assert.False(capture.IsDisposed);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 4097)]
    [InlineData(int.MaxValue - 2, 4)]
    public async Task InvalidTemplateRegionFailsBeforeCapture(int x, int width)
    {
        await using var operation = new Operation();
        var capture = new Capture();
        var preparer = new AuthoringImagePreparer(capture, new Encoder());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await preparer.CaptureTemplateAsync(operation.Lease, new Rectangle(x, 0, width, 4), .8, false));
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public async Task OversizedVisualSampleFailsBeforeCapture()
    {
        await using var operation = new Operation();
        var capture = new Capture();
        var preparer = new AuthoringImagePreparer(capture, new Encoder());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await preparer.CaptureVisualAsync(operation.Lease, new Rectangle(0, 0, 512, 513)));
        Assert.Equal(0, capture.Calls);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("clipped")]
    [InlineData("stride")]
    [InlineData("format")]
    [InlineData("disposed")]
    public async Task UnusableFrameIsDisposedBeforeEncoderCanRun(string reason)
    {
        await using var operation = new Operation();
        Rectangle actual = reason switch
        {
            "origin" => new Rectangle(TemplateRegion.X + 1, TemplateRegion.Y, TemplateRegion.Width, TemplateRegion.Height),
            "clipped" => new Rectangle(TemplateRegion.X, TemplateRegion.Y, TemplateRegion.Width - 1, TemplateRegion.Height),
            _ => TemplateRegion
        };
        int stride = reason == "stride" ? actual.Width * 4 - 1 : actual.Width * 4;
        var memory = new MemoryOwner(actual.Height * stride);
        var frame = Frame(actual, reason == "format" ? (FramePixelFormat)255 : FramePixelFormat.Bgra32, stride, memory);
        if (reason == "disposed") frame.Dispose();
        var encoder = new Encoder();
        var preparer = new AuthoringImagePreparer(new Capture(_ => Task.FromResult(frame)), encoder);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            preparer.CaptureTemplateAsync(operation.Lease, TemplateRegion, .8, false).WaitAsync(Deadline));
        Assert.Equal(0, encoder.Calls);
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Theory]
    [InlineData(FramePixelFormat.Bgra32, false)]
    [InlineData(FramePixelFormat.Bgr24, false)]
    [InlineData(FramePixelFormat.Rgba32, false)]
    [InlineData(FramePixelFormat.Bgra32, true)]
    public async Task PixelSampleReadsOnlyTheExactPointAndPreservesChannelOrder(FramePixelFormat format, bool black)
    {
        await using var operation = new Operation(HostInputOperationKind.CaptureTest);
        var region = new Rectangle(-107, -26, 1, 1);
        var memory = new MemoryOwner(8);
        if (!black) WritePixel(memory.Bytes, 0, format, r: 51, g: 33, b: 17, a: 91);
        var capture = new Capture(_ => Task.FromResult(Frame(region, format, 8, memory)));
        var encoder = new Encoder();
        var preparer = new AuthoringImagePreparer(capture, encoder);

        var sample = await preparer.SamplePixelAsync(operation.Lease, new ScreenPoint(region.X, region.Y)).WaitAsync(Deadline);
        Assert.Equal(new AuthoringPixelSample(-107, -26, black ? (byte)0 : (byte)51,
            black ? (byte)0 : (byte)33, black ? (byte)0 : (byte)17), sample);
        Assert.Equal(region, capture.LastRegion);
        Assert.Equal(0, encoder.Calls);
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Theory]
    [InlineData(FramePixelFormat.Bgra32)]
    [InlineData(FramePixelFormat.Bgr24)]
    [InlineData(FramePixelFormat.Rgba32)]
    public async Task VisualSampleDetachesPackedRowsAndReadsItsActualCenter(FramePixelFormat format)
    {
        await using var operation = new Operation();
        var region = new Rectangle(-11, 14, 2, 2);
        const int stride = 12;
        var memory = new MemoryOwner(stride * region.Height);
        Array.Fill(memory.Bytes, (byte)200);
        int pixelBytes = CaptureFrame.GetBytesPerPixel(format);
        WritePixel(memory.Bytes, 0, format, 1, 2, 3, 4);
        WritePixel(memory.Bytes, pixelBytes, format, 5, 6, 7, 8);
        WritePixel(memory.Bytes, stride, format, 9, 10, 11, 12);
        WritePixel(memory.Bytes, stride + pixelBytes, format, 13, 14, 15, 16);
        var preparer = new AuthoringImagePreparer(new Capture(_ => Task.FromResult(Frame(region, format, stride, memory))), new Encoder());

        var sample = await preparer.CaptureVisualAsync(operation.Lease, region).WaitAsync(Deadline);
        byte[] expected = [3, 2, 1, 4, 7, 6, 5, 8, 11, 10, 9, 12, 15, 14, 13, 16];
        if (format == FramePixelFormat.Bgr24)
            for (int i = 3; i < expected.Length; i += 4) expected[i] = 255;

        Assert.Equal(region, sample.Region);
        Assert.Equal((2, 2), (sample.Width, sample.Height));
        Assert.Equal(expected, sample.Bgra32);
        Assert.Equal(((byte)13, (byte)14, (byte)15), (sample.R, sample.G, sample.B));
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCaptureIsJoinedAndLateFrameDisposedBeforeCancellationCompletes(bool callerCancelled)
    {
        await using var operation = new Operation();
        using var caller = new CancellationTokenSource();
        var returned = new TaskCompletionSource<CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new MemoryOwner(TemplateRegion.Width * TemplateRegion.Height * 4);
        var frame = Frame(TemplateRegion, FramePixelFormat.Bgra32, TemplateRegion.Width * 4, memory);
        var capture = new Capture(region => region == TemplateRegion
            ? returned.Task : throw new InvalidDataException("A second request entered the fixture capture."));
        var encoder = new Encoder();
        var preparer = new AuthoringImagePreparer(capture, encoder);
        var actual = preparer.CaptureTemplateAsync(operation.Lease, TemplateRegion, .8, false, caller.Token);
        try
        {
            await capture.Entered.Task.WaitAsync(Deadline);
            if (callerCancelled) caller.Cancel();
            else operation.Arbiter.SignalStop();
            Assert.Equal(CancellationToken.None, capture.LastToken);
            Assert.False(actual.IsCompleted);
            Assert.Equal(0, memory.DisposeCalls);
            if (callerCancelled)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await preparer.SamplePixelAsync(operation.Lease, new ScreenPoint(1, 1)).WaitAsync(Deadline));
                Assert.Equal(1, capture.Calls);
            }
            returned.TrySetResult(frame);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actual.WaitAsync(Deadline));
        }
        finally
        {
            returned.TrySetResult(frame);
            await JoinCancellationAsync(actual);
        }
        Assert.Equal(1, memory.DisposeCalls);
        Assert.Equal(0, encoder.Calls);
        Assert.False(capture.IsDisposed);
    }

    [Fact]
    public async Task StopDuringAnAcceptedEncodeJoinsItAndSkipsDecode()
    {
        await using var operation = new Operation();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new MemoryOwner(TemplateRegion.Width * TemplateRegion.Height * 4);
        var frame = Frame(TemplateRegion, FramePixelFormat.Bgra32, TemplateRegion.Width * 4, memory);
        var encoder = new Encoder(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("The fixture encoder gate was not opened.");
            return "deliberately invalid image"u8.ToArray();
        });
        var preparer = new AuthoringImagePreparer(new Capture(_ => Task.FromResult(frame)), encoder);
        var actual = preparer.CaptureTemplateAsync(operation.Lease, TemplateRegion, .8, false);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            operation.Lease.SignalStop();
            Assert.False(actual.IsCompleted);
            Assert.False(frame.IsDisposed);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actual.WaitAsync(Deadline));
        }
        finally
        {
            release.Set();
            await JoinCancellationAsync(actual);
        }
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalPixelPublicationChecksStopAfterActualFrameDisposal(bool disposeFails)
    {
        await using var operation = new Operation();
        var memory = new MemoryOwner(4)
        {
            OnDispose = () =>
            {
                operation.Lease.SignalStop();
                if (disposeFails) throw new InvalidOperationException("Fixture frame disposal failed.");
            }
        };
        var capture = new Capture(region => Task.FromResult(Frame(region, FramePixelFormat.Bgra32, 4, memory)));
        var preparer = new AuthoringImagePreparer(capture, new Encoder());
        var actual = preparer.SamplePixelAsync(operation.Lease, new ScreenPoint(-5, 19));

        if (disposeFails)
            await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => actual.WaitAsync(Deadline));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actual.WaitAsync(Deadline));
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("wrong size")]
    [InlineData("oversized")]
    public async Task EncoderOutputMustBeBoundedPngAtTheRequestedGeometry(string kind)
    {
        await using var operation = new Operation();
        var memory = new MemoryOwner(TemplateRegion.Width * TemplateRegion.Height * 4);
        var frame = Frame(TemplateRegion, FramePixelFormat.Bgra32, TemplateRegion.Width * 4, memory);
        var encoder = new Encoder(_ => kind switch
        {
            "jpeg" => EncodeFixture(".jpg", TemplateRegion.Width, TemplateRegion.Height),
            "wrong size" => EncodeFixture(".png", TemplateRegion.Width + 1, TemplateRegion.Height),
            _ => GC.AllocateUninitializedArray<byte>(BoundedTemplateDecoder.MaxEncodedBytes + 1)
        });
        var preparer = new AuthoringImagePreparer(new Capture(_ => Task.FromResult(frame)), encoder);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            preparer.CaptureTemplateAsync(operation.Lease, TemplateRegion, .8, false).WaitAsync(Deadline));
        Assert.Equal(1, encoder.Calls);
        Assert.Equal(1, memory.DisposeCalls);
    }

    [Fact]
    public async Task ImportPreservesOnePixelEdgesAndKeepsBytesAfterTheChosenFileIsGone()
    {
        await using var operation = new Operation();
        string path = FixturePath();
        byte[] png = EncodeFixture(".png", 1, 2);
        var capture = new Capture();
        var encoder = new Encoder();
        var preparer = new AuthoringImagePreparer(capture, encoder);
        try
        {
            await File.WriteAllBytesAsync(path, png);
            var result = await preparer.ImportTemplateAsync(operation.Lease, path, .72, true).WaitAsync(Deadline);
            File.Delete(path);
            Assert.Equal(path, result.ImagePath);
            Assert.Equal(.72, result.Confidence);
            Assert.True(result.AutoClick);
            Assert.Equal((Size?)new Size(1, 2), result.TemplateSize);
            Assert.Equal(png, result.TemplateBytes);
            Assert.Null(result.SourceRegion);
            Assert.Null(result.InitialCenter);
            Assert.Equal(0, capture.Calls);
            Assert.Equal(0, encoder.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportRejectsOversizedFileBeforeDecodingAndKeepsItsLength()
    {
        await using var operation = new Operation();
        string path = FixturePath();
        long length = BoundedTemplateDecoder.MaxEncodedBytes + 1L;
        var preparer = new AuthoringImagePreparer(new Capture(), new Encoder());
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(length);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                preparer.ImportTemplateAsync(operation.Lease, path, .8, false).WaitAsync(Deadline));
            Assert.Contains("32 MB", error.Message);
            Assert.Equal(length, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(HostInputOperationKind.Playback)]
    [InlineData(HostInputOperationKind.Recording)]
    public async Task ProducerLeasesCannotBorrowAuthoringCapture(HostInputOperationKind kind)
    {
        await using var operation = new Operation(kind);
        var capture = new Capture();
        var preparer = new AuthoringImagePreparer(capture, new Encoder());
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await preparer.SamplePixelAsync(operation.Lease, new ScreenPoint(0, 0)));
        Assert.Equal(0, capture.Calls);
        Assert.True(operation.Lease.CanContinue);
    }

    [Fact]
    public async Task RetiredLeaseCannotCaptureUsingAReplacementWithTheSameStopGeneration()
    {
        await using var operation = new Operation();
        await operation.Lease.CompleteAsync(true);
        Assert.True(operation.Arbiter.TryAcquire(HostInputOperationKind.Picker, out var replacement, out _));
        var capture = new Capture();
        var preparer = new AuthoringImagePreparer(capture, new Encoder());
        try
        {
            Assert.Equal(operation.Lease.Generation, replacement!.Generation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await preparer.SamplePixelAsync(operation.Lease, new ScreenPoint(2, 1)));
            Assert.Equal(0, capture.Calls);
            Assert.True(replacement.CanContinue);
        }
        finally { await replacement!.CompleteAsync(true); }
    }

    private static CaptureFrame Frame(Rectangle region, FramePixelFormat format, int stride, MemoryOwner memory)
        => new(region.Width, region.Height, stride, format, CaptureSource.Gdi, memory, originX: region.X, originY: region.Y);

    private static byte[] EncodeFixture(string extension, int width, int height)
    {
        using var image = new OpenCvSharp.Mat(height, width, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(22, 51, 119));
        Assert.True(OpenCvSharp.Cv2.ImEncode(extension, image, out byte[] bytes));
        return bytes;
    }

    private static void WritePixel(byte[] bytes, int offset, FramePixelFormat format, byte r, byte g, byte b, byte a)
    {
        bytes[offset] = format == FramePixelFormat.Rgba32 ? r : b;
        bytes[offset + 1] = g;
        bytes[offset + 2] = format == FramePixelFormat.Rgba32 ? b : r;
        if (format != FramePixelFormat.Bgr24) bytes[offset + 3] = a;
    }

    private static string FixturePath() => Path.Combine(Path.GetTempPath(), $"ZenithAuthoring-{Guid.NewGuid():N}.png");

    private static async Task JoinCancellationAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { }
    }

    private sealed class Operation : IAsyncDisposable
    {
        public HostInputOperationArbiter Arbiter { get; } = new();
        public HostInputOperationLease Lease { get; }

        public Operation(HostInputOperationKind kind = HostInputOperationKind.Picker)
        {
            Assert.True(Arbiter.TryAcquire(kind, out var lease, out _));
            Lease = lease!;
        }

        public ValueTask DisposeAsync() => new(Lease.CompleteAsync(true));
    }

    private sealed class MemoryOwner(int length) : IMemoryOwner<byte>
    {
        public byte[] Bytes { get; } = new byte[length];
        public Memory<byte> Memory => Bytes;
        public int DisposeCalls { get; private set; }
        public Action? OnDispose { get; init; }

        public void Dispose()
        {
            DisposeCalls++;
            Array.Fill(Bytes, (byte)211);
            OnDispose?.Invoke();
        }
    }

    private sealed class Capture(Func<Rectangle, Task<CaptureFrame>>? capture = null) : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public bool IsDisposed { get; private set; }
        public int Calls { get; private set; }
        public Rectangle LastRegion { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            Calls++;
            LastRegion = region;
            LastToken = ct;
            Entered.TrySetResult();
            return capture?.Invoke(region) ?? throw new InvalidOperationException("Unexpected fixture capture.");
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Authoring should request only the selected region.");

        public void Dispose() => IsDisposed = true;
    }

    private sealed class Encoder(Func<CaptureFrame, byte[]>? encode = null) : IImageEncoder
    {
        public int Calls { get; private set; }

        public byte[] EncodePng(CaptureFrame frame)
        {
            Calls++;
            return encode?.Invoke(frame) ?? throw new InvalidOperationException("Unexpected fixture encode.");
        }

        public byte[] EncodeJpeg(CaptureFrame frame, int quality = 80)
            => throw new InvalidOperationException("Authoring should encode PNG.");
    }
}
