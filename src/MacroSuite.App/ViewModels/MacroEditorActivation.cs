using MacroSuite.Core.Licensing;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private IActivationService? _activationService;
    public IActivationService? ActivationService
    {
        get => _activationService;
        set
        {
            if (ReferenceEquals(_activationService, value)) return;
            if (_activationService is not null) _activationService.StateChanged -= OnActivationStateChanged;
            _activationService = value;
            if (value is not null) value.StateChanged += OnActivationStateChanged;
            OnActivationStateChanged(this, EventArgs.Empty);
        }
    }
    public bool HasActivation => _activationService?.IsActive != false;
    private void OnActivationStateChanged(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => OnActivationStateChanged(sender, args));
            return;
        }
        if (_disposed) return;
        OnPropertyChanged(nameof(HasActivation)); OnPropertyChanged(nameof(IsEditingLocked)); OnPropertyChanged(nameof(CanEditMacro));
        RunCommand?.RaiseCanExecuteChanged(); RecordCommand?.RaiseCanExecuteChanged();
        RefreshImportAvailability();
    }
    public async Task<bool> EnsureActivationForOperationAsync(CancellationToken ct = default)
    {
        if (_activationService is null) return true;
        ActivationOperationResult result = await _activationService.RefreshAsync(ct).ConfigureAwait(true);
        if (!result.Success || !_activationService.IsActive)
        {
            NotifyStatus(result.Message);
            return false;
        }
        _activationService.RequireActive();
        return true;
    }
    private void DetachActivationService()
    {
        if (_activationService is not null) _activationService.StateChanged -= OnActivationStateChanged;
    }
}
