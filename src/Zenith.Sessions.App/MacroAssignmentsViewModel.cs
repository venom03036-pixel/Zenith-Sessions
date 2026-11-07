using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record MacroAssignmentTargetChoice(MacroAssignmentTarget Target, string Name);

/// <summary>Explicit metadata assignments only. Owns pending work, borrows the store and host, and never starts a macro.</summary>
public sealed class MacroAssignmentsViewModel : Observable, IAsyncDisposable
{
    private readonly IMacroAssignmentStore _store;
    private readonly MacroHostCatalogViewModel _host;
    private readonly Func<MacroAssignmentTargetKind, MacroAssignmentTargetChoice?> _choose;
    private readonly Func<MacroAssignmentTarget, CancellationToken, Task<string?>> _resolve;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failure;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _active = Task.CompletedTask;
    private Task? _dispose;
    private Work? _work;
    private long _generation, _selectionEpoch;
    private bool _busy, _stale, _hasSnapshot, _closing, _confirmRemoval;
    private int _offset, _total;
    private int? _next;
    private MacroAssignmentStamp _stamp = MacroAssignmentStamp.Empty;
    private MacroAssignment? _selected;
    private MacroAssignmentTargetChoice? _chosen;
    private IReadOnlyList<MacroAssignment> _assignments = Array.Empty<MacroAssignment>();
    private string _status = "Refresh assignments, choose a saved target and select a fresh host macro. Execution remains unavailable.";

