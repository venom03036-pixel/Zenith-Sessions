using System.Collections.ObjectModel;
using System.Drawing;
using MacroSuite.App.Common;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;

namespace MacroSuite.App.ViewModels;

/// <summary>Draft generation and explicit save; has no playback capability. The owner awaits CloseAsync before disposal.</summary>
public sealed class AIMacroAuthoringViewModel : ObservableObject
{
    private readonly IAIMacroAuthoringService _service;
    private readonly Func<MacroModel, CancellationToken, Task> _saveDraft;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _activeTask = Task.CompletedTask;
    private AIMacroAuthoringSnapshot? _snapshot;
    private AIMacroDraft? _draft;
    private string _prompt = "";
    private string _macroName = "";
    private string _status = "Describe the steps you want. Add a screen area for clicks or image recognition.";
    private bool _isBusy;
    private bool _reviewed;
    private bool _closed;
    private bool _saved;

    public AIMacroAuthoringViewModel(IAIMacroAuthoringService service, Func<MacroModel, CancellationToken, Task> saveDraft)
    { _service = service; _saveDraft = saveDraft; }
    public ObservableCollection<AIMacroDraftRow> Rows { get; } = [];
    public string ProviderSummary => _service.ProviderSummary;
    public string Prompt { get => _prompt; set { if (SetProperty(ref _prompt, value)) InvalidateDraft(); } }
    public string MacroName { get => _macroName; set { if (SetProperty(ref _macroName, value)) { Reviewed = false; OnPropertyChanged(nameof(CanSave)); } } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanGenerate)); OnPropertyChanged(nameof(CanSave)); } }
    public bool CanEdit => !IsBusy && !_closed && !_saved;
    public bool CanGenerate => CanEdit && !string.IsNullOrWhiteSpace(Prompt) && Prompt.Length <= 6000;
    public bool Reviewed { get => _reviewed; set { SetProperty(ref _reviewed, value); OnPropertyChanged(nameof(CanSave)); } }
    public bool CanSave => CanEdit && Reviewed && _draft is not null && !string.IsNullOrWhiteSpace(MacroName) && MacroName.Length <= 80 && !MacroName.Any(char.IsControl);
    public bool HasDraft => _draft is not null;
    public bool HasScreenshot => _snapshot is not null;
    public byte[]? PreviewJpeg => _snapshot?.PreviewJpeg;
    public string ScreenSummary => _snapshot is null ? "No screen attached · keyboard, waits and repeat actions only" : $"Captured area: {_snapshot.Area.Width} × {_snapshot.Area.Height} at ({_snapshot.Area.X}, {_snapshot.Area.Y})";
    public string DraftSummary => _draft is null ? "" : $"{Rows.Count} instructions · {(_draft.Macro.Settings.RepeatUntilStopped ? "Repeat until stopped" : "Run once")} · {_draft.Provider} / {_draft.Model} · Estimated AI cost ${_draft.EstimatedCostUsd:F4}";
    public string DraftDescription => _draft?.Macro.Description ?? "";
    public MacroModel? SavedMacro { get; private set; }

    public Task GenerateAsync(CancellationToken ct = default) => BeginAsync(async token =>
    {
        InvalidateDraft();
        Status = "Generating a macro draft… No input will be sent.";
        AIMacroDraft draft = await _service.GenerateAsync(Prompt, _snapshot, token);
        token.ThrowIfCancellationRequested();
        _draft = draft;
        MacroName = draft.Macro.Name;
        foreach (var row in draft.Rows) Rows.Add(row);
        OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(DraftDescription)); OnPropertyChanged(nameof(DraftSummary));
        Status = "Review every action below. Save opens the normal editor; you can adjust the actions before running.";
    }, ct, CanGenerate);

    public Task CaptureAreaAsync(Rectangle region, CancellationToken ct = default) => BeginAsync(async token =>
    {
        AIMacroAuthoringSnapshot captured = await _service.CaptureAsync(region, token);
        if (token.IsCancellationRequested) { captured.Dispose(); token.ThrowIfCancellationRequested(); }
        _snapshot?.Dispose();
        _snapshot = captured;
        InvalidateDraft(); NotifyScreen();
        Status = "Screen area attached. Only this image will be sent to your configured AI provider when you generate.";
    }, ct, CanEdit);

    public Task SaveAsync(CancellationToken ct = default) => BeginAsync(async token =>
    {
        if (_draft is null) return;
        MacroModel source = _draft.Macro;
        var macro = new MacroModel { Id = source.Id, Name = MacroName.Trim(), Description = source.Description, CreatedAt = source.CreatedAt, Settings = source.Settings, Steps = source.Steps };
        await _saveDraft(macro, token);
        // A completed atomic save is successful even if cancellation raced its completion.
        SavedMacro = macro;
        _saved = true;
        Status = "Saved to your library and opened in the editor. It has not been run.";
        OnPropertyChanged(nameof(SavedMacro));
    }, ct, CanSave);

    public void ClearScreen()
    {
        if (!CanEdit) return;
        _snapshot?.Dispose(); _snapshot = null;
        InvalidateDraft(); NotifyScreen();
    }
    public void Cancel() => _operation?.Cancel();
    public void ReportProblem(string message) => Status = message;
    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        await _activeTask;
        _snapshot?.Dispose(); _snapshot = null;
        _lifetime.Dispose();
    }
    private Task BeginAsync(Func<CancellationToken, Task> body, CancellationToken ct, bool permitted)
    {
        if (!permitted || IsBusy || _closed) return Task.CompletedTask;
        _activeTask = RunAsync(body, ct);
        return _activeTask;
    }
    private async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _operation = linked;
        IsBusy = true;
        try { await body(linked.Token); }
        catch (OperationCanceledException) { Status = "Cancelled. No macro was run."; }
        catch (Exception ex) { Status = "Could not finish: " + ex.Message; }
        finally { _operation = null; IsBusy = false; }
    }
    private void InvalidateDraft()
    {
        _draft = null; Rows.Clear(); Reviewed = false;
        OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(DraftDescription)); OnPropertyChanged(nameof(DraftSummary)); OnPropertyChanged(nameof(CanGenerate));
    }
    private void NotifyScreen()
    { OnPropertyChanged(nameof(HasScreenshot)); OnPropertyChanged(nameof(PreviewJpeg)); OnPropertyChanged(nameof(ScreenSummary)); }
}
