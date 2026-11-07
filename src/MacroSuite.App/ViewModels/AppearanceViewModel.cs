using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using MacroSuite.App.Common;
using MacroSuite.App.Themes;
using Microsoft.Win32;

namespace MacroSuite.App.ViewModels;

public sealed class AppearanceViewModel : ObservableObject
{
    private readonly AppearanceStore _store;
    private readonly AppearanceAssetService _assets;
    private AppearanceProfile _draft;
    private AppearanceProfile? _selectedCustom;
    private ResolvedAppearance _resolved;
    private bool _customPreview, _dirty, _busy, _executionActive, _reducedEffects, _shuttingDown;
    private string _status = "", _role = "Accent", _hex = "#83D8C1";
    private CancellationTokenSource? _loadCancellation;
    private Task _operation = Task.CompletedTask;
    private int _loadGeneration;
    private AppearanceAsset? _asset;

    public AppearanceViewModel(string appDataRoot, string baseThemeId)
    {
        _store = new(appDataRoot); _assets = new(appDataRoot);
        var document = _store.Load(out string? message);
        foreach (var theme in document.CustomThemes) CustomThemes.Add(theme);
        _selectedCustom = CustomThemes.FirstOrDefault(p => p.Id == document.ActiveCustomId);
        _draft = _selectedCustom ?? AppearanceProfile.FromPreset(ThemeCatalog.Resolve(baseThemeId));
        _customPreview = _selectedCustom is not null;
        _resolved = _customPreview ? AppearancePalette.Resolve(_draft) : new(ThemeCatalog.Resolve(baseThemeId), 1, null);
        _status = message ?? "";
        _hex = _draft.Accent;
        ImportBackgroundCommand = new RelayCommand(() => StartImport(), () => !IsBusy && !_executionActive && !_shuttingDown);
        RemoveBackgroundCommand = new RelayCommand(() => { CancelLoading(); _asset = null; Edit(p => p with { AssetId = null, BackgroundKind = AppearanceBackgroundKind.Solid }); });
        SaveThemeCommand = new RelayCommand(() => Save(false), () => !IsBusy && !_shuttingDown);
        SaveAsThemeCommand = new RelayCommand(() => Save(true), () => !IsBusy && !_shuttingDown && CustomThemes.Count < 64);
        DuplicateThemeCommand = new RelayCommand(() => { ThemeName = _draft.Name + " copy"; Save(true); }, () => CustomThemes.Count < 64 && !IsBusy);
        RenameThemeCommand = new RelayCommand(() => Save(false), () => _selectedCustom is not null && !IsBusy);
        DeleteThemeCommand = new RelayCommand(Delete, () => _selectedCustom is not null && !IsBusy);
        ResetThemeCommand = new RelayCommand(() => { CancelLoading(); var reset = AppearanceProfile.FromPreset(ThemeCatalog.Resolve(_draft.BaseThemeId)); _asset = null; Edit(_ => reset with { Id = _draft.Id, Name = _draft.Name }); });
        RevertThemeCommand = new RelayCommand(Revert, () => IsDirty);
        ApplySwatchCommand = new RelayCommand(value => { if (value is string hex && AppearancePalette.TryParse(hex)) SelectedColorHex = hex; });
    }

