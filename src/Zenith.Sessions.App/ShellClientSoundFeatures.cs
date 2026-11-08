using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    /// <summary>How long a client that appeared is watched for its first sound before arrival muting gives up on it.</summary>
    internal static readonly TimeSpan ArrivalMuteWindow = TimeSpan.FromSeconds(120);

    private IClientAudioMuteControl _clientSound = null!;
    // _changingClientSound is a Mute or Unmute click, from the click until its results are shown; _mutingArrivals an arrival
    // operation. Only a click turns Mute and Unmute off.
    private bool _soundPermitted, _changingClientSound, _mutingArrivals, _muteArrivingClients;
    private CancellationTokenSource? _clientSoundStop, _arrivalSoundStop;
    private Task _clientSoundWork = Task.CompletedTask;
    private long _clientSoundGeneration;
    private sealed record ClientSoundRequest(long Generation, SessionObservation[] Processes, string Refusal);
    private string _clientSoundStatus = "Choose clients to mute or unmute their sound in Windows.";
    // Arrival muting, on the dispatcher only: the exact instances in the last usable scan (with those it could not read kept
    // by PID), the PIDs that scan could not read and that were first listed while nothing was watched (the instance behind
    // one is already running, so it is never an arrival once it reads), and the new ones still waiting for Windows to list
    // their sound. Each holds at most one entry per PID in a scan, so at most 128.
    private readonly HashSet<SessionProcessKey> _soundSeenClients = [];
    private readonly HashSet<int> _soundUnreadClients = [];
    private readonly Dictionary<SessionProcessKey, DateTimeOffset> _soundArrivals = [];
    private bool _soundBaseline, _arrivalMutePaused;

    public ObservableCollection<ClientAudioMuteResult> ClientSoundResults { get; } = [];
    public bool CanChangeClientSound => _soundPermitted && _ready && !IsLocked && !_shuttingDown
        && !_changingClientSound && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    /// <summary>A click's change or an arrival operation is running; Cancel sound work stops either.</summary>
    public bool IsChangingClientSound => _changingClientSound || _mutingArrivals;
    public int PendingSoundRestoreCount => _clientSound.PendingRestoreCount;
    public string ClientSoundStatus { get => _clientSoundStatus; private set => Set(ref _clientSoundStatus, value); }
    /// <summary>The draft of the saved "Mute clients when they appear" preference; only the saved value is applied.</summary>
    public bool MuteArrivingClients { get => _muteArrivingClients; set { if (Set(ref _muteArrivingClients, value)) PreferencesEdited(); } }
    public AsyncCommand MuteClients { get; private set; } = null!;
    public AsyncCommand UnmuteClients { get; private set; } = null!;
    public Command CancelClientSound { get; private set; } = null!;
    public Command ResumeArrivalMute { get; private set; } = null!;
    /// <summary>
    /// For this run only: Panic, Lock and Cancel pause arrival muting, and it stays paused until Resume, or until the preference is
    /// saved on after being off. Unlock and saving the unchanged preference never restart it (Rule 11).
    /// </summary>
    public bool IsArrivalMutePaused => _arrivalMutePaused && _savedSettings.MuteArrivingClients;
    public bool CanResumeArrivalMute => IsArrivalMutePaused && _soundPermitted && _ready && !IsLocked && !_shuttingDown;
    public string ArrivalMuteStatus => IsArrivalMutePaused
        ? "Muting new clients is paused by Panic, Lock or Cancel. Clients that appear are not muted until you choose Resume muting new clients."
        : "";
    /// <summary>
    /// Exit's warning, read after DisposeAsync: null until Exit starts, and then unless Windows did not confirm that every client
    /// Zenith muted was unmuted.
    /// </summary>
    internal string? ExitSoundWarning => !_shuttingDown ? null : PendingSoundRestoreCount switch
    {
        <= 0 => null,
        1 => "Windows could not confirm that 1 client Zenith muted was unmuted. Turn its sound on in Volume mixer. "
            + "Until then, Windows may start later clients of the same program muted.",
        var count => $"Windows could not confirm that {count} clients Zenith muted were unmuted. Turn their sound on in Volume mixer. "
            + "Until then, Windows may start later clients of the same program muted."
    };

    private void InitializeClientSoundFeatures(IClientAudioMuteControl? control)
    {
        _soundPermitted = !IsSample || control is not null;
        _clientSound = control ?? new WindowsClientAudioMuteControl();
        MuteClients = new(p => ChangeClientSoundAsync(ClientAudioMuteAction.Mute, p), ReportClientSoundFailure, CaptureClientSoundRequest);
        UnmuteClients = new(p => ChangeClientSoundAsync(ClientAudioMuteAction.Unmute, p), ReportClientSoundFailure, CaptureClientSoundRequest);
        CancelClientSound = new(_ => StopClientSoundOperations());
        ResumeArrivalMute = new(_ => ResumeArrivalMuting());
        PropertyChanged += ClientSoundOwnerChanged;
    }

    private void ClientSoundOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HasSelectedSession) or nameof(CheckedClientCount) or nameof(IsReady) or nameof(IsLocked))
            Raise(nameof(CanChangeClientSound));
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) Raise(nameof(CanResumeArrivalMute));
    }

    // The same pinned, fresh, exact-instance selection the priority and trim controls use.
    private object? CaptureClientSoundRequest(object? parameter)
        => CanChangeClientSound
            ? new ClientSoundRequest(_clientSoundGeneration, PinClientPrioritySelection(out var refusal), refusal) : null;

    private Task ChangeClientSoundAsync(ClientAudioMuteAction action, object? parameter)
    {
        if (!CanChangeClientSound || parameter is not ClientSoundRequest request || request.Generation != _clientSoundGeneration)
            return Task.CompletedTask;
        if (request.Processes.Length == 0)
        {
            ClientSoundStatus = request.Refusal;
            return Task.CompletedTask;
        }
        return StartClientSoundWork(request.Processes, action, arrival: false);
    }

    /// <summary>
    /// Dispatcher, after each sample. Only a fresh scan that names every readable client can tell which clients are new, so
    /// any other scan is skipped. Clients in the first such scan, and clients already running when the setting is saved on,
    /// are never arrivals. A new client is muted once Windows lists its sound, tried again on later scans for at most
    /// <see cref="ArrivalMuteWindow"/>, one Core Audio read per waiting client per scan (up to four on the scan that mutes it) on
    /// the sound worker. No timer or thread is added: this rides the existing sample cadence. A row Windows could not read
    /// names a PID that is still running but not which instance, so a client already known under that PID stays known (and a
    /// waiting arrival keeps waiting) instead of counting as new when a later scan reads it again. A PID that no known
    /// instance explains and that a scan could not read while nothing was watched (the first scan, the preference off, or
    /// paused) is already running, so its instance is known, not new, when a later scan first reads it; one first listed
    /// unreadable while watching did appear, and is muted once a scan reads it.
    /// </summary>
    private void ObserveClientSoundArrivals(DesktopSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (FreshClientInventory(snapshot, now) is not { } rows) return;
        bool watching = _soundBaseline && _savedSettings.MuteArrivingClients && !_arrivalMutePaused && _soundPermitted && _ready
            && !IsLocked && !_shuttingDown;
        var current = new HashSet<SessionProcessKey>();
        HashSet<int>? unreadable = null;
        // Only identity-unavailable rows pass FreshClientInventory with a Problem, and every PID in it is distinct.
        foreach (var row in rows)
            if (row.Problem is null) current.Add(row.ProcessKey);
            else (unreadable ??= []).Add(row.ProcessId);
        if (unreadable is not null)
            foreach (var key in _soundSeenClients) if (unreadable.Contains(key.ProcessId)) current.Add(key);
        if (watching)
            foreach (var key in current)
                if (!_soundSeenClients.Contains(key) && !_soundUnreadClients.Contains(key.ProcessId)) _soundArrivals.TryAdd(key, now);
        _soundSeenClients.Clear(); _soundSeenClients.UnionWith(current); _soundBaseline = true;
        // Keep only PIDs this scan still could not read, so the set never outgrows one scan (at most 128): a PID that now reads
        // is known through _soundSeenClients, and one that is gone may be reused by a genuinely new client.
        if (unreadable is null) _soundUnreadClients.Clear();
        else
        {
            _soundUnreadClients.IntersectWith(unreadable);
            if (!watching) _soundUnreadClients.UnionWith(unreadable);
        }
        foreach (var key in _soundArrivals.Keys.ToArray())
            if (!watching || !current.Contains(key)) _soundArrivals.Remove(key);
        if (_soundArrivals.Count == 0 || IsChangingClientSound) return;
        var arrived = rows.Where(row => row.Problem is null && _soundArrivals.ContainsKey(row.ProcessKey)).ToArray();
        var pinned = PinFreshClientInstances(arrived, out _);
        if (pinned.Length > 0) _ = StartClientSoundWork(pinned, ClientAudioMuteAction.Mute, arrival: true);
    }

    /// <summary>
    /// One operation at a time reaches the control. An arrival operation starts only when nothing else runs, and it never turns
    /// Mute and Unmute off, so a click during one is kept: it waits for that operation to finish and then runs. Each operation
    /// waits for the one before it, so <see cref="_clientSoundWork"/>, the latest, is the one Exit joins. An explicit
    /// operation's failure reaches its command; an arrival operation reports its own, because no command awaits it.
    /// </summary>
    private Task StartClientSoundWork(SessionObservation[] pinned, ClientAudioMuteAction action, bool arrival)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (arrival) { _arrivalSoundStop = stop; _mutingArrivals = true; }
        else
        {
            _clientSoundStop = stop; _changingClientSound = true;
            _clientSoundStatus = "Checking the selected clients and changing their sound…";
        }
        long generation = _clientSoundGeneration;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientSoundWork = RunClientSoundAsync(pinned, action, generation, stop, published.Task, _clientSoundWork, arrival);
        try
        {
            if (!arrival) ClientSoundResults.Clear();
            Raise(nameof(IsChangingClientSound)); Raise(nameof(ClientSoundStatus)); Raise(nameof(CanChangeClientSound));
        }
        finally { published.TrySetResult(); }
        return _clientSoundWork;
    }

    private async Task RunClientSoundAsync(SessionObservation[] pinned, ClientAudioMuteAction action,
        long generation, CancellationTokenSource stop, Task published, Task previous, bool arrival)
    {
        await published;
        try
        {
            // Only a click can start while another operation, an arrival one, is unfinished; it waits for it here. Stop does not cut
            // the wait short (it cancels that operation too), so this one never ends first. Any failure of the earlier operation
            // was already reported, by itself or by its command.
            await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            stop.Token.ThrowIfCancellationRequested();
            var result = await _clientSound.ChangeAsync(Array.AsReadOnly(pinned), action, stop.Token);
            if (result.Results.Count != pinned.Length || result.PendingRestoreCount is < 0 or > ClientAudioMuteBatchResult.MaximumProcesses
                || result.Results.Any(row => row is null || row.Action != action || !Enum.IsDefined(row.Status)
                    || !pinned.Any(client => client.InstanceKey == row.Session.InstanceKey))
                || result.Results.Select(row => row.Session.InstanceKey).Distinct().Count() != pinned.Length)
                throw new InvalidOperationException("The client sound report did not match the pinned selection.");
            bool stopped = stop.IsCancellationRequested || generation != _clientSoundGeneration || IsLocked || _shuttingDown;
            if (arrival) PublishArrivalSound(result, stopped);
            else
            {
                // Retain actual partial outcomes after Stop. The global stop notice keeps its own state.
                foreach (var row in result.Results) ClientSoundResults.Add(row);
                ClientSoundStatus = stopped ? "Sound work stopped. Review the individual results below." : result.Explanation;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { if (!arrival) ClientSoundStatus = "Sound work cancelled. A change Windows already accepted stays until Unmute or Exit."; }
        catch (Exception error) when (arrival) { ReportClientSoundFailure(error); }
        finally
        {
            if (arrival) { if (ReferenceEquals(_arrivalSoundStop, stop)) _arrivalSoundStop = null; _mutingArrivals = false; }
            else { if (ReferenceEquals(_clientSoundStop, stop)) _clientSoundStop = null; _changingClientSound = false; }
            stop.Dispose();
            Raise(nameof(IsChangingClientSound)); Raise(nameof(CanChangeClientSound)); Raise(nameof(PendingSoundRestoreCount));
        }
    }

    /// <summary>
    /// A client with no sound yet keeps waiting for a later scan until <see cref="ArrivalMuteWindow"/> has passed; every other
    /// outcome, and that last "no sound yet", is final and shown, so a client that was never muted is reported.
    /// </summary>
    private void PublishArrivalSound(ClientAudioMuteBatchResult result, bool stopped)
    {
        if (stopped) return; // Panic, Lock and Cancel already dropped the waiting arrivals; their own notice stays.
        var now = DateTimeOffset.UtcNow;
        var final = new List<ClientAudioMuteResult>();
        foreach (var row in result.Results)
        {
            var key = row.Session.ProcessKey;
            if ((row.Status is ClientAudioMuteStatus.NoAudioSession or ClientAudioMuteStatus.Busy)
                && _soundArrivals.TryGetValue(key, out var firstSeen) && now - firstSeen <= ArrivalMuteWindow) continue;
            _soundArrivals.Remove(key);
            final.Add(row);
        }
        // A Mute or Unmute clicked while this ran has cleared the list for its own results, which would replace these anyway.
        if (final.Count == 0 || _changingClientSound) return;
        ClientSoundResults.Clear();
        foreach (var row in final) ClientSoundResults.Add(row);
        // Only clients Zenith will unmute count as muted by it; one already muted for another reason is named separately.
        int muted = final.Count(row => row.Status is ClientAudioMuteStatus.Applied or ClientAudioMuteStatus.Unchanged
            && row.Current == true && row.RestorePending);
        int kept = final.Count(row => row.Status == ClientAudioMuteStatus.Unchanged && row.Current == true && !row.RestorePending);
        ClientSoundStatus = $"{muted} of {final.Count} new clients muted as they appeared. Unmute or Exit turns the sound Zenith muted on again."
            + (kept == 0 ? "" : kept == 1 ? " 1 was already muted and stays muted on Exit." : $" {kept} were already muted and stay muted on Exit.");
    }

    /// <summary>Panic, Lock, Cancel and Exit: stops the current change and pauses arrival muting for the rest of this run.</summary>
    private void StopClientSoundOperations()
    {
        _clientSoundGeneration++;
        _soundArrivals.Clear();
        _arrivalMutePaused = true;
        RaiseArrivalMute();
        _clientSoundStop?.Cancel();
        _arrivalSoundStop?.Cancel();
        _clientSound.CancelOperations();
    }

    /// <summary>
    /// The explicit restart. Every fresh scan still records the clients it names while paused, so a client that appeared then is
    /// already running, not an arrival; only clients that appear after this are muted.
    /// </summary>
    private void ResumeArrivalMuting()
    {
        if (!CanResumeArrivalMute) return;
        ResetArrivalMute();
        ClientSoundStatus = "Muting new clients resumed. Clients already running are left alone; use Mute for them.";
    }

    /// <summary>
    /// After an explicit Save settings: saving the preference on after it was off is an explicit start and ends a pause, as does
    /// saving it off. Saving it on again unchanged keeps the pause.
    /// </summary>
    private void ArrivalMutePreferenceSaved(bool wasOn)
    {
        if (!wasOn || !_savedSettings.MuteArrivingClients) ResetArrivalMute();
        else RaiseArrivalMute();
    }

    private void ResetArrivalMute()
    {
        _arrivalMutePaused = false;
        _soundArrivals.Clear();
        RaiseArrivalMute();
    }

    private void RaiseArrivalMute()
    { Raise(nameof(IsArrivalMutePaused)); Raise(nameof(CanResumeArrivalMute)); Raise(nameof(ArrivalMuteStatus)); }

    private void ReportClientSoundFailure(Exception error)
    {
        ClientSoundStatus = "The sound change could not be confirmed. Review the clients in Volume mixer before trying again.";
        Fail(error, ManagerSubsystem.ClientSound);
    }

    private async Task DisposeClientSoundFeaturesAsync()
    {
        StopClientSoundOperations();
        try { await _clientSoundWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientSound, error.GetType()); }
        PropertyChanged -= ClientSoundOwnerChanged;
        try { await _clientSound.DisposeAsync(); }
        catch (Exception error)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.ClientSound, error.GetType());
            ClientSoundStatus = "Windows could not confirm every client Zenith muted was unmuted. Turn their sound on in Volume mixer.";
        }
    }
}
