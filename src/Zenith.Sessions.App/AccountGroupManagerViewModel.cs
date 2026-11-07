using System.IO;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record AccountGroupChangeChoice(string Label, AccountGroupChangeKind Kind);

/// <summary>Owns one explicit metadata review/apply operation. No launch or credential authority.</summary>
public sealed class AccountGroupManagerViewModel : Observable, IAsyncDisposable
{
    private readonly Func<AccountGroupChange, CancellationToken, Task<AccountGroupChangePreview>> _review;
    private readonly Func<AccountGroupChangePreview, CancellationToken, Task<int>> _apply;
    private readonly Func<bool> _available;
    private readonly Func<IReadOnlyList<string>> _groups;
    private readonly IAccountGroupCatalog? _catalog;
    private readonly Func<Guid, CancellationToken, Task>? _showAccounts;
    private CancellationTokenSource? _active;
    private long _epoch;
    private bool _busy, _fenced, _disposed, _allowMerge;
    private string _source, _destination = "", _status = "Choose a group and review the change before applying it.";
    private AccountGroupChangePreview? _preview;
    private AccountGroupChangeChoice _choice;
    private bool _catalogReady, _publishingCatalog;
    private int _catalogPage, _catalogTotal;
    private long _catalogRevision;
    private string _catalogSearch = "", _loadedSearch = "", _newGroupName = "", _newColorHex = "", _selectedColorHex = "";
    private AccountGroupDefinition? _selectedGroup;
    private Task? _disposeTask;
    public static IReadOnlyList<AccountGroupChangeChoice> Choices { get; } = Array.AsReadOnly(new[]
    {
        new AccountGroupChangeChoice("Rename group", AccountGroupChangeKind.Rename),
        new AccountGroupChangeChoice("Remove group", AccountGroupChangeKind.Remove)
    });
    public IReadOnlyList<string> Groups { get; private set; }
    public string SourceName
    {
        get => _source;
        set
        {
            if (_publishingCatalog || !Set(ref _source, value ?? "")) return;
            if (_catalog is not null)
            {
                _selectedGroup = CatalogGroups.FirstOrDefault(group => string.Equals(group.Name, _source, StringComparison.OrdinalIgnoreCase));
                _selectedColorHex = _selectedGroup?.ColorHex ?? ""; Raise(nameof(SelectedGroup)); Raise(nameof(SelectedGroupSummary)); Raise(nameof(SelectedColorHex)); Raise(nameof(SelectedColorChoice));
            }
            InvalidateReview();
        }
    }
    public string DestinationName { get => _destination; set { if (Set(ref _destination, value ?? "")) InvalidateReview(); } }
    public AccountGroupChangeChoice Choice
    {
        get => _choice;
        set
        {
            if (value is null || !Choices.Contains(value) || !Set(ref _choice, value)) return;
            InvalidateReview(); Raise(nameof(IsRename)); Raise(nameof(DestinationLabel)); Raise(nameof(ApplyLabel));
        }
    }
    public bool IsRename => Choice.Kind == AccountGroupChangeKind.Rename;
    public string DestinationLabel => IsRename ? "New group name" : "Fallback group for accounts whose primary group is removed";
    public string ApplyLabel => IsRename ? "Apply rename" : "Remove group";
    public bool AllowMerge { get => _allowMerge; set { if (Set(ref _allowMerge, value)) InvalidateReview(); } }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshAvailability(); } }
    public bool CanEdit => !IsBusy && !_disposed && !_fenced && _available();
    public bool CanReview => CanEdit && !string.IsNullOrWhiteSpace(SourceName);
    public bool CanApply => CanEdit && _preview is { } p && (p.AffectedCount > 0 || p.SourceExists) &&
        (p.Change.Kind != AccountGroupChangeKind.Rename || !p.DestinationExists ||
         string.Equals(p.Change.SourceName, p.Change.DestinationName, StringComparison.OrdinalIgnoreCase) || p.Change.AllowMerge);
    public AccountGroupChangePreview? Preview => _preview;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand Review { get; }
    public AsyncCommand Apply { get; }
    public bool HasCatalog => _catalog is not null;
    public IReadOnlyList<AccountGroupDefinition> CatalogGroups { get; private set; } = Array.Empty<AccountGroupDefinition>();
    public IReadOnlyList<AccountGroupColorChoice> ColorChoices => GroupPresentation.Colors;
    public long CatalogRevision => _catalogRevision;
    public string CatalogSearch { get => _catalogSearch; set { if (Set(ref _catalogSearch, value ?? "")) { _catalogPage = 0; InvalidateReview(); Raise(nameof(CatalogPageLabel)); } } }
    public string NewGroupName { get => _newGroupName; set { if (Set(ref _newGroupName, value ?? "")) InvalidateReview(); } }
    public string NewColorHex { get => _newColorHex; set { if (Set(ref _newColorHex, value ?? "")) { Raise(nameof(NewColorChoice)); InvalidateReview(); } } }
    public string SelectedColorHex { get => _selectedColorHex; set { if (Set(ref _selectedColorHex, value ?? "")) { Raise(nameof(SelectedColorChoice)); InvalidateReview(); } } }
    public AccountGroupColorChoice? NewColorChoice { get => GroupPresentation.ColorChoice(NewColorHex); set { if (value is not null) NewColorHex = value.ColorHex ?? ""; } }
    public AccountGroupColorChoice? SelectedColorChoice { get => GroupPresentation.ColorChoice(SelectedColorHex); set { if (value is not null) SelectedColorHex = value.ColorHex ?? ""; } }
    public AccountGroupDefinition? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            // ItemsSource replacement can clear a ListBox selection. Retain the exact
            // selected ID until an explicit selection or authoritative lookup changes it.
            if (_publishingCatalog || value is null || !CatalogGroups.Contains(value) || !Set(ref _selectedGroup, value)) return;
            _source = value.Name; _selectedColorHex = value.ColorHex ?? "";
            Raise(nameof(SourceName)); Raise(nameof(SelectedColorHex)); Raise(nameof(SelectedColorChoice)); Raise(nameof(SelectedGroupSummary)); InvalidateReview();
        }
    }
    public string SelectedGroupSummary => SelectedGroup is { } group ? "Selected: " + group.Name : "Select a saved group to change its color or order.";
    public string CatalogPageLabel => !_catalogReady ? "Load groups to begin. Pages contain at most 100 groups; this is not a storage limit."
        : CatalogSearch != _loadedSearch ? "Search changed. Choose Search to load matching groups."
        : _catalogTotal == 0 ? "No matching groups. You can create an empty group below."
        : $"{_catalogPage * 100 + 1:N0}–{Math.Min(_catalogTotal, (_catalogPage + 1) * 100):N0} of {_catalogTotal:N0} groups · order is for group lists only";
    public bool CanRefreshCatalog => HasCatalog && CanEdit;
    public bool CanCreateGroup => CanRefreshCatalog && _catalogReady && !string.IsNullOrWhiteSpace(NewGroupName);
    public bool CanSaveGroupColor => CanRefreshCatalog && _catalogReady && SelectedGroup is not null;
    public bool CanShowAccounts => CanEdit && HasCatalog && SelectedGroup is not null && _showAccounts is not null;
    public bool CanPreviousCatalogPage => CanRefreshCatalog && _catalogReady && CatalogSearch == _loadedSearch && _catalogPage > 0;
    public bool CanNextCatalogPage => CanRefreshCatalog && _catalogReady && CatalogSearch == _loadedSearch && ((long)_catalogPage + 1) * 100 < _catalogTotal;
    public bool CanMoveGroupUp => CanSaveGroupColor && !KnownBoundary(first: true);
    public bool CanMoveGroupDown => CanSaveGroupColor && !KnownBoundary(first: false);
    public AsyncCommand RefreshCatalog { get; }
    public AsyncCommand PreviousCatalogPage { get; }
    public AsyncCommand NextCatalogPage { get; }
    public AsyncCommand CreateGroup { get; }
    public AsyncCommand SaveGroupColor { get; }
    public AsyncCommand MoveGroupUp { get; }
    public AsyncCommand MoveGroupDown { get; }
    public AsyncCommand ShowAccounts { get; }

    public AccountGroupManagerViewModel(string? selectedGroup, Func<IReadOnlyList<string>> groups, Func<bool> available,
        Func<AccountGroupChange, CancellationToken, Task<AccountGroupChangePreview>> review,
        Func<AccountGroupChangePreview, CancellationToken, Task<int>> apply, IAccountGroupCatalog? catalog = null,
        Func<Guid, CancellationToken, Task>? showAccounts = null)
    {
        _groups = groups; _available = available; _review = review; _apply = apply; _catalog = catalog; _showAccounts = showAccounts;
        Groups = Array.AsReadOnly(groups().Take(1000).ToArray());
        _source = selectedGroup ?? Groups.FirstOrDefault() ?? ""; _choice = Choices[0];
        Review = new(_ => ReviewAsync(), ReportFailure); Apply = new(_ => ApplyAsync(), ReportFailure);
        RefreshCatalog = new(_ => ReadCatalogAsync(0), ReportFailure);
        PreviousCatalogPage = new(_ => CanPreviousCatalogPage ? ReadCatalogAsync(_catalogPage - 1) : Task.CompletedTask, ReportFailure);
        NextCatalogPage = new(_ => CanNextCatalogPage ? ReadCatalogAsync(_catalogPage + 1) : Task.CompletedTask, ReportFailure);
        CreateGroup = new(_ => CreateGroupAsync(), ReportFailure); SaveGroupColor = new(_ => SaveGroupColorAsync(), ReportFailure);
        MoveGroupUp = new(_ => MoveGroupAsync(AccountGroupMoveDirection.Up), ReportFailure);
        MoveGroupDown = new(_ => MoveGroupAsync(AccountGroupMoveDirection.Down), ReportFailure);
        ShowAccounts = new(_ => ShowAccountsAsync(), ReportFailure);
    }
    private void InvalidateReview()
    {
        _epoch++; _active?.Cancel(); _preview = null; Raise(nameof(Preview));
        Status = "Review the current names and operation before applying."; RefreshAvailability();
    }
    private void RefreshAvailability()
    {
        Raise(nameof(CanEdit)); Raise(nameof(CanReview)); Raise(nameof(CanApply)); Raise(nameof(CanRefreshCatalog)); Raise(nameof(CanCreateGroup));
        Raise(nameof(CanSaveGroupColor)); Raise(nameof(CanPreviousCatalogPage)); Raise(nameof(CanNextCatalogPage)); Raise(nameof(CanMoveGroupUp)); Raise(nameof(CanMoveGroupDown));
        Raise(nameof(CanShowAccounts));
    }
    public void Fence(string reason)
    {
        if (_disposed) return;
        _fenced = true; _epoch++; _active?.Cancel(); _preview = null; Raise(nameof(Preview));
        Status = reason; RefreshAvailability();
    }
    private async Task ReviewAsync()
    {
        if (!CanReview) return;
        var change = new AccountGroupChange(Choice.Kind, SourceName, DestinationName, IsRename && AllowMerge).Validated();
        long epoch = ++_epoch; using var stop = new CancellationTokenSource(); _active = stop; IsBusy = true;
        _preview = null; Raise(nameof(Preview));
        Status = "Reviewing all matching accounts…";
        try
        {
            var preview = await _review(change, stop.Token);
            if (epoch != _epoch || stop.IsCancellationRequested || !_available() || _fenced || _disposed) return;
            _preview = preview; Raise(nameof(Preview));
            bool mergeBlocked = change.Kind == AccountGroupChangeKind.Rename && preview.DestinationExists
                && !string.Equals(change.SourceName, change.DestinationName, StringComparison.OrdinalIgnoreCase) && !change.AllowMerge;
            Status = mergeBlocked ? $"{preview.AffectedCount:N0} account(s) across the library. The new name is already in use. Enable merging and review again to combine these groups."
                : preview.AffectedCount == 0 ? preview.SourceExists
                ? "This saved group is empty. Its definition can still be renamed or removed; no accounts or saved sign-ins will change."
                : "No accounts currently belong to this group. Nothing will change."
                : $"{preview.AffectedCount:N0} account(s) across the library; {preview.PrimaryCount:N0} use this as their primary group. " +
                  (change.Kind == AccountGroupChangeKind.Remove ? "Only group memberships change. Accounts and saved sign-ins stay in place."
                  : preview.DestinationExists && !string.Equals(change.SourceName, change.DestinationName, StringComparison.OrdinalIgnoreCase) && !change.AllowMerge
                      ? "The new name is already in use. Enable merging and review again to combine these memberships."
                      : "Names and duplicate memberships will be updated together. Accounts and saved sign-ins stay in place.");
        }
        catch (OperationCanceledException)
        { if (epoch == _epoch && !_fenced && !_disposed) Status = "Review stopped. Review again before applying."; }
        catch (Exception error)
        { if (epoch == _epoch && !_fenced && !_disposed) ReportFailure(error); }
        finally
        { if (ReferenceEquals(_active, stop)) _active = null; IsBusy = false; }
    }
    private async Task ApplyAsync()
    {
        if (!CanApply || _preview is not { } expected) return;
        long epoch = ++_epoch; using var stop = new CancellationTokenSource(); _active = stop; IsBusy = true;
        _preview = null; Raise(nameof(Preview)); Status = "Saving group changes…";
        bool completed = false;
        try
        {
            int changed = await _apply(expected, stop.Token);
            completed = true;
            if (epoch != _epoch || stop.IsCancellationRequested || !_available() || _fenced || _disposed) return;
            if (_catalog is not null)
            {
                var target = await _catalog.FindGroupAsync(expected.Change.DestinationName, stop.Token);
                if (target is not null) ValidateDefinition(target);
                await ReloadCatalogAsync(new(CatalogSearch, _catalogPage), target?.Id, target?.Name ?? "", epoch, stop.Token);
                if (!Current(epoch, stop.Token)) return;
            }
            else
            {
                // The operation is complete. Rebinding the suggestion list can clear an old
                // ComboBox selection; retain the resulting group for the next explicit edit.
                _active = null;
                Groups = Array.AsReadOnly(_groups().Take(1000).ToArray()); Raise(nameof(Groups));
                SourceName = expected.Change.DestinationName;
            }
            Status = changed == 0 ? "Group operation completed. No stored group memberships needed changing."
                : $"Updated {changed:N0} account(s). Accounts and saved sign-ins were preserved.";
        }
        catch (OperationCanceledException)
        { if (epoch == _epoch && !_fenced && !_disposed) Status = "The change was stopped. Refresh the library before reviewing again; any committed change is preserved."; }
        catch (Exception error)
        {
            if (epoch == _epoch && !_fenced && !_disposed)
            {
                if (completed) { _catalogReady = false; Raise(nameof(CatalogPageLabel)); Status = "The group operation finished, but the list could not refresh. Saved metadata was preserved. Reload groups before another change."; }
                else ReportFailure(error);
            }
        }
        finally
        { if (ReferenceEquals(_active, stop)) _active = null; IsBusy = false; }
    }
    private bool Current(long epoch, CancellationToken ct) => epoch == _epoch && !ct.IsCancellationRequested && !_fenced && !_disposed && _available();
    private bool KnownBoundary(bool first)
    {
        if (_selectedGroup is null || _loadedSearch.Length != 0 || CatalogSearch != _loadedSearch) return false;
        int index = -1; for (int item = 0; item < CatalogGroups.Count; item++) if (CatalogGroups[item].Id == _selectedGroup.Id) { index = item; break; }
        return index >= 0 && (first ? _catalogPage == 0 && index == 0 : (long)_catalogPage * 100 + index == _catalogTotal - 1);
    }
    private Task ReadCatalogAsync(int page)
    {
        if (!CanRefreshCatalog) return Task.CompletedTask;
        var query = new AccountGroupCatalogQuery(CatalogSearch, page).Validated();
        Guid? selected = SelectedGroup?.Id; string name = SourceName;
        return RunCatalogAsync("Loading saved groups…", async (epoch, ct) =>
        {
            await ReloadCatalogAsync(query, selected, name, epoch, ct);
            if (Current(epoch, ct)) Status = "Group catalog loaded. Colors and ordering affect group lists only; they do not launch accounts.";
        });
    }
    private Task CreateGroupAsync()
    {
        if (!CanCreateGroup) return Task.CompletedTask;
        string name = AccountGroups.ValidateName(NewGroupName); string? color = GroupPresentation.ColorInput(NewColorHex); long revision = _catalogRevision;
        bool finished = false;
        return RunCatalogAsync("Creating an empty group…", async (epoch, ct) =>
        {
            var created = await _catalog!.CreateGroupAsync(name, color, revision, ct); finished = true; ValidateDefinition(created);
            if (created.Name != name || created.ColorHex != color) throw new InvalidDataException("The created group response is inconsistent. Reload the groups.");
            if (!Current(epoch, ct)) return;
            await ReloadCatalogAsync(new(CatalogSearch, _catalogPage), created.Id, created.Name, epoch, ct);
            if (Current(epoch, ct)) { _newGroupName = ""; Raise(nameof(NewGroupName)); Status = "Created empty group “" + created.Name + "”. No account memberships or saved sign-ins changed."; }
        }, () => finished);
    }
    private Task SaveGroupColorAsync()
    {
        if (!CanSaveGroupColor || SelectedGroup is not { } expected) return Task.CompletedTask;
        string? color = GroupPresentation.ColorInput(SelectedColorHex); long revision = _catalogRevision; bool finished = false;
        return RunCatalogAsync("Saving group color…", async (epoch, ct) =>
        {
            var saved = await _catalog!.SetGroupColorAsync(expected, color, revision, ct); finished = true; ValidateDefinition(saved);
            if (saved.Id != expected.Id || saved.Name != expected.Name || saved.ColorHex != color || saved.SortOrder != expected.SortOrder || saved.Revision < expected.Revision)
                throw new InvalidDataException("The saved group response is inconsistent. Reload the groups.");
            if (!Current(epoch, ct)) return;
            await ReloadCatalogAsync(new(CatalogSearch, _catalogPage), saved.Id, saved.Name, epoch, ct);
            if (Current(epoch, ct)) Status = expected.ColorHex == color ? "This color is already saved. Nothing changed." : "Group color saved. Account memberships and launch priorities were preserved.";
        }, () => finished);
    }
    private Task MoveGroupAsync(AccountGroupMoveDirection direction)
    {
        if (!(direction == AccountGroupMoveDirection.Up ? CanMoveGroupUp : CanMoveGroupDown) || SelectedGroup is not { } expected) return Task.CompletedTask;
        long revision = _catalogRevision; bool finished = false;
        return RunCatalogAsync("Saving group order…", async (epoch, ct) =>
        {
            var saved = await _catalog!.MoveGroupAsync(expected, direction, revision, ct); finished = true; ValidateDefinition(saved);
            if (saved.Id != expected.Id || saved.Name != expected.Name || saved.ColorHex != expected.ColorHex || saved.Revision < expected.Revision
                || saved.SortOrder != expected.SortOrder && saved.Revision <= expected.Revision)
                throw new InvalidDataException("The reordered group response is inconsistent. Reload the groups.");
            if (!Current(epoch, ct)) return;
            await ReloadCatalogAsync(new(CatalogSearch, _catalogPage), saved.Id, saved.Name, epoch, ct);
            if (Current(epoch, ct)) Status = saved.SortOrder == expected.SortOrder ? "No order change: this group is already at that boundary."
                : "Group order saved. This changes group lists only, not account priority, memberships or launch order.";
        }, () => finished);
    }
    private Task ShowAccountsAsync()
    {
        if (!CanShowAccounts || SelectedGroup is not { } expected) return Task.CompletedTask;
        return RunCatalogAsync("Opening the selected group’s accounts…", async (epoch, ct) =>
        {
            try { await _showAccounts!(expected.Id, ct); }
            catch (InvalidOperationException) { throw new ArgumentException("This saved group is no longer available. Reload groups before choosing it."); }
            if (Current(epoch, ct)) Status = "The accounts filter is updated. Close this dialog to view it; no accounts were launched.";
        });
    }
    private async Task RunCatalogAsync(string status, Func<long, CancellationToken, Task> work, Func<bool>? finished = null)
    {
        if (!CanRefreshCatalog) return;
        long epoch = ++_epoch; using var stop = new CancellationTokenSource(); _active = stop; IsBusy = true;
        _preview = null; Raise(nameof(Preview)); Status = status;
        try { await work(epoch, stop.Token); }
        catch (OperationCanceledException)
        { if (epoch == _epoch && !_fenced && !_disposed) Status = "Group operation stopped. Reload groups to review any completed metadata changes."; }
        catch (Exception error)
        {
            if (epoch == _epoch && !_fenced && !_disposed)
            {
                _catalogReady = false; Raise(nameof(CatalogPageLabel));
                if (finished?.Invoke() == true) Status = "The group operation finished, but the list could not refresh. Saved metadata was preserved. Reload groups before another change.";
                else ReportFailure(error);
            }
        }
        finally
        {
            if (stop.IsCancellationRequested) { _catalogReady = false; Raise(nameof(CatalogPageLabel)); }
            if (ReferenceEquals(_active, stop)) _active = null; IsBusy = false;
        }
    }
    private async Task ReloadCatalogAsync(AccountGroupCatalogQuery input, Guid? selectedId, string selectedName, long epoch, CancellationToken ct)
    {
        var catalog = _catalog ?? throw new InvalidOperationException("This provider does not support a group catalog.");
        var query = input.Validated(); var page = ValidatePage(await catalog.ReadGroupCatalogAsync(query, ct), query);
        if (!Current(epoch, ct)) return;
        if (page.Items.Count == 0 && page.Total > 0 && query.Page > 0)
        {
            query = query with { Page = Math.Min(query.Page, (page.Total - 1) / query.PageSize) };
            page = ValidatePage(await catalog.ReadGroupCatalogAsync(query, ct), query);
        }
        AccountGroupDefinition? selected = null;
        if (selectedId is { } id)
        {
            selected = page.Items.FirstOrDefault(group => group.Id == id) ?? await catalog.FindGroupAsync(id, ct);
            if (selected is not null && selected.Id != id) throw new InvalidDataException("The selected group response is inconsistent. Reload the groups.");
        }
        else if (!string.IsNullOrWhiteSpace(selectedName))
        {
            string? lookup = null; try { lookup = AccountGroups.ValidateName(selectedName); } catch (ArgumentException) { /* Retain an unfinished name draft; do not query it. */ }
            if (lookup is not null)
            {
                selected = page.Items.FirstOrDefault(group => string.Equals(group.Name, lookup, StringComparison.OrdinalIgnoreCase)) ?? await catalog.FindGroupAsync(lookup, ct);
                if (selected is not null && !string.Equals(selected.Name, lookup, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The selected group response is inconsistent. Reload the groups.");
            }
        }
        else selected = page.Items.FirstOrDefault();
        if (selected is not null) ValidateDefinition(selected);
        if (!Current(epoch, ct)) return;
        _publishingCatalog = true;
        try
        {
            _catalogReady = true; _catalogPage = query.Page; _catalogTotal = page.Total; _catalogRevision = page.CatalogRevision;
            _catalogSearch = _loadedSearch = query.Search; _selectedGroup = selected; _selectedColorHex = selected?.ColorHex ?? "";
            CatalogGroups = page.Items; Groups = Array.AsReadOnly(page.Items.Select(group => group.Name).ToArray());
            Raise(nameof(CatalogGroups)); Raise(nameof(Groups));
            _source = selected?.Name ?? selectedName;
            Raise(nameof(SourceName)); Raise(nameof(SelectedGroup)); Raise(nameof(SelectedGroupSummary)); Raise(nameof(SelectedColorHex)); Raise(nameof(SelectedColorChoice));
            Raise(nameof(CatalogSearch)); Raise(nameof(CatalogRevision)); Raise(nameof(CatalogPageLabel)); RefreshAvailability();
        }
        finally { _publishingCatalog = false; }
    }
    private static AccountGroupCatalogPage ValidatePage(AccountGroupCatalogPage page, AccountGroupCatalogQuery query)
    {
        if (page is null || page.Items is null || page.Page != query.Page || page.PageSize != query.PageSize || page.Items.Count > query.PageSize
            || page.Total < page.Items.Count || page.Total < 0 || page.CatalogRevision < 1) throw new InvalidDataException("The group page is invalid. Reload the groups.");
        var items = new AccountGroupDefinition[page.Items.Count]; var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < items.Length; index++)
        {
            var group = page.Items[index]; ValidateDefinition(group);
            if (!ids.Add(group.Id) || !names.Add(group.Name)) throw new InvalidDataException("The group page contains duplicate groups. Reload the groups.");
            items[index] = group;
        }
        return page with { Items = Array.AsReadOnly(items) };
    }
    private static void ValidateDefinition(AccountGroupDefinition group)
    {
        try { if (group is null || group.Validated() != group) throw new InvalidDataException("The saved group is invalid. Reload the groups."); }
        catch (ArgumentException) { throw new InvalidDataException("The saved group is invalid. Reload the groups."); }
    }
    private void ReportFailure(Exception error)
    {
        if (_fenced || _disposed) return;
        _preview = null; Raise(nameof(Preview));
        Status = error is ArgumentException or RevisionConflictException or InvalidDataException or AccountGroupCatalogConflictException ? error.Message
            : "The group change could not finish. Existing files were preserved. Refresh the library and review again.";
        RefreshAvailability();
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        Fence("Group review closed. Refresh the library to inspect any completed changes."); _disposed = true;
        await Task.WhenAll(Review.Execution, Apply.Execution, RefreshCatalog.Execution, PreviousCatalogPage.Execution, NextCatalogPage.Execution,
            CreateGroup.Execution, SaveGroupColor.Execution, MoveGroupUp.Execution, MoveGroupDown.Execution, ShowAccounts.Execution); RefreshAvailability();
    }
}