    public event Action? Changed;
    public ObservableCollection<AppearanceProfile> CustomThemes { get; } = [];
    public IReadOnlyList<string> ColorRoles { get; } = ["Accent", "Buttons", "Background", "Panels", "Text"];
    public IReadOnlyList<string> BackgroundKinds { get; } = ["Solid color", "Image or GIF", "Windows desktop acrylic"];
    public IReadOnlyList<string> SuggestedColors => _asset?.Swatches ?? [];
    public AppearanceProfile CurrentProfile => _draft;
    public ThemePreset CurrentPalette => _resolved.Palette;
    public AppearanceAsset? Asset => _asset;
    public double EffectivePanelOpacity => _resolved.EffectivePanelOpacity;
    public bool ShouldAnimate => _draft.Animate && !_executionActive && !_reducedEffects && !_shuttingDown;
    public bool RuntimeReducedEffects { get => _reducedEffects; set { if (SetProperty(ref _reducedEffects, value)) { OnPropertyChanged(nameof(ShouldAnimate)); OnPropertyChanged(nameof(EffectsStatus)); Changed?.Invoke(); } } }
    public string EffectsStatus => RuntimeReducedEffects ? "Effects reduced to protect the resource target: animation, blur, and acrylic are paused." : "GIFs pause during automation and when Zenith is inactive or minimized.";
    public bool IsDirty => _dirty;
    public bool IsBusy => _busy;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ContrastNotice => _resolved.Notice ?? "Text and control contrast is protected automatically.";
    public string ThemeName { get => _draft.Name; set => Edit(p => p with { Name = (value ?? "")[..Math.Min(value?.Length ?? 0, 80)] }); }
    public bool AutomaticText { get => _draft.AutomaticText; set => Edit(p => p with { AutomaticText = value }); }
    public bool ColorsFromImage { get => _draft.ColorsFromImage; set { Edit(p => p with { ColorsFromImage = value }); if (value && _asset is not null) UseImageColors(); } }
    public double PanelOpacityPercent { get => _draft.PanelOpacity * 100; set { if (double.IsFinite(value)) Edit(p => p with { PanelOpacity = Math.Clamp(value / 100, 0, 1) }); } }
    public double BlurRadius { get => _draft.BlurRadius; set { if (double.IsFinite(value)) Edit(p => p with { BlurRadius = Math.Clamp(value, 0, 24) }); } }
    public double BackgroundDimPercent { get => _draft.Dim * 100; set { if (double.IsFinite(value)) Edit(p => p with { Dim = Math.Clamp(value / 100, 0, 1) }); } }
    public int BackgroundKindIndex
    {
        get => (int)_draft.BackgroundKind;
        set
        {
            if (value is < 0 or > 2 || value == (int)_draft.BackgroundKind) return;
            Edit(p => p with { BackgroundKind = (AppearanceBackgroundKind)value });
            if (value == 1 && _asset is null && _draft.AssetId is not null) StartLoadingSavedAsset();
        }
    }
    public bool AnimateBackground { get => _draft.Animate; set => Edit(p => p with { Animate = value }); }
    public AppearanceProfile? SelectedCustomTheme
    {
        get => _selectedCustom;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCustom)) return;
            _selectedCustom = value; _draft = value; _customPreview = true; _dirty = false;
            Resolve(); Persist(); StartLoadingSavedAsset();
        }
    }
    public string SelectedColorRole { get => _role; set { if (SetProperty(ref _role, value)) { _hex = RoleColor(); OnPropertyChanged(nameof(SelectedColorHex)); OnPropertyChanged(nameof(ColorR)); OnPropertyChanged(nameof(ColorG)); OnPropertyChanged(nameof(ColorB)); } } }
    public string SelectedColorHex
    {
        get => _hex;
        set
        {
            if (!SetProperty(ref _hex, value ?? "")) return;
            if (!AppearancePalette.TryParse(_hex)) { Status = "Use a six-digit hex color, for example #83D8C1."; return; }
            string color = _hex.ToUpperInvariant();
            Edit(p => _role switch
            {
                "Buttons" => p with { Button = color, ColorsFromImage = false },
                "Background" => p with { Canvas = color, ColorsFromImage = false },
                "Panels" => p with { Panel = color, ColorsFromImage = false },
                "Text" => p with { Text = color, AutomaticText = false },
                _ => p with { Accent = color, ColorsFromImage = false }
            });
        }
    }
    public int ColorR { get => AppearancePalette.Parse(RoleColor()).R; set => SetRgb(value, ColorG, ColorB); }
    public int ColorG { get => AppearancePalette.Parse(RoleColor()).G; set => SetRgb(ColorR, value, ColorB); }
    public int ColorB { get => AppearancePalette.Parse(RoleColor()).B; set => SetRgb(ColorR, ColorG, value); }
    private void SetRgb(int r, int g, int b) => SelectedColorHex = $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";
    private string RoleColor() => _role switch { "Buttons" => _draft.Button, "Background" => _draft.Canvas, "Panels" => _draft.Panel, "Text" => _draft.Text, _ => _draft.Accent };
    public RelayCommand ImportBackgroundCommand { get; }
    public RelayCommand RemoveBackgroundCommand { get; }
    public RelayCommand SaveThemeCommand { get; }
    public RelayCommand SaveAsThemeCommand { get; }
    public RelayCommand DuplicateThemeCommand { get; }
    public RelayCommand RenameThemeCommand { get; }
    public RelayCommand DeleteThemeCommand { get; }
    public RelayCommand ResetThemeCommand { get; }
    public RelayCommand RevertThemeCommand { get; }
    public RelayCommand ApplySwatchCommand { get; }

    public void Initialize() => StartLoadingSavedAsset();
    public Task WaitForBackgroundAsync() => _operation;
    public void SetResourcePressure(bool reduced) => RuntimeReducedEffects = reduced;
    public void SetExecutionActive(bool active)
    {
        if (_executionActive == active) return;
        _executionActive = active;
        if (!active) RuntimeReducedEffects = false;
        OnPropertyChanged(nameof(ShouldAnimate)); RefreshCommands(); Changed?.Invoke();
    }
    public void ReportBackdropStatus(string message) { if (!string.IsNullOrWhiteSpace(message)) Status = message; }
    public void UsePreset(string id)
    {
        CancelLoading(); _asset = null; _selectedCustom = null; _draft = AppearanceProfile.FromPreset(ThemeCatalog.Resolve(id));
        _customPreview = false; _dirty = false; Resolve(); Persist();
    }
    private void Edit(Func<AppearanceProfile, AppearanceProfile> change)
    {
        AppearanceProfile next = change(_draft); if (next == _draft) return;
        _draft = next; _customPreview = true; _dirty = true; Resolve();
    }
    private void Resolve()
    {
        _resolved = _customPreview ? AppearancePalette.Resolve(_draft) : new(ThemeCatalog.Resolve(_draft.BaseThemeId), 1, null);
        _hex = RoleColor(); OnPropertyChanged(string.Empty); RefreshCommands(); Changed?.Invoke();
    }
    private void RefreshCommands()
    {
        ImportBackgroundCommand?.RaiseCanExecuteChanged(); SaveThemeCommand?.RaiseCanExecuteChanged(); SaveAsThemeCommand?.RaiseCanExecuteChanged();
        DuplicateThemeCommand?.RaiseCanExecuteChanged(); RenameThemeCommand?.RaiseCanExecuteChanged(); DeleteThemeCommand?.RaiseCanExecuteChanged(); RevertThemeCommand?.RaiseCanExecuteChanged();
    }
    private bool Persist()
    {
        try { _store.Save(new() { ActiveCustomId = _selectedCustom?.Id, CustomThemes = CustomThemes.ToList() }); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Status = $"Could not save appearance: {ex.Message}"; return false; }
    }
    public bool TrySaveDraft()
    {
        if (IsBusy) { Status = "Wait for the background import to finish before saving settings."; return false; }
        if (IsDirty) { Save(false); return !IsDirty; }
        return Persist();
    }
    private void Save(bool asNew)
    {
        if (string.IsNullOrWhiteSpace(_draft.Name)) { Status = "Enter a theme name before saving."; return; }
        if ((asNew || _selectedCustom is null) && CustomThemes.Count >= 64) { Status = "The theme library is full. Delete a custom theme first."; return; }
        var saved = _draft with { Id = asNew || _selectedCustom is null ? Guid.NewGuid().ToString("N") : _selectedCustom.Id, Name = _draft.Name.Trim() };
        var before = CustomThemes.ToArray(); var previous = _selectedCustom;
        int index = asNew || _selectedCustom is null ? -1 : CustomThemes.IndexOf(_selectedCustom);
        if (index < 0) CustomThemes.Add(saved); else CustomThemes[index] = saved;
        _selectedCustom = saved;
        if (!Persist()) { CustomThemes.Clear(); foreach (var item in before) CustomThemes.Add(item); _selectedCustom = previous; return; }
        _draft = saved; _dirty = false; Status = "Custom theme saved."; Resolve(); QueueAssetCleanup();
    }
    private void Delete()
    {
        if (_selectedCustom is null) return;
        var removed = _selectedCustom; int index = CustomThemes.IndexOf(removed); CustomThemes.Remove(removed); _selectedCustom = null;
        if (!Persist()) { CustomThemes.Insert(index, removed); _selectedCustom = removed; return; }
        UsePreset(removed.BaseThemeId); Status = "Custom theme deleted. Its base preset is active."; QueueAssetCleanup();
    }
    private void QueueAssetCleanup() => StartOperation(async () =>
    {
        var cancellation = _loadCancellation = new();
        try
        {
            var keep = CustomThemes.Select(p => p.AssetId).Append(_draft.AssetId).Where(id => id is not null).Cast<string>().ToArray();
            await _assets.RemoveUnusedAsync(keep, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status = $"The theme was saved, but unused backgrounds could not be removed: {ex.Message}"; }
        finally { if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null; cancellation.Dispose(); }
    });
    private void Revert()
    {
        _draft = _selectedCustom ?? AppearanceProfile.FromPreset(ThemeCatalog.Resolve(_draft.BaseThemeId));
        _customPreview = _selectedCustom is not null; _dirty = false; Resolve(); StartLoadingSavedAsset();
    }
    private void StartImport()
    {
        var picker = new OpenFileDialog { Title = "Choose a theme background", Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true };
        if (picker.ShowDialog() == true) ImportFileAsync(picker.FileName);
    }
    public Task ImportFileAsync(string path) => StartOperation(() => ImportCoreAsync(path));

    // Chain replacements so only one bounded decoder owns memory at a time. Shutdown
    // awaits the entire chain, including a decoder cancelled by a later selection.
    private Task StartOperation(Func<Task> action)
    {
        CancelLoading();
        int generation = _loadGeneration;
        Task previous = _operation;
        return _operation = RunAfterAsync(previous, generation, action);
    }
    private async Task RunAfterAsync(Task previous, int generation, Func<Task> action)
    {
        await previous;
        if (_shuttingDown || generation != _loadGeneration) return;
        await action();
    }
    private async Task ImportCoreAsync(string path)
    {
        if (_executionActive || _shuttingDown) { Status = "Stop automation before importing a background."; return; }
        var cancellation = _loadCancellation = new(); _busy = true; OnPropertyChanged(nameof(IsBusy)); RefreshCommands();
        try
        {
            var asset = await _assets.ImportAsync(path, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _asset = asset; Edit(p => p with { AssetId = asset.Id, BackgroundKind = AppearanceBackgroundKind.Image });
            if (_draft.ColorsFromImage) UseImageColors(); Status = asset.Notice ?? "Background imported. Save the theme to keep it.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { Status = $"Background could not be imported: {ex.Message}"; }
        finally { if (ReferenceEquals(_loadCancellation, cancellation)) { _loadCancellation = null; _busy = false; OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); } cancellation.Dispose(); }
    }
    private void StartLoadingSavedAsset()
    {
        CancelLoading(); _asset = null;
        if (_draft.AssetId is not null && _draft.BackgroundKind == AppearanceBackgroundKind.Image)
        {
            string id = _draft.AssetId;
            StartOperation(() => LoadSavedAssetAsync(id));
        }
        else Changed?.Invoke();
    }
    private async Task LoadSavedAssetAsync(string id)
    {
        var cancellation = _loadCancellation = new();
        try { var asset = await _assets.LoadAsync(id, cancellation.Token); if (!cancellation.IsCancellationRequested) { _asset = asset; OnPropertyChanged(nameof(SuggestedColors)); Changed?.Invoke(); } }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { Status = $"The saved background is unavailable; using solid colors. {ex.Message}"; Changed?.Invoke(); }
        finally { if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null; cancellation.Dispose(); }
    }
    private void UseImageColors()
    {
        if (_asset?.Swatches.Count is not > 0) return;
        Color dominant = AppearancePalette.Parse(_asset.Swatches[0]);
        Color accent = _asset.Swatches.Select(AppearancePalette.Parse).OrderByDescending(c => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))).First();
        bool light = AppearancePalette.Luminance(dominant) > .55;
        Color canvas = AppearancePalette.Mix(dominant, light ? Colors.White : Colors.Black, light ? .78 : .82);
        Color panel = AppearancePalette.Mix(canvas, light ? Colors.Black : Colors.White, .035);
        Edit(p => p with { Canvas = AppearancePalette.Hex(canvas), Panel = AppearancePalette.Hex(panel), Accent = AppearancePalette.Hex(accent), Button = AppearancePalette.Hex(accent), AutomaticText = true });
    }
    private void CancelLoading() { _loadGeneration++; _loadCancellation?.Cancel(); }
    public async Task ShutdownAsync()
    {
        _shuttingDown = true; CancelLoading(); Changed?.Invoke();
        try { await _operation; } catch (OperationCanceledException) { }
        _asset = null;
    }
}
