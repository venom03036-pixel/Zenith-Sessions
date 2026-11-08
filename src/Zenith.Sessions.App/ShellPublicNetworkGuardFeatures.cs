using System;
using System.Threading;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Public-network guard (R-api-18, also R-shell-23): off by default; a saved schema-1 choice, applied after Save settings, never from
// a draft. While it is on, Zenith watches which profile Windows gives the connected networks through the Network List Manager's own
// change notifications (no polling, Rule 14). Entering "a connected network with internet access is Public" takes a launch hold at
// once, on the thread Windows calls, so a start the queue is deciding this moment already sees it; then, on the dispatcher, it
// locks the dashboard like Lock, once per Public network, with a notice that says why and what to do. Leaving that state lifts the
// hold and signals the queue, so held requests start on their own; the lock stays until Unlock, like every lock. Windows not
// answering (Unknown) never holds (Rule 16). Panic and Lock leave the guard watching: it is an observation like monitoring, and
// stopping it would let a launch through on the very network it guards against. Saving it off, a Windows session end and Exit stop
// it; Exit disposes it (Rule 13). No network name or id is read, shown or logged.
public sealed partial class ShellViewModel
{
    private const string PublicNetworkOff = "Off. New launches are not held for the network profile.";
    private const string PublicNetworkSample = "Not available in sample mode: Zenith does not read this PC's network profile while it shows sample profiles.";
    private IPublicNetworkMonitor? _publicNetwork;
    private bool _publicNetworkGuard, _publicNetworkEnforced;
    private volatile bool _publicNetworkWatching;
    // 0 or 1: read by queue admission on its own thread, so it is a volatile int rather than a bool behind the dispatcher.
    private int _publicNetworkHeld;
    private string _publicNetworkStatus = PublicNetworkOff;

    /// <summary>Settings draft of the saved "Hold launches on a public network" choice; only the saved value is applied.</summary>
    public bool PublicNetworkGuard { get => _publicNetworkGuard; set { if (Set(ref _publicNetworkGuard, value)) PreferencesEdited(); } }
    /// <summary>True while the saved guard holds launches because the active network is Public; the footer says so on every page.</summary>
    public bool IsPublicNetworkHeld => Volatile.Read(ref _publicNetworkHeld) == 1;
    public string PublicNetworkStatus { get => _publicNetworkStatus; private set => Set(ref _publicNetworkStatus, value); }

    private void InitializePublicNetworkGuardFeatures(IPublicNetworkMonitor? monitor)
    {
        // Sample mode shows the choice but never reads this PC's network unless a test injects its own monitor. The Windows monitor
        // touches nothing until Start, so creating it here costs nothing while the guard stays off.
        _publicNetwork = monitor ?? (IsSample ? null : new WindowsPublicNetworkMonitor());
        if (_publicNetwork is null) _publicNetworkStatus = PublicNetworkSample;
    }

    // UI thread, after settings load and after each explicit Save settings: watch, or stop watching, to match the saved choice.
    private void ApplySavedPublicNetworkGuard()
    {
        if (_publicNetwork is not { } monitor || _shuttingDown) return;
        bool on = _savedSettings.PublicNetworkGuard;
        if (on && !_publicNetworkWatching)
        {
            _publicNetworkWatching = true;
            monitor.Changed += PublicNetworkChanged;
            NetworkExposure exposure;
            try { exposure = monitor.Start(); }
            catch (Exception error)
            {
                // The Windows monitor answers Unknown itself when Windows refuses; a monitor that throws is kept and reported by type.
                exposure = NetworkExposure.Unknown;
                Fail(error, ManagerSubsystem.PublicNetwork, "Windows could not report the network profile, so launches are not held for it. The guard keeps listening for network changes.");
            }
            ApplyNetworkExposure(exposure);
        }
        else if (!on && _publicNetworkWatching) StopPublicNetworkWatch();
        else if (!on) PublicNetworkStatus = PublicNetworkOff;
    }

