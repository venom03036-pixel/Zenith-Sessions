using System.Collections.ObjectModel;
using MacroSuite.App.Common;
using MacroSuite.Core.Usage;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// Live AI usage analytics: billed tokens, spend, and cap remaining from persisted records.
/// </summary>
public sealed class UsageDashboardViewModel : ObservableObject, IDisposable
{
    private readonly UsageTracker _usageTracker;
    private readonly IUsageRepository _repository;
    private readonly Func<decimal, decimal, bool>? _persistCaps;
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _refreshLock = new();
    private Task _refreshTask = Task.CompletedTask;
    private bool _refreshRequested;
    private bool _disposed;

    private decimal _dailySpendCap;
    private decimal _monthlySpendCap;
    private decimal _totalSpentToday;
    private decimal _totalSpentMonth;
    private int _tokensInToday;
    private int _tokensOutToday;
    private int _requestsToday;
    private string _statusMessage = string.Empty;
    private string _emptyTrailMessage = "No AI requests recorded yet. Estimates appear here after you use AI vision or generate a macro.";
    private bool _isRefreshing;

    public ObservableCollection<UsageRecord> RecentRecords { get; } = new();

    public decimal DailySpendCap
    {
        get => _dailySpendCap;
        set => SetProperty(ref _dailySpendCap, Math.Max(0m, value));
    }

    public decimal MonthlySpendCap
    {
        get => _monthlySpendCap;
        set => SetProperty(ref _monthlySpendCap, Math.Max(0m, value));
    }

    public decimal TotalSpentToday
    {
        get => _totalSpentToday;
        private set => SetProperty(ref _totalSpentToday, value);
    }

    public decimal TotalSpentMonth
    {
        get => _totalSpentMonth;
        private set => SetProperty(ref _totalSpentMonth, value);
    }

    public int TokensInToday
    {
        get => _tokensInToday;
        private set => SetProperty(ref _tokensInToday, value);
    }

    public int TokensOutToday
    {
        get => _tokensOutToday;
        private set => SetProperty(ref _tokensOutToday, value);
    }

    public int RequestsToday
    {
        get => _requestsToday;
        private set => SetProperty(ref _requestsToday, value);
    }

    public string TokensTodayDisplay => $"{TokensInToday} in / {TokensOutToday} out";

    public decimal RemainingDailyBudget => Math.Max(0m, (_usageTracker.DailySpendCapUsd ?? DailySpendCap) - TotalSpentToday);

    public decimal RemainingMonthlyBudget => Math.Max(0m, (_usageTracker.MonthlySpendCapUsd ?? MonthlySpendCap) - TotalSpentMonth);

    public string EmptyTrailMessage
    {
        get => _emptyTrailMessage;
        private set => SetProperty(ref _emptyTrailMessage, value);
    }

    public bool HasRecords => RecentRecords.Count > 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCapsCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public UsageDashboardViewModel(
        UsageTracker usageTracker,
        IUsageRepository repository,
        Func<decimal, decimal, bool>? persistCaps = null)
    {
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _persistCaps = persistCaps;

        DailySpendCap = _usageTracker.DailySpendCapUsd ?? 5.00m;
        MonthlySpendCap = _usageTracker.MonthlySpendCapUsd ?? 50.00m;
        if (!_usageTracker.DailySpendCapUsd.HasValue)
            _usageTracker.DailySpendCapUsd = DailySpendCap;
        if (!_usageTracker.MonthlySpendCapUsd.HasValue)
            _usageTracker.MonthlySpendCapUsd = MonthlySpendCap;

        SaveCapsCommand = new RelayCommand(SaveCaps);
        RefreshCommand = new RelayCommand(() => { _ = RefreshDataAsync(); });

        _usageTracker.UsageRecorded += OnUsageRecorded;
        _ = RefreshDataAsync();
    }

    private void OnUsageRecorded(UsageRecord record)
    {
        _ = record;
        _ = RefreshDataAsync();
    }

