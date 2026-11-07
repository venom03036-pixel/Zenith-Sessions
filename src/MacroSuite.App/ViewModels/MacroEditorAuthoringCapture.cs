using System.Drawing;
using System.Windows;
using MacroSuite.App.Authoring;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private enum TemplatePrompt { Choose, Capture, Import, ScreenSnip }

    private AuthoringImagePreparer? _authoringPreparer;
    private AuthoringImagePreparer AuthoringPreparer => _authoringPreparer ??= new(_captureService, _imageEncoder);
    private long _authoringSelectionRevision;

    internal Func<Window?, CancellationToken, ImageTemplateSourceRequest?> AuthoringSourcePrompter { get; set; }
        = ImageSourceDialog.PromptSource;
    internal Func<Window?, double, CancellationToken, ImageTemplateSourceRequest?> AuthoringImportPrompter { get; set; }
        = ImageSourceDialog.PromptImportSource;
    internal Func<Window?, Func<ScreenPoint, CancellationToken, Task<AuthoringPixelSample>>, CancellationToken, Task<ScreenPoint?>>
        AuthoringPointPrompter { get; set; } = ColorPickerOverlay.PromptPointAsync;
    internal Func<Window?, VisualWaitDraft, CancellationToken, VisualWaitDecision?> AuthoringVisualWaitPrompter { get; set; }
        = VisualWaitSetupDialog.Prompt;

    internal readonly record struct AuthoringSelection(MacroScriptItemViewModel? Document, MacroStep? Step,
        MacroStep? RootStep, int Count, int Index, long SelectionRevision, long DraftRevision, long StopGeneration);

    internal AuthoringSelection CaptureAuthoringSelection()
    {
        int index = SelectedStepIndex;
        return new(SelectedScript, GetStepAt(index), index >= 0 ? _actionStore[index] : null,
            _actionStore.Count, index, _authoringSelectionRevision, ActionEditor.DraftRevision, InputOperations.StopGeneration);
    }

    internal bool IsCurrentAuthoringSelection(AuthoringSelection expected)
    {
        if (_disposed || _shutdownStarted || InputOperations.StopGeneration != expected.StopGeneration
            || _authoringSelectionRevision != expected.SelectionRevision || ActionEditor.DraftRevision != expected.DraftRevision
            || !ReferenceEquals(SelectedScript, expected.Document) || _actionStore.Count != expected.Count) return false;
        int index = SelectedStepIndex;
        return index == expected.Index && ReferenceEquals(GetStepAt(index), expected.Step)
            && (index < 0 || ReferenceEquals(_actionStore[index], expected.RootStep));
    }

    private void RequireCurrentAuthoring(AuthoringInteraction owner, AuthoringSelection selection)
    {
        if (!OwnsAuthoringDraft(owner) || !IsCurrentAuthoringSelection(selection))
            throw new OperationCanceledException(owner.Lease.StopToken);
    }

    private static Window? AuthoringWindow => Application.Current is { } app && app.Dispatcher.CheckAccess()
        ? app.MainWindow : null;

    private Rectangle? PickAuthoringRegion(CancellationToken ct)
        => ReferenceEquals(RegionPrompter, DefaultRegionPrompter)
            ? RegionSelectorWindow.PromptSelection(AuthoringWindow, ct) : RegionPrompter();

    private Rectangle? PlaceAuthoringImage(Rectangle? region, System.Drawing.Size size, CancellationToken ct)
        => ReferenceEquals(ImageAreaPlacementPrompter, DefaultImageAreaPlacementPrompter)
            ? ImageSearchAreaPlacementWindow.Prompt(region, size, ct) : ImageAreaPlacementPrompter(region, size);

    private async Task<T> WithAuthoringPrivacyAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        IDisposable privacy = dispatcher is not null && !dispatcher.CheckAccess()
            ? await dispatcher.InvokeAsync(() => ScreenCapturePrivacy.HideEngine()) : ScreenCapturePrivacy.HideEngine();
        try { return await action(); }
        finally
        {
            try
            {
                if (dispatcher is not null && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(privacy.Dispose);
                else privacy.Dispose();
            }
            catch (Exception) { throw new HostInputOperationCleanupException(); }
        }
    }

    public bool TryPromptImageTemplate(out ImageTemplateSelection? selection) => TryPromptImageTemplate(null, out selection);

    internal bool TryPromptImageTemplate(AuthoringInteraction? parent, out ImageTemplateSelection? selection)
    {
        selection = PromptImageTemplate(TemplatePrompt.Choose, .8, parent);
        return selection is not null;
    }

    public ImageTemplateSelection? PromptOwnedTargetImage(bool import, double confidence)
        => PromptImageTemplate(import ? TemplatePrompt.Import : TemplatePrompt.Capture, confidence);

    internal ImageTemplateSelection? PromptOwnedScreenSnipTemplate() => PromptImageTemplate(TemplatePrompt.ScreenSnip, .8);

    private ImageTemplateSelection? PromptImageTemplate(TemplatePrompt prompt, double confidence, AuthoringInteraction? parent = null)
    {
        var stamp = CaptureAuthoringSelection();
        try
        {
            var selection = parent is null
                ? RunOwnedInteraction(Choose, (ImageTemplateSelection?)null,
                    prompt == TemplatePrompt.Import ? HostInputOperationKind.Picker : HostInputOperationKind.CaptureTest, lockDraft: true)
                : Choose(parent);
            return selection is not null && !string.IsNullOrWhiteSpace(selection.ImagePath)
                && IsCurrentAuthoringSelection(stamp) ? selection : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            if (!_disposed && !_shutdownStarted) NotifyStatus($"Could not prepare the image template: {error.Message}");
            // A borrowed preflight must report uncertainty to its actual owner.
            if (parent is not null && error is HostInputOperationCleanupException) throw;
            return null;
        }

        ImageTemplateSelection? Choose(AuthoringInteraction owner)
        {
            RequireCurrentAuthoring(owner, stamp);
            var selection = prompt switch
            {
                TemplatePrompt.Choose when ImagePrompter is { } choose => choose(),
                TemplatePrompt.Import when ImageImportPrompter is { } import => import(confidence),
                TemplatePrompt.Capture when ImageCaptureOnlyPrompter is { } capture => capture(confidence),
                _ => RunAuthoringWork(owner, () => PrepareImageTemplateAsync(owner, stamp, prompt, confidence), retireBeforeDelivery: parent is null)
            };
            if (!CanPublishAuthoringResult(owner) || !IsCurrentAuthoringSelection(stamp)) return null;
            if (selection is null) NotifyStatus("Image capture cancelled.");
            return selection;
        }
    }

    private async Task<ImageTemplateSelection?> PrepareImageTemplateAsync(AuthoringInteraction owner,
        AuthoringSelection stamp, TemplatePrompt prompt, double confidence)
    {
        var token = owner.Lease.StopToken;
        RequireCurrentAuthoring(owner, stamp);
        InputModeGate.Set(InputMode.CapturingImage);
        ImageTemplateSourceRequest? source = prompt switch
        {
            TemplatePrompt.Choose => AuthoringSourcePrompter(AuthoringWindow, token),
            TemplatePrompt.Import => AuthoringImportPrompter(AuthoringWindow, confidence, token),
            _ => new(ImageTemplateSourceKind.Screen, null, confidence, false)
        };
        if (source is null) return null;
        RequireCurrentAuthoring(owner, stamp);

        ImageTemplateSelection? prepared;
        if (source.Kind == ImageTemplateSourceKind.File)
        {
            if (string.IsNullOrWhiteSpace(source.FilePath)) return null;
            prepared = await AuthoringPreparer.ImportTemplateAsync(owner.Lease, source.FilePath, source.Confidence, source.AutoClick, token);
        }
        else if (source.Kind == ImageTemplateSourceKind.Screen)
        {
            if (prompt == TemplatePrompt.Choose) ScreenCapturePrivacy.PumpUi(TimeSpan.FromMilliseconds(50));
            RequireCurrentAuthoring(owner, stamp);
            prepared = await WithAuthoringPrivacyAsync<ImageTemplateSelection?>(async () =>
            {
                RequireCurrentAuthoring(owner, stamp);
                var region = PickAuthoringRegion(token);
                RequireCurrentAuthoring(owner, stamp);
                return region.HasValue
                    ? await AuthoringPreparer.CaptureTemplateAsync(owner.Lease, region.Value, source.Confidence, source.AutoClick, token)
                    : null;
            });
        }
        else throw new InvalidOperationException("Choose a supported image source.");

        RequireCurrentAuthoring(owner, stamp);
        if (prompt == TemplatePrompt.ScreenSnip) return prepared;
        if (prepared?.TemplateSize is not { } size) return null;
        var placed = PlaceAuthoringImage(prepared.SourceRegion, size, token);
        RequireCurrentAuthoring(owner, stamp);
        return placed is null ? null : prepared with { SourceRegion = placed };
    }

    private ColorPickResult? PromptAuthoringColor()
    {
        var stamp = CaptureAuthoringSelection();
        return RunOwnedInteraction(owner =>
        {
            RequireCurrentAuthoring(owner, stamp);
            if (ColorSamplePrompter is not null || ColorPrompter is not null)
            {
                var sample = ColorSamplePrompter?.Invoke()
                    ?? (ColorPrompter?.Invoke() is { } fallback ? new ColorPickResult(fallback, 200, 200) : null);
                RequireCurrentAuthoring(owner, stamp);
                return sample;
            }
            var exact = RunAuthoringWork(owner, () => PickAuthoringPixelAsync(owner, stamp));
            if (!CanPublishAuthoringResult(owner) || !IsCurrentAuthoringSelection(stamp)) return null;
            return exact is { } point ? new ColorPickResult(Color.FromArgb(point.R, point.G, point.B), point.X, point.Y) : null;
        }, (ColorPickResult?)null, HostInputOperationKind.CaptureTest, lockDraft: true);
    }

    private Task<AuthoringPixelSample?> PickAuthoringPixelAsync(AuthoringInteraction owner, AuthoringSelection stamp)
        => WithAuthoringPrivacyAsync<AuthoringPixelSample?>(async () =>
        {
            var token = owner.Lease.StopToken;
            RequireCurrentAuthoring(owner, stamp);
            var point = await AuthoringPointPrompter(AuthoringWindow,
                (position, ct) => AuthoringPreparer.SamplePixelAsync(owner.Lease, position, ct), token);
            RequireCurrentAuthoring(owner, stamp);
            // The overlay's task has closed the exact overlay and joined any hover sample.
            return point.HasValue ? await AuthoringPreparer.SamplePixelAsync(owner.Lease, point.Value, token) : null;
        });

    private VisualPresenceCondition? PromptOwnedPresence(VisualPresenceKind kind)
    {
        var stamp = CaptureAuthoringSelection();
        try
        {
            var condition = RunOwnedInteraction(owner => RunAuthoringWork(owner, () =>
                WithAuthoringPrivacyAsync<VisualPresenceCondition?>(async () =>
                {
                    RequireCurrentAuthoring(owner, stamp);
                    var region = PickAuthoringRegion(owner.Lease.StopToken);
                    RequireCurrentAuthoring(owner, stamp);
                    if (region is not { Width: > 0, Height: > 0 } area) return null;
                    var center = new ScreenPoint(checked(area.X + area.Width / 2), checked(area.Y + area.Height / 2));
                    var sample = await AuthoringPreparer.SamplePixelAsync(owner.Lease, center, owner.Lease.StopToken);
                    RequireCurrentAuthoring(owner, stamp);
                    return new VisualPresenceCondition
                    {
                        Kind = kind, X = area.X, Y = area.Y, Width = area.Width, Height = area.Height,
                        R = sample.R, G = sample.G, B = sample.B, SimilarityPercent = 90, RequiredPixelFraction = .08
                    };
                })), (VisualPresenceCondition?)null, HostInputOperationKind.CaptureTest, lockDraft: true);
            return IsCurrentAuthoringSelection(stamp) ? condition : null;
        }
        catch (Exception error)
        {
            if (!_disposed && !_shutdownStarted) NotifyStatus($"Could not sample the selected region: {error.Message}");
            return null;
        }
    }

    private VisualWaitStep? PromptOwnedVisualWait()
    {
        var stamp = CaptureAuthoringSelection();
        try
        {
            var step = RunOwnedInteraction(owner => RunAuthoringWork(owner, () => PrepareVisualWaitAsync(owner, stamp)),
                (VisualWaitStep?)null, HostInputOperationKind.CaptureTest, lockDraft: true);
            return IsCurrentAuthoringSelection(stamp) ? step : null;
        }
        catch (Exception error)
        {
            if (!_disposed && !_shutdownStarted) NotifyStatus($"Could not prepare the visual wait: {error.Message}");
            return null;
        }
    }

    private async Task<VisualWaitStep?> PrepareVisualWaitAsync(AuthoringInteraction owner, AuthoringSelection stamp)
    {
        var draft = new VisualWaitDraft();
        var token = owner.Lease.StopToken;
        InputModeGate.Set(InputMode.TestingVisualMatch);
        while (true)
        {
            RequireCurrentAuthoring(owner, stamp);
            var decision = AuthoringVisualWaitPrompter(AuthoringWindow, draft, token);
            RequireCurrentAuthoring(owner, stamp);
            if (decision is null) return null;
            if (decision.Intent == VisualWaitIntent.Create) return decision.CreatedStep;
            draft = decision.Draft;
            try
            {
                if (decision.Intent == VisualWaitIntent.PickPixel)
                {
                    var sample = await PickAuthoringPixelAsync(owner, stamp);
                    RequireCurrentAuthoring(owner, stamp);
                    if (sample.HasValue) draft = draft with { PixelSample = sample, RegionSample = null };
                }
                else if (decision.Intent == VisualWaitIntent.PickRegion)
                {
                    var sample = await WithAuthoringPrivacyAsync<AuthoringVisualSample?>(async () =>
                    {
                        RequireCurrentAuthoring(owner, stamp);
                        var region = PickAuthoringRegion(token);
                        RequireCurrentAuthoring(owner, stamp);
                        return region.HasValue ? await AuthoringPreparer.CaptureVisualAsync(owner.Lease, region.Value, token) : null;
                    });
                    RequireCurrentAuthoring(owner, stamp);
                    if (sample is not null) draft = draft with { RegionSample = sample, PixelSample = null };
                }
                else return null;
            }
            catch (Exception error) when (error is not (OperationCanceledException or HostInputOperationCleanupException))
            {
                RequireCurrentAuthoring(owner, stamp);
                NotifyStatus($"Could not sample the visual wait target: {error.Message}");
            }
        }
    }

    private ProducerAuthoringRequest? _producerAuthoringRequest;

    private sealed class ProducerAuthoringRequest(AuthoringInteraction owner, HostInputOperationKind kind)
    {
        internal AuthoringInteraction Owner { get; } = owner;
        internal HostInputOperationKind Kind { get; } = kind;
        internal Task Work { get; set; } = Task.CompletedTask;
    }

    private sealed record ProducerTargetPin(MacroTargetRule? RequestedRule, ResolvedMacroTarget? Target, Exception? Failure = null);

    private Task StartProducerAfterImageDraft(HostInputOperationKind kind, MacroScriptItemViewModel? script)
    {
        var initial = CaptureAuthoringSelection();
        var owner = BeginEditorInteraction(HostInputOperationKind.CaptureTest, lockDraft: true, initial.StopGeneration);
        if (owner is null) return Task.CompletedTask;
        var request = new ProducerAuthoringRequest(owner, kind);
        MacroTargetRule? requestedTarget = script?.Target;
        var pin = new TaskCompletionSource<ProducerTargetPin>(TaskCreationOptions.RunContinuationsAsynchronously);
        _producerAuthoringRequest = request;
        request.Work = FinishProducerPreflightAsync(request, initial, script, pin.Task);
        // The actual request is already published. Resolve before yielding to any
        // queued focus change or starting a capture/placement dialog.
        try { pin.SetResult(new(requestedTarget, ResolveProducerTarget(requestedTarget))); }
        catch (Exception error) { pin.SetResult(new(requestedTarget, null, error)); }
        return request.Work;
    }

    private ResolvedMacroTarget? ResolveProducerTarget(MacroTargetRule? rule)
        => rule is null ? null : rule.Mode == MacroTargetMode.Screen
            ? new ResolvedMacroTarget(rule, null) : Targets.Resolve(rule, Targets.Snapshot.LastExternal);

    private async Task FinishProducerPreflightAsync(ProducerAuthoringRequest request, AuthoringSelection initial,
        MacroScriptItemViewModel? script, Task<ProducerTargetPin> pendingPin)
    {
        var pin = await pendingPin;
        var owner = request.Owner;
        try
        {
            var pinnedTarget = pin.Target;
            bool applied = RunEditorInteraction(owner, current =>
            {
                if (pin.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                RequireCurrentAuthoring(current, initial);
                if (!Equals(script?.Target, pin.RequestedRule)) return false;
                RefreshInputOwnershipPresentation();
                RequireCurrentAuthoring(current, initial);
                return ActionEditor.TryCommit(current);
            }, false, lockDraft: false);
            var afterCommit = CaptureAuthoringSelection();
            if (!await owner.Completion.Task || !applied || owner.Lease.StopToken.IsCancellationRequested
                || afterCommit.StopGeneration != initial.StopGeneration || !IsCurrentAuthoringSelection(afterCommit)
                || !ReferenceEquals(initial.Document, afterCommit.Document) || initial.Step?.StepId != afterCommit.Step?.StepId
                || !Equals(script?.Target, pin.RequestedRule) || script is not null && !MacroScripts.Contains(script)) return;
            if (pinnedTarget?.Rule.Mode is not (null or MacroTargetMode.Screen)
                && Targets.Inspect(pinnedTarget) is not { IsValid: true, IsOwnWindow: false })
            {
                NotifyStatus("The original macro target changed during image setup. Select it again before starting.");
                return;
            }

            _producerAuthoringRequest = null;
            if (request.Kind == HostInputOperationKind.Playback)
                await RunMacroWithGenerationAsync(script, initial.StopGeneration, pin);
            else await StartRecordingWithGenerationAsync(initial.StopGeneration, pin, initial.Document);
        }
        catch (Exception error)
        {
            await owner.Completion.Task;
            if (!_disposed && !_shutdownStarted) NotifyStatus($"Could not finish image setup: {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_producerAuthoringRequest, request)) _producerAuthoringRequest = null;
        }
    }
}
