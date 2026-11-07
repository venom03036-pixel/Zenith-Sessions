using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using MacroSuite.App.Authoring;
using MacroSuite.App.Overlays;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using MacroSuite.Infrastructure.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    private static readonly Rectangle AuthoringRegion = new(-120, 26, 6, 7);
    private static readonly Rectangle AuthoringPlacement = new(-240, 17, 12, 14);

    [Theory]
    [InlineData("Add")]
    [InlineData("Change")]
    [InlineData("Apply")]
    [InlineData("ControlFlow")]
    public Task EditorAuthoring_DefaultImageRoutesPublishEmbeddedSharedCapture(string route) => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture);
        SetupAuthoringCapture(vm);
        if (route == "ControlFlow")
        {
            vm.ReplaceStepAt(0, new IfThenElseStep(new VisualPresenceCondition { Kind = VisualPresenceKind.ColorPresent },
                [new DelayStep(TimeSpan.FromMilliseconds(20))], []), reloadPanel: true);
            vm.SelectedStep = vm.Steps[0];
        }
        var original = vm.GetStepAt(0)!;
        switch (route)
        {
            case "Add": vm.AddImageCommand.Execute(null); break;
            case "Change": vm.ChangeSelectedActionKind(EditableActionKind.ImageWait); break;
            case "Apply":
                vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
                Assert.True(vm.ActionEditor.Apply());
                break;
            default: vm.ActionEditor.CaptureImageTemplateCommand.Execute(null); break;
        }

        if (route == "ControlFlow")
        {
            var block = Assert.IsType<IfThenElseStep>(vm.GetStepAt(0));
            var condition = Assert.IsType<VisualPresenceCondition>(block.Condition);
            Assert.NotEmpty(condition.TemplateBytes!);
            Assert.Single(block.ThenSteps);
            Assert.Equal(VisualPresenceKind.ImagePresent, condition.Kind);
            Assert.Equal(original.StepId, block.StepId);
        }
        else
        {
            int index = route == "Add" ? 1 : 0;
            var wait = Assert.IsType<WaitUntilStep>(vm.GetStepAt(index));
            var image = Assert.IsType<ImageCondition>(wait.Condition);
            Assert.NotEmpty(image.TemplateBytes!);
            Assert.Equal(AuthoringPlacement, image.SearchRegion);
            if (route == "Add")
            {
                var click = Assert.IsType<ClickStep>(vm.GetStepAt(2));
                Assert.Equal(ImageCondition.LastMatchXVariable, click.XVariable);
                Assert.Equal(ImageCondition.LastMatchYVariable, click.YVariable);
            }
            else Assert.Equal(original.StepId, wait.StepId);
        }
        var request = Assert.Single(capture.Requests);
        Assert.Equal(AuthoringRegion, request.Region);
        Assert.False(request.Cancelable);
        Assert.NotEqual(Environment.CurrentManagedThreadId, request.ThreadId);
        Assert.True(capture.Frame!.IsDisposed);
        Assert.False(arbiter.Snapshot.IsBusy);
        await vm.ShutdownAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorAuthoring_TargetCaptureAndImportKeepBytesThroughApplyAndEncryptedReload(bool import) => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        var arbiter = new HostInputOperationArbiter();
        var store = new ProtectedMacroStore(folder.Library, new AuthoringFixtureKeys());
        var model = new MacroModel { Name = "Owned image fixture", Steps = [new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(4, 4)))] };
        string document = store.GetLibraryPath(model.Id);
        store.Save(model, document);
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture, documents: store);
        SetupAuthoringCapture(vm);
        byte[] importedBytes = SearchAreaTemplatePng(6, 7);
        string chosen = Path.Combine(folder.Root, "chosen.png");
        if (import)
        {
            File.WriteAllBytes(chosen, importedBytes);
            vm.AuthoringImportPrompter = (_, confidence, _) => new(ImageTemplateSourceKind.File, chosen, confidence, false);
        }
        var original = vm.GetStepAt(0);
        (import ? vm.ActionEditor.ImportImageTemplateCommand : vm.ActionEditor.CaptureTargetImageCommand).Execute(null);
        Assert.Same(original, vm.GetStepAt(0));
        Assert.True(vm.ActionEditor.HasDraftChanges);
        Assert.True(vm.ActionEditor.Apply());
        var saved = Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(vm.GetStepAt(0)).Condition);
        Assert.NotEmpty(saved.TemplateBytes!);
        if (import)
        {
            Assert.Empty(capture.Requests);
            Assert.Equal(importedBytes, saved.TemplateBytes);
            File.Delete(chosen); // A reopened document must use the embedded bytes.
        }
        else
        {
            Assert.Single(capture.Requests);
            Assert.True(capture.Frame!.IsDisposed);
        }
        var reopened = Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(store.Load(document).Model.Steps.Single()).Condition);
        Assert.Equal(saved.TemplateBytes, reopened.TemplateBytes);
        Assert.Equal(AuthoringPlacement, reopened.SearchRegion);
        Assert.Empty(Directory.EnumerateFiles(folder.Root, "*.png", SearchOption.AllDirectories));
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorAuthoring_FramelessWaitPumpsDispatcherAndShutdownJoinsAcceptedSharedCapture() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe { Block = true };
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture);
        SetupAuthoringCapture(vm);
        Task? shutdown = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var check = Task.Run(async () =>
        {
            try
            {
                await capture.Entered.Task.WaitAsync(OwnershipDeadline);
                await dispatcher.InvokeAsync(() =>
                {
                    Assert.Equal(HostInputOperationKind.CaptureTest, arbiter.Snapshot.Kind);
                    Assert.False(vm.ActionEditor.TryCommit());
                    shutdown = vm.ShutdownAsync();
                    Assert.False(shutdown.IsCompleted);
                    Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
                }).Task.WaitAsync(OwnershipDeadline);
            }
            finally { capture.Release.TrySetResult(); }
        });
        vm.AddImageCommand.Execute(null);
        await check;
        Assert.NotNull(shutdown);
        await shutdown!.WaitAsync(OwnershipDeadline);
        Assert.Single(vm.Steps);
        Assert.False(Assert.Single(capture.Requests).Cancelable);
        Assert.True(capture.Frame!.IsDisposed);
        Assert.False(arbiter.Snapshot.IsBusy);
    });

    [Fact]
    public Task EditorAuthoring_EarlyModalFrameExitKeepsActualCompletionObserver() => OwnershipOnUi(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var actual = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveFrame = dispatcher.BeginInvoke(() => frame.Continue = false);
        bool delivered = ModalPreparationBridge.TryComplete(dispatcher, actual.Task, out string result, out Task observation, frame);
        try
        {
            Assert.False(delivered);
            Assert.Null(result);
            Assert.False(actual.Task.IsCompleted);
            Assert.False(observation.IsCompleted);
        }
        finally
        {
            actual.TrySetResult("Late detached result");
            await Task.WhenAll(observation, leaveFrame.Task).WaitAsync(OwnershipDeadline);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task EditorAuthoring_StaleDraftOrReplacedSameIdStepCannotPublishOrRestoreOldAction(bool replaceStep)
        => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe { Block = true };
        using var vm = CreateAuthoringCaptureEditor(folder.Library, new(), capture);
        SetupAuthoringCapture(vm);
        var original = vm.GetStepAt(0)!;
        var replacement = new DelayStep(TimeSpan.FromSeconds(2)) { StepId = original.StepId };
        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var edit = Task.Run(async () =>
        {
            try
            {
                await capture.Entered.Task.WaitAsync(OwnershipDeadline);
                await dispatcher.InvokeAsync(() =>
                {
                    if (replaceStep) vm.Steps.ReplaceAt(0, replacement); // Simulate a replaced stored row despite disabled UI edits.
                    else
                    {
                        vm.ActionEditor.KindIndex = (int)EditableActionKind.KeyPress;
                        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
                    }
                }).Task.WaitAsync(OwnershipDeadline);
            }
            finally { capture.Release.TrySetResult(); }
        });
        Assert.False(vm.ActionEditor.Apply());
        await edit;
        Assert.Same(replaceStep ? replacement : original, vm.GetStepAt(0));
        if (!replaceStep) Assert.Equal((int)EditableActionKind.ImageWait, vm.ActionEditor.KindIndex);
        await vm.ShutdownAsync();
    });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public Task EditorAuthoring_ProducerAcquiresFreshLeaseOnlyAfterExactImagePreflight(bool recording, bool panicDuringCapture, bool nested)
        => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe { Block = true };
        using var player = new OwnershipPlayer();
        using var recorder = new OwnershipRecorder();
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture, player, recorder);
        SetupAuthoringCapture(vm);
        if (nested)
        {
            vm.Steps.ReplaceAll([new LoopStep(2, [new DelayStep(TimeSpan.FromMilliseconds(120))])]);
            vm.SelectedStep = (MacroStepItemViewModel)vm.OutlineRows[1]!;
        }
        using var targets = new PreflightTargetProbe();
        typeof(MacroEditorViewModel).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, targets);
        vm.SelectedScript!.Target = new MacroTargetRule { Mode = MacroTargetMode.ActiveExternalWindow };
        var originalChooser = vm.AuthoringSourcePrompter;
        vm.AuthoringSourcePrompter = (window, token) =>
        {
            Assert.Same(targets.Original, Assert.Single(targets.Resolved));
            Assert.Same(targets.Other, targets.Current);
            return originalChooser(window, token);
        };
        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
        Assert.True(vm.ActionEditor.RequiresImagePreparation);
        var activation = new OwnershipActivation();
        vm.ActivationService = activation;
        var original = vm.GetStepAt(0);
        var focusChange = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => targets.Current = targets.Other));
        Task start = recording ? vm.StartRecordingAsync() : vm.RunMacroAsync();
        try
        {
            await focusChange.Task.WaitAsync(OwnershipDeadline);
            await capture.Entered.Task.WaitAsync(OwnershipDeadline);
            var preflight = arbiter.Snapshot;
            Assert.Equal(HostInputOperationKind.CaptureTest, preflight.Kind);
            Assert.Equal(0, activation.Calls);
            if (panicDuringCapture) vm.SignalEmergencyStop();
            capture.Release.TrySetResult();
            if (panicDuringCapture)
            {
                await start.WaitAsync(OwnershipDeadline);
                Assert.Equal(0, activation.Calls);
                Assert.Same(original, vm.GetStepAt(0));
            }
            else
            {
                await activation.Entered.Task.WaitAsync(OwnershipDeadline);
                Assert.Equal(recording ? HostInputOperationKind.Recording : HostInputOperationKind.Playback, arbiter.Snapshot.Kind);
                Assert.NotEqual(preflight.OperationId, arbiter.Snapshot.OperationId);
                Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(vm.GetStepAt(0)).Condition);
                Assert.Same(targets.Original, Assert.Single(targets.Resolved));
                Assert.Same(targets.Original, targets.LastInspected);
            }
        }
        finally
        {
            vm.SignalEmergencyStop(); capture.Release.TrySetResult(); activation.Release.TrySetResult();
            await start.WaitAsync(OwnershipDeadline);
        }
        Assert.Equal(0, player.Plays);
        Assert.Equal(0, recorder.Starts);
        Assert.False(arbiter.Snapshot.IsBusy);
        await vm.ShutdownAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorAuthoring_LegacyNullTargetPinRefusesRuleChangedAfterProducerAdmission(bool recording)
        => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        using var player = new OwnershipPlayer();
        using var recorder = new OwnershipRecorder();
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture, player, recorder);
        using var targets = new PreflightTargetProbe();
        typeof(MacroEditorViewModel).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, targets);
        SetupAuthoringCapture(vm);
        var document = vm.SelectedScript!;
        document.Target = null;
        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
        Assert.True(vm.ActionEditor.RequiresImagePreparation);
        var activation = new OwnershipActivation();
        activation.Release.TrySetResult();
        vm.ActivationService = activation;
        var producerKind = recording ? HostInputOperationKind.Recording : HostInputOperationKind.Playback;
        bool changedAfterAdmission = false;
        void ChangeTargetAfterAdmission(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (changedAfterAdmission || args.PropertyName != nameof(vm.CanEditMacro) || arbiter.Snapshot.Kind != producerKind)
                return;
            changedAfterAdmission = true;
            document.Target = new MacroTargetRule { Mode = MacroTargetMode.ActiveExternalWindow };
        }
        vm.PropertyChanged += ChangeTargetAfterAdmission;
        try
        {
            var start = recording ? vm.StartRecordingAsync() : vm.RunMacroAsync();
            await start.WaitAsync(OwnershipDeadline);
            Assert.True(changedAfterAdmission);
            Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(vm.GetStepAt(0)).Condition);
            Assert.Empty(targets.Resolved);
            Assert.Equal(0, activation.Calls);
            Assert.Equal(0, player.Plays);
            Assert.Equal(0, recorder.Starts);
            Assert.False(arbiter.Snapshot.IsBusy);
        }
        finally
        {
            vm.PropertyChanged -= ChangeTargetAfterAdmission;
            vm.SignalEmergencyStop();
            await vm.ShutdownAsync();
        }
    });

    [Fact]
    public Task EditorAuthoring_ReentrantDeliveryRefusesNewOwnerButNextDeliberatePromptWorks() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture);
        int prompts = 0;
        vm.RegionPrompter = () => ++prompts == 1 ? null : AuthoringRegion;
        bool triedDuringDelivery = false;
        void Reenter(string message)
        {
            if (message != "Image capture cancelled." || triedDuringDelivery) return;
            triedDuringDelivery = true;
            Assert.False(arbiter.Snapshot.IsBusy);
            Assert.Null(vm.PromptOwnedScreenSnipTemplate());
            Assert.Equal(1, prompts);
        }
        vm.StatusChanged += Reenter;
        try { Assert.Null(vm.PromptOwnedScreenSnipTemplate()); }
        finally { vm.StatusChanged -= Reenter; }
        Assert.True(triedDuringDelivery);
        Assert.NotNull(vm.PromptOwnedScreenSnipTemplate());
        Assert.Equal(2, prompts);
        Assert.Single(capture.Requests);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorAuthoring_EncodedF10AndLegacyRawFramePreservePhysicalRegionAndOwnership() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture);
        vm.RegionPrompter = () => AuthoringRegion;
        vm.ImageAreaPlacementPrompter = (_, _) => throw new InvalidOperationException("F10 does not open placement.");
        var selection = vm.PromptOwnedScreenSnipTemplate();
        Assert.NotNull(selection);
        Assert.Equal(AuthoringRegion, selection!.SourceRegion);
        Assert.NotEmpty(selection.TemplateBytes!);
        Assert.True(capture.Frame!.IsDisposed);
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.True(vm.TryInsertStepAfterSelection(new WaitUntilStep(new ImageCondition(selection.TemplateBytes!, searchRegion: selection.SourceRegion))));

        using var raw = await vm.CaptureOwnedScreenSnipAsync().WaitAsync(OwnershipDeadline);
        Assert.NotNull(raw);
        Assert.Same(capture.Frame, raw);
        Assert.False(raw!.IsDisposed);
        Assert.Equal(FramePixelFormat.Bgra32, raw.Format);
        Assert.Equal(AuthoringRegion.X, raw.OriginX);
        Assert.Equal(AuthoringRegion.Y, raw.OriginY);
        Assert.Equal(AuthoringRegion.Size, new Size(raw.Width, raw.Height));
        Assert.All(capture.Requests, request => Assert.False(request.Cancelable));
        await vm.ShutdownAsync();
        Assert.False(raw.IsDisposed);
    });

    [Fact]
    public Task EditorAuthoring_CleanupFailureReturnsNoEncodedF10Result() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe { Block = true };
        var arbiter = new HostInputOperationArbiter();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, arbiter, capture);
        vm.RegionPrompter = () => AuthoringRegion;
        var dispatcher = Dispatcher.CurrentDispatcher;
        CancellationTokenRegistration failedStop = default;
        var check = Task.Run(async () =>
        {
            try
            {
                await capture.Entered.Task.WaitAsync(OwnershipDeadline);
                await dispatcher.InvokeAsync(() =>
                {
                    var lease = (HostInputOperationLease)typeof(MacroEditorViewModel)
                        .GetField("_editorInteractionLease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                    failedStop = lease.StopToken.Register(() => throw new InvalidOperationException("Synthetic stop callback failure"));
                    lease.SignalStop();
                }).Task.WaitAsync(OwnershipDeadline);
            }
            finally { capture.Release.TrySetResult(); }
        });
        try
        {
            Assert.Null(vm.PromptOwnedScreenSnipTemplate());
            await check;
            Assert.True(capture.Frame!.IsDisposed);
            Assert.True(arbiter.Snapshot.IsQuarantined);
            Assert.Single(vm.Steps);
            await vm.ShutdownAsync();
        }
        finally { capture.Release.TrySetResult(); await failedStop.DisposeAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorAuthoring_RegionPresenceUsesExactCenterAndNeverInventsColorOnFailure(bool fail) => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe { Fail = fail };
        using var vm = CreateAuthoringCaptureEditor(folder.Library, new(), capture);
        vm.RegionPrompter = () => AuthoringRegion;
        vm.AddIfCommand.Execute(null);
        Assert.Equal(new Rectangle(AuthoringRegion.X + AuthoringRegion.Width / 2, AuthoringRegion.Y + AuthoringRegion.Height / 2, 1, 1),
            Assert.Single(capture.Requests).Region);
        if (fail) Assert.Single(vm.Steps);
        else
        {
            var condition = Assert.IsType<VisualPresenceCondition>(Assert.IsType<IfThenElseStep>(vm.GetStepAt(1)).Condition);
            Assert.Equal((byte)0, condition.R);
            Assert.Equal((byte)0, condition.G);
            Assert.Equal((byte)0, condition.B);
        }
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorAuthoring_FinalColorReadUsesExactClickAfterHoverEnds() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, new(), capture);
        var click = new ScreenPoint(-8, 15);
        vm.AuthoringPointPrompter = async (_, preview, token) =>
        {
            var hover = await preview(new ScreenPoint(3, 4), token);
            Assert.NotEqual((byte)0, hover.R);
            return click;
        };
        vm.AddColorConditionCommand.Execute(null);
        Assert.Equal(new[] { new Rectangle(3, 4, 1, 1), new Rectangle(click.X, click.Y, 1, 1) }, capture.Requests.Select(request => request.Region));
        var condition = Assert.IsType<ColorCondition>(Assert.IsType<WaitUntilStep>(vm.GetStepAt(1)).Condition);
        Assert.Equal(new Point(click.X, click.Y), condition.Location);
        Assert.Equal((byte)0, condition.R);
        Assert.Equal((byte)0, condition.G);
        Assert.Equal((byte)0, condition.B);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorAuthoring_VisualWaitReopensCurrentDetachedDraftAfterOwnedRegionCapture() => WpfTestApplication.RunAsync(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var capture = new AuthoringCaptureProbe();
        using var vm = CreateAuthoringCaptureEditor(folder.Library, new(), capture);
        vm.RegionPrompter = () => AuthoringRegion;
        var original = new VisualWaitDraft(ModeIndex: 2, SearchModeIndex: 1, MethodIndex: 2, SimilarityPercent: 86,
            MaximumAccuracy: true, PollText: "250", TimeoutText: "17", TimeoutActionIndex: 2, RetryText: "5");
        int prompts = 0;
        vm.AuthoringVisualWaitPrompter = (_, draft, _) =>
        {
            if (++prompts == 1) return new(VisualWaitIntent.PickRegion, original);
            Assert.Equal(original, draft with { RegionSample = null });
            Assert.Equal(AuthoringRegion, draft.RegionSample!.Region);
            return new(VisualWaitIntent.Create, draft, VisualWaitSetupDialog.CreateStep(draft));
        };
        vm.AddVisualWaitCommand.Execute(null);
        Assert.Equal(2, prompts);
        var step = Assert.IsType<VisualWaitStep>(vm.GetStepAt(1));
        Assert.Equal(250, step.PollIntervalMs);
        Assert.Equal(17_000, step.TimeoutMs);
        Assert.Equal(5, step.RetryCount);
        Assert.Equal(AuthoringRegion.Width * AuthoringRegion.Height * 4, step.TemplateBgra!.Length);
        Assert.True(capture.Frame!.IsDisposed);
        await vm.ShutdownAsync();
    });

    private MacroEditorViewModel CreateAuthoringCaptureEditor(string directory, HostInputOperationArbiter arbiter,
        AuthoringCaptureProbe capture, OwnershipPlayer? player = null, OwnershipRecorder? recorder = null, IMacroDocumentStore? documents = null)
    {
        var feature = new DummyFeatureGate();
        var vm = new MacroEditorViewModel(capture, player ?? new OwnershipPlayer(), new AIVisionRouter(new InMemorySecretVault()),
            new DummyApprovalGate(), feature, new UsageTracker(new DummyUsageRepository(), feature), new DummyAuditLogger(),
            customMacrosDirectory: directory, inputRecorder: recorder ?? new OwnershipRecorder(), recordingDesktop: new ExternalRecordingDesktop(),
            documentStore: documents ?? new OwnershipDocuments(directory, TimeSpan.Zero), inputOperations: arbiter);
        vm.SelectedStep = vm.Steps[0];
        typeof(MacroEditorViewModel).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new OwnershipTargets());
        return vm;
    }

    private static void SetupAuthoringCapture(MacroEditorViewModel vm)
    {
        vm.RegionPrompter = () => AuthoringRegion;
        vm.ImageAreaPlacementPrompter = (_, size) =>
        {
            Assert.Equal(AuthoringRegion.Size, size);
            return AuthoringPlacement;
        };
        vm.AuthoringSourcePrompter = (_, _) => new(ImageTemplateSourceKind.Screen, null, .84, true);
    }

    private sealed class AuthoringFixtureKeys : ILocalMacroKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] GetKey(bool createIfMissing) => (byte[])_key.Clone();
    }

    private sealed class PreflightTargetProbe : ITargetWindowService
    {
        public TargetWindowInfo Original { get; } = new(new IntPtr(101), 5, 10, "owned-fixture-a", "A", new Rectangle(0, 0, 60, 80), 1);
        public TargetWindowInfo Other { get; } = new(new IntPtr(102), 6, 11, "owned-fixture-b", "B", new Rectangle(50, 70, 60, 80), 2);
        public TargetWindowInfo? Current { get; set; }
        public List<TargetWindowInfo?> Resolved { get; } = new();
        public TargetWindowInfo? LastInspected { get; private set; }
        public bool SupportsWindowTargeting => true;
        public TargetWindowSnapshot Snapshot => new(Current ?? Original, Current ?? Original, 1, 1, true);
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TargetWindowInfo>>([Original, Other]);
        public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null)
        { Resolved.Add(preferredExternal); return new(rule, preferredExternal); }
        public TargetValidation Inspect(ResolvedMacroTarget target)
        { LastInspected = target.Window; return new(true, false, false, target.Window!.ClientBounds, target.Window.Generation); }
        public void RegisterOwnWindow(IntPtr window) { }
        public bool TryActivate(ResolvedMacroTarget target) => throw new InvalidOperationException("This fixture stops before native activation.");
        public void Dispose() { }
    }

    private sealed class AuthoringCaptureProbe : IScreenCaptureService
    {
        internal readonly record struct Request(Rectangle Region, bool Cancelable, int ThreadId);
        public ConcurrentQueue<Request> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = OwnershipGate();
        public TaskCompletionSource Release { get; } = OwnershipGate();
        public bool Block { get; init; }
        public bool Fail { get; init; }
        public CaptureFrame? Frame { get; private set; }
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public async Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            Requests.Enqueue(new(region, ct.CanBeCanceled, Environment.CurrentManagedThreadId));
            Entered.TrySetResult();
            if (Block) await Release.Task.ConfigureAwait(false);
            if (Fail) throw new InvalidOperationException("Synthetic shared capture failed.");
            var capture = region.X < 0 ? new DummyCaptureService { SampleR = 0, SampleG = 0, SampleB = 0 } : new DummyCaptureService();
            return Frame = await capture.CaptureRegionAsync(region, CancellationToken.None);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { Release.TrySetResult(); Frame?.Dispose(); }
    }
}
