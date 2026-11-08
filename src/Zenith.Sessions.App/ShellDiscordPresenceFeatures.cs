using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Discord rich presence (R-api-17): off by default; saved schema-1 choices applied after Save settings, never from a draft. While
// on, the Discord app running on this PC is told, over its own local named pipe, how many Roblox clients Zenith sees running and,
// when the second choice is on, which game they are in; Discord shows that to whoever can see the user's status. Zenith sends
// nothing over the internet for this. The content is computed on every desktop presentation from the same scan that fills the
// Sessions page (the observed client processes) and the presence history's last-known destinations; a game is named only from
// what Zenith already knows locally (favorites, the Join page's recently played names), never looked up, else by its place ID. The
// publisher (Core) sends only when the content changes, keeps one read pending so Discord quitting is seen at once, and tries
// again at most once a minute while Discord is not running (Rules 14, 16). A fault is recorded by type and shown once until the
// status is shown again; an application ID Discord refuses stops the status until a new ID is saved or Resume Discord status.
// Lock clears the status (the pipe is closed) and Unlock shows it again; Panic clears it until Resume Discord status (Rule 11);
// Exit closes the pipe (Rules 12, 13). The application ID is a public identifier, kept in ordinary settings. Sample mode never
// connects. Rule 18: CPU bounded by the sample cadence and one frame per content change; memory by one activity and one 64 KiB
// frame bound; no timer or thread of its own (one awaited worker while on); every path takes the lifetime token.
public sealed partial class ShellViewModel
{
    private const string DiscordPresenceOff = "Off. Nothing is shown in Discord.";
    private const string DiscordPresenceSample = "Not available in sample mode: Zenith does not talk to Discord while it shows sample profiles.";
    private enum PresenceStop { None, Panic, Refused }
    private IDiscordPresencePipe _presencePipe = null!;
    private DiscordPresencePublisher _presence = null!;
    private bool _presencePermitted, _discordPresence, _discordPresenceGame, _presenceFailureShown, _presenceAbsent;
    private string _discordApplicationId = "", _discordPresenceStatus = DiscordPresenceOff, _lastPresenceProblem = "", _presenceRefusedId = "";
    private PresenceStop _presenceStop;
    private PresenceActivity _latestPresence = new(0);
    private PresenceActivity? _presenceShown;

    /// <summary>Settings draft of the saved "Show Zenith in your Discord status" choice; only the saved value is applied.</summary>
    public bool DiscordPresence { get => _discordPresence; set { if (Set(ref _discordPresence, value)) PreferencesEdited(); } }
    /// <summary>Settings draft of the saved "Include the game" choice.</summary>
    public bool DiscordPresenceGame { get => _discordPresenceGame; set { if (Set(ref _discordPresenceGame, value)) PreferencesEdited(); } }
    /// <summary>Settings draft of the Discord application ID (a public snowflake, or empty); checked when settings are saved.</summary>
    public string DiscordApplicationId { get => _discordApplicationId; set { if (Set(ref _discordApplicationId, value ?? "")) PreferencesEdited(); } }
    /// <summary>The publisher runs: the saved choice is on, an application ID is saved, and neither Lock, Panic nor a refused ID stopped it.</summary>
    public bool IsDiscordPresenceActive => _presence.IsRunning;
    /// <summary>Panic, or an application ID Discord refused, stopped the status for this run while the saved choice is still on.</summary>
    public bool IsDiscordPresenceStopped => _presenceStop != PresenceStop.None && _savedSettings.DiscordPresence;
    public bool CanResumeDiscordPresence => IsDiscordPresenceStopped && _presencePermitted && _ready && !IsLocked && !_shuttingDown && _savedSettings.DiscordApplicationId.Length > 0;
    public string DiscordPresenceStatus { get => _discordPresenceStatus; private set => Set(ref _discordPresenceStatus, value); }
    public Command ResumeDiscordPresence { get; private set; } = null!;
    /// <summary>Tests: what the last desktop presentation computed for Discord, whether or not it was sent.</summary>
    internal PresenceActivity LatestPresence => _latestPresence;

    private void InitializeDiscordPresenceFeatures(IDiscordPresencePipe? pipe)
    {
        // Sample mode shows the controls but never opens Discord's pipe unless a test injects its own.
        _presencePermitted = !IsSample || pipe is not null;
        _presencePipe = pipe ?? new WindowsDiscordPresencePipe();
        _presence = new DiscordPresencePublisher(_presencePipe, Environment.ProcessId);
        _presence.Reported += PresenceReported;
        if (!_presencePermitted) _discordPresenceStatus = DiscordPresenceSample;
        ResumeDiscordPresence = new(_ => ResumePresence());
        PropertyChanged += DiscordPresenceOwnerChanged;
    }