    public IReadOnlyList<MacroAssignment> Assignments => _assignments;
    public MacroAssignmentTargetChoice? ChosenTarget => _chosen;
    public MacroAssignment? SelectedAssignment
    {
        get => _selected;
        set
        {
            if (value is not null && !Assignments.Any(item => ReferenceEquals(item, value))) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _selected = value; _selectionEpoch++; _confirmRemoval = false;
            Raise(nameof(SelectedAssignment)); Raise(nameof(ConfirmRemoval)); RefreshAvailability();
        }
    }
    public bool ConfirmRemoval
    { get => _confirmRemoval; set { if (Set(ref _confirmRemoval, value)) RefreshAvailability(); } }
    public bool IsBusy => _busy;
    public bool IsStale => _stale;
    public bool CanManage => CanStart() && !_busy;
    public bool CanAssign => CanManage && _hasSnapshot && !_stale && ChosenTarget is not null && _host.CaptureSelection() is not null;
    public bool CanRemove => CanManage && _hasSnapshot && !_stale && ConfirmRemoval && SelectedAssignment is { } selected
        && Assignments.Any(item => ReferenceEquals(item, selected));
    public bool CanPrevious => CanManage && _hasSnapshot && !_stale && _offset > 0;
    public bool CanNext => CanManage && _hasSnapshot && !_stale && _next is not null;
    public string Status => _status;
    public string PageSummary => !_hasSnapshot ? "No assignment snapshot reviewed."
        : $"{(_total == 0 ? 0 : _offset + 1)}–{_offset + Assignments.Count} of {_total} assignments" + (_stale ? " · last received, stale" : " · saved references, targets unchecked");
    public string TargetSummary => ChosenTarget is { } choice
        ? $"{choice.Target.Kind}: {choice.Name} · last selected name; membership is resolved at future start"
        : "Choose the selected account, current saved group or selected recipient bundle explicitly.";
    public Task ActiveOperation => _active;
    public Command ChooseAccount { get; }
    public Command ChooseGroup { get; }
    public Command ChooseBundle { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Previous { get; }
    public AsyncCommand Next { get; }
    public AsyncCommand Assign { get; }
    public AsyncCommand Remove { get; }
    public Command Cancel { get; }

    public MacroAssignmentsViewModel(IMacroAssignmentStore store, MacroHostCatalogViewModel host,
        Func<MacroAssignmentTargetKind, MacroAssignmentTargetChoice?> choose,
        Func<MacroAssignmentTarget, CancellationToken, Task<string?>> resolve,
        Func<bool> canUse, Action<Exception>? failure = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store)); _host = host ?? throw new ArgumentNullException(nameof(host));
        _choose = choose ?? throw new ArgumentNullException(nameof(choose)); _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _canUse = canUse ?? throw new ArgumentNullException(nameof(canUse)); _failure = failure ?? (_ => { });
        ChooseAccount = new(_ => ChooseTarget(MacroAssignmentTargetKind.Account));
        ChooseGroup = new(_ => ChooseTarget(MacroAssignmentTargetKind.Group)); ChooseBundle = new(_ => ChooseTarget(MacroAssignmentTargetKind.Bundle));
        Refresh = new(_ => RefreshAsync(), ReportFailure); Previous = new(_ => PreviousAsync(), ReportFailure);
        Next = new(_ => NextAsync(), ReportFailure); Assign = new(_ => AssignAsync(), ReportFailure); Remove = new(_ => RemoveAsync(), ReportFailure);
        Cancel = new(_ => CancelWork()); _host.PropertyChanged += HostChanged;
    }

    private bool CanStart() => !_closing && !_lifetime.IsCancellationRequested && _canUse();
    private void HostChanged(object? sender, PropertyChangedEventArgs args) => RefreshAvailability();
    public void RefreshAvailability()
    { Raise(nameof(CanManage)); Raise(nameof(CanAssign)); Raise(nameof(CanRemove)); Raise(nameof(CanPrevious)); Raise(nameof(CanNext)); }

    public void ChooseTarget(MacroAssignmentTargetKind kind)
    {
        if (!CanStart()) return;
        long generation = _generation;
        try
        {
            var choice = _choose(kind);
            if (!CanStart() || generation != _generation) return;
            if (choice is null) { _status = "No saved target is selected for that choice. Existing references were kept."; Publish(); return; }
            choice.Target.Validated();
            if (choice.Target.Kind != kind) throw new ArgumentException();
            // The same rule the domain applies before writing, so an unstorable name is explained now instead of failing at Assign.
            if (MacroAssignment.CheckDisplayName(choice.Name) is var problem and not MacroAssignmentNameProblem.None)
            {
                _status = $"That target's saved name {NameReason(problem)}, so it cannot be stored as an assignment hint. Rename it, then choose again. Existing references were kept.";
                Publish(); return;
            }
            _chosen = choice; _status = "Target reference captured. Select a fresh saved host macro; assigning never starts playback.";
            Publish();
        }
        catch (Exception error)
        {
            if (generation != _generation || !CanStart()) return;
            _status = "That target choice is unavailable or invalid. Existing references were kept.";
            ReportFailure(error); Publish();
        }
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        var selected = SelectedAssignment?.Target;
        return StartAsync("Reading saved assignments…", work => ReadPageAsync(work, new(), selected,
            "Assignments refreshed. Saved references are unchecked; off-page host macros are unreviewed, not missing."), ct);
    }
    public Task PreviousAsync(CancellationToken ct = default)
    {
        if (!CanPrevious) return Task.CompletedTask;
        var query = new MacroAssignmentQuery(Math.Max(0, _offset - MacroAssignment.MaximumPageSize), MacroAssignment.MaximumPageSize, _stamp);
        var selected = SelectedAssignment?.Target;
        return StartAsync("Reading the previous assignment page…", work => ReadPageAsync(work, query, selected,
            "Previous assignment page loaded. Saved references remain unchecked."), ct);
    }
    public Task NextAsync(CancellationToken ct = default)
    {
        if (!CanNext) return Task.CompletedTask;
        var query = new MacroAssignmentQuery(_next!.Value, MacroAssignment.MaximumPageSize, _stamp);
        var selected = SelectedAssignment?.Target;
        return StartAsync("Reading the next assignment page…", work => ReadPageAsync(work, query, selected,
            "Next assignment page loaded. Saved references remain unchecked."), ct);
    }

    public Task AssignAsync(CancellationToken ct = default)
    {
        if (!CanAssign || ChosenTarget is not { } target || _host.CaptureSelection() is not { } macro) return Task.CompletedTask;
        var stamp = _stamp;
        return StartAsync("Reviewing the captured target and saved macro…", async work =>
        {
            string? currentName = await _resolve(target.Target, work.Stop.Token); Ensure(work);
            if (currentName is null)
            { _status = "The captured target no longer exists. Existing assignment references were kept for review."; Publish(); return; }
            if (UnstorableNameStatus(currentName, macro.Macro.Name) is { } refusal) { _status = refusal; Publish(); return; }
            var value = new MacroAssignment(target.Target, currentName, macro.Macro.Id, macro.Macro.Name).Validated();
            if (!await _host.RevalidateSelectionAsync(macro, work.Stop.Token))
            { Ensure(work); _status = "The captured saved macro could not be revalidated. Refresh host macros and choose again."; Publish(); return; }
            Ensure(work); work.WriteIssued = true;
            var written = ValidateWrite(await _store.SaveAsync(value, stamp, work.Stop.Token), stamp);
            work.Committed = true; work.Changed = written.State == MacroAssignmentWriteState.Applied; Ensure(work);
            await ReadPageAsync(work, new(Expected: written.Stamp), value.Target, work.Changed
                ? "Assignment saved for the captured target. No macro was started."
                : "The assignment already matches. Nothing changed and no macro was started.");
        }, ct);
    }

    public Task RemoveAsync(CancellationToken ct = default)
    {
        if (!CanRemove || SelectedAssignment is not { } selected) return Task.CompletedTask;
        var stamp = _stamp;
        return StartAsync("Removing the confirmed assignment reference…", async work =>
        {
            Ensure(work); work.WriteIssued = true;
            var written = ValidateWrite(await _store.RemoveAsync(selected.Target, stamp, work.Stop.Token), stamp);
            work.Committed = true; work.Changed = written.State == MacroAssignmentWriteState.Applied; Ensure(work);
            await ReadPageAsync(work, new(Expected: written.Stamp), selected.Target, work.Changed
                ? "Assignment removed. The saved target and macro were kept."
                : "That assignment was already absent. Nothing changed.");
        }, ct);
    }

    private Task StartAsync(string status, Func<Work, Task> body, CancellationToken ct)
    {
        if (!CanManage) return Task.CompletedTask;
        var work = new Work(++_generation, _selectionEpoch, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct));
        _work = work; _busy = true; _status = status;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = _active = RunAsync(work, body, published.Task);
        try { Raise(nameof(IsBusy)); Raise(nameof(Status)); RefreshAvailability(); }
        finally { published.TrySetResult(); }
        return active;
    }
    private async Task RunAsync(Work work, Func<Work, Task> body, Task published)
    {
        await published;
        try { Ensure(work); await body(work); }
        catch (OperationCanceledException)
        {
            if (work.Generation == _generation && !_closing)
            { _stale = true; _status = work.WriteIssued ? "Assignment request cancelled. A metadata change may already be saved; refresh assignments to review."
                : "Assignment request cancelled. Refresh explicitly when the controls are available."; Publish(); }
        }
        catch (Exception error)
        {
            if (work.Generation == _generation && !_closing)
            {
                _stale = true;
                _status = !CanStart() ? "Assignment request stopped because the controls are unavailable. Refresh explicitly when ready."
                    : work.Committed ? work.Changed ? "The assignment change was saved, but the updated list could not be read. Refresh assignments; do not assume the write failed."
                        : "The assignment needed no change, but the updated list could not be read. Refresh assignments before another edit."
                    : FailureMessage(error, work.WriteIssued);
                ReportFailure(error); Publish();
            }
        }
        finally
        {
            if (ReferenceEquals(_work, work)) { _work = null; _busy = false; Raise(nameof(IsBusy)); RefreshAvailability(); }
            work.Stop.Dispose();
        }
    }
    private void Ensure(Work work)
    { work.Stop.Token.ThrowIfCancellationRequested(); if (work.Generation != _generation || !CanStart()) throw new OperationCanceledException(); }

    private async Task ReadPageAsync(Work work, MacroAssignmentQuery query, MacroAssignmentTarget? preferred, string status)
    {
        Ensure(work); var page = (await _store.ReadAsync(query, work.Stop.Token)).Validated(query); Ensure(work);
        if (work.SelectionEpoch != _selectionEpoch) preferred = SelectedAssignment?.Target;
        _assignments = page.Items; _stamp = page.Stamp; _offset = query.Offset; _total = page.Total; _next = page.NextOffset;
        _hasSnapshot = true; _stale = false; _confirmRemoval = false;
        _selected = Assignments.FirstOrDefault(item => item.Target == preferred); _status = status; Publish();
    }
    private static MacroAssignmentWriteResult ValidateWrite(MacroAssignmentWriteResult result, MacroAssignmentStamp expected)
    {
        if (result is null || !Enum.IsDefined(result.State) || result.Stamp is null) throw new InvalidDataException();
        result.Stamp.Validated();
        if (result.State == MacroAssignmentWriteState.Unchanged ? result.Stamp != expected
            : result.Stamp == MacroAssignmentStamp.Empty || expected != MacroAssignmentStamp.Empty
                && (result.Stamp.Generation != expected.Generation || result.Stamp.Revision <= expected.Revision)) throw new InvalidDataException();
        return result;
    }
    // Names are user data that may hold credential-like text, so these explanations never repeat them.
    private static string NameReason(MacroAssignmentNameProblem problem) => problem switch
    {
        MacroAssignmentNameProblem.LinkOrCredential => "looks like a link, access code or credential",
        MacroAssignmentNameProblem.InvalidUnicode => "contains invalid Unicode text",
        _ => $"is empty, longer than {MacroAssignment.MaximumNameLength} characters or contains line breaks or control characters"
    };
    private static string? UnstorableNameStatus(string targetName, string macroName)
    {
        if (MacroAssignment.CheckDisplayName(targetName) is var target and not MacroAssignmentNameProblem.None)
            return $"The captured target's current name {NameReason(target)}, so it cannot be stored as an assignment hint. Rename the target, then assign again. Nothing was saved and existing references were kept.";
        if (MacroAssignment.CheckDisplayName(macroName) is var macro and not MacroAssignmentNameProblem.None)
            return $"The selected saved macro's name {NameReason(macro)}, so it cannot be stored as an assignment hint. Rename it in Personal Zenith, refresh host macros and select it again. Nothing was saved and existing references were kept.";
        return null;
    }
    private static string FailureMessage(Exception error, bool issued) => error switch
    {
        MacroAssignmentConflictException => "Assignments changed elsewhere. Existing rows were kept as stale; refresh before editing again.",
        MacroAssignmentBusyException => "Assignment storage is busy. Existing rows were kept; refresh after its current work finishes.",
        MacroAssignmentCapacityException => "The assignment storage budget is full. Existing references were kept; saved account profiles are not limited.",
        MacroAssignmentStorageException { Problem: MacroAssignmentStorageProblem.OutcomeUnknown } => "The assignment write outcome is unknown. It may already be saved; refresh before retrying.",
        MacroAssignmentStorageException { Problem: MacroAssignmentStorageProblem.UnsupportedVersion } => "Assignment storage needs a compatible application version. Existing files and rows were preserved.",
        MacroAssignmentStorageException { Problem: MacroAssignmentStorageProblem.UnsafePath } => "Assignment storage could not use its configured location. Existing files and rows were preserved.",
        MacroAssignmentStorageException => "Assignment storage could not be confirmed. Existing files and rows were preserved for review.",
        _ when issued => "The assignment write could not be confirmed. It may already be saved; refresh before retrying.",
        _ => "The assignment request could not be confirmed. Existing rows were kept; refresh and review before continuing."
    };
    private void ReportFailure(Exception error)
    {
        // Diagnostic observers receive a fixed description with no provider message, inner exception or path.
        Exception safe = error switch
        {
            MacroAssignmentConflictException => new MacroAssignmentConflictException(), MacroAssignmentBusyException => new MacroAssignmentBusyException(),
            MacroAssignmentCapacityException => new MacroAssignmentCapacityException(), MacroAssignmentStorageException storage => new MacroAssignmentStorageException(storage.Problem),
            _ => new InvalidOperationException("The macro assignment request could not be confirmed.")
        };
        try { _failure(safe); } catch { }
    }
    private void Publish()
    {
        Raise(nameof(Assignments)); Raise(nameof(SelectedAssignment)); Raise(nameof(ConfirmRemoval)); Raise(nameof(ChosenTarget));
        Raise(nameof(Status)); Raise(nameof(IsStale)); Raise(nameof(PageSummary)); Raise(nameof(TargetSummary)); RefreshAvailability();
    }
    public void CancelWork()
    {
        if (_closing) return;
        _generation++; _stale = true;
        _status = _work?.WriteIssued == true ? "Assignment request stopped. A metadata change may already be saved; refresh assignments to review."
            : "Assignment request stopped. Refresh explicitly when ready.";
        _work?.Stop.Cancel(); Publish();
    }
    public ValueTask DisposeAsync()
    {
        if (_dispose is not null) return new(_dispose);
        _closing = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispose = DisposeCoreAsync(_active, published.Task);
        _generation++; _stale = true; _status = "Assignment controls closed. Saved references were kept; no work will resume automatically.";
        _lifetime.Cancel(); _work?.Stop.Cancel();
        try { Publish(); } finally { published.TrySetResult(); }
        return new(_dispose);
    }
    private async Task DisposeCoreAsync(Task active, Task published)
    {
        await published;
        try { await active; }
        finally { _host.PropertyChanged -= HostChanged; _lifetime.Dispose(); RefreshAvailability(); }
    }
    private sealed class Work(long generation, long selectionEpoch, CancellationTokenSource stop)
    {
        public long Generation { get; } = generation;
        public long SelectionEpoch { get; } = selectionEpoch;
        public CancellationTokenSource Stop { get; } = stop;
        public bool WriteIssued, Committed, Changed;
    }
}
