using System.Buffers;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Vision;
using MacroSuite.Core.Capture;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public sealed class ImageScanSessionTests
{
    [Fact]
    public async Task InterleavedRequestsShareOneCapturePerSurface_AndAllGroupsGetResults()
    {
        using var capture = new SceneCapture(120, 80);
        using var template = Pattern(9, 7);
        capture.Put(template, 20, 20);
        using var session = NewSession(capture);
        var first = Request(template, ImageScanResolution.Original);
        var middle = Request(template, ImageScanResolution.Original) with { Region = new Rectangle(10, 10, 80, 60) };
        var third = Request(template, ImageScanResolution.Original);
        bool completeBatch = false;
        for (int attempt = 0; attempt < 15; attempt++)
        {
            capture.ScreenCaptures = 0; capture.Regions.Clear();
            var results = await session.ScanAsync([first, middle, third], default);
            if (results.All(result => result.Status == ImageScanStatus.Complete))
            {
                Assert.Equal(1, capture.ScreenCaptures);
                Assert.Single(capture.Regions);
                Assert.All(results, result => Assert.Single(result.Hits));
                completeBatch = true;
                break;
            }
            await Task.Delay(11);
        }
        Assert.True(completeBatch);
    }

    [Fact]
    public async Task MeasuredCadenceReportsActualScanStarts_SeparatelyFromRequestedMinimum()
    {
        using var capture = new SceneCapture(120, 80);
        using var template = Pattern(9, 7);
        using var session = NewSession(capture);
        var request = Request(template, ImageScanResolution.Original);
        await session.ScanAsync([request], default);
        await Task.Delay(70);
        await session.ScanAsync([request], default);
        Assert.Equal(10, session.Performance.EffectiveIntervalMs);
        Assert.Equal(request.Id, session.Performance.MeasuredActionId);
        Assert.Equal(10, session.Performance.MeasuredRequestedIntervalMs);
        Assert.True(session.Performance.MeasuredIntervalMs >= 50);
    }

    [Fact]
    public async Task NativeOnlySharedSourceStillSupportsScaledScanningWithoutChangingPixelFrames()
    {
        var source = new SceneCapture(1920, 1080);
        using var template = Pattern(43, 31);
        source.Put(template, 100, 100);
        using var shared = new SharedCaptureService(source);
        using var session = new OpenCvImageScanSession(shared, new ImageScanBudget { Enabled = false });
        var request = Request(template, ImageScanResolution.P720);
        var result = (await session.ScanAsync([request], default))[0];
        Assert.NotEqual(ImageScanStatus.Failed, result.Status);
        Assert.Equal(new Size(1280, 720), result.SampledSize);
        using var native = await shared.CaptureScreenAsync();
        Assert.Equal(1920, native.Width); Assert.Equal(1080, native.Height);
        using var pixel = await shared.CaptureRegionAsync(new Rectangle(100, 100, 1, 1));
        Assert.Equal(1, pixel.Width); Assert.Equal(1, pixel.Height);
        Assert.Equal(template.At<Vec4b>(0, 0).Item0, pixel.Memory.Span[0]);
    }

    [Fact]
    public async Task PreparedTemplateBudget_RejectsExcessAndReclaimsUnsubscribedImages()
    {
        using var capture = new SceneCapture(64, 64); using var template = Pattern(32, 32);
        using var session = new OpenCvImageScanSession(capture, new ImageScanBudget { Enabled = false }, 1600);
        var first = Request(template, ImageScanResolution.Original); var second = Request(template, ImageScanResolution.Original);
        ImageScanResult? rejected = null;
        for (int i = 0; i < 10; i++)
        {
            var results = await session.ScanAsync([first, second], default);
            rejected = results.FirstOrDefault(r => r.Status == ImageScanStatus.Failed);
            if (rejected is not null) break;
            await Task.Delay(11);
        }
        Assert.NotNull(rejected); Assert.Contains("budget", rejected.ErrorMessage!);
        Assert.Equal(1024, session.PreparedImageBytes);
        var remaining = rejected.Id == first.Id ? first : second;
        var recovered = (await session.ScanAsync([remaining], default))[0];
        Assert.NotEqual(ImageScanStatus.Failed, recovered.Status); Assert.Equal(1024, session.PreparedImageBytes);
    }

    [Fact]
    public async Task FourKOverviewMapsNegativeCoordinatesAndFreshCropRecoversOddAlignment()
    {
        using var capture = new SceneCapture(3840,2160,-3840,-150);
        using var template = Pattern(43,29);
        capture.Put(template,3301,1903);
        using var session = NewSession(capture);
        var request = Request(template,ImageScanResolution.P720);
        var observed = new List<ImageScanHit>();
        ImageScanResult? result = null;
        for(int i=0;i<40;i++)
        {
            result=(await session.ScanAsync(new[]{request},default))[0];
            foreach(var candidate in result.Hits)
            {
                var fresh=await session.RevalidateAsync(request,candidate,default);
                if(fresh is not null) observed.Add(fresh);
            }
            if(observed.Count>0)break;
            await Task.Delay(11);
        }
        Assert.NotNull(result); Assert.Equal(new Size(1280,720),result.SampledSize);
        Assert.Contains(observed,hit=>hit.Bounds.X==-539 && hit.Bounds.Y==1753);
        Assert.All(capture.Regions,area=>Assert.True(area.Width<3840 && area.Height<2160));
    }

    [Fact]
    public async Task NativeMultipleOccurrencesAndDisappearanceAreRevalidated()
    {
        using var capture=new SceneCapture(640,360,100,-200);
        using var template=Pattern(31,21);
        capture.Put(template,5,7);capture.Put(template,400,300);
        using var session=NewSession(capture);
        var request=Request(template,ImageScanResolution.Original);
        var result=(await session.ScanAsync(new[]{request},default))[0];
        Assert.Equal(ImageScanStatus.Complete,result.Status); Assert.Equal(2,result.Hits.Count);
        var hit=result.Hits.Single(h=>h.Bounds.X==105);
        Assert.NotNull(await session.RevalidateAsync(request,hit,default));
        capture.Clear();
        Assert.Null(await session.RevalidateAsync(request,hit,default));
    }

    [Fact]
    public async Task SmallTemplateMissRemainsPartialUntilNativeTilesCoverEveryPosition()
    {
        using var capture=new SceneCapture(1920,1080);
        using var template=Pattern(7,5);
        capture.Put(template,1913,1075);
        capture.Put(template,511,511); // Crosses both native-tile seams.
        using var session=NewSession(capture);
        var request=Request(template,ImageScanResolution.P480);
        long generation=0; bool found=false,completed=false; int seamObservations=0;
        for(int i=0;i<25;i++)
        {
            var result=(await session.ScanAsync(new[]{request},default))[0];
            if(result.Status==ImageScanStatus.Deferred){await Task.Delay(11);continue;}
            if(generation==0)generation=result.Generation;
            Assert.Equal(generation,result.Generation);
            if(i==0)Assert.Equal(ImageScanStatus.Partial,result.Status);
            foreach(var hit in result.Hits)
            {
                if(hit.Bounds.X==1913&&hit.Bounds.Y==1075)found=true;
                if(hit.Bounds.X==511&&hit.Bounds.Y==511)seamObservations++;
            }
            if(result.Status==ImageScanStatus.Complete){completed=true;break;}
            await Task.Delay(11);
        }
        Assert.True(found);Assert.True(completed);Assert.Equal(1,seamObservations);
    }

    [Fact]
    public async Task DenseNativeMatchesPaginateWithoutStarvingLaterLocations()
    {
        using var capture=new SceneCapture(900,300);
        using var template=Pattern(17,13);
        for(int i=0;i<70;i++)capture.Put(template,5+(i%30)*29,5+(i/30)*70);
        using var session=NewSession(capture);
        var request=Request(template,ImageScanResolution.Original);
        var positions=new HashSet<System.Drawing.Point>();long generation=0;
        for(int i=0;i<12;i++)
        {
            var result=(await session.ScanAsync(new[]{request},default))[0];
            if(result.Status==ImageScanStatus.Deferred){await Task.Delay(11);continue;}
            Assert.InRange(result.Hits.Count,0,32);
            if(generation==0)generation=result.Generation;Assert.Equal(generation,result.Generation);
            foreach(var hit in result.Hits)positions.Add(hit.Bounds.Location);
            if(result.Status==ImageScanStatus.Complete)break;
            await Task.Delay(11);
        }
        Assert.Equal(70,positions.Count);
    }

    [Fact]
    public async Task BadTemplateIsFailedAndCancellationDoesNotBecomeNotFound()
    {
        using var capture=new SceneCapture(200,100);
        using var session=NewSession(capture);
        var request=new ImageScanRequest(Guid.NewGuid(),null,new byte[]{1,2,3},.9,null,ImageScanResolution.P720);
        Assert.Equal(ImageScanStatus.Failed,(await session.ScanAsync(new[]{request},default))[0].Status);
        using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ScanAsync(new[]{request},cts.Token));
    }

    [Fact]
    public async Task CancellationAfterBatchStartsReturnsExplicitCancelledForUnfinishedRequests()
    {
        using var capture = new SceneCapture(120, 80);
        using var template = Pattern(9, 7);
        using var session = NewSession(capture);
        using var cancellation = new CancellationTokenSource();
        capture.BeforeScreenCapture = () => cancellation.Cancel();
        var request = Request(template, ImageScanResolution.Original);
        var result = Assert.Single(await session.ScanAsync([request], cancellation.Token));
        Assert.Equal(ImageScanStatus.Cancelled, result.Status);
        Assert.Empty(result.Hits);
        capture.BeforeScreenCapture = null;
        await Task.Delay(11);
        Assert.Equal(ImageScanStatus.Complete, Assert.Single(await session.ScanAsync([request], default)).Status);
    }

    private static OpenCvImageScanSession NewSession(SceneCapture capture)=>new(capture,new ImageScanBudget{Enabled=false});
    private static ImageScanRequest Request(Mat template,ImageScanResolution resolution)
    { Cv2.ImEncode(".png",template,out byte[] bytes);return new(Guid.NewGuid(),null,bytes,.95,null,resolution,true,10); }
    private static Mat Pattern(int width,int height)
    {
        var mat=new Mat(height,width,MatType.CV_8UC4);
        var random=new Random(17);
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)mat.Set(y,x,new Vec4b((byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256),255));
        return mat;
    }
    private sealed class SceneCapture : IScreenCaptureService
    {
        private readonly Mat _scene;
        private readonly Rectangle _bounds;
        public List<Rectangle> Regions{get;}=new();
        public int ScreenCaptures;
        public Action? BeforeScreenCapture;
        public CaptureSource CurrentSource=>CaptureSource.Gdi;
        public bool IsSupported=>true;
        public SceneCapture(int width,int height,int x=0,int y=0)
        {_scene=new Mat(height,width,MatType.CV_8UC4,new Scalar(32,32,32,255));_bounds=new Rectangle(x,y,width,height);}
        public void Put(Mat template,int x,int y){using var roi=new Mat(_scene,new Rect(x,y,template.Width,template.Height));template.CopyTo(roi);}
        public void Clear()=>_scene.SetTo(new Scalar(32,32,32,255));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct=default)
        { BeforeScreenCapture?.Invoke(); ScreenCaptures++; return Task.FromResult(Frame(_bounds)); }
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle area,CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();Regions.Add(area);return Task.FromResult(Frame(Rectangle.Intersect(area,_bounds)));}
        private unsafe CaptureFrame Frame(Rectangle bounds)
        {
            int stride=bounds.Width*4;
            var owner=MemoryPool<byte>.Shared.Rent(stride*bounds.Height);
            using var roi=new Mat(_scene,new Rect(bounds.X-_bounds.X,bounds.Y-_bounds.Y,bounds.Width,bounds.Height));
            fixed(byte* ptr=owner.Memory.Span)
            {using var destination=new Mat(bounds.Height,bounds.Width,MatType.CV_8UC4,(IntPtr)ptr,stride);roi.CopyTo(destination);}
            return new CaptureFrame(bounds.Width,bounds.Height,stride,FramePixelFormat.Bgra32,CaptureSource.Gdi,owner,originX:bounds.X,originY:bounds.Y);
        }
        public void Dispose()=>_scene.Dispose();
    }
}
