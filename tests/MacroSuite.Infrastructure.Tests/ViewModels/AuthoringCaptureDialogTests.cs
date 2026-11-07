using System.Windows;
using System.Windows.Threading;
using MacroSuite.App.Authoring;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;
using DrawingRect = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class AuthoringCaptureDialogTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public Task HoverJoinsOneActualReadAndSkipsIntermediatePointerLocations() => WpfTestApplication.RunAsync(async () =>
    {
        long clock = 0;
        var first = new ScreenPoint(-20, 5);
        var latest = new ScreenPoint(-10, 8);
        var startedFirst = Signal();
        var startedLatest = Signal();
        var releaseFirst = SampleGate();
        var releaseLatest = SampleGate();
        var calls = new List<ScreenPoint>();
        var published = new List<AuthoringPixelSample?>();
        var preview = new ColorHoverPreview((point, _) =>
        {
            calls.Add(point);
            if (calls.Count == 1) { startedFirst.TrySetResult(); return releaseFirst.Task; }
            startedLatest.TrySetResult();
            return releaseLatest.Task;
        }, published.Add, default, () => clock);

        preview.Observe(first);
        try
        {
            await startedFirst.Task.WaitAsync(Deadline);
            preview.Observe(new(-15, 6));
            preview.Observe(latest);
            Assert.Equal(new[] { first }, calls);
            clock = ColorHoverPreview.SampleIntervalMs;
            releaseFirst.SetResult(new(first.X, first.Y, 10, 20, 30));
            await startedLatest.Task.WaitAsync(Deadline);
            Assert.Equal(new[] { first, latest }, calls);
            Assert.Empty(published);
            releaseLatest.SetResult(new(latest.X, latest.Y, 0, 0, 0));
            await preview.Completion.WaitAsync(Deadline);
            Assert.Equal(new AuthoringPixelSample(latest.X, latest.Y, 0, 0, 0), Assert.Single(published));
        }
        finally
        {
            preview.Stop();
            releaseFirst.TrySetResult(default);
            releaseLatest.TrySetResult(default);
            await preview.Completion.WaitAsync(Deadline);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingOrPanicRetainsAnUncooperativeHoverWithoutPublishingIt(bool panic) => WpfTestApplication.RunAsync(async () =>
    {
        var entered = Signal();
        var release = SampleGate();
        using var stop = new CancellationTokenSource();
        int calls = 0, published = 0;
        var preview = new ColorHoverPreview((_, _) =>
        {
            calls++;
            entered.TrySetResult();
            return release.Task;
        }, _ => published++, stop.Token);
        preview.Observe(new(1, 2));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            preview.Observe(new(3, 4));
            if (panic) stop.Cancel(); else preview.Stop();
            Assert.False(preview.Completion.IsCompleted);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult(new(1, 2, 10, 20, 30));
            await preview.Completion.WaitAsync(Deadline);
        }
        preview.Observe(new(8, 9));
        Assert.Equal(1, calls);
        Assert.Equal(0, published);
    });

    [Fact]
    public Task HoverHasNoTimerAndUsesLatestPointWhenTheCadenceAllowsIt() => WpfTestApplication.RunAsync(async () =>
    {
        long clock = 0;
        var calls = new List<ScreenPoint>();
        var preview = new ColorHoverPreview((point, _) =>
        {
            calls.Add(point);
            return Task.FromResult(new AuthoringPixelSample(point.X, point.Y, 1, 2, 3));
        }, _ => { }, default, () => clock);

        preview.Observe(new(10, 20));
        await preview.Completion.WaitAsync(Deadline);
        clock = ColorHoverPreview.SampleIntervalMs - 1;
        preview.Observe(new(30, 40));
        preview.Observe(new(50, 60));
        clock++;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(new[] { new ScreenPoint(10, 20) }, calls);
        preview.Observe(new(50, 60));
        await preview.Completion.WaitAsync(Deadline);
        Assert.Equal(new[] { new ScreenPoint(10, 20), new ScreenPoint(50, 60) }, calls);
        preview.Stop();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WrongPointOrOrdinaryReadFailureRemainsUnavailable(bool throwError) => WpfTestApplication.RunAsync(async () =>
    {
        var published = new List<AuthoringPixelSample?>();
        var preview = new ColorHoverPreview((_, _) => throwError
            ? Task.FromException<AuthoringPixelSample>(new InvalidOperationException("Owned sample unavailable."))
            : Task.FromResult(new AuthoringPixelSample(99, 88, 0, 0, 0)), published.Add, default);

        preview.Observe(new(-9, -8));
        await preview.Completion.WaitAsync(Deadline);
        Assert.Null(Assert.Single(published));
        preview.Stop();
    });

    [Fact]
    public Task HoverSkipsOwnChromeAndClearsOldColorWhenPointerReturnsOverIt() => WpfTestApplication.RunAsync(async () =>
    {
        int calls = 0;
        var published = new List<AuthoringPixelSample?>();
        var point = new ScreenPoint(4, 7);
        var preview = new ColorHoverPreview((p, _) =>
        {
            calls++;
            return Task.FromResult(new AuthoringPixelSample(p.X, p.Y, 2, 3, 4));
        }, published.Add, default);

        preview.Observe(point, canSample: false);
        Assert.Equal(0, calls);
        preview.Observe(point, canSample: true);
        await preview.Completion.WaitAsync(Deadline);
        Assert.Equal(new AuthoringPixelSample(4, 7, 2, 3, 4), Assert.Single(published));
        preview.Observe(point, canSample: false);
        Assert.Null(published[1]);
        Assert.Equal(1, calls);
        preview.Stop();
    });

    [Fact]
    public Task CleanupUncertaintyStopsTheExactHoverAndRemainsObservable() => WpfTestApplication.RunAsync(async () =>
    {
        int closeRequests = 0, publications = 0;
        var preview = new ColorHoverPreview((_, _) => Task.FromException<AuthoringPixelSample>(new HostInputOperationCleanupException()),
            _ => publications++, default, closeOnFailure: () => closeRequests++);
        preview.Observe(new(4, 8));
        Task first = preview.Completion;
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => first.WaitAsync(Deadline));
        preview.Observe(new(10, 12));
        Assert.Same(first, preview.Completion);
        Assert.Equal(1, closeRequests);
        Assert.Equal(0, publications);
    });

    [Fact]
    public void PhysicalPointPreservesNegativeCoordinatesAndRejectsImpossibleValues()
    {
        Assert.True(ColorPickerOverlay.TryGetPhysicalPoint(new Point(-237.4, 21.8), out var point));
        Assert.Equal(new ScreenPoint(-237, 22), point);
        Assert.False(ColorPickerOverlay.TryGetPhysicalPoint(new Point(double.NaN, 1), out _));
        Assert.False(ColorPickerOverlay.TryGetPhysicalPoint(new Point((double)int.MaxValue + 50, 1), out _));
    }

    [Fact]
    public Task PostedCloseCannotReachAReplacementScope() => WpfTestApplication.RunAsync(async () =>
    {
        using var oldStop = new CancellationTokenSource();
        using var newStop = new CancellationTokenSource();
        int oldCloses = 0, newCloses = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;
        using (var old = new AuthoringModalCancellation(dispatcher, () => oldCloses++, oldStop.Token))
        {
            oldStop.Cancel(); // Queues only. Retire this exact scope before the dispatcher pumps.
            Assert.Equal(0, oldCloses);
        }
        using var replacement = new AuthoringModalCancellation(dispatcher, () =>
        {
            Assert.True(dispatcher.CheckAccess());
            newCloses++;
        }, newStop.Token);
        await Task.Run(() => newStop.Cancel());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, oldCloses);
        Assert.Equal(1, newCloses);
    });

    [Fact]
    public Task CancellationCanDeferAnOuterCloseWhileItsNativeChildStillOwnsTheModal()
        => WpfTestApplication.RunAsync(async () =>
        {
            using var stop = new CancellationTokenSource();
            bool nativeChildOpen = true;
            int closes = 0;
            using var scope = new AuthoringModalCancellation(Dispatcher.CurrentDispatcher,
                () => closes++, stop.Token, () => !nativeChildOpen);
            stop.Cancel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, closes);
            nativeChildOpen = false;
            Assert.True(stop.IsCancellationRequested);
            // No new/ambient close is posted. The captured chooser checks the stop
            // token and closes itself only after the native child returns.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, closes);
        });

    [Fact]
    public Task PostedCloseFailureIsReportedAsCleanupUncertainty()
        => WpfTestApplication.RunAsync(async () =>
        {
            using var stop = new CancellationTokenSource();
            var scope = new AuthoringModalCancellation(Dispatcher.CurrentDispatcher,
                () => throw new InvalidOperationException("Owned close failed."), stop.Token);
            stop.Cancel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Throws<HostInputOperationCleanupException>(scope.Dispose);
        });

    [Fact]
    public Task ProductionVisualWaitDialogRestoresEveryDraftFieldAcrossPickPhases()
        => WpfTestApplication.RunAsync(() =>
        {
            var styles = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml")
            };
            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Add(styles);
            try
            {
                var draft = new VisualWaitDraft(2, 1, 2, 97, 2, true, "260", "12.5", 2, "9",
                    new(-8, 15, 0, 0, 0), Region());
                var dialog = new VisualWaitSetupDialog(draft, default);
                try { Assert.Equal(draft, dialog.ReadDraft()); }
                finally { dialog.Close(); }
            }
            finally { merged.Remove(styles); }
            return Task.CompletedTask;
        });

    [Fact]
    public void VisualWaitUsesExactPhysicalRegionAndPreservesTheConfiguredAction()
    {
        var region = Region();
        var draft = new VisualWaitDraft(2, 1, 2, 97, 2, true, "260", "12", 2, "9", RegionSample: region);
        var step = Assert.IsType<MacroSuite.Core.Steps.VisualWaitStep>(VisualWaitSetupDialog.CreateStep(draft));
        Assert.Equal((-450, -30, 4, 5), (step.X, step.Y, step.Width, step.Height));
        Assert.Equal(("Region", "Nearby", "ColorImageAndAi", "MaximumAccuracy", "Retry"),
            (step.Mode, step.SearchMode, step.MatchingMethod, step.Quality, step.OnTimeout));
        Assert.Equal((97, 5, 260, 12000, 9), (step.SimilarityPercent, step.NeighborhoodSize, step.PollIntervalMs, step.TimeoutMs, step.RetryCount));
        Assert.Equal((4, 5), (step.TemplateWidth, step.TemplateHeight));
        Assert.Equal(region.Bgra32, step.TemplateBgra);
    }

    [Fact]
    public void VisualWaitAcceptsSampledBlackAndDoesNotTreatAMissingSampleAsBlack()
    {
        Assert.Null(VisualWaitSetupDialog.CreateStep(new()));
        var draft = new VisualWaitDraft(ModeIndex: 1, PixelSample: new(-900, -100, 0, 0, 0));
        var step = Assert.IsType<MacroSuite.Core.Steps.VisualWaitStep>(VisualWaitSetupDialog.CreateStep(draft));
        Assert.Equal((-900, -100, 1, 1), (step.X, step.Y, step.Width, step.Height));
        Assert.Equal("#000000", step.Hex);
        Assert.Equal(3, step.NeighborhoodSize);
        Assert.Null(step.TemplateBgra);
    }

    [Fact]
    public void VisualWaitRejectsTheWrongSampleKindAndMalformedRegionBytes()
    {
        Assert.Null(VisualWaitSetupDialog.CreateStep(new(ModeIndex: 2, PixelSample: new(4, 5, 1, 2, 3))));
        Assert.Null(VisualWaitSetupDialog.CreateStep(new(RegionSample: Region())));
        Assert.Null(VisualWaitSetupDialog.CreateStep(new(ModeIndex: 2, RegionSample: Region() with { Bgra32 = [] })));
        Assert.Null(VisualWaitSetupDialog.CreateStep(new(ModeIndex: 2, RegionSample: Region() with { Width = 513 })));
        Assert.Null(VisualWaitSetupDialog.CreateStep(new(ModeIndex: 2, RegionSample: Region() with { Region = new(-450, -30, 3, 5) })));
    }

    [Fact]
    public async Task AlreadyStoppedPromptsDoNotOpenAnyWindowOrInvokeASampler()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageSourceDialog.PromptSource(null, stop.Token));
        Assert.Throws<OperationCanceledException>(() => ImageSourceDialog.PromptImportSource(null, .8, stop.Token));
        Assert.Throws<OperationCanceledException>(() => RegionSelectorWindow.PromptSelection(null, stop.Token));
        Assert.Throws<OperationCanceledException>(() => ImageSearchAreaPlacementWindow.Prompt(null, new DrawingSize(4, 4), stop.Token));
        Assert.Throws<OperationCanceledException>(() => VisualWaitSetupDialog.Prompt(null, new(), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ColorPickerOverlay.PromptPointAsync(null,
            (_, _) => throw new InvalidOperationException("A stopped prompt must never sample."), stop.Token));
    }

    [Fact]
    public void RegionSelectionRejectsLocalCoordinatesIfEitherScreenTransformFails()
    {
        var start = new Point(10, 20);
        var end = new Point(90, 100);
        Assert.False(RegionSelectorWindow.TryGetPhysicalSelection(start, end,
            _ => throw new InvalidOperationException("Presentation source unavailable."), out var missing));
        Assert.Equal(DrawingRect.Empty, missing);
        Assert.False(RegionSelectorWindow.TryGetPhysicalSelection(start, end, point => point == start
            ? new Point(-1600, 50) : throw new InvalidOperationException("Presentation source removed."), out var partial));
        Assert.Equal(DrawingRect.Empty, partial);
    }

    [Fact]
    public void RegionSelectionUsesBothPhysicalCornersOnANegativeOriginDisplay()
    {
        Assert.True(RegionSelectorWindow.TryGetPhysicalSelection(new(16, 8), new(4, 2),
            point => new(-2000 + point.X * 2, -150 + point.Y * 1.5), out var region));
        Assert.Equal(new DrawingRect(-1992, -147, 24, 9), region);
    }

    [Fact]
    public void PlacementCannotMaskUncertainCleanupWithAnEarlierSetupFailure()
    {
        var setup = new InvalidOperationException("Owned placement setup failed.");
        var later = new ArgumentException("Owned modal failed.");
        var cleanup = new HostInputOperationCleanupException();
        Assert.Same(cleanup, ImageSearchAreaPlacementWindow.PreferPlacementFailure(setup, cleanup));
        Assert.Same(cleanup, ImageSearchAreaPlacementWindow.PreferPlacementFailure(cleanup, later));
        Assert.Same(setup, ImageSearchAreaPlacementWindow.PreferPlacementFailure(setup, later));
    }

    private static AuthoringVisualSample Region()
        => new(new DrawingRect(-450, -30, 4, 5), 4, 5, new byte[4 * 5 * 4], 0, 0, 0);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<AuthoringPixelSample> SampleGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
