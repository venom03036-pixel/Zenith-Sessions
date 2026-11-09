#if DEVELOPER_MODE
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;
using Zenith.MacroHost.Execution;
using Zenith.MacroHost.Windows;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    internal ForegroundControlBinding CreateForegroundBinding(SavedMacroPlanPreparer preparer,
        ForegroundPeerOwnership ownership, Action<Type> fault)
    {
        var generation = InputOperations.StopGeneration;
        if (_disposed || _shutdownStarted || !EnsureEmergencyStopReady()
            || ActivationService is not { IsActive: true } activation)
            throw new InvalidOperationException("The Personal editor cannot open foreground playback.");
        var targets = Targets;
        var playback = new CompanionForegroundPlayback(InputOperations, preparer, targets, _inputPlayer, activation, _featureGate,
            ct => PhysicalInputReadiness.WaitForNeutralAsync(TimeSpan.FromMilliseconds(750), ct), fault);
        return new(playback, _companionCatalog.HostSessionId, generation, (peer, request, ct) =>
        {
            if (!ownership.Authorizes(peer, request, ct)) return ValueTask.FromResult<TargetWindowInfo?>(null);
            var expected = new TargetWindowInfo((nint)request.Window, request.ProcessId,
                DateTime.FromFileTimeUtc(request.CreationFileTime).Ticks, "", "", default, 0);
            try
            {
                var target = targets.Resolve(new MacroTargetRule { Mode = MacroTargetMode.ActiveExternalWindow }, expected);
                return ValueTask.FromResult(target.Window);
            }
            catch (InvalidOperationException) { return ValueTask.FromResult<TargetWindowInfo?>(null); }
        });
    }
}
#endif
