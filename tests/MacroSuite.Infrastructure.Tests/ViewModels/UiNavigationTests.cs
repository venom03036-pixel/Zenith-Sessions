using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.App.Common;
using MacroSuite.App.ViewModels;
using MacroSuite.App.Views;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public Task UiNavigation_RetainsProductionEditorScrollSelectionAndDraft(int actions)
        => WpfTestApplication.RunAsync(async () =>
        {
            var bodyCpu = ThreadCpuSample.Take();
            using var folder = new RoutingTempFolder();
            using var vm = CreateRecordingViewModel(new TestInputRecorder(), directory: folder.Library,
                documents: new RecordingDocumentSpy(folder.Library));
            PopulateNavigationMacro(vm, actions);
            using var fixture = new NavigationLayoutFixture();
            var retained = new RetainedViewHost(); fixture.Attach(retained);
            retained.ViewModel = vm; await fixture.SettleAsync();
            var view = FindNavigationVisual<MacroEditorView>(retained)!;
            view.Should().NotBeNull(); view.IsVisible.Should().BeTrue();
            var list = NavigationVisuals<ListBox>(view).Single(x => x.Name == "ActionSequenceList");
            var scroll = FindNavigationVisual<ScrollViewer>(list)!;
            scroll.ScrollableHeight.Should().BeGreaterThan(1_000);
            vm.SelectedStep = vm.Steps[0]; vm.ActionEditor.X = 777;
            vm.ActionEditor.HasDraftChanges.Should().BeTrue();
            await fixture.SettleAsync();
            await DragNavigationThumbAsync(fixture, list, scroll, .82);
            double expected = scroll.VerticalOffset;
            expected.Should().BeGreaterThan(scroll.ScrollableHeight * .70);
            var selected = vm.SelectedStep; var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++)
            {
                retained.ViewModel = fixture.OtherPage; await fixture.SettleAsync();
                retained.ViewModel = vm; await fixture.SettleAsync();
                FindNavigationVisual<MacroEditorView>(retained).Should().BeSameAs(view);
            }
            scroll.VerticalOffset.Should().BeApproximately(expected, 2, "returning to the editor must not jump to the off-screen selected row");
            vm.SelectedStep.Should().BeSameAs(selected); vm.ActionEditor.X.Should().Be(777);
            vm.ActionEditor.HasDraftChanges.Should().BeTrue();
            ((MouseMoveStep)vm.Steps[0].Step).Position.X.Should().Be(0, "the retained draft remains unapplied");
            int realized = CountNavigationVisuals<ListBoxItem>(list);
            realized.Should().BeInRange(1, 180, "the real production action list must remain virtualized");
            Console.WriteLine($"UiNavigation retained: {actions} actions, 60 tab transitions {elapsed.ElapsedMilliseconds} ms; offset {expected:F1}->{scroll.VerticalOffset:F1}; {realized} realized rows; native-hidden WPF layout only.");

            // Exercise the former shell host with the same production template. A
            // content swap must actually reconstruct its visual, proving that this
            // scenario distinguishes retained navigation from the previous behavior.
            view.DataContext = null;
            var baseline = new ContentControl(); fixture.Attach(baseline);
            baseline.Content = vm; await fixture.SettleAsync();
            var before = FindNavigationVisual<MacroEditorView>(baseline)!;
            baseline.Content = fixture.OtherPage; await fixture.SettleAsync();
            baseline.Content = vm; await fixture.SettleAsync();
            FindNavigationVisual<MacroEditorView>(baseline).Should().NotBeSameAs(before);
            FindNavigationVisual<MacroEditorView>(baseline)!.DataContext = null;
            var body = ThreadCpuBudget.Check($"UiNavigation retained body ({actions} actions)", NavigationBodyCpuBudget, bodyCpu);
            Console.WriteLine($"UiNavigation retained CPU: body {body}; slowest layout pass {fixture.SlowestPass}.");
        });

    [Theory]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public Task UiNavigation_ThumbWinsOverPendingFollowAndExplicitJumpStillWorks(int actions)
        => WpfTestApplication.RunAsync(async () =>
        {
            var bodyCpu = ThreadCpuSample.Take();
            using var folder = new RoutingTempFolder();
            using var vm = CreateRecordingViewModel(new TestInputRecorder(), directory: folder.Library,
                documents: new RecordingDocumentSpy(folder.Library));
            PopulateNavigationMacro(vm, actions);
            using var fixture = new NavigationLayoutFixture();
            var view = new MacroEditorView { DataContext = vm }; fixture.Attach(view);
            await fixture.SettleAsync();
            var list = NavigationVisuals<ListBox>(view).Single(x => x.Name == "ActionSequenceList");
            var scroll = FindNavigationVisual<ScrollViewer>(list)!;
            vm.SelectedStep = vm.Steps[0]; await fixture.SettleAsync();
            for (int i = 0; i < 20; i++)
            {
                // A public navigation request uses the same deferred scroll queue as
                // recording/playback follow. The thumb starts before that queue runs.
                vm.JumpToBottom();
                await DragNavigationThumbAsync(fixture, list, scroll, i % 2 == 0 ? .15 : .82);
            }
            double manual = scroll.VerticalOffset;
            await fixture.SettleAsync(); await fixture.SettleAsync();
            scroll.VerticalOffset.Should().BeApproximately(manual, 2, "a cancelled pending follow must not run after the thumb has settled");
            manual.Should().BeGreaterThan(scroll.ScrollableHeight * .70);
            vm.JumpToTop(); await fixture.SettleAsync();
            scroll.VerticalOffset.Should().BeLessThan(80, "an explicit Jump after manual scrolling must remain available");
            vm.IsRecording = true;
            try
            {
                vm.IsRecordingFollowEnabled.Should().BeTrue();
                await DragNavigationThumbAsync(fixture, list, scroll, .5);
                vm.IsRecordingFollowEnabled.Should().BeFalse();
                vm.ShowResumeFollowBanner.Should().BeTrue();
                CountNavigationVisuals<ListBoxItem>(list).Should().BeInRange(1, 180);
            }
            finally { vm.IsRecording = false; view.DataContext = null; }
            Console.WriteLine($"UiNavigation rapid thumb: {actions} actions, 20 pending-scroll races; manual offset {manual:F1}; explicit Jump works; recording Follow pauses. Native-hidden routed-thumb verification only.");
            var body = ThreadCpuBudget.Check($"UiNavigation rapid thumb body ({actions} actions)", NavigationBodyCpuBudget, bodyCpu);
            Console.WriteLine($"UiNavigation rapid thumb CPU: body {body}; slowest layout pass {fixture.SlowestPass}.");
        });

    [Fact]
    public Task UiNavigationLayout_FailsALayoutPassThatComputesPastItsCpuBudget()
        => WpfTestApplication.RunAsync(async () =>
        {
            using var fixture = new NavigationLayoutFixture(TimeSpan.FromMilliseconds(50));
            fixture.Attach(new CpuBoundMeasure(TimeSpan.FromMilliseconds(300)));

            var error = await Assert.ThrowsAsync<ThreadCpuBudgetExceededException>(() => fixture.SettleAsync());

            error.Use.Cpu.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
            error.Message.Should().Contain("Layout pass (" + nameof(UiNavigationLayout_FailsALayoutPassThatComputesPastItsCpuBudget))
                .And.Contain("over its 50 ms CPU budget");
        });

    /// <summary>
    /// Dispatcher-thread CPU one layout pass of the production editor may use. The heaviest pass
    /// is the first layout of 10,000 actions: on 2026-11-08 it used 0.4-1.0 s and allocated about
    /// 110 MB, most of it for the route-target lists; since those lists are built once per change
    /// and selected by item it uses 0.2-0.5 s and about 20 MB (QUALITY_STATUS F-4). In a loaded
    /// full run passes stayed under 0.52 s of CPU while their waits grew to 32 s, so the budget
    /// counts CPU: a busy machine does not fail it, a pass that computes for 114 s does. Some full
    /// runs still put the editor in F-4's slow state (3.4-18.9 s per pass after the route-list
    /// fix); these tests then fail on purpose, naming the pass and its CPU, until that is fixed.
    /// </summary>
    private static readonly TimeSpan LayoutPassCpuBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Dispatcher-thread CPU a whole navigation body may use, including editor work outside layout
    /// passes such as selecting an action. Measured alone: 1.7-4.3 s before the F-4 route-list fix,
    /// the most being the 10,000-action rapid-thumb body, which allocated 673 MB; 1.2-1.9 s and at
    /// most 190 MB after it.
    /// </summary>
    private static readonly TimeSpan NavigationBodyCpuBudget = TimeSpan.FromSeconds(15);

    private sealed class CpuBoundMeasure(TimeSpan cpu) : FrameworkElement
    {
        private bool _burned;
        protected override Size MeasureOverride(Size availableSize)
        {
            if (!_burned) { _burned = true; ThreadCpuBudget.Burn(cpu); }
            return new Size(10, 10);
        }
    }

    private static void PopulateNavigationMacro(MacroEditorViewModel vm, int count)
    {
        vm.AddGeneratedMacro(new MacroModel
        {
            Name = "Isolated UI navigation sample",
            Steps = Enumerable.Range(0, count).Select(i => (MacroStep)new MouseMoveStep
            { Position = new System.Drawing.Point(i, i % 1080) }).ToList()
        }).Should().BeTrue();
    }

    private static async Task DragNavigationThumbAsync(NavigationLayoutFixture fixture, ListBox list, ScrollViewer scroll, double fraction)
    {
        var bar = NavigationVisuals<ScrollBar>(list).Single(b => b.Orientation == Orientation.Vertical);
        var track = FindNavigationVisual<Track>(bar)!;
        var thumb = track.Thumb!;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        // Start from the current scrollbar value, and route a real Thumb drag
        // through ScrollBar's class handlers and deferred ScrollViewer command.
        double target = scroll.ScrollableHeight * fraction;
        double travel = Math.Max(1, track.ActualHeight - thumb.ActualHeight);
        double delta = (target - bar.Value) / Math.Max(1, bar.Maximum - bar.Minimum) * travel;
        thumb.RaiseEvent(new DragDeltaEventArgs(0, delta) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, delta, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await fixture.SettleAsync();
    }

    private static T? FindNavigationVisual<T>(DependencyObject root) where T : DependencyObject
        => NavigationVisuals<T>(root).FirstOrDefault();
    private static int CountNavigationVisuals<T>(DependencyObject root) where T : DependencyObject
        => NavigationVisuals<T>(root).Count();
    private static IEnumerable<T> NavigationVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in NavigationVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class NavigationLayoutFixture : IDisposable
    {
        private readonly ResourceDictionary _styles = new() { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml") };
        private readonly HwndSource _source;
        private readonly Grid _root = new();
        private readonly TimeSpan _layoutPassCpuBudget;
        public object OtherPage { get; } = new NavigationOtherPage();
        /// <summary>The layout pass that used the most dispatcher-thread CPU so far.</summary>
        public ThreadCpuUse SlowestPass { get; private set; }
        public NavigationLayoutFixture(TimeSpan? layoutPassCpuBudget = null)
        {
            _layoutPassCpuBudget = layoutPassCpuBudget ?? LayoutPassCpuBudget;
            Application.Current.Resources.MergedDictionaries.Add(_styles);
            var editorTemplate = new DataTemplate(typeof(MacroEditorViewModel)) { VisualTree = new FrameworkElementFactory(typeof(MacroEditorView)) };
            _root.Resources[new DataTemplateKey(typeof(MacroEditorViewModel))] = editorTemplate;
            _root.Resources[new DataTemplateKey(typeof(NavigationOtherPage))] = new DataTemplate(typeof(NavigationOtherPage)) { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
            _source = new HwndSource(new HwndSourceParameters("Zenith private native-hidden UI layout")
            { Width = 1320, Height = 740, WindowStyle = unchecked((int)0x80000000), PositionX = -30000, PositionY = -30000 });
            _source.RootVisual = _root;
        }
        public void Attach(UIElement control) { _root.Children.Clear(); _root.Children.Add(control); }
        /// <summary>
        /// Lays the tree out, lets idle work run, then lays it out again. Each synchronous layout
        /// pass is held to a dispatcher-thread CPU budget; the idle wait is not, because waiting is
        /// not editor work (the shared dispatcher's hang guard bounds it).
        /// </summary>
        public async Task SettleAsync([CallerMemberName] string caller = "", [CallerLineNumber] int line = 0)
        {
            Pass($"Layout pass ({caller}, line {line})", () =>
            {
                _root.Measure(new Size(1320, 740)); _root.Arrange(new Rect(0, 0, 1320, 740)); _root.UpdateLayout();
            });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Pass($"Layout pass after idle ({caller}, line {line})", _root.UpdateLayout);
        }
        private void Pass(string what, Action pass)
        {
            var use = ThreadCpuBudget.Run(what, _layoutPassCpuBudget, pass);
            if (use.Cpu >= SlowestPass.Cpu) SlowestPass = use;
        }
        public void Dispose()
        {
            foreach (var view in NavigationVisuals<MacroEditorView>(_root).ToArray()) view.DataContext = null;
            _source.RootVisual = null; _source.Dispose();
            Application.Current.Resources.MergedDictionaries.Remove(_styles);
        }
        private sealed class NavigationOtherPage { }
    }
}
