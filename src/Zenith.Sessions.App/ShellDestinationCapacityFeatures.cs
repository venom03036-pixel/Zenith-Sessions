using System.Collections.Specialized;
using System.ComponentModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private readonly HashSet<AccountRow> _capacitySubscriptions = [];
    private string _requestedCapacity = "Choose recipients and explicitly use a public server to compare its reported capacity.";
    public string RequestedCapacitySummary { get => _requestedCapacity; private set => Set(ref _requestedCapacity, value); }
    private const string NoBundleCapacity = "Select a saved bundle to compare its recipients with the used server's reported capacity.";
    private string _bundleCapacity = NoBundleCapacity;
    /// <summary>Advice for "Queue bundle": counts every recipient reference in the selected bundle, not checked accounts.</summary>
    public string BundleCapacitySummary { get => _bundleCapacity; private set => Set(ref _bundleCapacity, value); }
    public int DestinationRecipientCount
    {
        get
        {
            int checkedCount = 0;
            foreach (var row in Accounts) if (row.IsChecked) checkedCount++;
            return checkedCount > 0 ? checkedCount : SelectedAccount is not null ? 1 : 0;
        }
    }
    /// <summary>"Find a server with room for N" needs a usable destination and at least one checked or selected account to find room for.</summary>
    public bool CanFindServerWithRoom => ServerBrowser.CanUseDestination && DestinationRecipientCount > 0;
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
        Raise(nameof(DestinationRecipientCount)); Raise(nameof(CanFindServerWithRoom));
        string? unavailable = IsLocked || _shuttingDown
            ? "Capacity advice is unavailable while controls are locked or stopping. No seats are reserved."
            : ServerBrowser.HasDestinationDraft
                ? "Use or discard the pending destination before comparing reported capacity."
                : null;
        RequestedCapacitySummary = unavailable ?? ServerBrowser.AssessRequestedCapacity(DestinationRecipientCount).Explanation;
        // RecipientBundles is created after this feature is initialized, so a refresh can precede it.
        BundleCapacitySummary = unavailable ?? (RecipientBundles?.SelectedBundle is { } bundle
            ? bundle.Name + " · " + ServerBrowser.AssessRequestedCapacity(bundle.ProfileIds.Count).Explanation
            : NoBundleCapacity);
    }
    private void DisposeDestinationCapacity()
    {
        Accounts.CollectionChanged -= CapacityAccountsChanged;
        ServerBrowser.PropertyChanged -= CapacityDestinationChanged;
        foreach (var row in _capacitySubscriptions) row.PropertyChanged -= CapacityRecipientChanged;
        _capacitySubscriptions.Clear();
    }
}