    // Any thread: Windows delivers on the thread that subscribed (the UI thread) in production, a test's fake on its own. The hold
    // is taken here, before the dispatcher catches up; the lock and the notice follow on the dispatcher, never re-entrantly.
    private void PublicNetworkChanged(NetworkExposure exposure)
    {
        if (!_publicNetworkWatching || _shuttingDown || _dispatcher.HasShutdownStarted) return;
        if (PublicNetworkPolicy.Holds(exposure) && Interlocked.Exchange(ref _publicNetworkHeld, 1) == 0) _launchQueue.Signal();
        _dispatcher.BeginInvoke(() => ApplyNetworkExposure(exposure));
    }

    // UI thread. Entering "guard on and network Public" takes the hold, locks the dashboard like Lock (once per Public network, not
    // again while on it) and says why; leaving it lifts the hold, signals the queue and says so, while the lock stays until Unlock.
    private void ApplyNetworkExposure(NetworkExposure exposure)
    {
        if (!_publicNetworkWatching || _shuttingDown) { ClearPublicNetworkHold(); return; }
        bool hold = PublicNetworkPolicy.Holds(exposure);
        Volatile.Write(ref _publicNetworkHeld, hold ? 1 : 0);
        if (hold && !_publicNetworkEnforced)
        {
            _publicNetworkEnforced = true;
            _diagnostics.Record(ManagerEventKind.PublicNetworkDetected);
            const string reason = "Public network: Windows files the active connection as Public, so new launches are held and the controls are locked. Monitoring continues. Join a private network, or turn off Hold launches on a public network in Settings and save; then unlock.";
            if (!IsLocked) LockControls(reason);
            else { AddEvent(reason); Inform(reason); }
            _launchQueue.Signal();
        }
        else if (!hold && _publicNetworkEnforced)
        {
            _publicNetworkEnforced = false;
            _diagnostics.Record(ManagerEventKind.PublicNetworkCleared);
            string lifted = (exposure == NetworkExposure.Unknown
                ? "Windows could not report the active network's profile, so launches are no longer held for a public network."
                : "The active network is no longer Public. Launch requests held for it start again on their own while the queue is active")
                + (IsLocked ? "; the dashboard stays locked until you unlock it." : ".");
            AddEvent(lifted); Inform(lifted);
            _launchQueue.Signal();
        }
        Raise(nameof(IsPublicNetworkHeld));
        PublicNetworkStatus = exposure switch
        {
            NetworkExposure.Public => "On. The active network is Public: new launches are held until it is Private or Domain, or until this choice is saved off.",
            NetworkExposure.NotPublic => "On. The active network is Private or Domain, or none is connected: launches are allowed, and they are held as soon as a Public network with internet access connects.",
            _ => "On, but Windows could not report the active network's profile, so launches are not held. The guard keeps listening for network changes."
        };
    }

    private void ClearPublicNetworkHold()
    {
        bool wasHeld = Interlocked.Exchange(ref _publicNetworkHeld, 0) == 1;
        _publicNetworkEnforced = false;
        if (!wasHeld) return;
        _launchQueue.Signal();
        Raise(nameof(IsPublicNetworkHeld));
    }

    // Saved off, a Windows session end and Exit: stop listening and lift any hold. A notification already under way is ignored by
    // the watching flag. Not a Panic or Lock step: see the file comment.
    private void StopPublicNetworkWatch()
    {
        if (_publicNetwork is not { } monitor) return;
        bool watching = _publicNetworkWatching;
        _publicNetworkWatching = false;
        if (watching)
        {
            monitor.Changed -= PublicNetworkChanged;
            try { monitor.Stop(); }
            catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.PublicNetwork, error.GetType()); }
        }
        ClearPublicNetworkHold();
        if (!_shuttingDown) PublicNetworkStatus = PublicNetworkOff;
    }

    private void DisposePublicNetworkGuardFeatures()
    {
        StopPublicNetworkWatch();
        if (_publicNetwork is not { } monitor) return;
        try { monitor.Dispose(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.PublicNetwork, error.GetType()); }
    }
}
