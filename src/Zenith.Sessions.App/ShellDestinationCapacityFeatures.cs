using System.Collections.Specialized;
using System.ComponentModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private readonly HashSet<AccountRow> _capacitySubscriptions = [];
    private string _requestedCapacity = "Choose recipients and explicitly use a public server to compare its reported capacity.";
    public string RequestedCapacitySummary { get => _requestedCapacity; private set => Set(ref _requestedCapacity, value); }
    public int DestinationRecipientCount
    {
        get
        {
            int checkedCount = 0;
            foreach (var row in Accounts) if (row.IsChecked) checkedCount++;
            return checkedCount > 0 ? checkedCount : SelectedAccount is not null ? 1 : 0;
        }
    }
    private void InitializeDestinationCapacity()
    {
        Accounts.CollectionChanged += CapacityAccountsChanged;
        ServerBrowser.PropertyChanged += CapacityDestinationChanged;
        foreach (var row in Accounts) SubscribeCapacityRow(row);
    }
    private void SubscribeCapacityRow(AccountRow row)
    { if (_capacitySubscriptions.Add(row)) row.PropertyChanged += CapacityRecipientChanged; }
    private void CapacityAccountsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in _capacitySubscriptions) row.PropertyChanged -= CapacityRecipientChanged;
            _capacitySubscriptions.Clear(); foreach (var row in Accounts) SubscribeCapacityRow(row);
        }
        else
        {
            if (args.OldItems is not null) foreach (AccountRow row in args.OldItems)
                if (_capacitySubscriptions.Remove(row)) row.PropertyChanged -= CapacityRecipientChanged;
            if (args.NewItems is not null) foreach (AccountRow row in args.NewItems) SubscribeCapacityRow(row);
        }
        RefreshDestinationCapacity();
    }
    private void CapacityRecipientChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(AccountRow.IsChecked)) RefreshDestinationCapacity(); }
    private void CapacityDestinationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ServerBrowserViewModel.CanUseDestination) or nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.RequestedServerObservation))
            RefreshDestinationCapacity();
    }
    private void RefreshDestinationCapacity()
    {
        Raise(nameof(DestinationRecipientCount));
        RequestedCapacitySummary = IsLocked || _shuttingDown
            ? "Capacity advice is unavailable while controls are locked or stopping. No seats are reserved."
            : ServerBrowser.HasDestinationDraft
                ? "Use or discard the pending destination before comparing reported capacity."
                : ServerBrowser.AssessRequestedCapacity(DestinationRecipientCount).Explanation;
    }
    private void DisposeDestinationCapacity()
    {
        Accounts.CollectionChanged -= CapacityAccountsChanged;
        ServerBrowser.PropertyChanged -= CapacityDestinationChanged;
        foreach (var row in _capacitySubscriptions) row.PropertyChanged -= CapacityRecipientChanged;
        _capacitySubscriptions.Clear();
    }
}