    private void SaveCaps()
    {
        if (_disposed) return;
        try
        {
            if (_persistCaps != null && !_persistCaps(DailySpendCap, MonthlySpendCap))
            {
                StatusMessage = "Could not save budgets. The previous limits are still active.";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save budgets. The previous limits are still active. {ex.Message}";
            return;
        }
        _usageTracker.DailySpendCapUsd = DailySpendCap;
        _usageTracker.MonthlySpendCapUsd = MonthlySpendCap;
        OnPropertyChanged(nameof(RemainingDailyBudget));
        OnPropertyChanged(nameof(RemainingMonthlyBudget));
        StatusMessage = _persistCaps == null
            ? "Daily and monthly limits saved for this session."
            : "Daily and monthly limits saved. Daily resets at UTC midnight; monthly resets on the 1st UTC.";
    }

    public Task RefreshDataAsync()
    {
        lock (_refreshLock)
        {
            if (_disposed) return Task.CompletedTask;
            _refreshRequested = true;
            if (!_isRefreshing)
            {
                _isRefreshing = true;
                // One owned worker coalesces refresh requests; shutdown cancels and joins it.
                CancellationToken ct = _lifetime.Token;
                _refreshTask = Task.Run(() => RefreshLoopAsync(ct), ct);
            }
            return _refreshTask;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        bool finishedNormally = false;
        try
        {
            while (true)
            {
                lock (_refreshLock)
                {
                    if (_disposed || !_refreshRequested)
                    {
                        _isRefreshing = false;
                        finishedNormally = true;
                        return;
                    }
                    _refreshRequested = false;
                }
                await RefreshOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Usage dashboard refresh failed: {ex}");
            try
            {
                await ApplyOnUiAsync(() => StatusMessage = $"Could not refresh usage history: {ex.Message}", ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception updateError)
            {
                System.Diagnostics.Trace.TraceError($"Usage dashboard status update failed: {updateError}");
            }
        }
        finally
        {
            if (!finishedNormally)
            {
                lock (_refreshLock) _isRefreshing = false;
            }
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var startOfDay = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        UsageTotals day = await _usageTracker.GetTotalsAsync(startOfDay, ct).ConfigureAwait(false);
        UsageTotals month = await _usageTracker.GetTotalsAsync(startOfMonth, ct).ConfigureAwait(false);
        var recent = await _repository.GetRecentAsync(50, ct).ConfigureAwait(false);

        await ApplyOnUiAsync(() =>
        {
            TotalSpentToday = day.CostUsd;
            TotalSpentMonth = month.CostUsd;
            TokensInToday = day.InputTokens;
            TokensOutToday = day.OutputTokens;
            RequestsToday = day.RequestCount;
            OnPropertyChanged(nameof(TokensTodayDisplay));
            OnPropertyChanged(nameof(RemainingDailyBudget));
            OnPropertyChanged(nameof(RemainingMonthlyBudget));

            RecentRecords.Clear();
            foreach (var r in recent)
                RecentRecords.Add(r);

            EmptyTrailMessage = RecentRecords.Count == 0
                ? "No AI requests recorded yet. Estimates appear here after you use AI vision or generate a macro."
                : string.Empty;
            OnPropertyChanged(nameof(HasRecords));
        }, ct).ConfigureAwait(false);
    }

    private Task ApplyOnUiAsync(Action update, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_syncContext == null)
        {
            lock (_refreshLock)
            {
                if (!_disposed) update();
            }
            return Task.CompletedTask;
        }
        return PostUpdateAsync(update, ct);
    }

    private async Task PostUpdateAsync(Action update, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        _syncContext!.Post(_ =>
        {
            try
            {
                lock (_refreshLock)
                {
                    if (!_disposed && !ct.IsCancellationRequested) update();
                }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);
        await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task pending;
        lock (_refreshLock)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshRequested = false;
            pending = _refreshTask;
        }
        _usageTracker.UsageRecorded -= OnUsageRecorded;
        _lifetime.Cancel();
        try { pending.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally { _lifetime.Dispose(); }
    }
}
