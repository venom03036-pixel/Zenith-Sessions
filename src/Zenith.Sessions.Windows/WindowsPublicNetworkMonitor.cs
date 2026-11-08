using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Reads which profile Windows gives each connected network through the Network List Manager, the same source as Windows' own
/// network settings, and listens to its change notifications, so nothing polls (Rule 14): a connectivity change, a network added
/// or removed, or a profile the user changed re-reads the list once. Only each network's category and connectivity are read; its
/// name and id are never read or kept. A Windows error on any read answers Unknown, which never holds (Rule 16), and the
/// subscription stays so the next change is read again. Start, Stop and Dispose are expected on one STA thread (the manager's UI
/// thread); Windows then delivers <see cref="Changed"/> on that thread through its message loop. A change that arrives while a
/// read is under way is folded into one more read, so a burst of notifications costs at most two reads.
/// </summary>
public sealed class WindowsPublicNetworkMonitor : IPublicNetworkMonitor
{
    // netlistmgr.h: NLM_NETWORK_CATEGORY, NLM_CONNECTIVITY, NLM_ENUM_NETWORK and NLM_NETWORK_PROPERTY_CHANGE values.
    private const int CategoryPrivate = 1, CategoryDomain = 2;
    private const int ConnectivityInternet = 0x40 | 0x400;
    private const int EnumConnected = 1;
    private const int PropertyConnection = 1, PropertyCategory = 0x10;
    private const int Ok = 0;
    private readonly object _gate = new();
    private NetworkList.INetworkListManager? _manager;
    private IConnectionPoint? _listPoint, _networkPoint;
    private int _listCookie, _networkCookie;
    private bool _disposed, _reading, _pending;

    public event Action<NetworkExposure>? Changed;

