using System.Windows.Controls;
using System.ComponentModel;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _observedViewModel;
    private bool _syncingKey;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ObserveViewModel();
        Unloaded += (_, _) => StopObserving();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveViewModel(); };
    }

    private void ObserveViewModel()
    {
        StopObserving();
        _observedViewModel = DataContext as SettingsViewModel;
        if (_observedViewModel != null)
            _observedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SyncKey();
    }

    private void StopObserving()
    {
        if (_observedViewModel != null)
            _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _observedViewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ApiKeyInput)) SyncKey();
    }

    private void SyncKey()
    {
        _syncingKey = true;
        try { ApiKeyBox.Password = _observedViewModel?.ApiKeyInput ?? string.Empty; }
        finally { _syncingKey = false; }
    }

    private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_syncingKey && DataContext is SettingsViewModel vm)
            vm.ApiKeyInput = ApiKeyBox.Password;
    }
}