    private void DiscordPresenceOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IsReady)) RaiseDiscordPresenceAvailability();
        // Lock cleared the status (StopOperations); Unlock shows it again unless Panic or a refusal keeps it stopped.
        else if (args.PropertyName == nameof(IsLocked)) ApplySavedDiscordPresence(wasOn: true);
    }
    private void RaiseDiscordPresenceAvailability()
    {
        Raise(nameof(IsDiscordPresenceActive)); Raise(nameof(IsDiscordPresenceStopped)); Raise(nameof(CanResumeDiscordPresence));
    }

    // UI thread, after settings load, after each explicit Save settings and after Unlock: run, or stop, to match the saved choice.
    // Saving the choice on after it was off is an explicit start, so it clears a stop; a new application ID answers a refusal of the
    // old one; saving unchanged never restarts a Panic stop (Rule 11). While locked nothing starts: Lock cleared the status.
    private void ApplySavedDiscordPresence(bool wasOn)
    {
        if (!_presencePermitted || _shuttingDown) return;
        bool on = _savedSettings.DiscordPresence; string id = _savedSettings.DiscordApplicationId;
        if (on && !wasOn) ClearPresenceStop();
        if (_presenceStop == PresenceStop.Refused && !string.Equals(id, _presenceRefusedId, StringComparison.Ordinal)) ClearPresenceStop();
        if (!on || id.Length == 0 || IsLocked) { _presence.Stop(); _presenceShown = null; if (!on) _presenceStop = PresenceStop.None; }
        else if (_presenceStop == PresenceStop.None) StartPresence(id);
        RefreshDiscordPresenceStatus();
    }
    private void ClearPresenceStop() { _presenceStop = PresenceStop.None; _presenceFailureShown = false; _lastPresenceProblem = ""; _presenceRefusedId = ""; }

    // Start is idempotent for the same application and restarts for a new one; the latest content goes out at once.
    private void StartPresence(string applicationId)
    {
        bool fresh = !_presence.IsRunning || !string.Equals(_presence.ApplicationId, applicationId, StringComparison.Ordinal);
        _presence.Start(applicationId);
        if (fresh) { _presenceShown = null; _presenceAbsent = false; }
        _presence.Publish(_latestPresence);
    }

    private void RefreshDiscordPresenceStatus()
    {
        DiscordPresenceStatus = !_presencePermitted ? DiscordPresenceSample
            : !_savedSettings.DiscordPresence ? DiscordPresenceOff
            : _presenceStop == PresenceStop.Panic ? "Stopped by Panic: your Discord status was cleared. Choose Resume Discord status to show it again."
            : _presenceStop == PresenceStop.Refused ? "Stopped: " + _lastPresenceProblem
            : _savedSettings.DiscordApplicationId.Length == 0 ? "On, but no Discord application ID is saved, so nothing is shown. Enter the ID below and save settings."
            : IsLocked ? "Cleared while the dashboard is locked. Your status is shown again after Unlock."
            : _lastPresenceProblem.Length > 0 ? "On. " + _lastPresenceProblem
            : _presenceAbsent ? "On. Discord is not running on this PC, so nothing is shown yet. Zenith connects when it is, checking when the client count changes and otherwise once a minute."
            : _presenceShown is { } shown ? "On. Discord shows: " + shown.Line + "."
            : "On. Connecting to Discord…";
        RaiseDiscordPresenceAvailability();
    }

    // Dispatcher, from the presentation step: the observed client processes are the count; the game comes from the presence
    // history's last-known destinations, which the same step just updated. The content is kept even while off, so a start shows it.
    private void ObserveDiscordPresence(DesktopSnapshot snapshot)
    {
        int clients = snapshot.Sessions.Count;
        string? game = null;
        if (_savedSettings.DiscordPresenceGame && RecentClients.Snapshot() is { } view)
            game = PresenceText.Game(view.Active.Select(entry => entry.LastKnownDestination?.PlaceId ?? 0), PresenceGameName);
        _latestPresence = new(clients, game);
        if (_presence.IsRunning && !_shuttingDown) _presence.Publish(_latestPresence);
    }

    /// <summary>UI thread. A name Zenith already holds for the place: a favorite's, or a recently played chip's; never a lookup.</summary>
    internal string? PresenceGameName(long placeId)
    {
        if (DestinationFavorites.Favorites.FirstOrDefault(favorite => favorite.PlaceId == placeId) is { } favorite) return favorite.Name;
        return RecentlyPlayed?.Chips.FirstOrDefault(chip => chip.PlaceId == placeId && chip.HasName)?.Name;
    }

    // The publisher's worker thread: only a hop to the dispatcher happens here.
    private void PresenceReported(PresenceReport report)
    {
        if (Volatile.Read(ref _shuttingDown) || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() => ApplyPresenceReport(report)));
    }

    private void ApplyPresenceReport(PresenceReport report)
    {
        if (_shuttingDown) return;
        string code = report.Code is { } number ? " (code " + number.ToString(CultureInfo.InvariantCulture) + ")" : "";
        switch (report.Status)
        {
            case PresenceStatus.Connected:
                _presenceAbsent = false;
                _diagnostics.Record(ManagerEventKind.DiscordPresenceConnected);
                break;
            case PresenceStatus.Published:
                _presenceShown = report.Activity; _presenceAbsent = false; _lastPresenceProblem = ""; _presenceFailureShown = false;
                break;
            case PresenceStatus.DiscordNotRunning:
                // Not a failure: Discord is simply not there (or just quit). The status says so and nothing is recorded.
                _presenceShown = null; _presenceAbsent = true;
                break;
            case PresenceStatus.HandshakeRefused:
                // A refusal of an ID that is no longer the saved one (a new ID was saved while the report hopped here) says nothing about the new one.
                if (report.ApplicationId is { } refused && !string.Equals(refused, _savedSettings.DiscordApplicationId, StringComparison.Ordinal)) break;
                _presence.Stop(); _presenceShown = null; _presenceStop = PresenceStop.Refused; _presenceRefusedId = _savedSettings.DiscordApplicationId;
                ReportPresenceFailure(typeof(InvalidOperationException), report.Code == DiscordIpcAnswer.InvalidApplicationCode
                    ? "Discord does not know the saved application ID" + code + ". Check the Application ID on the application's page in the Discord Developer Portal and save it again, or choose Resume Discord status to try this one again."
                    : "Discord refused Zenith's connection" + code + ". Choose Resume Discord status to try again.", force: true);
                break;
            case PresenceStatus.UpdateRefused:
                ReportPresenceFailure(typeof(InvalidOperationException), "Discord refused the status update" + code + ". The next change is sent again.");
                break;
            default:
                _presenceShown = null;
                ReportPresenceFailure(report.ErrorType ?? typeof(IOException), "The status could not be sent to Discord. Zenith connects again when the client count changes, and otherwise within a minute.");
                break;
        }
        RefreshDiscordPresenceStatus();
    }

    // Every failure is kept by type (Rule 10); only the first since the status was last shown is shown as an error, so a flapping
    // Discord is one error line, not one per attempt. A forced report (a refused application ID) is shown even then.
    private void ReportPresenceFailure(Type type, string message, bool force = false)
    {
        _lastPresenceProblem = message;
        _diagnostics.RecordFailure(ManagerSubsystem.DiscordPresence, type);
        if (_presenceFailureShown && !force) { RefreshDiagnosticsView(); return; }
        _presenceFailureShown = true;
        ShowError(message);
        AddEvent("Operation failed (" + type.Name + ").");
        RefreshDiagnosticsView();
    }

    /// <summary>The explicit start after Panic or a refused application ID.</summary>
    private void ResumePresence()
    {
        if (!CanResumeDiscordPresence) return;
        ClearPresenceStop();
        StartPresence(_savedSettings.DiscordApplicationId);
        RefreshDiscordPresenceStatus();
        Inform("Discord status resumed. Zenith shows it again as soon as Discord accepts the connection.");
    }

    // Panic and Lock step: both clear the status by closing the pipe. Lock is lifted by Unlock (the owner-changed handler); Panic
    // keeps it stopped until Resume Discord status or the choice is saved on after being off.
    private void StopDiscordPresenceOperations(bool locking)
    {
        if (!_presencePermitted) return;
        _presence.Stop(); _presenceShown = null;
        if (!locking && _savedSettings.DiscordPresence) _presenceStop = PresenceStop.Panic;
        RefreshDiscordPresenceStatus();
    }

    // Exit's synchronous stage: the worker is cancelled and closes the pipe, which clears the status.
    private void StopDiscordPresence() => _presence.Stop();

    private async Task DisposeDiscordPresenceFeaturesAsync()
    {
        PropertyChanged -= DiscordPresenceOwnerChanged;
        _presence.Reported -= PresenceReported;
        StopDiscordPresence();
        try { await _presence.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordPresence, error.GetType()); }
        try { (_presencePipe as IDisposable)?.Dispose(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordPresence, error.GetType()); }
        RaiseDiscordPresenceAvailability();
    }
}