    public NetworkExposure Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager is null && !TrySubscribe()) return NetworkExposure.Unknown;
            return ReadFolded();
        }
    }

    public void Stop() { lock (_gate) Unsubscribe(); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
        }
    }

    /// <summary>NLM_NETWORK_CATEGORY: 0 Public, 1 Private, 2 Domain-authenticated. A value Windows adds later is read as Public, the cautious side.</summary>
    internal static NetworkProfile Profile(int category) => category switch
    {
        CategoryPrivate => NetworkProfile.Private,
        CategoryDomain => NetworkProfile.Domain,
        _ => NetworkProfile.Public
    };

    /// <summary>NLM_CONNECTIVITY: only the IPv4 or IPv6 internet bits count; subnet or local traffic does not.</summary>
    internal static bool ReachesInternet(int connectivity) => (connectivity & ConnectivityInternet) != 0;

    // Caller holds _gate. Creates the manager and subscribes to both its event interfaces; false, with nothing kept, when Windows refuses.
    private bool TrySubscribe()
    {
        NetworkList.INetworkListManager manager;
        try { manager = (NetworkList.INetworkListManager)new NetworkList.NetworkListManager(); }
        catch (Exception error) when (error is COMException or InvalidCastException) { return false; }
        var sink = new Sink(this);
        IConnectionPoint? listPoint = null, networkPoint = null;
        int listCookie = 0, networkCookie = 0;
        try
        {
            var container = (IConnectionPointContainer)manager;
            Guid listEvents = typeof(NetworkList.INetworkListManagerEvents).GUID, networkEvents = typeof(NetworkList.INetworkEvents).GUID;
            // The connection-point interfaces come from an unannotated assembly; FindConnectionPoint either answers or throws.
            container.FindConnectionPoint(ref listEvents, out listPoint);
            listPoint!.Advise(sink, out listCookie);
            container.FindConnectionPoint(ref networkEvents, out networkPoint);
            networkPoint!.Advise(sink, out networkCookie);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            Release(listPoint, listCookie);
            Release(networkPoint, networkCookie);
            Marshal.ReleaseComObject(manager);
            return false;
        }
        _manager = manager;
        _listPoint = listPoint; _networkPoint = networkPoint;
        _listCookie = listCookie; _networkCookie = networkCookie;
        return true;
    }

    // Caller holds _gate.
    private void Unsubscribe()
    {
        Release(_listPoint, _listCookie);
        Release(_networkPoint, _networkCookie);
        _listPoint = null; _networkPoint = null; _listCookie = _networkCookie = 0;
        if (_manager is { } manager) { Marshal.ReleaseComObject(manager); _manager = null; }
    }

    private static void Release(IConnectionPoint? point, int cookie)
    {
        if (point is null) return;
        try { if (cookie != 0) point.Unadvise(cookie); }
        catch (COMException) { } // The network service is gone, so there is nothing left to unsubscribe from.
        Marshal.ReleaseComObject(point);
    }

    // Windows' thread (the STA that subscribed). Publishes only the fresh reading, never the notification's own detail.
    private void Notify()
    {
        NetworkExposure exposure;
        lock (_gate)
        {
            if (_disposed || _manager is null) return;
            // A notification delivered while a read is pumping its own COM calls: that read runs once more and publishes.
            if (_reading) { _pending = true; return; }
            exposure = ReadFolded();
        }
        Changed?.Invoke(exposure);
    }

    // Caller holds _gate. Reads once, and once more for each notification that arrived meanwhile.
    private NetworkExposure ReadFolded()
    {
        _reading = true;
        try
        {
            NetworkExposure exposure;
            do { _pending = false; exposure = Read(); } while (_pending);
            return exposure;
        }
        finally { _reading = false; }
    }

    // Caller holds _gate. One enumeration of the connected networks; Unknown on any Windows error.
    private NetworkExposure Read()
    {
        if (_manager is not { } manager) return NetworkExposure.Unknown;
        NetworkList.IEnumNetworks? list = null;
        var networks = new List<ConnectedNetwork>();
        try
        {
            if (manager.GetNetworks(EnumConnected, out list) != Ok || list is null) return NetworkExposure.Unknown;
            while (true)
            {
                uint fetched = 0;
                int result = list.Next(1, out var network, ref fetched);
                if (result < 0) return NetworkExposure.Unknown;
                if (result != Ok || fetched != 1 || network is null) break; // S_FALSE: no more connected networks.
                try
                {
                    if (network.GetCategory(out int category) != Ok || network.GetConnectivity(out int connectivity) != Ok) return NetworkExposure.Unknown;
                    networks.Add(new(Profile(category), ReachesInternet(connectivity)));
                }
                finally { Marshal.ReleaseComObject(network); }
            }
            return PublicNetworkPolicy.Classify(CollectionsMarshal.AsSpan(networks));
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return NetworkExposure.Unknown; }
        finally { if (list is not null) Marshal.ReleaseComObject(list); }
    }

    /// <summary>The event sink Windows calls. Each call re-reads the list; the notification's own detail (ids, flags) is not kept.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class Sink(WindowsPublicNetworkMonitor owner) : NetworkList.INetworkListManagerEvents, NetworkList.INetworkEvents
    {
        public int ConnectivityChanged(int connectivity) { owner.Notify(); return Ok; }
        public int NetworkAdded(Guid networkId) { owner.Notify(); return Ok; }
        public int NetworkDeleted(Guid networkId) { owner.Notify(); return Ok; }
        public int NetworkConnectivityChanged(Guid networkId, int connectivity) { owner.Notify(); return Ok; }
        // A name, description or icon change says nothing about the profile; a connection or category change does.
        public int NetworkPropertyChanged(Guid networkId, int change) { if ((change & (PropertyConnection | PropertyCategory)) != 0) owner.Notify(); return Ok; }
    }

    /// <summary>
    /// Network List Manager declarations, each a prefix of its vtable in Windows SDK order (netlistmgr.h). The list manager, network
    /// and enumerator are dual interfaces, so the runtime places their IDispatch slots; only the members Zenith calls carry real
    /// signatures, the others hold their position. The event interfaces are plain IUnknown interfaces that the sink implements.
    /// https://learn.microsoft.com/windows/win32/api/netlistmgr/
    /// </summary>
    internal static class NetworkList
    {
        [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
        internal class NetworkListManager { }

        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetworkListManager
        {
            [PreserveSig] int GetNetworks(int flags, out IEnumNetworks networks);
        }

        [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IEnumNetworks
        {
            [PreserveSig] int get__NewEnum(out nint enumerator);
            /// <summary>Called with a count of one: the single slot and the fetched count (0 at the end, with S_FALSE).</summary>
            [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.Interface)] out INetwork network, ref uint fetched);
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetwork
        {
            // Never called: the network's name and description are not read.
            [PreserveSig] int GetName(out nint name);
            [PreserveSig] int SetName(nint name);
            [PreserveSig] int GetDescription(out nint description);
            [PreserveSig] int SetDescription(nint description);
            [PreserveSig] int GetNetworkId(out Guid networkId);
            [PreserveSig] int GetDomainType(out int domainType);
            [PreserveSig] int GetNetworkConnections(out nint connections);
            [PreserveSig] int GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
            [PreserveSig] int get_IsConnectedToInternet(out short connected);
            [PreserveSig] int get_IsConnected(out short connected);
            [PreserveSig] int GetConnectivity(out int connectivity);
            [PreserveSig] int GetCategory(out int category);
            [PreserveSig] int SetCategory(int category);
        }

        [ComImport, Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface INetworkListManagerEvents
        {
            [PreserveSig] int ConnectivityChanged(int connectivity);
        }

        [ComImport, Guid("DCB00004-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface INetworkEvents
        {
            [PreserveSig] int NetworkAdded(Guid networkId);
            [PreserveSig] int NetworkDeleted(Guid networkId);
            [PreserveSig] int NetworkConnectivityChanged(Guid networkId, int connectivity);
            [PreserveSig] int NetworkPropertyChanged(Guid networkId, int change);
        }
    }
}
