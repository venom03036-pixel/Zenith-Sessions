using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Zenith.MacroHost.Contracts;

namespace Zenith.Sessions.App;

public sealed partial class AccountRow(AccountProfile profile) : Observable
{
    public AccountProfile Profile { get; } = profile;
    private AuthorizationMetadata? _authorizationMetadata;
    public string Label => _authorizationMetadata is { VerifiedUsername.Length:>0 } known?known.VerifiedUsername:Profile.Label;
    public string Alias => Profile.Label;
    public string Initial => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "Z";
    public string Username => _authorizationMetadata is { VerifiedUsername.Length:>0 } known?"@"+known.VerifiedUsername:
        Profile.Username.Length==0?"Sign in to identify this account":"@"+Profile.Username+" · entered label";
    public long? KnownUserId=>_authorizationMetadata?.VerifiedUserId;
    public long? AuthorizationGeneration=>_authorizationMetadata?.Generation;
    /// <summary>Last-known summary, carried to the next page load only while the protected store is busy.</summary>
    internal AuthorizationMetadata? CachedAuthorization=>_authorizationMetadata;
    internal const string BusyIdentity="Sign-in status busy";
    /// <summary>The protected store was busy, so this row's saved sign-in was not read. It is not a missing sign-in.</summary>
    public bool SignInStatusBusy=>_identity==BusyIdentity;
    public string Group => Profile.GroupsSummary;
    public string FavoriteLabel=>Profile.IsFavorite?"★ Favorite":"☆ Add favorite";
    public string Notes=>Profile.Notes.Length==0?"No notes added.":Profile.Notes;
    private string _identity="Not linked", _linkedAccount="No official account linked";
    private DateTimeOffset? _expiresUtc;
    private bool _signInExpired; // The cached summary's expiry has passed (at load or on the clock).
    public string Identity => _identity;
    public string LinkedAccount => _linkedAccount;
    /// <summary>Sets the status wording only. The row's button still follows the cached summary (<see cref="NeedsSignIn"/>).</summary>
    public void SetAuthorization(string state,string account,DateTimeOffset? expiresUtc=null)
    { _identity=state; _linkedAccount=account; _expiresUtc=expiresUtc; Raise(nameof(Identity)); Raise(nameof(LinkedAccount)); Raise(nameof(PowerLabel)); Raise(nameof(PowerExplanation)); }
    public void SetCachedAuthorization(AuthorizationMetadata metadata)
    {
        if(_publicData is not null && _authorizationMetadata?.VerifiedUserId!=metadata.VerifiedUserId) ClearPublicPresentation();
        _authorizationMetadata=metadata; _signInExpired=metadata.ExpiresUtc<=DateTimeOffset.UtcNow;
        string state=metadata.Health!=AuthorizationHealth.Linked?"Sign in required":_signInExpired?"Expired link":"Saved link";
        SetAuthorization(state,$"Last-known linked ID {metadata.VerifiedUserId}. Authorization is checked before use.",metadata.ExpiresUtc);
        Raise(nameof(Label));Raise(nameof(Initial));Raise(nameof(Username));Raise(nameof(KnownUserId));Raise(nameof(AuthorizationGeneration));
    }
    public void UpdateAuthorizationClock(DateTimeOffset now)
    {
        bool changed=false;
        if(_authorizationMetadata is { } metadata && !_signInExpired && now>=metadata.ExpiresUtc) { _signInExpired=true; changed=true; }
        if(_identity is "Linked" or "Saved link" && _expiresUtc is { } expiry && now>=expiry) { _identity="Expired"; Raise(nameof(Identity)); changed=true; }
        if(changed) { Raise(nameof(PowerLabel));Raise(nameof(PowerExplanation)); }
    }
    /// <summary>The preferred game: the public directory's name when one was found for this place (selected row only), else the place ID.</summary>
    public string Destination => Profile.PreferredPlaceId is not { } place ? "No game selected"
        : _preferredGame is { } game && game.PlaceId==place ? $"{game.Name} · Place {place}" : $"Place {place}";
    private bool _checked;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

public sealed record GroupFilter(string Label,string? Value,string? ColorHex=null)
{
    public static GroupFilter All { get; }=new("All groups",null);
    public override string ToString()=>Label;
}
public sealed record AccountSortChoice(string Label,AccountOrder Value);
public sealed class LaunchRow(LaunchOperation operation) : Observable
{
    public LaunchOperation Operation { get; private set; }=operation;
    public Guid Id=>Operation.Intent.OperationId;
    private string _label="Saved profile";
    public string Label { get=>_label;set=>Set(ref _label,value); }
    public string State=>Operation.State switch {LaunchState.Preparing=>"Preparing account",LaunchState.Blocked when Operation.Problem==LaunchProblem.PreparationInterrupted=>"Review preparation",LaunchState.Blocked when Operation.Problem==LaunchProblem.Interrupted=>NotStartedInterrupted,LaunchState.Arrived=>"Arrival verified",LaunchState.ReviewRequired=>"Review required",LaunchState.ClientStartedUnverified=>"Client started · unverified",_=>Operation.State.ToString()};
    public string Detail=>LaunchDescriptions.Explain(Operation);
    /// <summary>A recovered request that never started a client (Blocked/Interrupted). It can only be cancelled, so it never says review.
    /// The account list uses the same words.</summary>
    internal const string NotStartedInterrupted="Not started · interrupted";
    public string RequestedDestination=>$"Place {Operation.Intent.PlaceId}"+(Operation.Intent.PrivateServer is not null?" · saved private server (arrival unverified)":Operation.Intent.RequestedJobId is { } job?$" · {job}":" · server not specified");
    public string ProcessReceipt=>Operation.ProcessReceipt is{ } receipt?$"PID {receipt.ProcessId} · start {new DateTimeOffset(receipt.ProcessStartUtcTicks,TimeSpan.Zero).ToLocalTime():T} · not a liveness check":"No process-start receipt";
    public string ResourceEstimate=>Operation.ResourceBudget is { } budget
        ? $"Pinned estimate: {budget.PhysicalBytes/(double)CapacityPolicy.GiB:0.00} GiB physical / {budget.CommitBytes/(double)CapacityPolicy.GiB:0.00} GiB commit"
        : "Legacy request: original memory estimate was not recorded";
    public bool CanCancel=>Operation.State is LaunchState.Queued or LaunchState.Blocked or LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing;
    public bool CanReview=>IsReviewable(Operation);
    public static bool IsReviewable(LaunchOperation operation)=>operation.State is LaunchState.Uncertain or LaunchState.ReviewRequired or LaunchState.Arrived or LaunchState.ClientStartedUnverified
        ||LaunchDescriptions.Terminal(operation.State)&&LaunchDescriptions.HasUnresolvedReservation(operation);
    public void Update(LaunchOperation value) { if(Operation==value) return;Operation=value;Raise(nameof(State));Raise(nameof(Detail));Raise(nameof(ProcessReceipt));Raise(nameof(ResourceEstimate));Raise(nameof(CanCancel));Raise(nameof(CanReview)); }
}

public sealed class SessionRow(SessionObservation observation) : Observable
{
    private bool _checked;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    private bool _forceCloseOffered;
    /// <summary>R-instances-12: true only after Close left this exact client open. Panic, Lock, exit, a force close and the client's exit withdraw it.</summary>
    public bool ForceCloseOffered { get => _forceCloseOffered; internal set => Set(ref _forceCloseOffered, value); }
    private SessionLogView _log = SessionLogView.Unknown;
    public SessionLogView Log => _log;
    public void UpdateLog(SessionLogView value) { if (_log != value) { _log = value; Raise(nameof(Log)); } }
    public SessionObservation Observation { get; private set; } = observation;
    public string InstanceKey => Observation.InstanceKey;
    public SessionProcessKey ProcessKey => Observation.ProcessKey;
    public string ProcessLabel => Observation.ProcessLabel;
    public string Status => Observation.Status;
    public string MemoryDisplay => Observation.Problem is null ? Observation.MemoryDisplay : "Unavailable";
    public string PrivateCommitBytes => Observation.Problem is null ? $"{Observation.PrivateCommitBytes / (double)CapacityPolicy.GiB:0.00} GiB" : "Unavailable";
    public double? CpuPercent => Observation.Problem is null && Observation.CpuPercent is { } percent && double.IsFinite(percent) && percent is >=0 and <=100?percent:null;
    public string PrivateWorkingSet=>Observation.PrivateWorkingSetBytes is { } bytes && Observation.Problem is null?$"{bytes/(double)CapacityPolicy.GiB:0.00} GiB":"Unavailable";
    public string VirtualAddress=>Observation.VirtualAddressBytes is { } bytes && Observation.Problem is null?$"{bytes/(double)CapacityPolicy.GiB:0.00} GiB":"Unavailable";
    public string PageFaults=>Observation.PageFaultsPerSecond is { } rate && Observation.Problem is null?$"{rate:0.0}":"Unavailable";
    public string IoTransfers=>Observation.Problem is null && Observation.IoRates is { } io
        ? "R "+TelemetryDisplay.Number(io.ReadBytesPerSecond/1024,"0.0"," KiB/s")+" · W "+TelemetryDisplay.Number(io.WriteBytesPerSecond/1024,"0.0"," KiB/s")+" · other "+TelemetryDisplay.Number(io.OtherBytesPerSecond/1024,"0.0"," KiB/s") : "Unavailable / warming up";
    public string ObservedTime=>Observation.ObservedUtc.LocalDateTime.ToString("T");
    private string _gpu=ClientGpuText.Off;
    /// <summary>This client's busiest GPU engine and dedicated GPU memory, from the latest fresh snapshot's confirmed client row
    /// (matched by PID and start time); "Off in Settings" until the saved option is on; never a number for an unknown value.</summary>
    public string Gpu=>_gpu;
    public void UpdateGpu(GpuClientTelemetry? client,bool fresh,bool enabled)
    { string text=ClientGpuText.Format(client,fresh,enabled); if(_gpu!=text) { _gpu=text; Raise(nameof(Gpu)); } }
    public void Update(SessionObservation value)
    {
        var prior = Observation; Observation = value;
        if(prior.ObservedUtc!=value.ObservedUtc) Raise(nameof(ObservedTime));
        if (prior.Status != value.Status) Raise(nameof(Status));
        if (prior.WorkingSetBytes != value.WorkingSetBytes || prior.Problem != value.Problem) Raise(nameof(MemoryDisplay));
        if (prior.PrivateCommitBytes != value.PrivateCommitBytes || prior.Problem != value.Problem) Raise(nameof(PrivateCommitBytes));
        if (prior.CpuPercent != value.CpuPercent || prior.Problem!=value.Problem) Raise(nameof(CpuPercent));
        if(prior.PrivateWorkingSetBytes!=value.PrivateWorkingSetBytes || prior.Problem!=value.Problem) Raise(nameof(PrivateWorkingSet));
        if(prior.VirtualAddressBytes!=value.VirtualAddressBytes || prior.Problem!=value.Problem) Raise(nameof(VirtualAddress));
        if(prior.PageFaultsPerSecond!=value.PageFaultsPerSecond || prior.Problem!=value.Problem) Raise(nameof(PageFaults));
        if(prior.IoRates!=value.IoRates || prior.Problem!=value.Problem) Raise(nameof(IoTransfers));
    }
}

public sealed partial class ShellViewModel : Observable, IAsyncDisposable
{
    private readonly IAccountRepository _accounts;
    private readonly SettingsStore _settingsStore;
    // Dispatcher-owned: a completed local write cannot reactivate work stopped after its request began.
    private long _settingsActivationGeneration;
    private bool SettingsActivationCurrent(long generation) => generation == _settingsActivationGeneration && !_shuttingDown;
    private readonly IFavoriteDestinationStore _favoriteStore;
    public FavoriteDestinationsViewModel DestinationFavorites { get; }
    private readonly IDesktopMonitor _monitor;
    private WindowsSessionLogCorrelator? _logCorrelator;
    private readonly SessionLogProjection _logProjection = new();
    private readonly ProtectedCredentialVault _vault;
    private readonly IRobloxSessionClient _sessionClient;
    private readonly IRobloxSessionSignIn _signIn;
    private readonly RobloxSessionLinkService _linkService;
    private readonly IRobloxShareLinkClient _shareLinkClient;
    private readonly IRobloxLaunchTicketClient _launchTicketClient;
    private readonly IPrivateServerStore _privateServerStore;
    private readonly AccountIdentityProjection _identityProjection;
    private readonly CredentialCleanupService _credentialCleanup;
    private readonly ManagerDiagnostics _diagnostics;
    private readonly LaunchQueueStore _queueStore;
    private readonly LaunchCoordinator _launchQueue;
    private LaunchQueueSnapshot? _pendingQueue;
    private LaunchQueueSnapshot _latestQueue=LaunchQueueSnapshot.Empty;
    private int _queueUiScheduled;
    private bool _queueReady,_queuePaused=true;
    private LaunchQueuePauseReason _shownQueuePauseReason;
    private sealed record ResourceAdmissionSnapshot(DesktopSnapshot Desktop,LaunchAdmission Admission);
    private ResourceAdmissionSnapshot? _resourceAdmission;
    private DesktopSnapshot? _latestDesktop;
    // When the newest fresh complete process inventory was observed (UTC ticks; 0 before one). Noted by the sampler and by
    // admission, so it is current whether or not the queue is deciding; it only moves forward.
    private long _trustedInventoryUtcTicks;
    private string _queueStatus="Opening launch queue…";
    private bool _queueExpanded;
    public bool QueueExpanded { get=>_queueExpanded;set=>Set(ref _queueExpanded,value); }
    public string QueueStatus { get=>_queueStatus;private set=>Set(ref _queueStatus,value); }
    public bool QueueReady { get=>_queueReady;private set {if(Set(ref _queueReady,value)) {if(value) RefreshVisibleQueueStatus();Raise(nameof(CanSetAsideInterruptedQueueWrite));RefreshOverview();}} }
    /// <summary>Offered only while the queue is unavailable because an interrupted save sits behind a readable queue.</summary>
    public bool CanSetAsideInterruptedQueueWrite=>!QueueReady && _queueStore.RecoveryBlocker is {Kind:LaunchQueueBlockerKind.InterruptedWrite};
    /// <summary>The question the view asks before Set aside interrupted save. A queue that was
    /// already running cannot reopen in this run, so that question promises only a restart.</summary>
    public string SetAsideInterruptedQueueWriteConfirmation=>(_launchQueue.Opened
        ?"An interrupted save (launch-queue.json.tmp) stopped the launch queue. Rename it to a kept launch-queue.json.interrupted file? Nothing is deleted. The queue stays stopped until you restart Zenith Sessions; it then reopens paused from its last completed save and no client is started."
        :"An interrupted save (launch-queue.json.tmp) is blocking the launch queue. Rename it to a kept launch-queue.json.interrupted file and reopen the queue from its last completed save? Nothing is deleted. The queue reopens paused; nothing starts on its own.")
        +" A client started while that save was being written will appear as needing review.";
    public string QueuePauseLabel=>EffectiveQueuePaused?"Resume queue":"Pause queue";
    private readonly ResourcePressureTracker _pressure=new();
    private CancellationTokenSource? _authorizationStop;
    private Guid? _linkingProfile;
    private bool _preserveStopNotice;
    // UI-thread only: set by ReportPresentationFault, cleared when a sample presentation completes. So it says whether the
    // newest sample taken by any dispatcher step is on screen, which is what a manual Refresh reports.
    private bool _presentationFaulted;
    private const string RefreshPresentationFailed="Refresh could not show the latest measurements, so the dashboard may be out of date. Monitoring and launch safety checks continue.";
    private readonly Dispatcher _dispatcher;
    // The shell's monotonic clock (GetTimestamp): the idle lock and arrival muting measure elapsed time on it, so a wall-clock
    // change neither shortens nor extends them and tests drive both without waiting. Wall-clock stamps that are compared with
    // scans and files (DateTimeOffset.UtcNow) are not routed through it.
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queryCancellation;
    private Task _queryTask = Task.CompletedTask, _monitorTask = Task.CompletedTask, _initializeTask = Task.CompletedTask;
    /// <summary>The owned debounced account query or busy-store retry; shutdown joins it.</summary>
    internal Task AccountQuery=>_queryTask;
    private Task? _disposeTask;
    private readonly HashSet<Task> _profileOperations=[];
    /// <summary>Profile ids whose removal is queued or running; touched only on the UI thread.</summary>
    private readonly HashSet<Guid> _pendingProfileRemovals=[];
    private readonly SemaphoreSlim _profileSerial=new(1,1);
    private bool _shuttingDown,_settingsNeedRepair,_libraryUnavailable;
    private Guid? _selectAfterReload;
    private bool _initializationStarted;
    private SessionsSettings _settings = new SessionsSettings().Validated();
    private SessionsSettings _savedSettings = new SessionsSettings().Validated();
    private readonly SemaphoreSlim _preferencesWriter = new(1, 1);
    private int _pendingSaves;
    private bool _saveFailed;
    private string _pressureNotice="Waiting for a coherent resource sample.",_diagnosticsStatus="Diagnostics starting";
    private string _credentialCleanupStatus="Credential cleanup has not run.";
    // Set once by the startup sign-in folder sweep; empty when nothing was kept. Kept with each credential cleanup status.
    private string _signInFolderNote="";
    public string CredentialCleanupStatus { get=>_credentialCleanupStatus; private set=>Set(ref _credentialCleanupStatus,value); }
    public string PressureNotice { get=>_pressureNotice; private set=>Set(ref _pressureNotice,value); }
    public string DiagnosticsStatus { get=>_diagnosticsStatus; private set=>Set(ref _diagnosticsStatus,value); }
    private int _queryGeneration, _page;
    private int _filteredTotal;
    private bool _updatingGroups;
    private bool _ready, _locked;
    private string _tab = "Overview", _search = "", _notice = "Starting local services…", _errorNotice = "";
    private GroupFilter _group=GroupFilter.All;
    private AccountOrder _order;
    private AccountRow? _selected;
    private int _accountCount;
    private string _capacity = "Waiting for measurements";
    private string _pageLabel = "0 accounts", _saveStatus = "Not loaded";
    public string DataRoot { get; }
    public bool IsSample { get; }
    // Several managers can run at once (one per data folder), so the tray, window and taskbar say which one this is.
    public string InstanceTitle { get; }
    public bool IsReady=>_ready;
    // The sidebar's library label; it names the same library as InstanceTitle, so another folder never reads as the personal one.
    public string ProductLabel { get; }
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    private SessionRow? _selectedSession;
    public SessionRow? SelectedSession { get => _selectedSession; set { if (Set(ref _selectedSession,value)) { Raise(nameof(HasSelectedSession)); Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates));FollowSelectedSessionInHistory(value);RefreshTelemetryPresentation(); } } }
    public bool HasSelectedSession => SelectedSession is not null;
    public ObservableCollection<LaunchRow> Launches { get; }=[];
    public IReadOnlyList<GroupFilter> Groups { get; private set; } = [GroupFilter.All];
    public ObservableCollection<string> Events { get; } = [];
    public IReadOnlyList<ThemePreset> Themes => SessionsThemes.Choices;
    public RecentClientHistoryViewModel RecentClients { get; }
    private string _processInventoryStatus="Process inventory freshness has not been established.";
    public string ProcessInventoryStatus {get=>_processInventoryStatus;private set=>Set(ref _processInventoryStatus,value);}
    public IReadOnlyList<ProviderCapability> Capabilities => ProviderCapabilities.Current;
    public IReadOnlyList<AccountOrder> Orders { get; } = Enum.GetValues<AccountOrder>();
    public IReadOnlyList<AccountSortChoice> SortChoices { get; } = [new("Name",AccountOrder.Name),new("Priority",AccountOrder.Priority),new("Recently updated",AccountOrder.RecentlyUpdated),new("Favorites first",AccountOrder.FavoritesFirst)];
    public AccountSortChoice SortChoice { get=>SortChoices.First(c=>c.Value==Order); set { if(value is not null) Order=value.Value; } }
    public string Tab { get => _tab; private set { if (Set(ref _tab, value)) {foreach (var name in new[] { nameof(IsOverview), nameof(IsAccounts), nameof(IsSessions),nameof(IsJoinGame),nameof(IsSessionWorkspace),nameof(IsWindows), nameof(IsAutomation), nameof(IsDiagnostics),nameof(IsSettings), nameof(IsGuide), nameof(IsNews), nameof(PageName), nameof(WindowTitle) }) Raise(name);if(IsDiagnostics) RefreshDiagnosticsView(true);RefreshClientPreviewContext();RefreshTelemetryPresentation();RefreshPublicImageActivity();RefreshJoinGameSources(opened:IsJoinGame);if(IsOverview) RefreshOverview();if(IsJoinGame) ResetJoinFlow();News?.SetActive(IsNews);} } }
    public bool IsOverview => Tab == "Overview"; public bool IsAccounts => Tab == "Accounts"; public bool IsSessions => Tab == "Sessions";
    public bool IsAutomation => Tab == "Automation"; public bool IsSettings => Tab == "Settings"; public bool IsGuide => Tab == "Guide";
    public bool IsJoinGame=>Tab=="JoinGame";
    public bool IsSessionWorkspace=>IsSessions || IsJoinGame;
    public bool IsDiagnostics=>Tab=="Diagnostics";
    public bool IsWindows=>Tab=="Windows";
    // The open page as the sidebar names it. The window title leads with it (taskbar, Alt-Tab) and the window announces it to
    // screen readers, because a shortcut such as Ctrl+2 changes the page without moving focus. The manager name stays after it.
    public string PageName=>Strings["Page_"+Tab];
    public string WindowTitle=>PageName+" · "+InstanceTitle;
    public bool IsLocked { get => _locked; private set { if (Set(ref _locked, value)) { Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates)); Raise(nameof(CanRefreshInstallations)); Raise(nameof(CanVerifyInstallationPublisher)); NotifyPerformanceTransfer(); RefreshDestinationCapacity(); GameDetails?.RefreshAvailability(); RecentClients?.RefreshAvailability();RefreshClientPreviewContext(); MacroHostCatalog?.RefreshAvailability(); MacroAssignments?.RefreshAvailability(); News?.RefreshAvailability(); RefreshPublicImageActivity();RefreshOverview(); } } }
    private bool _compactLayout;
    public bool CompactLayout { get=>_compactLayout; set=>Set(ref _compactLayout,value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) ScheduleQuery(); } }
    public GroupFilter Group { get => _group; set { if(_updatingGroups && value is null) return;if (Set(ref _group, value ?? GroupFilter.All) && !_updatingGroups) ScheduleQuery(); } }
    public AccountOrder Order { get => _order; set { if (Set(ref _order, value)) { Raise(nameof(SortChoice)); ScheduleQuery(); } } }
    public AccountRow? SelectedAccount { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(HasSelection)); RefreshDestinationCapacity(); RefreshJoinGameSources(); RefreshPreferredGameName(); } } }
    public bool HasSelection => SelectedAccount is not null;
    public bool CanNext => (_page + 1L) * 100 < _filteredTotal;
    public bool CanPrevious => _page > 0;
    public string EmptyAccountsText => !_ready?(_libraryUnavailable?"Library unavailable. Existing files were preserved; open Settings for recovery.":"Opening your local library…")
        :AccountCount == 0 ? "Add your first account profile to get started." : "No profiles match this search or group.";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    /// <summary>
    /// The last failure, shown beside <see cref="Notice"/> in the error colour until it is dismissed, replaced by a newer
    /// failure or cleared by Panic or Lock, so a later notice cannot hide it. Empty when there is none.
    /// </summary>
    public string ErrorNotice => _errorNotice;
    public bool HasErrorNotice => _errorNotice.Length > 0;
    public Command DismissError { get; }
    public string PageLabel { get => _pageLabel; private set => Set(ref _pageLabel, value); }
    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public int AccountCount { get => _accountCount; private set => Set(ref _accountCount, value); }
    public string Capacity { get => _capacity; private set => Set(ref _capacity, value); }
    public bool Dense { get => _settings.DenseAccounts; set { if (_settings.DenseAccounts == value) return; _settings = _settings with { DenseAccounts = value }; Raise(); PreferencesEdited(); } }
    private string _spareRam = "3", _physicalPerClient = "2.5", _commitPerClient = "3", _maximumClients = "", _oauthClientId = "", _callbackPort="47635";
    public string SpareRam { get => _spareRam; set { if (Set(ref _spareRam, value)) PreferencesEdited(); } }
    public string PhysicalPerClient { get => _physicalPerClient; set { if (Set(ref _physicalPerClient, value)) PreferencesEdited(); } }
    public string CommitPerClient { get => _commitPerClient; set { if (Set(ref _commitPerClient, value)) PreferencesEdited(); } }
    public string MaximumClients { get => _maximumClients; set { if (Set(ref _maximumClients, value)) PreferencesEdited(); } }
    public string OAuthClientId { get => _oauthClientId; set { if (Set(ref _oauthClientId, value)) PreferencesEdited(); } }
    public string CallbackPort { get => _callbackPort; set { if (Set(ref _callbackPort,value)) PreferencesEdited(); } }
    public string CallbackUrl => int.TryParse(CallbackPort,out int port) ? $"http://localhost:{port}/zenith/oauth/" : "Enter a valid callback port";
    private bool _closeButtonExits;
    /// <summary>Settings draft: the window's close button exits this manager instead of hiding it to the tray. Applies after Save settings.</summary>
    public bool CloseButtonExits { get => _closeButtonExits; set { if (Set(ref _closeButtonExits, value)) PreferencesEdited(); } }
    /// <summary>The close button's name and tooltip: what it does with the saved choice, never an unsaved draft.</summary>
    public string CloseButtonLabel => _savedSettings.CloseButtonExits ? "Exit manager" : "Close to tray";
    /// <summary>
    /// UI thread, when the window is asked to close: true when the saved choice is to exit and this manager is not already
    /// stopping (tray Exit under way, or Windows ending the session), so a close never starts a second exit. False keeps the
    /// default, hide to the tray.
    /// </summary>
    internal bool CloseButtonExitsNow => _savedSettings.CloseButtonExits && !_shuttingDown;
    public bool IsAuthorizing => _authorizationStop is not null;
    public string ThemeName => SessionsThemes.Choice(_settings.Theme).Name;
    public Command Navigate { get; }
    public Command OpenFolder { get; }
    public Command Panic { get; }
    public Command Lock { get; }
    public Command Unlock { get; }
    public AsyncCommand SaveSettings { get; }
    public AsyncCommand ApplyTheme { get; }
    public AsyncCommand NextPage { get; }
    public AsyncCommand PreviousPage { get; }
    public AsyncCommand FocusSession { get; }
    public AsyncCommand MinimizeSession { get; }
    public AsyncCommand RestoreSession { get; }
    public AsyncCommand ToggleFavorite { get; }
    public AsyncCommand QueueSelected { get; }
    public AsyncCommand ToggleQueuePause { get; }
    public AsyncCommand CancelLaunch { get; }
    public AsyncCommand SetAsideInterruptedQueueWrite { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand LinkAccount { get; }
    public AsyncCommand AddRobloxAccount { get; }
    public AsyncCommand RefreshAuthorization { get; }
    public AsyncCommand DisconnectAccount { get; }
    public AsyncCommand CleanupCredentials { get; }
    public Command CancelAuthorization { get; }
    public event Action<ThemePreset>? ThemeChanged;
    public event Action? PanicRequested;
    public ReleaseUpdatesViewModel ReleaseUpdates { get; }

    public ShellViewModel(string root, bool sample, Dispatcher dispatcher,IDesktopMonitor? monitor=null,
        IPublicRobloxDirectory? publicDirectory=null,IRobloxPublicServerDirectory? serverDirectory=null,IAccountRepository? accounts=null,
        IFavoriteDestinationStore? favoriteStore=null, IPerformanceSettingsTransferStore? performanceTransferStore=null,
        IClientInstallationDiscovery? installationDiscovery=null,IPublicRobloxGameDirectory? gameDirectory=null,
        ISessionHistoryStore? historyStore=null,IClientPreviewProvider? previewProvider=null,IRobloxSessionSignIn? signIn=null,
        IRobloxSessionClient? sessionClient=null,IClientExecutableTrustVerifier? executableTrust=null,IRecipientBundleStore? recipientBundleStore=null,
        IRobloxHandoffProbe? handoffProbe=null,WindowsConfiguredNativeLaunchProvider? nativeProvider=null,IZenithMacroCatalogClient? macroCatalogClient=null,
        IMacroAssignmentStore? macroAssignmentStore=null,bool enableAutomaticPublicImages=false,
        IClientProcessPriorityControl? clientPriority=null,IRobloxShareLinkClient? shareLinkClient=null,IPrivateServerStore? privateServerStore=null,
        TimeProvider? idleClock=null,IClientWorkingSetTrimControl? clientTrim=null,IClientFaultProbe? faultProbe=null,
        IPublicRobloxClientReleaseDirectory? clientReleases=null,IClientAudioMuteControl? clientSound=null,IClientLogCleanup? clientLogCleanup=null,
        IClientLogTailReader? clientLogTails=null,IClientUpdateInstaller? clientUpdateInstaller=null,IRobloxLaunchTicketClient? launchTicketClient=null, IKeepAwakeInputExecutor? keepAwakeInput=null,IForegroundWindowSwitch? foregroundSwitch=null,IUserInputIdleSource? userIdle=null,CapabilityGate? capabilityGate=null,IShutdownScheduler? shutdownScheduler=null,IPublicNetworkMonitor? publicNetworkMonitor=null,IDiscordWebhookStore? discordWebhookStore=null,IDiscordWebhookTransport? discordTransport=null,IDiscordPresencePipe? discordPresencePipe=null,IDataFolderEncryption? dataEncryption=null,IPublicRobloxNewsFeed? newsFeed=null,IRobloxAccountDataClient? accountDataClient=null,IUploadLimitControl? uploadLimit=null,IRobloxSignedInServerClient? signedServerClient=null,IReleaseUpdateBackend? releaseUpdateBackend=null)
    {
        DataRoot = root; IsSample = sample; InstanceTitle = StartupOptions.TitleFor(root, sample); ProductLabel = StartupOptions.SidebarLabelFor(root, sample); _dispatcher = dispatcher;_enableAutomaticPublicImages=enableAutomaticPublicImages;
        _idleLock=new(idleClock);_clock=idleClock??TimeProvider.System;
        _monitor=monitor??new WindowsDesktopMonitor();
        _accounts = accounts??new AccountRepository(root); _settingsStore = new(root);
        _vault=new(root);
        _sessionClient=sessionClient??new RobloxSessionClient();
        _identityProjection=new(root,_accounts,_vault);
        _signIn=signIn??new OwnedRobloxSignIn(root,dispatcher,()=>_savedSettings.KeepSignInBrowserData);
        _shareLinkClient=shareLinkClient??new RobloxShareLinkClient();_privateServerStore=privateServerStore??new PrivateServerStore(root);
        _launchTicketClient=launchTicketClient??new RobloxLaunchTicketClient();
        _diagnostics=new(root);
        _accountDataClient=accountDataClient??new RobloxAccountDataClient();
        _signedServerClient=signedServerClient??new RobloxSignedInServerClient();
        // Saved-session upkeep after a share-link answer reports only the type of a failure it absorbs, never its text.
        _linkService=new(_accounts,_vault,_sessionClient,_signIn,_identityProjection,_shareLinkClient,failed:type=>_diagnostics.RecordFailure(ManagerSubsystem.SignIn,type),
            launchTickets:_launchTicketClient,accountData:_accountDataClient,signedServers:_signedServerClient,privateServers:_privateServerStore);
        _credentialCleanup=new(_accounts,_vault);
        InitializeTelemetry();
        InitializeClientPreviews(previewProvider);
        // Crash records only mean something for real processes, so an injected (test or synthetic) monitor gets no default probe.
        RecentClients=new(historyStore??new RecentClientHistoryStore(root),CanUseDirectories,
            error=>_diagnostics.RecordFailure(ManagerSubsystem.Telemetry,error.GetType()),faultProbe??(monitor is null?new WindowsClientFaultProbe():null));
        _queueStore=new(root);
        // One capture for the provider: the official page by default, or the direct ticket request once that mode is saved (R-api-7).
        _nativeCapture=new SelectableNativeHandoffCapture(new OwnedRobloxNativeHandoffCapture(root,dispatcher),new DirectTicketNativeHandoffCapture(_launchTicketClient));
        // An injected development provider transfers ownership. The real one reads each account's saved extra arguments (R-launch-16)
        // and each bootstrapper's accepted publisher (R-launch-17) from saved state at preparation time. Either one reads the trial the
        // user recorded as verified (R-launch-0) from the saved settings when its capability is read: without one it stays Unverified.
        _nativeLaunch=nativeProvider??new(_linkService,_nativeCapture,_vault,new SavedExtraClientArguments(this),new SavedAcceptedBootstrapperPublishers(this),new SavedNativeLaunchVerifications(this));
        if(nativeProvider is not null)nativeProvider.TryUseVerificationSource(new SavedNativeLaunchVerifications(this));
        _launchQueue=new(_queueStore,_nativeLaunch,ValidateLaunchAsync,ReadLaunchAdmission,observedReservations:ReadObservedReservations,
            budgetResolver:ReadNewLaunchBudget,candidateAdmission:ReadCandidateLaunchAdmission);
        _launchQueue.Changed+=QueueChanged;_launchQueue.Faulted+=QueueFaulted;
        ReleaseUpdates=new(root,dispatcher,()=>!_shuttingDown&&!IsLocked&&QueueReady&&_latestQueue.Operations.All(operation=>LaunchDescriptions.Terminal(operation.State)&&!LaunchDescriptions.HasUnresolvedReservation(operation)),backend:releaseUpdateBackend,canOperate:()=>_ready&&!IsSample&&!_shuttingDown&&!IsLocked);
        InitializeDirectoryFeatures(publicDirectory,serverDirectory,gameDirectory);
        InitializeDestinationCapacity();
        _favoriteStore=favoriteStore??new FavoriteDestinationsStore(root);
        DestinationFavorites=new(_favoriteStore,ServerBrowser,CanUseDirectories);
        InitializeRecipientBundles(recipientBundleStore);
        InitializePerformanceFeatures();
        InitializePerformanceTransfer(performanceTransferStore);
        InitializeInstallationFeatures(installationDiscovery,executableTrust,clientReleases);
        InitializeClientUpdateFeatures(clientUpdateInstaller);
        InitializeDiagnosticsFeatures(clientLogTails);
        InitializeDataEncryption(dataEncryption);InitializeNews(newsFeed);
        InitializeVaultPasswordFeatures();InitializePortableBackupFeatures();
        InitializeClientWindowFeatures();
        InitializeClientPriorityFeatures(clientPriority);
        InitializeClientMemoryTrimFeatures(clientTrim);
        InitializeClientSoundFeatures(clientSound);
        InitializeSignInBrowserData();
        InitializeClientLogCleanupFeatures(clientLogCleanup);
        InitializeKeepAwakeFeatures(keepAwakeInput,foregroundSwitch,userIdle,capabilityGate);
        InitializeScheduledShutdownFeatures(shutdownScheduler);
        InitializePublicNetworkGuardFeatures(publicNetworkMonitor);
        InitializeDiscordAlertFeatures(discordWebhookStore,discordTransport);
        InitializeDiscordPresenceFeatures(discordPresencePipe);
        InitializeUploadLimitFeatures(uploadLimit);
        InitializeClientCloseFeatures();
        InitializeMacroHostCatalog(macroCatalogClient);
        InitializeMacroAssignments(macroAssignmentStore);
        Navigate = new(p => { if (!IsLocked && p is string tab && new[] { "Overview", "Accounts", "Sessions", "JoinGame", "Windows", "Automation", "Diagnostics", "Settings", "Guide", "News" }.Contains(tab)) Tab = tab; });
        OpenFolder = new(_ => { try { Process.Start(new ProcessStartInfo { FileName = DataRoot, UseShellExecute = true }); } catch(System.ComponentModel.Win32Exception ex) { Fail(ex,ManagerSubsystem.Library); } });
        DismissError = new(_ => ClearError());
        Panic = new(_ => StopOperations("Panic stop: automation paused. Roblox clients remain open."));
        Lock = new(_ => LockDashboard()); Unlock = new(_ => RequestDashboardUnlock());
        SaveSettings = new(_ => SavePreferencesAsync(), error => Fail(error,ManagerSubsystem.Settings));
        ApplyTheme = new(async p => { if (_ready && p is string id) { _settings = _settings with { Theme = SessionsThemes.Normalize(id) }; ApplyEffectiveTheme(); await SaveCurrentSettingsAsync(); } }, error => Fail(error,ManagerSubsystem.Settings));
        LanguageChoices=CreateLanguageChoices();
        ApplyLanguage=new(async p=> { if(_ready && !_shuttingDown && p is string id) { _settings=_settings with { Language=ShellLanguages.Normalize(id) };ApplyEffectiveLanguage();await SaveCurrentSettingsAsync(); } },error=>Fail(error,ManagerSubsystem.Settings));
        NextPage = new(async _ => { if ((_page + 1L) * 100 < _filteredTotal) { _page++; await LoadAccountsAsync(_lifetime.Token); } }, error => Fail(error,ManagerSubsystem.Library));
        PreviousPage = new(async _ => { if (_page > 0) { _page--; await LoadAccountsAsync(_lifetime.Token); } }, error => Fail(error,ManagerSubsystem.Library));
        FocusSession = new(async p => { if (!IsLocked && p is SessionRow session) Notice = await _monitor.FocusAsync(session.Observation, _lifetime.Token) ? "Roblox window activated." : "Window changed or Windows denied activation. Select its taskbar button to bring it forward."; }, error => Fail(error,ManagerSubsystem.ClientWindows));
        MinimizeSession=new(p=>ChangeWindowAsync(p,ClientWindowAction.Minimize),error=>Fail(error,ManagerSubsystem.ClientWindows));
        RestoreSession=new(p=>ChangeWindowAsync(p,ClientWindowAction.Restore),error=>Fail(error,ManagerSubsystem.ClientWindows));
        ToggleFavorite=new(async _=> { if(!IsLocked && SelectedAccount is { } row) await SaveAccountAsync(row.Profile with { IsFavorite=!row.Profile.IsFavorite }); },error=>Fail(error,ManagerSubsystem.Library));
        QueueSelected=new(async _=>
        {
            if(IsLocked || !QueueReady || SelectedAccount is not { } selected) return;
            var binding=selected.KnownUserId is >0 && selected.AuthorizationGeneration is >0?new LaunchAccountBinding(selected.KnownUserId.Value,selected.AuthorizationGeneration.Value):null;
            var operation=await _launchQueue.EnqueueAsync(selected.Profile,_lifetime.Token,binding);
            Notice=operation.State==LaunchState.Blocked?LaunchDescriptions.Explain(operation.Problem):"Launch request queued. It is not a running client.";
            QueueExpanded=true;
        },error=>Fail(error,ManagerSubsystem.Queue));
        ToggleQueuePause=new(_=>ToggleQueuePausePresentationAsync(),error=>Fail(error,ManagerSubsystem.Queue));
        CancelLaunch=new(async p=> { if(!IsLocked && QueueReady && p is LaunchRow row) await _launchQueue.CancelAsync(row.Id,_lifetime.Token); },error=>Fail(error,ManagerSubsystem.Queue));
        SetAsideInterruptedQueueWrite=new(_=>SetAsideInterruptedQueueWriteAsync(),error=>Fail(error,ManagerSubsystem.Queue));
        Refresh = new(async _ => { if(!_ready) throw new AccountLinkException("Resolve the startup error and restart Zenith Sessions before refreshing the library."); await LoadAccountsAsync(_lifetime.Token);
            // A display fault was already recorded by type and reported once by the step that met it; the refresh only says it is not current.
            if(await SampleAsync(_lifetime.Token,forcePresentation:true)) Notice = "Latest available observations shown. Measurement times are listed.";
            else ShowError(RefreshPresentationFailed); }, error => Fail(error,ManagerSubsystem.Library));
        LinkAccount=new(_=>AuthorizeSelectedAsync(false),error=>Fail(error,ManagerSubsystem.SignIn));
        AddRobloxAccount=new(_=>AddRobloxAccountAsync(),error=>Fail(error,ManagerSubsystem.SignIn));
        RefreshAuthorization=new(_=>AuthorizeSelectedAsync(true),error=>Fail(error,ManagerSubsystem.SignIn));
        DisconnectAccount=new(_=>DisconnectSelectedAsync(),error=>Fail(error,ManagerSubsystem.SignIn));
        CancelAuthorization=new(_=>_authorizationStop?.Cancel());
        InitializeAccountHealthFeatures();
        InitializeAccountObservationFeatures();
        InitializeSignedServerFeatures();
        CleanupCredentials=new(async _=> { if(!_ready) throw new AccountLinkException("Resolve the library startup error before retrying credential cleanup."); await CleanupCredentialsAsync(); },error=>Fail(error,ManagerSubsystem.Vault));
        InitializeHandoffProbe(handoffProbe);
        InitializeNativeTrialFeatures();
        InitializeSettingsSearch();
    }

    // Once shutdown has begun the workspace never opens: the window's Loaded event can still arrive late, for example
    // while a startup-failure message is open, and an initialization started then would run outside DisposeAsync's wait.
    public Task InitializeAsync()
    { if(!_initializationStarted && !_shuttingDown) { _initializationStarted=true; _initializeTask=InitializeCoreAsync(); } return _initializeTask; }
    private async Task InitializeCoreAsync()
    {
        // Startup is automatic, not a new user resume request. Its activation authority expires on the
        // first Panic/Lock, including one before the window's Loaded event begins initialization.
        const long activationGeneration = 0;
        // The startup catch-all names the stage that was running, so a failure says which subsystem stopped startup.
        var stage=ManagerSubsystem.Settings;
        try
        {
            var loaded = await Task.Run(() => _settingsStore.LoadAsync(_lifetime.Token)); _settings = _savedSettings = loaded.Settings;
            _settingsNeedRepair=loaded.Notice is not null;
            var limits = _settings.Capacity!;
            SpareRam = limits.SparePhysicalGiB.ToString(CultureInfo.CurrentCulture); PhysicalPerClient = limits.NewClientPhysicalGiB.ToString(CultureInfo.CurrentCulture);
            CommitPerClient = limits.NewClientCommitGiB.ToString(CultureInfo.CurrentCulture); MaximumClients = limits.UserMaximum?.ToString(CultureInfo.CurrentCulture) ?? ""; OAuthClientId = _settings.OAuthClientId;
            CallbackPort=_settings.OAuthCallbackPort.ToString(CultureInfo.InvariantCulture);
            _closeButtonExits=_settings.CloseButtonExits;_muteArrivingClients=_settings.MuteArrivingClients;_checkRobloxUpdates=_settings.CheckRobloxUpdates;
            _keepSignInBrowserData=_settings.KeepSignInBrowserData; Raise(nameof(KeepSignInBrowserData));
            _defaultClientArguments=_settings.DefaultClientArguments;_shortenWaitAfterJoin=_settings.ShortenWaitAfterJoin;
            LoadNativeLaunchModeDraft(); RefreshBootstrapperLaunch();
            _showClientGpu=_settings.ShowClientGpu;ApplyClientGpu(_showClientGpu);
            _publicNetworkGuard=_settings.PublicNetworkGuard;_discordAlerts=_settings.DiscordAlerts;
            _discordPresence=_settings.DiscordPresence;_discordPresenceGame=_settings.DiscordPresenceGame;_discordApplicationId=_settings.DiscordApplicationId;
            _idleLockMinutes=_settings.IdleLockMinutes; ApplySavedIdleLock();
            _keepAwakeMinutes=_settings.KeepAwakeMinutes; ApplySavedKeepAwake(0, SettingsActivationCurrent(activationGeneration));
            _panicShortcutText=SavedPanicShortcut.ToString(); ApplySavedPanicShortcut();
            LoadPerformanceDraft(_settings.Performance!);ApplyPerformance(_settings.Performance!);
            LoadClientLayoutDraft(_settings.WindowLayout!);
            foreach (string field in new[] { nameof(SpareRam), nameof(PhysicalPerClient), nameof(CommitPerClient), nameof(MaximumClients), nameof(OAuthClientId), nameof(CallbackPort),nameof(CallbackUrl), nameof(Dense), nameof(ThemeName), nameof(CloseButtonExits), nameof(CloseButtonLabel), nameof(IdleLockMinutes), nameof(PanicShortcutText), nameof(MuteArrivingClients), nameof(DefaultClientArguments), nameof(ShortenWaitAfterJoin), nameof(CheckRobloxUpdates), nameof(ShowClientGpu), nameof(KeepAwakeMinutes), nameof(PublicNetworkGuard), nameof(DiscordAlerts), nameof(DiscordPresence), nameof(DiscordPresenceGame), nameof(DiscordApplicationId) }) Raise(field);
            ApplyEffectiveLanguage();ApplyEffectiveTheme();
            stage=ManagerSubsystem.Library;await Task.Run(() => _accounts.InitializeAsync(_lifetime.Token));
            stage=ManagerSubsystem.SignIn;await SweepSignInFoldersAsync();
            stage=ManagerSubsystem.ClientPriority;await CheckLeftoverClientCpuCapsAsync();
            stage=ManagerSubsystem.UploadLimit;await LoadUploadLimitRecordAsync();
            stage=ManagerSubsystem.Vault;await RefreshVaultPasswordStateAsync();await CleanupCredentialsAsync();
            stage=ManagerSubsystem.Library;
            string? projectionNotice=null;
            try
            {
                if (!_vaultPasswordUnavailable && _vaultPasswordMode is VaultPasswordMode.Disabled or VaultPasswordMode.Unlocked)
                {
                    var projection=await Task.Run(()=>_identityProjection.ReplayAsync(_lifetime.Token));
                    if(projection.Pending>0) projectionNotice="Linked account names need reconciliation. Pending metadata journals were retained; account authorization is checked separately.";
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException || ex is OperationCanceledException && !_lifetime.IsCancellationRequested)
            {
                projectionNotice="Linked-name recovery needs attention. Existing metadata journals were preserved; your account library is still available.";
                _diagnostics.RecordFailure(ManagerSubsystem.Library,ex.GetType());
            }
            if(projectionNotice is not null) AddEvent(projectionNotice);
            string? queueRecoveryNotice=null;
            try
            {
                await Task.Run(()=>_launchQueue.InitializeAsync(_lifetime.Token));QueueReady=true;
                queueRecoveryNotice=_queueStore.RecoveryNotice;
                if(queueRecoveryNotice is not null)AddEvent(queueRecoveryNotice);
            }
            catch(Exception ex) when(ex is not OperationCanceledException) {ShowQueueBlocker("Launch queue unavailable. Existing queue files were preserved; account profiles remain usable.");_diagnostics.RecordFailure(ManagerSubsystem.Queue,ex.GetType());}
            if (IsSample)
            {
                var initial = await Task.Run(() => _accounts.ReadAsync(new(), _lifetime.Token));
                if (initial.Total == 0) for (int i = 1; i <= 16; i++)
                    await Task.Run(() => _accounts.SaveAsync(AccountProfile.Create($"Sample account {i:00}") with { Group = i <= 8 ? "Card Clash" : "Bubble Gum", PreferredPlaceId = i <= 8 ? 110829983956014 : 2512643572 }, _lifetime.Token));
            }
            stage=ManagerSubsystem.Telemetry;await RecentClients.InitializeAsync();
            stage=ManagerSubsystem.DiscordAlerts;await LoadDiscordWebhookSummaryAsync();
            stage=ManagerSubsystem.Library;await LoadAccountsAsync(_lifetime.Token); _ready = true; RefreshOverview(); Raise(nameof(IsReady)); Raise(nameof(EmptyAccountsText)); Raise(nameof(CanRefreshInstallations)); NotifyPerformanceTransfer(); GameDetails.RefreshAvailability(); RecentClients.RefreshAvailability();RefreshClientPreviewContext();MacroHostCatalog.RefreshAvailability();MacroAssignments.RefreshAvailability();RefreshPublicImageActivity();
            PreferencesEdited(); Notice = loaded.Notice ?? queueRecoveryNotice ?? projectionNotice ?? "Ready. Account metadata stays local; running processes require verified identity before automation.";
            AddEvent(IsSample ? "Isolated sample profile library opened; no sample session is shown as a live client." : "Local account library opened.");
            _diagnostics.Record(ManagerEventKind.Started);
            LoadClientDisplaysOnce(); // After the Ready notice, so a monitor-read failure is not overwritten by it.
            // Start the owned diagnostic worker only after initialization succeeds. Isolated
            // sample/injected-monitor tests never inspect unrelated desktop logs.
            if (!IsSample && _monitor is WindowsDesktopMonitor) _logCorrelator = new(recoveredFailure: RecordRecoveredLogCorrelationFailure);
            // The safety sampler owns a pool loop; only its coalesced presentation step uses the dispatcher.
            var monitorStop=_lifetime.Token;_monitorTask = Task.Run(() => MonitorLoopAsync(monitorStop));
            stage=ManagerSubsystem.Directory;await DestinationFavorites.LoadAsync(_lifetime.Token);
            await RecipientBundles.LoadAsync(_lifetime.Token);
            // Apply the saved lock before optional update scheduling can admit its first check.
            // The updater's owner consults current lock/sample/shutdown permission at every check boundary.
            ApplySavedPublicNetworkGuard();
            if (SettingsActivationCurrent(activationGeneration)) ApplySavedClientUpdateCheck(restart:true);
            await ReleaseUpdates.InitializeAsync(_lifetime.Token);
            // Lock keeps alert monitoring enabled; only clearing a newer Panic requires current activation authority.
            ApplySavedDiscordAlerts(wasOn:false, allowResume:SettingsActivationCurrent(activationGeneration));
            if (SettingsActivationCurrent(activationGeneration)) ApplySavedDiscordPresence(wasOn:false);
            else RefreshDiscordPresenceStatus();
        }
        catch (OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _ready=false; _libraryUnavailable=true; ApplySavedPanicShortcut(); RefreshOverview(); Raise(nameof(IsReady)); Raise(nameof(EmptyAccountsText)); SaveStatus="Startup needs attention"; Fail(ex,stage); }
    }
    private void ScheduleQuery()
    {
        if (!_ready) return; _page = 0;
        int intentGeneration=++_queryGeneration;
        var priorCancellation = _queryCancellation; priorCancellation?.Cancel();
        var prior = _queryTask;
        _queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _queryTask = ReplaceQueryAsync(prior, priorCancellation, intentGeneration, _queryCancellation.Token);
    }
    private async Task ReplaceQueryAsync(Task prior, CancellationTokenSource? priorCancellation, int intentGeneration, CancellationToken ct)
    {
        try { await prior; } finally { priorCancellation?.Dispose(); }
        await DebouncedQueryAsync(intentGeneration,ct);
    }
    private async Task DebouncedQueryAsync(int intentGeneration,CancellationToken ct)
    {
        try { await Task.Delay(180, ct); if(intentGeneration==_queryGeneration)await LoadAccountsAsync(ct,intentGeneration); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex,ManagerSubsystem.Library); }
    }
    private async Task LoadAccountsAsync(CancellationToken ct,int? reservedGeneration=null,AccountGroupDefinition? expectedGroup=null)
    {
        int generation = reservedGeneration??++_queryGeneration;
        if(generation!=_queryGeneration||ct.IsCancellationRequested)return;
        long publicImageEpoch=_directoryGeneration;
        _publicImageLoader.CancelWork();
        var query = new AccountQuery(Search, Group.Value, Order, _page);
        var page = await Task.Run(() => _accounts.ReadAsync(query, ct), ct);
        var groupPicker = await Task.Run(() => ReadGroupPickerAsync(query.Group,ct), ct);
        if(expectedGroup is not null && !groupPicker.Definitions.Any(group=>group==expectedGroup))
            throw new RevisionConflictException();
        int allCount = query.Group is null && query.Search.Length == 0 ? page.Total
            : (await Task.Run(() => _accounts.ReadAsync(new(PageSize: 1), ct), ct)).Total;
        if (generation != _queryGeneration || ct.IsCancellationRequested) return;
        // A busy store is retryable and says nothing about a saved sign-in. Keep each visible row's
        // last-known link instead (launch and dispatch re-check the vault generation and fail closed).
        var lastKnown=new Dictionary<Guid,AuthorizationMetadata>(Accounts.Count);
        foreach(var published in Accounts) if(published.CachedAuthorization is { } known) lastKnown[published.Profile.Id]=known;
        var (rows,storeBusy)=await Task.Run(async ()=>
        {
            var result=new List<AccountRow>();
            var ids=page.Items.Select(p=>p.Id).ToArray();
            var summaries=new Dictionary<Guid,AuthorizationMetadata>(ids.Length);
            HashSet<Guid>? unavailable=null, busy=null;
            try { foreach(var summary in await _vault.ReadSummariesAsync(ids,ct)) summaries[summary.ProfileId]=summary; }
            catch(AuthorizationVaultBusyException) { busy=[..ids]; }
            catch(Exception ex) when(ex is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException)
            {
                // One damaged or locked record must not hide every other row's link. Only on this
                // degraded path, re-read each visible profile alone (same validation, same lease)
                // so just the failing rows are marked; cancellation still propagates.
                unavailable=[];
                for(int i=0;i<ids.Length;i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { if(await _vault.GetSummaryAsync(ids[i],ct) is { } summary) summaries[ids[i]]=summary; }
                    // Every later read would wait out the same 2-second lease deadline.
                    catch(AuthorizationVaultBusyException) { busy=[..ids[i..]]; break; }
                    catch(Exception itemError) when(itemError is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException) { unavailable.Add(ids[i]); }
                }
            }
            foreach(var item in page.Items)
            {
                ct.ThrowIfCancellationRequested(); var row=new AccountRow(item);
                if(summaries.TryGetValue(item.Id,out var summary)) row.SetCachedAuthorization(summary);
                else if(busy?.Contains(item.Id)==true)
                {
                    if(lastKnown.TryGetValue(item.Id,out var known)) row.SetCachedAuthorization(known);
                    else row.SetAuthorization(AccountRow.BusyIdentity,"Protected storage was busy, so this saved sign-in was not read. Zenith checks again shortly, or choose Refresh. Protected files were not changed.");
                }
                else if(unavailable?.Contains(item.Id)==true) row.SetAuthorization("Link index unavailable","Protected files were preserved. Sign in again or review storage access; account metadata remains usable.");
                result.Add(row);
            }
            return (result,busy is not null);
        },ct);
        await RefreshMainAccountDisplayAsync(rows,generation,ct);
        if(generation!=_queryGeneration || ct.IsCancellationRequested) return;
        // Selection may change while the query, credential summaries or main-account lookup
        // waits. Capture the latest UI choice only at publication, before collection reset.
        Guid? selected = _selectAfterReload??SelectedAccount?.Profile.Id;
        _publishingAccountRows=true;
        try
        {
        Accounts.Clear(); foreach (var row in rows)
        {
            // Attach cached images only at final UI publication, after any Lock/Panic clear.
            if(!IsLocked && row.KnownUserId is { } userId && _publicPresentation.TryGetCached(userId,out var presentation) && presentation is not null && _publicPresentation.IsCurrent(presentation))
                row.SetPublicPresentation(presentation);
            if(!IsLocked) ApplyKnownPreferredGameName(row);
            Accounts.Add(row);
        }
        RefreshAccountLaunchStates();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Profile.Id == selected);
        }
        finally { _publishingAccountRows=false; ReconcileSignedServerSelection(); }
        _selectAfterReload=null;
        AccountCount = allCount; _filteredTotal = page.Total; PageLabel = page.Total == 0 ? "No matching profiles" : $"{page.Page * 100 + 1}–{Math.Min(page.Total, (page.Page + 1) * 100)} of {page.Total} profiles";RecordOverviewLibraryLoaded();
        Raise(nameof(CanNext)); Raise(nameof(CanPrevious)); Raise(nameof(EmptyAccountsText));
        string? selectedGroup = Group.Value;
        _groupCatalogChoices=groupPicker.Definitions;Raise(nameof(GroupCatalogChoices));Raise(nameof(HasGroupCatalog));GroupChoicesNotice=groupPicker.Notice;
        if (!Groups.Skip(1).SequenceEqual(groupPicker.Filters))
        {
            _updatingGroups = true;
            try
            {
                Groups=new[]{GroupFilter.All}.Concat(groupPicker.Filters).ToArray();
                _group=Groups.FirstOrDefault(g=>string.Equals(g.Value,selectedGroup,StringComparison.OrdinalIgnoreCase))??GroupFilter.All;
                Raise(nameof(Groups));
            }
            finally { _updatingGroups = false; Raise(nameof(Group)); }
            if(selectedGroup is not null && Group.Value is null) ScheduleQuery();
        }
        if(generation==_queryGeneration && !ct.IsCancellationRequested && publicImageEpoch==_directoryGeneration) QueueCurrentPublicImages();
        if(storeBusy && generation==_queryGeneration && !ct.IsCancellationRequested) ScheduleBusyStoreRetry(generation);
    }
    private static readonly TimeSpan BusyStoreRetryDelay=TimeSpan.FromSeconds(1);
    private int? _busyStoreRetryGeneration;
    /// <summary>
    /// Re-reads the same page once after the protected store reported busy. It is owned like a
    /// debounced query (chained into <see cref="_queryTask"/>, cancelled with <see cref="_queryCancellation"/>
    /// and joined at shutdown), keeps the current page, and allows one retry per query generation,
    /// so a store that stays busy cannot loop. Any newer search, filter, page or reload supersedes it.
    /// </summary>
    private void ScheduleBusyStoreRetry(int generation)
    {
        if(_busyStoreRetryGeneration==generation || _shuttingDown || _lifetime.IsCancellationRequested) return;
        _busyStoreRetryGeneration=generation;
        // The prior query may be the one publishing now; it is awaited, never cancelled, before its source is disposed.
        var priorCancellation=_queryCancellation; var prior=_queryTask;
        _queryCancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _queryTask=RetryBusyStoreQueryAsync(prior,priorCancellation,generation,_queryCancellation.Token);
    }
    private async Task RetryBusyStoreQueryAsync(Task prior,CancellationTokenSource? priorCancellation,int generation,CancellationToken ct)
    {
        try { await prior; } finally { priorCancellation?.Dispose(); }
        try { await Task.Delay(BusyStoreRetryDelay,ct); if(generation==_queryGeneration && !_libraryUnavailable) await LoadAccountsAsync(ct,generation); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex,ManagerSubsystem.Library); }
    }
    public Task SaveAccountAsync(AccountProfile profile)=>OwnProfileOperation(()=>SaveAccountCoreAsync(profile));
    private async Task ChangeWindowAsync(object? parameter,ClientWindowAction action)
    {
        if(IsLocked || parameter is not SessionRow session) return;
        if(_monitor is not IClientWindowControl windows) { Notice="This desktop provider does not support window control."; return; }
        long generation=_windowOperationsGeneration;
        var result=await windows.ChangeAsync(session.Observation,action,_lifetime.Token);
        if(generation!=_windowOperationsGeneration || IsLocked || _shuttingDown) return;
        Notice=result.Explanation;
        if(!result.Applied) _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows);
    }
    private async Task SaveAccountCoreAsync(AccountProfile profile)
    {
        if(!_ready) throw new InvalidOperationException("The local library has not opened successfully.");
        var saved=await Task.Run(() => _accounts.SaveAsync(profile, _lifetime.Token)); _selectAfterReload=saved.Id;
        _diagnostics.Record(ManagerEventKind.ProfileSaved);
        // The commit is authoritative. A failed list refresh must not reach the dialog as a failed
        // save: a retry would carry the old revision and report a false "changed elsewhere" conflict.
        // No retry or compensating write; the next successful reload selects the saved row.
        try { await LoadAccountsAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Notice = "Profile saved, but the account list could not refresh. Use Refresh to reload it.";
            AddEvent($"Profile saved; list refresh failed ({ex.GetType().Name}).");
            _diagnostics.RecordFailure(ManagerSubsystem.Library,ex.GetType());
            return;
        }
        Notice = "Profile saved. Entered usernames are metadata until official account linking verifies them."; AddEvent("Profile saved.");
    }
    /// <summary>True while a removal of this row's profile is queued or running. UI-thread state, like <see cref="_profileOperations"/>.</summary>
    public bool IsRemovalPending(AccountRow row)=>_pendingProfileRemovals.Contains(row.Profile.Id);
    /// <summary>Shows why a removal is refused and returns true when any of these rows is already being removed.</summary>
    public bool ReportPendingRemoval(IReadOnlyList<AccountRow> rows)
    {
        if(!rows.Any(IsRemovalPending)) return false;
        Notice=rows.Count==1?"That profile is already being removed. Wait for the removal to finish."
            :"Some checked profiles are already being removed. Wait for that removal to finish, then check the remaining profiles again.";
        return true;
    }
    /// <summary>Removes the rows' profiles. A second request for a profile whose removal is still queued or running is refused with a notice,
    /// so it cannot repeat the queue stop and authorization invalidation and then fail as a stale-revision conflict.</summary>
    public Task DeleteAccountsAsync(IReadOnlyList<AccountRow> rows)
    {
        if(ReportPendingRemoval(rows)) return Task.CompletedTask;
        var ids=rows.Select(r=>r.Profile.Id).Distinct().ToArray();
        foreach(var id in ids) _pendingProfileRemovals.Add(id);
        return RemoveOwnedProfilesAsync(ids,rows);
    }
    private async Task RemoveOwnedProfilesAsync(Guid[] ids,IReadOnlyList<AccountRow> rows)
    {
        // Released on every path (completed, faulted, cancelled or refused before it started) so the profile can be removed again later.
        try { await OwnProfileOperation(()=>DeleteAccountsCoreAsync(rows)); }
        finally { foreach(var id in ids) _pendingProfileRemovals.Remove(id); }
    }
    private async Task DeleteAccountsCoreAsync(IReadOnlyList<AccountRow> rows)
    {
        string queueStopped=StopQueueForSavedStateRemoval("Trial stopped because a profile was removed. Review any process-start receipt before retrying; existing Roblox clients remain open.");
        if(QueueReady)
        {
            foreach(var row in rows)await _launchQueue.InvalidateAuthorizationAsync(row.Profile.Id,_lifetime.Token);
        }
        await _launchQueue.WaitForActiveCleanupAsync(_lifetime.Token);
        // Only the link service knows which row an Add moved onto to refresh its saved sign-in. It stops that Add too, and keeps
        // an Add off these rows until they and their saved sign-ins are gone. The command is then joined so its notice comes first.
        // A share-link check it stopped belongs to the Private server panel, whose check then ends cancelled and says the account
        // was removed (ShareLinkViewModel.RunCheckAsync); a launch's native preparation it stopped belongs to the launch queue,
        // whose authorizations for these rows were invalidated above. The sign-in lane may meanwhile be running a command for
        // other rows, so it is neither cancelled nor waited for then.
        using var removal=await _linkService.BeginProfileRemovalAsync(rows.Select(r=>r.Profile.Id).ToArray(),_lifetime.Token);
        if(IsSignInLaneOperation(removal.StoppedOperationKind) || _linkingProfile is { } linking && rows.Any(r=>r.Profile.Id==linking))
        { _authorizationStop?.Cancel(); await Task.WhenAll(AddRobloxAccount.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,RefreshPresence.Execution,RefreshPrivateDetails.Execution,BefriendReviewedAccounts.Execution,BrowseSignedServers.Execution,NextSignedServers.Execution,RenameOwnedPrivateServer.Execution,InspectNativeHandoff.Execution); }
        await Task.Run(() => _accounts.DeleteManyAsync(rows.Select(r => r.Profile).ToArray(), _lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token); await CleanupCredentialsAsync();
        Notice = $"Removed {rows.Count} profile(s). Roblox clients were left open. {CredentialCleanupStatus}{queueStopped}"; AddEvent("Selected profiles removed.");
        _diagnostics.Record(ManagerEventKind.ProfilesRemoved);
    }
    /// <summary>The saved-session operations this shell starts in its sign-in lane (<see cref="_authorizationStop"/>).</summary>
    private static bool IsSignInLaneOperation(SavedSessionOperationKind kind)=>kind is SavedSessionOperationKind.AddAccount
        or SavedSessionOperationKind.SignIn or SavedSessionOperationKind.SavedSignInCheck or SavedSessionOperationKind.HandoffInspection or SavedSessionOperationKind.AccountObservation;
    public Task ExportAsync(string path)=>OwnProfileOperation(()=>ExportCoreAsync(path));
    private async Task ExportCoreAsync(string path)
    {
        await Task.Run(async () =>
        {
            string temporary = path + ".tmp";
            try
            {
                await using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,32768,FileOptions.Asynchronous|FileOptions.WriteThrough))
                {
                    await using var writer=new Utf8JsonWriter(file);
                    async Task FlushBoundedAsync()
                    {
                        await writer.FlushAsync(_lifetime.Token);
                        if(file.Length>16*1024*1024-64) throw new InvalidDataException("The export exceeds 16 MiB. Shorten notes or export a smaller library. The existing destination was preserved.");
                    }
                    writer.WriteStartObject(); writer.WriteNumber("Version",ProfileMetadataImport.CurrentVersion);
                    writer.WritePropertyName("Groups"); writer.WriteStartArray();
                    if(_accounts is IAccountGroupCatalog catalog)
                    {
                        // Saved groups, including empty ones, in list order. All pages must come from one catalog
                        // revision so a concurrent reorder cannot duplicate or drop a group in the file.
                        long revision=0; int written=0;
                        for(int page=0;;page++)
                        {
                            var groups=await catalog.ReadGroupCatalogAsync(new(Page:page,PageSize:1000),_lifetime.Token);
                            if(groups.Total>ProfileMetadataImport.MaximumGroups) throw new InvalidDataException("Export at most 100,000 saved groups per operation.");
                            if(page==0) revision=groups.CatalogRevision;
                            else if(groups.CatalogRevision!=revision) throw new InvalidDataException("The saved groups changed while exporting. Export again; the existing destination was preserved.");
                            foreach(var group in groups.Items)
                            {
                                _lifetime.Token.ThrowIfCancellationRequested();
                                writer.WriteStartObject(); writer.WriteString("Name",group.Name);
                                if(group.ColorHex is null) writer.WriteNull("ColorHex"); else writer.WriteString("ColorHex",group.ColorHex);
                                writer.WriteNumber("SortOrder",written++); writer.WriteEndObject();
                            }
                            await FlushBoundedAsync();
                            if(groups.Items.Count==0||written>=groups.Total) break;
                        }
                    }
                    writer.WriteEndArray();
                    writer.WritePropertyName("Profiles"); writer.WriteStartArray();
                    int exported=0;
                    for(int page=0;;page++)
                    {
                        var batch=await _accounts.ReadAsync(new(Page:page),_lifetime.Token);
                        if(batch.Total>100000) throw new InvalidDataException("Export at most 100,000 profiles per operation.");
                        foreach(var profile in batch.Items) { _lifetime.Token.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer,profile); }
                        await FlushBoundedAsync();
                        exported+=batch.Items.Count; if(exported>=batch.Total) break;
                    }
                    writer.WriteEndArray();writer.WriteEndObject();await writer.FlushAsync(_lifetime.Token);await file.FlushAsync(_lifetime.Token);file.Flush(true);
                }
                _lifetime.Token.ThrowIfCancellationRequested(); File.Move(temporary,path,true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        Notice = "Profiles, group memberships, saved groups (including empty ones), group colors and group order exported. Authorization and credentials are excluded.";
        _diagnostics.Record(ManagerEventKind.MetadataExported);
    }
    public Task ImportAsync(string path)=>OwnProfileOperation(()=>ImportCoreAsync(path));
    private async Task ImportCoreAsync(string path)
    {
        var imported = await Task.Run(async () =>
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length is 0 or > ProfileMetadataImport.MaximumBytes) throw new InvalidDataException("Import requires a nonempty metadata file of at most 16 MiB.");
            byte[] bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, _lifetime.Token);
            return ProfileMetadataImport.DecodeDocument(bytes, _lifetime.Token);
        });
        // Exported IDs and Roblox user IDs are hints, never identity (CONFLICT-04), so they cannot detect a
        // row this library already has. Name plus username, ignoring case, does; such rows are skipped.
        var existing = await Task.Run(() => ReadProfileKeysAsync(_lifetime.Token));
        var validated = imported.Profiles.Where(profile => !existing.Contains(ProfileKey(profile)))
            .Select(profile => profile with { Id = Guid.NewGuid(), Revision = 0, RobloxUserId = null }).ToArray();
        int skipped = imported.Profiles.Count - validated.Length;
        // Saved groups and profiles commit together, so a failure part way adds neither.
        int addedGroups = 0;
        if (_accounts is IAccountMetadataImport library)
        { if (validated.Length > 0 || imported.Groups.Count > 0) addedGroups = await Task.Run(() => library.ImportMetadataAsync(imported.Groups, validated, _lifetime.Token)); }
        else if (validated.Length > 0) await Task.Run(() => _accounts.CreateManyAsync(validated, _lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token);
        string skippedText = skipped == 0 ? "" : $" Skipped {skipped} already in this library (same name and username).";
        string groupsText = imported.Groups.Count == 0 ? ""
            : _accounts is not IAccountMetadataImport ? " Saved groups, colors and order were not added because this library has no group list."
            : (addedGroups == 0 ? "" : $" Added {addedGroups} saved group(s) with their colors and order.")
              + (addedGroups == imported.Groups.Count ? "" : $" Kept this library's colors and order for {imported.Groups.Count - addedGroups} group(s) it already had.");
        Notice = $"Imported {validated.Length} profiles.{skippedText}{groupsText} Sign-in is required separately."; AddEvent("Profile metadata imported.");
        _diagnostics.Record(ManagerEventKind.ProfilesImported);
    }
    private static string ProfileKey(AccountProfile profile) => profile.Label.Trim() + "\n" + profile.Username.Trim();
    private async Task<HashSet<string>> ReadProfileKeysAsync(CancellationToken ct)
    {
        // A label cannot contain a line break, so the joined key is unambiguous.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int read = 0;
        for (int page = 0; ; page++)
        {
            var batch = await _accounts.ReadAsync(new(Page: page), ct);
            foreach (var profile in batch.Items) { ct.ThrowIfCancellationRequested(); keys.Add(ProfileKey(profile)); }
            read += batch.Items.Count; if (batch.Items.Count == 0 || read >= batch.Total) return keys;
        }
    }
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await SampleAsync(ct).ConfigureAwait(false);
                // One owned loop, no catch-up bursts. Essential native measurements stay at the policy's fixed cadence.
                var policy=Volatile.Read(ref _activePerformance);
                await Task.Delay(Math.Min(policy.EssentialResourceSampleMs,policy.ProcessSampleMs),ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The loop runs on the pool; the stop and its explanation belong to the dispatcher. A closing shell records the type only.
            if(Volatile.Read(ref _shuttingDown) || _dispatcher.HasShutdownStarted) _diagnostics.RecordFailure(ManagerSubsystem.Telemetry,ex.GetType());
            else await _dispatcher.InvokeAsync(() => {MarkOverviewMonitorStopped();Fail(ex,ManagerSubsystem.Telemetry);});
        }
    }
    // The newest sample each dispatcher step still has to show. The serialized sampler writes them; ApplySampleUi takes them.
    private sealed record SamplePresentation(DesktopSnapshot Snapshot,CapacityDecision Capacity,LaunchReservationProjectionResult Projection,PressureDecision Pressure,DateTimeOffset Now);
    private DesktopSnapshot? _pendingSampleContext;
    private SamplePresentation? _pendingSamplePresentation;
    private int _sampleUiScheduled;
    /// <returns>For a forced presentation, whether the newest sample is on screen once its dispatcher step has run; otherwise true.</returns>
    private async Task<bool> SampleAsync(CancellationToken ct,bool forcePresentation=false)
    {
        // The safety half never waits for the dispatcher: the loop runs it on the pool, so a busy UI cannot stretch the
        // 1 Hz essential samples past the pressure tracker's three-second continuity limit and restart recovery.
        await _sampleSerial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var snapshot = await _monitor.SampleAsync(ct).ConfigureAwait(false);
        // One bounded, pure reducer on the existing sample lane; no row-owned task/timer.
        await Task.Run(()=>_telemetryHistory.Observe(snapshot,DateTimeOffset.UtcNow),ct).ConfigureAwait(false);
        // Launches whose receipts carry a browser tracker are offered too, so their own client logs can be found by tracker.
        _logCorrelator?.Offer(snapshot,launches:LaunchLogExpectation.From(Volatile.Read(ref _latestQueue)));
        var memory = snapshot.Resources;var now=DateTimeOffset.UtcNow;
        var limits=Volatile.Read(ref _savedSettings).Capacity!;
        var projection=LaunchReservationProjection.Evaluate(Volatile.Read(ref _latestQueue),snapshot,limits,now);
        NoteTrustedInventory(snapshot,projection);
        var capacity=CapacityPolicy.Evaluate(memory,limits,projection.Reservations,projection.OccupiedForAdmission,now);
        bool previousHold=_lastPressure.Held;
        // Cached half-second calls must not reset a one-second safety sample's recovery history.
        var systemHealth=CapacityPolicy.EvaluateSystemPressure(memory,limits,now);
        if(_pressureObserved!=memory || !systemHealth.SamplesHealthy)
        {Volatile.Write(ref _lastPressure,_pressure.Observe(memory,systemHealth,now));_pressureObserved=memory;}
        var pressure=_lastPressure;
        // Cache pressure/sample health only. Each start recomputes remaining reservations,
        // excluding its own not-yet-emitted forecast, at the final native boundary.
        // Publish the sample and its health together. Readers must never combine a new
        // critical-memory/CPU/GPU observation with the previous sample's healthy decision.
        Volatile.Write(ref _resourceAdmission,new ResourceAdmissionSnapshot(snapshot,new(systemHealth.SamplesHealthy && !pressure.Held,pressure.Explanation)));
        Volatile.Write(ref _latestDesktop,snapshot);
        _launchQueue.Signal();
        // The preview context follows every sample; the dashboard only when due. Both wait for the dispatcher outside this section.
        Volatile.Write(ref _pendingSampleContext,snapshot);
        if(PresentationDue(Volatile.Read(ref _activePerformance),forcePresentation || previousHold!=pressure.Held))
            Volatile.Write(ref _pendingSamplePresentation,new SamplePresentation(snapshot,capacity,projection,pressure,now));
        }
        finally {_sampleSerial.Release();}
        if(!forcePresentation) {ScheduleSampleUi();return true;}
        // A manual refresh waits for the newest presentation and reports whether it is shown. The loop's already queued step
        // can run first and take it, so the answer is the shared presentation state, not whichever step happened to show it.
        return await _dispatcher.InvokeAsync(()=>{ApplySampleUi();return !_presentationFaulted;},DispatcherPriority.Background,ct);
    }
    // One coalesced dispatcher step at a time, as QueueChanged does. A busy UI skips intermediate samples, never the newest.
    private void ScheduleSampleUi()
    {
        if(Interlocked.Exchange(ref _sampleUiScheduled,1)!=0 || Volatile.Read(ref _shuttingDown) || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            Interlocked.Exchange(ref _sampleUiScheduled,0);
            ApplySampleUi();
        }));
    }
    private void ApplySampleUi()
    {
        var snapshot=Interlocked.Exchange(ref _pendingSampleContext,null);
        var presentation=Interlocked.Exchange(ref _pendingSamplePresentation,null);
        if(_shuttingDown) return;
        // The sample, pressure and admission are already published. A display defect here must not end the safety
        // sampler, so whichever step meets it (the loop's or a manual refresh's) reports it once and sampling continues.
        try
        {
            if(snapshot is not null) { ObserveClientPreviewContext(snapshot); ObserveClientSoundArrivals(snapshot); }
            if(presentation is not null) PresentSample(presentation);
        }
        catch(Exception ex) {ReportPresentationFault(ex);}
    }
    private void PresentSample(SamplePresentation sample)
    {
        // Resource headlines are the Overview projection's, which also checks freshness.
        var (snapshot,capacity,projection,pressure,now)=sample;
        Capacity=(projection.ForecastsKnown?capacity.Explanation+" ":"")+projection.Explanation;
        RefreshNativeTrialFeatures();
        PressureNotice=pressure.Explanation;RecordOverviewSample(snapshot,capacity,projection);
        RefreshDiagnosticsView();
        RefreshDestinationCapacity();
        foreach(var account in Accounts) account.UpdateAuthorizationClock(DateTimeOffset.UtcNow);
        ProcessInventoryStatus=snapshot.Problem is null && snapshot.ProcessInventoryComplete && snapshot.ProcessObservedUtc is { } at && at<=now.AddSeconds(1) && now-at<=RecentClientHistoryReducer.MaximumInventoryAge
            ? $"Process inventory sampled {at.LocalDateTime:T} · complete scan. Account identity remains unverified."
            : "Process scan incomplete, stale or unavailable. Displayed rows may be last-known; missing clients are not confirmed exits.";
        // Replace only observations whose measurements changed; profile views are not rebuilt by telemetry.
        bool clientRowsChanged=(_sessionRows??=new(ReleaseSessionRow,CreateSessionRow)).Reconcile(Sessions,snapshot.Sessions);
        PresentClientGpu(snapshot,now);
        // One log snapshot for both the update and the row views, so a newer correlator pass cannot split them.
        var logs = _logCorrelator?.Snapshot;
        _logProjection.Update(snapshot.Sessions, logs, now);
        // One registry snapshot per tick feeds both readers, and the optional early release of the launch wait, which runs
        // before the pacing status is refreshed so the status shows the shortened wait in the same tick.
        var evidence=_logProjection.EvidenceSnapshot(now);
        ObserveJoinedClientForPacing(evidence);
        RefreshLaunchEvidenceWith(evidence);
        RecentClients.Observe(snapshot,evidence,now);
        ObserveClientAlerts(now);
        ObserveDiscordPresence(snapshot);
        ObserveClientArrivals(snapshot,evidence);
        // Rows retained from a failed, partial or stale scan keep their log text but not Join same / Copy.
        var logViews = _logProjection.Snapshot(now, snapshot, logs);
        foreach (var row in Sessions)
            row.UpdateLog(logViews.TryGetValue(new(null, row.Observation.ProcessId, row.Observation.ProcessStartUtcTicks), out var log)
                ? log : SessionLogView.Unknown);
        if (IsJoinGame) RefreshRecentlyPlayed();
        // Releases a departed row; in History its retained series stays viewable as an ended, last-known choice.
        PresentHistoryChoices();
        if(clientRowsChanged) NotifyClientSelection();
        RefreshTelemetryPresentation();
        _presentationFaulted=false;
    }
    // Dispatcher-thread only; created once so each presentation reuses its scratch sets and delegates.
    private SessionRowReconciler? _sessionRows;
    private SessionRow CreateSessionRow(SessionObservation session)
    { var row=new SessionRow(session);row.PropertyChanged+=SessionSelectionChanged;return row; }
    private void ReleaseSessionRow(SessionRow row)=>row.PropertyChanged-=SessionSelectionChanged;
    private void ReportPresentationFault(Exception exception)
    {
        // Every fault reaches diagnostics (type only, Rule 10); the notice and event are latched until a presentation completes.
        _diagnostics.RecordFailure(ManagerSubsystem.Telemetry,exception.GetType());
        if(_presentationFaulted) return;
        _presentationFaulted=true;
        Notice="The dashboard could not show the latest measurement. Monitoring and launch safety checks continue; restart Zenith Sessions if the display stays out of date.";
        AddEvent($"Display update failed ({exception.GetType().Name}). Monitoring continues.");
    }
    private async Task SavePreferencesAsync()
    {
        if(!_ready) throw new AccountLinkException("Local services have not opened successfully. Resolve the startup error before changing preferences.");
        long activationGeneration = _settingsActivationGeneration;
        var culture = CultureInfo.CurrentCulture;
        if (!double.TryParse(SpareRam, NumberStyles.Float, culture, out double spare) || !double.TryParse(PhysicalPerClient, NumberStyles.Float, culture, out double physical)
            || !double.TryParse(CommitPerClient, NumberStyles.Float, culture, out double commit)) throw new ArgumentException("Enter valid memory amounts in GiB.");
        int? maximum = null;
        if (!string.IsNullOrWhiteSpace(MaximumClients)) { if (!int.TryParse(MaximumClients, out int value) || value < 1) throw new ArgumentException("The optional client limit must be a positive whole number."); maximum = value; }
        var limits = (_savedSettings.Capacity! with { SparePhysicalGiB = spare, NewClientPhysicalGiB = physical, NewClientCommitGiB = commit, UserMaximum = maximum }).Validated();
        var performance=ReadPerformanceDraft();
        if(!PanicShortcut.TryParse(PanicShortcutText,out var panicShortcut,out string panicProblem)) throw new ArgumentException(panicProblem);
        // The specific rule an extra argument breaks is the error shown; the saved form is the canonical single-spaced text.
        string defaultClientArguments=ExtraClientArgumentPolicy.Validate(DefaultClientArguments);
        // Pin this explicit request before the asynchronous profile check. Failed requests
        // must never become the source for an unrelated appearance-only save.
        if(!global::Zenith.Sessions.Core.DiscordApplicationId.TryParse(DiscordApplicationId,out string discordApplicationId,out string applicationProblem)) throw new ArgumentException(applicationProblem);
        var requested=(_savedSettings with { Capacity=limits,Performance=performance,WindowLayout=ReadClientLayoutDraft(),CloseButtonExits=CloseButtonExits,IdleLockMinutes=IdleLockMinutes,PanicShortcut=panicShortcut.ToString(),MuteArrivingClients=MuteArrivingClients,PublicNetworkGuard=PublicNetworkGuard,DiscordAlerts=DiscordAlerts,DiscordPresence=DiscordPresence,DiscordPresenceGame=DiscordPresenceGame,DiscordApplicationId=discordApplicationId,ShowClientGpu=ShowClientGpu,KeepAwakeMinutes=KeepAwakeMinutes,KeepSignInBrowserData=KeepSignInBrowserData,CheckRobloxUpdates=CheckRobloxUpdates,DefaultClientArguments=defaultClientArguments,ShortenWaitAfterJoin=ShortenWaitAfterJoin,
            NativeLaunchMode=NativeLaunchModes.ToSettingsValue(SelectedNativeLaunchMode.Mode) }).Validated();
        if(performance.MainAccountProfileId is { } main && await Task.Run(()=>_accounts.FindAsync(main,_lifetime.Token)) is null)
            throw new ArgumentException("The chosen main account no longer exists. Choose another saved profile or clear the selection before saving.");
        bool muteArrivalsWasOn=_savedSettings.MuteArrivingClients;bool updateChecksWereOn=_savedSettings.CheckRobloxUpdates;bool discordAlertsWereOn=_savedSettings.DiscordAlerts,discordPresenceWasOn=_savedSettings.DiscordPresence;
        await SaveCurrentSettingsAsync(requested, activationGeneration);
        DefaultClientArguments=defaultClientArguments;
        ShowSavedPanicShortcut(panicShortcut);
        if (SettingsActivationCurrent(activationGeneration)) ArrivalMutePreferenceSaved(muteArrivalsWasOn);
        else RaiseArrivalMute();
        if (SettingsActivationCurrent(activationGeneration)) ClientUpdateCheckPreferenceSaved(updateChecksWereOn);
        ApplySavedPublicNetworkGuard();
        ApplySavedDiscordAlerts(discordAlertsWereOn, allowResume:SettingsActivationCurrent(activationGeneration));
        if (SettingsActivationCurrent(activationGeneration)) ApplySavedDiscordPresence(discordPresenceWasOn);
        else RefreshDiscordPresenceStatus();
    }
    private void PreferencesEdited()
    {
        if (!_ready) return;
        var limits = _savedSettings.Capacity!;
        bool unchanged = double.TryParse(SpareRam, out double spare) && spare == limits.SparePhysicalGiB
            && double.TryParse(PhysicalPerClient, out double physical) && physical == limits.NewClientPhysicalGiB
            && double.TryParse(CommitPerClient, out double commit) && commit == limits.NewClientCommitGiB
            && MaximumClients.Trim() == (limits.UserMaximum?.ToString(CultureInfo.CurrentCulture) ?? "")
            && _settings.DenseAccounts == _savedSettings.DenseAccounts && _settings.Theme == _savedSettings.Theme && _settings.Language == _savedSettings.Language
            && CloseButtonExits == _savedSettings.CloseButtonExits && IdleLockMinutes == _savedSettings.IdleLockMinutes && PanicShortcutDraftIsSaved
            && MuteArrivingClients == _savedSettings.MuteArrivingClients
            && KeepSignInBrowserData == _savedSettings.KeepSignInBrowserData
            && CheckRobloxUpdates == _savedSettings.CheckRobloxUpdates
            && DefaultClientArgumentsDraftIsSaved && ShortenWaitAfterJoin == _savedSettings.ShortenWaitAfterJoin
            && SelectedNativeLaunchMode.Mode == SavedNativeLaunchMode
            && ShowClientGpu == _savedSettings.ShowClientGpu
            && KeepAwakeMinutes == _savedSettings.KeepAwakeMinutes
            && PublicNetworkGuard == _savedSettings.PublicNetworkGuard
            && DiscordAlerts == _savedSettings.DiscordAlerts
            && DiscordPresence == _savedSettings.DiscordPresence && DiscordPresenceGame == _savedSettings.DiscordPresenceGame && DiscordApplicationId.Trim() == _savedSettings.DiscordApplicationId
            && TryReadPerformanceDraft(out var performance) && performance==_savedSettings.Performance
            && TryReadClientLayoutDraft(out var windows) && windows==_savedSettings.WindowLayout;
        Raise(nameof(CallbackUrl));
        SaveStatus = _pendingSaves > 0 ? "Saving…" : _saveFailed ? "Save failed — retry" : !unchanged?"Unsaved settings":_settingsNeedRepair?"Recovered settings — save to repair":"Settings saved";
    }
    private async Task SaveCurrentSettingsAsync(SessionsSettings? requested=null, long? activationGeneration=null)
    {
        string theme=_settings.Theme,language=_settings.Language;bool dense=_settings.DenseAccounts;var window=_settings.ManagerWindow;
        _pendingSaves++; PreferencesEdited();
        try
        {
            await _preferencesWriter.WaitAsync(_lifetime.Token);
            try
            {
                // Appearance and window-position autosaves merge with the latest successful explicit save,
                // even when they waited behind it. Never persist uncommitted field drafts.
                var snapshot=(requested??_savedSettings) with { Theme=theme,Language=language,DenseAccounts=dense,ManagerWindow=window };
                await Task.Run(() => _settingsStore.SaveAsync(snapshot, _lifetime.Token));
                ApplyPerformance(snapshot.Performance!);
                int keepAwakeBefore=_savedSettings.KeepAwakeMinutes;
                _savedSettings = snapshot; _saveFailed = false; _settingsNeedRepair=false;
                ApplySavedNativeLaunchMode();
                _settings=snapshot with { Theme=_settings.Theme,Language=_settings.Language,DenseAccounts=_settings.DenseAccounts,ManagerWindow=_settings.ManagerWindow };
                Raise(nameof(CloseButtonLabel)); Raise(nameof(CanResumeClientArrivalLayout));
                ApplyClientGpu(snapshot.ShowClientGpu);
                if (snapshot.IdleLockMinutes != _idleLock.Minutes) ApplySavedIdleLock();
                // Only a changed choice re-arms the timer; a theme or window autosave leaves the scheduled press where it is.
                if (snapshot.KeepAwakeMinutes != keepAwakeBefore)
                    ApplySavedKeepAwake(keepAwakeBefore, activationGeneration is not { } captured || SettingsActivationCurrent(captured));
                _diagnostics.Record(ManagerEventKind.SettingsSaved);
                Notice = "Preferences saved to your writable settings folder.";
                // Only Save settings registers the saved panic shortcut, or tries a refused one again; autosaves leave it alone.
                if (requested is not null && !ApplySavedPanicShortcut()) Notice = "Preferences saved. " + PanicShortcutStatus;
            }
            finally { _preferencesWriter.Release(); }
        }
        catch { _saveFailed = true; throw; }
        finally { _pendingSaves--; PreferencesEdited(); }
    }
    /// <summary>UI thread, before the window is first shown: where it was left last time, or null. Read-only; see SettingsStore.ReadManagerWindow.</summary>
    internal ManagerWindowPlacement? ReadSavedManagerWindow()=>_settingsStore.ReadManagerWindow();
    // An ordinary local settings write takes milliseconds. Exit gives the window placement this long, waiting behind another
    // settings write included, so a slow or network data root delays only the window closing, never the stop before it.
    private static readonly TimeSpan ManagerWindowSaveBudget=TimeSpan.FromSeconds(2);
    /// <summary>
    /// UI thread. Keeps where the window was left through the same merge as the theme autosave, so unsaved setting edits are
    /// never written. Exit calls it after the shutdown fence (see <see cref="ExitAsync"/>), so it waits on its own short budget,
    /// never the shell's lifetime, and it is quiet: no notice, no save status and no SettingsSaved event. Skipped when nothing
    /// changed, when startup did not finish, and while recovered settings wait for review, because an exit must not repair them
    /// silently. A failed or timed-out save is recorded by type only (Rule 10) and never stops the exit. Windows cannot cancel
    /// a file open or replace already under way, so only those can run past the budget.
    /// </summary>
    internal async Task SaveManagerWindowAsync(ManagerWindowPlacement? placement)
    {
        if(placement is null || !placement.IsValid || !_ready || _settingsNeedRepair || placement==_savedSettings.ManagerWindow) return;
        _settings=_settings with { ManagerWindow=placement };
        using var budget=new CancellationTokenSource(ManagerWindowSaveBudget);
        try
        {
            await _preferencesWriter.WaitAsync(budget.Token);
            try
            {
                var snapshot=_savedSettings with { Theme=_settings.Theme,Language=_settings.Language,DenseAccounts=_settings.DenseAccounts,ManagerWindow=placement };
                await Task.Run(() => _settingsStore.SaveAsync(snapshot,budget.Token));
                _savedSettings=snapshot;
            }
            finally { _preferencesWriter.Release(); }
        }
        // Running out of budget is a failed write, not a shutdown cancellation, so it is kept as a timeout.
        catch(OperationCanceledException) when(budget.IsCancellationRequested) { _diagnostics.RecordFailure(ManagerSubsystem.Settings,typeof(TimeoutException)); }
        catch(Exception ex) { _diagnostics.RecordFailure(ManagerSubsystem.Settings,ex.GetType()); }
    }
    /// <summary>
    /// UI thread: tray Exit, the window's Exit and the close button when it exits. Runs <see cref="DisposeAsync"/>, which
    /// stops work first and then, before any owner is awaited, keeps where the window was left (<paramref name="placement"/>,
    /// or nothing when null). Automation, the launch queue and monitoring therefore never wait on a settings write.
    /// </summary>
    internal Task ExitAsync(ManagerWindowPlacement? placement)=>_disposeTask??=DisposeCoreAsync(placement);
    // RequestCloseAsync and ForceCloseAsync live in ShellClientCloseFeatures.cs (R-instances-12).
    public void LockDashboard()=>LockControls("Controls locked. Monitoring continues; automation requires explicit restart.");
    private void LockControls(string reason)=>StopOperations(reason,ManagerEventKind.ControlsLocked);
    /// <summary>
    /// Panic and Lock are absolute (Rule 11). Every step runs on its own, in the same order as before, so one that throws (a
    /// cancellation callback or a property-changed handler, for example) never skips the launch-queue stop, the pause, the lock
    /// or the notice after it. A failed step is kept by type only (Rule 10) and shown as an error once every step has run.
    /// </summary>
    private void StopOperations(string reason,ManagerEventKind kind=ManagerEventKind.Panic)
    {
        _settingsActivationGeneration++;
        bool locking=kind==ManagerEventKind.ControlsLocked;
        List<Type>? failed=null;
        void Step(Action step) { try { step(); } catch(Exception ex) { (failed??=[]).Add(ex.GetType()); } }
        Step(HoldQueuePresentation);Step(()=>MacroAssignments.CancelWork());Step(()=>MacroHostCatalog.CancelWork());
        Step(ReleaseUpdates.CancelWork);
        Interlocked.Increment(ref _accountSetupEpoch);_preserveStopNotice=true;
        Step(ClearPrivateAccountObservations);
        // The installation stop clears the selection first, so the trial fence below keeps the Panic/Lock reason (Z1600).
        Step(CancelInstallationWork);
        Step(()=>FenceNativeTrial("Trial stopped by Panic or Lock. Review possible clients before retrying."));
        Step(_launchTicketClient.ForgetAllTokens); // The next direct ticket request starts with a fresh handshake (R-api-7).
        Step(CancelDiagnosticExport);Step(CancelOverviewFocus);
        Step(()=>ClientPreviews.Fence("Previews stopped by Panic or Lock. Start explicitly to resume."));Step(()=>_authorizationStop?.Cancel());
        Step(()=>HandoffInspectionStatus="Inspection stopped by Panic or Lock. No native dispatch was permitted; review sign-in before retrying.");
        Step(_launchQueue.SignalPanic);Step(StopClientArrivalLayout);Step(StopClientWindowOperations);Step(StopClientCloseOperations);Step(StopClientPriorityOperations);Step(StopClientTrimOperations);Step(StopClientSoundOperations);Step(()=>StopUploadLimitOperations(panic:true));Step(StopClientLogCleanup);Step(()=>StopClientUpdateWork(locking));Step(StopKeepAwake);Step(()=>StopScheduledShutdownOperations(locking));Step(()=>StopDiscordAlertOperations(locking));Step(()=>StopDiscordPresenceOperations(locking));Step(StopGroupChanges);Step(StopSignInBrowserData);
        Step(CancelPerformanceTransfers);Step(StopDataEncryption);Step(StopVaultPasswordWork);Step(StopPortableBackup);Step(News.Stop);Step(()=>CancelDirectoryFeatures(locking));
        _queuePaused=true;Step(()=>Raise(nameof(QueuePauseLabel)));
        Step(()=>QueueStatus="Queue paused. Pending or uncertain requests require review before retrying.");Step(RefreshOverview);
        // Every listener hears Panic, even after an earlier one throws.
        if(PanicRequested is {} listeners) foreach(Action listener in listeners.GetInvocationList()) Step(listener);
        // Every stop, the idle and Windows session locks included, clears an error already showing; one raised after a lock
        // begins stays through Unlock. MANAGER-IMPLEMENTATION.md states this rule; keeping an existing error is an owner decision.
        Step(ClearError);Step(()=>Notice=reason);Step(()=>AddEvent(reason));_diagnostics.Record(kind);
        if(locking) { Step(()=>ClearSettingsSearch.Execute(null));Step(()=>IsLocked=true); }
        if(failed is null) return;
        // Every step has run. The stop notice stays; the error says the stop may be incomplete and how to recover.
        string stop=locking?"Lock":"Panic";
        foreach(var type in failed) _diagnostics.RecordFailure(ManagerSubsystem.Lifecycle,type);
        foreach(var type in failed) AddEvent($"{stop} step failed ({type.Name}).");
        ShowError($"{stop} ran every stop step, but {failed.Count} failed. Only the error types were saved to Diagnostics. Restart Zenith Sessions before starting automation again.");
    }
    private void AddEvent(string text) { Events.Insert(0, $"{DateTime.Now:T}  {text}"); while (Events.Count > 100) Events.RemoveAt(Events.Count - 1); }
    /// <summary>Shows the standard message for <paramref name="exception"/> as the error and records the failure, by type only, against <paramref name="subsystem"/>.</summary>
    public void Fail(Exception exception,ManagerSubsystem subsystem)=>Fail(exception,subsystem,
        exception is JsonException?"The selected file is not a valid profile export. Existing profiles were preserved."
            :exception is ArgumentException or InvalidDataException or RevisionConflictException or AccountLinkException or RobloxSessionException or LaunchQueueException or ReportFileException ? exception.Message
            : "The operation could not finish. Your existing data was preserved. Check the data folder permissions and retry.");
    /// <summary>Shows <paramref name="message"/> as the error and records the failure by type only, naming the subsystem that failed. The message is shown as given, so it must carry no secrets (Rule 10).</summary>
    public void Fail(Exception exception,ManagerSubsystem subsystem,string message)
    {
        ShowError(message);
        Type type=FailureType(exception);
        AddEvent($"Operation failed ({type.Name}).");
        _diagnostics.RecordFailure(subsystem,type);
        // After a startup failure or monitor fault no sample loop refreshes diagnostics, so the
        // failure itself updates the status and, when the tab is open, the visible rows.
        RefreshDiagnosticsView();
    }
    // A chosen-report failure is recorded by its cause, not by the ReportFileException wrapper, so a denied target
    // (UnauthorizedAccessException) or a path failure (Win32Exception) is not journaled as a plain IOException.
    // A held file and a missing folder are both still journaled as IOException by ManagerDiagnostics' closed type list;
    // only the activity line names the exact cause type.
    private static Type FailureType(Exception exception)=>exception is ReportFileException report?report.CauseType:exception.GetType();
    // Raised even when the text is unchanged, so the footer's live region announces a repeated failure again.
    private void ShowError(string message) { _errorNotice=message; Raise(nameof(ErrorNotice)); Raise(nameof(HasErrorNotice)); }
    private void ClearError() { if(_errorNotice.Length>0) ShowError(""); }
    // Last-chance seam for UnhandledExceptionGuard. Only a Type crosses it: exception messages can carry
    // links, share codes or account names (Rule 10), so it never goes through Fail(), which echoes some messages.
    internal bool IsShuttingDown=>_shuttingDown;
    /// <summary>
    /// UI thread only. Shows a fixed error for a fault that escaped every handler, as <see cref="Fail(Exception,ManagerSubsystem)"/> does,
    /// also after Panic or Lock: the error has its own line, so the stop notice stays. While locked (the idle lock, for example) the
    /// error is set behind the lock screen exactly like a Fail error, and Unlock keeps it, so the user is told to restart.
    /// </summary>
    internal void ContainUnhandled(Type errorType)
    {
        ShowError("Zenith Sessions caught an unexpected error and kept running. Only the error type was saved to Diagnostics. If anything looks wrong, restart the app.");
        AddEvent($"Unexpected error caught ({errorType.Name}).");
        _diagnostics.Record(ManagerEventKind.OperationFailed,errorType);
    }
    /// <summary>Any thread. False once this shell's log has begun closing, so the caller must keep its own record.</summary>
    internal bool TryRecordUnhandled(Type errorType)=>_diagnostics.TryRecord(ManagerEventKind.OperationFailed,errorType);
    /// <summary>
    /// Any thread (the log correlator's worker calls it). A pass the correlator recovered from is recorded by type only
    /// (Rule 10). No notice: its rows already read Unavailable and the next pass retries; a failure that ends the
    /// correlator surfaces when it is disposed.
    /// </summary>
    internal void RecordRecoveredLogCorrelationFailure(Type errorType)=>_diagnostics.RecordFailure(ManagerSubsystem.Telemetry,errorType);
    /// <summary>Flushes and closes this shell's log; idempotent, and later records are ignored. Used only when nothing else will flush it.</summary>
    internal ValueTask CloseDiagnosticsAsync()=>_diagnostics.DisposeAsync();
    // Raised even when the text is unchanged, so the footer's live region announces a repeated notice, such as a second copy, again.
    public void Inform(string message) { if(!Set(ref _notice,message,nameof(Notice))) Raise(nameof(Notice)); }
    /// <summary>
    /// UI thread only. Windows is signing out, restarting or shutting down, and WPF closes every window next without
    /// the tray Exit path, so <see cref="DisposeAsync"/> never runs. Stops all work exactly as exit does, records
    /// Shutdown and waits at most <paramref name="flushBudget"/> for the log to reach disk; true when it did. Safe to
    /// repeat and safe before, during or after DisposeAsync: the log takes one Shutdown record and ignores later ones.
    /// </summary>
    internal bool PrepareForSessionEnd(TimeSpan flushBudget)
    {
        // The session end may be the shutdown Zenith scheduled arriving; exit's cancel of that schedule is skipped.
        _windowsSessionEnding=true;
        BeginShutdown();
        _diagnostics.Record(ManagerEventKind.Shutdown);
        // Bounded, and safe on the UI thread: the log's writer and its close run on the thread pool, never this dispatcher.
        return _diagnostics.DisposeAsync().AsTask().Wait(flushBudget);
    }
    public ValueTask DisposeAsync()=>new(_disposeTask??=DisposeCoreAsync(null));
    // First, synchronous stage of every exit (tray Exit and Windows session end), once: nothing new may start
    // and every in-flight operation is cancelled, in this order, before any owner is awaited.
    private void BeginShutdown()
    {
        if(_shuttingDown) return;
        _settingsActivationGeneration++;
        _shuttingDown=true;
        // Cancellation callbacks and presentation listeners are external to this owner. Every stop must still run if one throws.
        StopShutdownStep(ReleaseUpdates.BeginShutdown);
        StopShutdownStep(ClearPrivateAccountObservations);
        StopShutdownStep(StopIdleLock);
        StopShutdownStep(StopKeepAwakeForShutdown);
        StopShutdownStep(StopClientPriorityOperations);
        StopShutdownStep(StopClientTrimOperations);
        StopShutdownStep(StopClientSoundOperations);
        StopShutdownStep(StopSignInBrowserData);
        StopShutdownStep(StopShutdownCountdown);
        StopShutdownStep(StopPublicNetworkWatch);
        StopShutdownStep(StopDiscordAlertOutbox);
        StopShutdownStep(StopDiscordPresence);
        StopShutdownStep(StopClientCloseOperations);
        StopShutdownStep(()=>StopUploadLimitOperations(panic:false));
        StopShutdownStep(()=>DestinationSearch?.CancelWork(clearInput:true));
        StopShutdownStep(MacroAssignments.CancelWork);
        StopShutdownStep(MacroHostCatalog.CancelWork);
        StopShutdownStep(DisposeSettingsSearch);
        StopShutdownStep(RefreshClientPreviewContext);
        StopShutdownStep(CancelPerformanceTransfers);
        StopShutdownStep(StopDataEncryption);StopShutdownStep(StopVaultPasswordWork);StopShutdownStep(StopPortableBackup);StopShutdownStep(News.Stop);
        StopShutdownStep(CancelInstallationWork);
        StopShutdownStep(()=>RecipientBundles.CancelWork());
        StopShutdownStep(_launchQueue.SignalPanic);
        StopShutdownStep(_lifetime.Cancel);
        StopShutdownStep(()=>_queryCancellation?.Cancel());
        StopShutdownStep(()=>_authorizationStop?.Cancel());
    }
    private void StopShutdownStep(Action stop)
    {
        try { stop(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.Lifecycle,error.GetType()); }
    }
    // The journal exists to explain a failed exit, so the fault and the shutdown marker are written and the
    // writer drained even when an owner throws. _lifetime stays undisposed then: skipped owners may still read its token.
    private async Task DisposeCoreAsync(ManagerWindowPlacement? exitWindow)
    {
        try { await DisposeOwnersAsync(exitWindow); }
        catch(Exception ex) when(RecordShutdownFault(ex)) { } // Never entered: the filter records and the fault keeps propagating.
        finally { _diagnostics.Record(ManagerEventKind.Shutdown); await _diagnostics.DisposeAsync(); }
        _lifetime.Dispose();
    }
    private bool RecordShutdownFault(Exception ex)
    {
        if(ex is not OperationCanceledException) _diagnostics.RecordFailure(ManagerSubsystem.Lifecycle,ex.GetType());
        return false;
    }
    private async Task DisposeOwnersAsync(ManagerWindowPlacement? exitWindow)
    {
        BeginShutdown();
        // Exit only. Work is already fenced and cancelled, so this write, bounded on its own, only delays waiting for owners that are stopping.
        await SaveManagerWindowAsync(exitWindow);
        await StopOwnerAsync(DisposeClientPriorityFeaturesAsync);
        await StopOwnerAsync(DisposeClientMemoryTrimFeaturesAsync);
        await StopOwnerAsync(DisposeClientSoundFeaturesAsync);
        await StopOwnerAsync(DisposeClientLogCleanupFeaturesAsync);
        await StopOwnerAsync(DisposeClientUpdateFeaturesAsync);
        await StopOwnerAsync(()=>_keepAwakeTick);
        await StopOwnerAsync(DisposeScheduledShutdownFeaturesAsync);
        StopShutdownStep(DisposePublicNetworkGuardFeatures);
        await StopOwnerAsync(DisposeDiscordAlertFeaturesAsync);
        await StopOwnerAsync(DisposeDiscordPresenceFeaturesAsync);
        await StopOwnerAsync(DisposeDataEncryptionAsync);await StopOwnerAsync(DisposeNewsAsync);
        await StopOwnerAsync(DisposeUploadLimitFeaturesAsync);
        await StopOwnerAsync(DisposeClientCloseFeaturesAsync);
        await StopOwnerAsync(DisposePortableBackupAsync);
        await StopOwnerAsync(DisposeVaultPasswordAsync);
        await _initializeTask;
        // Each owner stops on its own from here: a failed stop is recorded by type and must not skip the owners after it,
        // the Shutdown record, or exit's warning about client priorities Windows could not restore.
        await StopOwnerAsync(DisposeMacroAssignmentsAsync);
        await StopOwnerAsync(()=>ReleaseUpdates.DisposeAsync());
        await StopOwnerAsync(()=>MacroHostCatalog.DisposeAsync());
        try{await DisposeHandoffProbeAsync();}catch(Exception ex){Fail(ex,ManagerSubsystem.Launch);}
        await AddRobloxAccount.Execution;
        await ForgetSignInBrowserData.Execution;
        await Task.WhenAll(ImportPerformanceSettings.Execution, ExportPerformanceSettings.Execution);
        // A refresh and a native start both read Roblox's release over the shared anonymous public client, which
        // StopDirectoryFeaturesAsync releases below (Rule 13). Both were cancelled above; each is awaited here first.
        await Task.WhenAll(RefreshInstallations.Execution, VerifyInstallationPublisher.Execution, StartNativeTrial.Execution);
        await StopOwnerAsync(()=>_executableTrust.DisposeAsync());
        try { await Task.WhenAll(_queryTask, _monitorTask, SaveSettings.Execution, ApplyTheme.Execution,ApplyLanguage.Execution,
            NextPage.Execution, PreviousPage.Execution, Refresh.Execution, FocusSession.Execution,FocusOverviewClient.Execution,MinimizeSession.Execution,RestoreSession.Execution,ToggleFavorite.Execution,QueueSelected.Execution,ToggleQueuePause.Execution,CancelLaunch.Execution,SetAsideInterruptedQueueWrite.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,RefreshPresence.Execution,RefreshPrivateDetails.Execution,BefriendReviewedAccounts.Execution,BrowseSignedServers.Execution,NextSignedServers.Execution,RenameOwnedPrivateServer.Execution,DisconnectAccount.Execution,CleanupCredentials.Execution,ExportDiagnostics.Execution); } catch (OperationCanceledException) { } catch(Exception ex) { Fail(ex,ManagerSubsystem.Lifecycle); }
        try { await Task.WhenAll(_profileOperations.ToArray()); } catch(OperationCanceledException) { } catch(Exception ex) { Fail(ex,ManagerSubsystem.Library); }
        await StopOwnerAsync(DisposeRecipientBundlesAsync);
        await StopOwnerAsync(StopDirectoryFeaturesAsync);
        await StopOwnerAsync(()=>RecentClients.DisposeAsync());
        try {await ClientPreviews.DisposeAsync();} catch(Exception ex) {Fail(ex,ManagerSubsystem.Previews);}
        TrackOverviewFocusRow(); // _shuttingDown is set, so this releases the selected tile's subscription.
        await StopOwnerAsync(DisposeClientWindowFeaturesAsync);
        await _favoritesReloadTask;
        await StopOwnerAsync(()=>DestinationFavorites.DisposeAsync());
        await Task.WhenAll(DestinationFavorites.Refresh.Execution,DestinationFavorites.SaveCurrent.Execution,DestinationFavorites.Rename.Execution,DestinationFavorites.Delete.Execution);
        StopOwner(_favoriteStore as IDisposable);
        if (_logCorrelator is not null)
            try { await _logCorrelator.DisposeAsync(); }
            catch (Exception ex) { Fail(ex,ManagerSubsystem.Telemetry); } // Optional diagnostics must not prevent vault/queue/native owner cleanup.
        // A launch queue that cannot confirm its native provider stopped still lets the native trial, sign-in and vault owners stop.
        await StopOwnerAsync(()=>_launchQueue.DisposeAsync());_launchQueue.Changed-=QueueChanged;_launchQueue.Faulted-=QueueFaulted;StopOwner(_queueStore);
        await StopOwnerAsync(DisposeNativeTrialFeaturesAsync);
        _queryCancellation?.Dispose(); _preferencesWriter.Dispose(); _profileSerial.Dispose(); StopOwner(_credentialCleanup); await StopOwnerAsync(()=>_linkService.DisposeAsync());
        if(_signIn is IAsyncDisposable signInOwner) await StopOwnerAsync(()=>signInOwner.DisposeAsync());
        StopOwner(_identityProjection); StopOwner(_vault); StopOwner(_sessionClient as IDisposable); StopOwner(_accountDataClient as IDisposable); StopOwner(_signedServerClient as IDisposable);
        StopOwner(_accounts as IDisposable); StopOwner(_monitor as IDisposable); StopOwner(_settingsStore);_sampleSerial.Dispose();
    }
    // Shutdown only. Cancellation is expected while stopping (Error Handling Standards); any other fault keeps its type only (Rule 10, via Fail).
    private async Task StopOwnerAsync(Func<ValueTask> stop)
    {
        try { await stop(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StopShutdownStep(()=>Fail(ex,ManagerSubsystem.Lifecycle)); }
    }
    private Task StopOwnerAsync(Func<Task> stop)=>StopOwnerAsync(()=>new ValueTask(stop()));
    private void StopOwner(IDisposable? owner)
    {
        try { owner?.Dispose(); }
        catch (Exception ex) { StopShutdownStep(()=>Fail(ex,ManagerSubsystem.Lifecycle)); }
    }
    private async Task AuthorizeSelectedAsync(bool refresh)
    {
        if(IsLocked || !_ready || SelectedAccount is not { } selected || IsAuthorizing) return;
        if(_nativeLaunch.IsBusy){Notice="Finish or stop the native trial before changing saved sign-in.";return;}
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authorizationStop=stop; _linkingProfile=selected.Profile.Id; Raise(nameof(IsAuthorizing));
        _preserveStopNotice=false;
        Notice=refresh?"Checking the selected saved Roblox sign-in…":"Complete sign-in on Roblox’s page in the private window, then choose Save this Roblox sign-in.";
        try
        {
            var identity=await Task.Run(()=>refresh?_linkService.CheckSavedAsync(selected.Profile.Id,stop.Token):_linkService.LinkAsync(selected.Profile.Id,stop.Token),stop.Token);
            await LoadAccountsAsync(_lifetime.Token);
            stop.Token.ThrowIfCancellationRequested();
            if(IsLocked || _shuttingDown) return;
            Notice=$"Saved sign-in verified for @{identity.Username}. Native client launch and arrival remain separately unverified.";
            AddEvent("Roblox sign-in identity verified; selected account session stored with Windows protection.");
            _diagnostics.Record(refresh?ManagerEventKind.AuthorizationChecked:ManagerEventKind.AuthorizationLinked);
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested)
        { if(!_lifetime.IsCancellationRequested) { await LoadAccountsAsync(_lifetime.Token); if(!_preserveStopNotice) Notice="Account sign-in cancelled. Existing profiles were preserved."; } }
        catch(OperationCanceledException ended) when(!_lifetime.IsCancellationRequested)
        {
            // The sign-in or check ended on its own side; the user's stop was never used. The command discards
            // cancellations, so rethrowing left the prompt to finish signing in after its window had closed.
            await LoadAccountsAsync(_lifetime.Token);
            if(_preserveStopNotice || IsLocked || _shuttingDown) return;
            Notice=refresh?"The saved sign-in check ended before it finished. Existing profiles were preserved; review this account's sign-in status, then check again."
                :"Roblox sign-in ended before a session was saved. Existing profiles were preserved; start sign-in again when you are ready.";
            AddEvent(refresh?"Saved sign-in check ended before it finished.":"Roblox sign-in ended before a session was saved.");
            _diagnostics.RecordFailure(ManagerSubsystem.SignIn,ended.GetType());
        }
        catch(AccountLinkException refused) when(refused.ProfileRemoved)
        {
            // The row is being removed from the library, or already was: the service refused before anything was read or sent
            // (N-B1a-1). A routine refusal, not a failure: it is shown as a notice and records nothing (O-C1-3).
            if(!_lifetime.IsCancellationRequested) await LoadAccountsAsync(_lifetime.Token);
            if(stop.IsCancellationRequested || _preserveStopNotice || IsLocked || _shuttingDown) return;
            Notice=refused.Message;
        }
        catch(Exception)
        {
            if(!_lifetime.IsCancellationRequested) await LoadAccountsAsync(_lifetime.Token);
            if(stop.IsCancellationRequested && !_preserveStopNotice && !_lifetime.IsCancellationRequested)
                Notice="Account sign-in cancelled. Existing profiles were preserved.";
            stop.Token.ThrowIfCancellationRequested();
            if(IsLocked || _shuttingDown) return;
            throw;
        }
        finally { _authorizationStop=null; _linkingProfile=null; stop.Dispose(); Raise(nameof(IsAuthorizing)); }
    }
    private async Task DisconnectSelectedAsync()
    {
        if(IsLocked || SelectedAccount is not { } selected) return;
        ClearPrivateAccountObservations();
        _preserveStopNotice=false;
        string queueStopped=StopQueueForSavedStateRemoval("Trial stopped because a saved sign-in was removed. Review any process-start receipt before retrying; existing Roblox clients remain open.");
        if(QueueReady) await _launchQueue.InvalidateAuthorizationAsync(selected.Profile.Id,_lifetime.Token);
        await _launchQueue.WaitForActiveCleanupAsync(_lifetime.Token);
        _authorizationStop?.Cancel(); await Task.WhenAll(AddRobloxAccount.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,RefreshPresence.Execution,RefreshPrivateDetails.Execution,BefriendReviewedAccounts.Execution,BrowseSignedServers.Execution,NextSignedServers.Execution,RenameOwnedPrivateServer.Execution,InspectNativeHandoff.Execution);
        await Task.Run(()=>_linkService.DisconnectAsync(selected.Profile.Id,_lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token);
        if(!_preserveStopNotice && !IsLocked && !_shuttingDown) Notice="Saved sign-in removed from Zenith. The account profile and running clients were preserved. This does not sign out Roblox on other devices."+queueStopped;
        _diagnostics.Record(ManagerEventKind.AuthorizationDisconnected);
    }
    private async Task CleanupCredentialsAsync()
    {
        var result=await Task.Run(()=>_credentialCleanup.RunAsync(_lifetime.Token));
        // Store-wide damage first: it fails every record, and the Retry button below cannot repair it either.
        CredentialCleanupStatus=(result.Remaining==0?"No pending credential cleanup."
            :result.StoreDamaged?$"{result.Remaining} unused protected record(s) await cleanup. A shared file that every saved sign-in relies on ({ProtectedCredentialVault.WriterLockFileName} in the accounts.vault folder) is invalid, so Zenith stopped cleanup instead of guessing what to delete. Retrying will not repair it. Keep the accounts.vault folder for review; it is in the data folder (More options › Open data folder)."
            :result.RecordDamaged?$"{result.Remaining} unused protected record(s) await cleanup. A saved sign-in file from a removed profile is damaged, so Zenith left it in place instead of guessing what to delete. Retrying will not repair it. Keep the accounts.vault folder for review; it is in the data folder (More options › Open data folder)."
            :$"{result.Remaining} unused protected record(s) await cleanup. Check storage access and choose Retry credential cleanup.")+CurrentSignInFolderNote();
        if(result.ErrorType is not null) { AddEvent("Unused protected-file cleanup needs attention; the durable cleanup journal was retained."); _diagnostics.RecordFailure(ManagerSubsystem.Vault); }
    }
    // The startup sweep's note holds only while what it kept may still be on disk. A sign-in or handoff browser's admission
    // removes every leftover before it opens, so after one the note would be stale on every later status.
    private string CurrentSignInFolderNote()
    {
        if(_signInFolderNote.Length>0 && !SignInProfileDirectory.SweptLeftoversMayRemain(DataRoot)) _signInFolderNote="";
        return _signInFolderNote;
    }
    // Startup only: it runs under the single-instance lock and before the library is ready, so no sign-in or handoff
    // check in this process can be using a folder. A crash otherwise left its browser data until the next sign-in.
    private async Task SweepSignInFoldersAsync()
    {
        try
        {
            var swept=await Task.Run(()=>SignInProfileDirectory.SweepOwned(DataRoot,_lifetime.Token));
            if(swept.Removed>0) AddEvent($"Removed {swept.Removed} leftover private sign-in folder(s) from an earlier session.");
            // Held by another owner, or a file in them was still open: nothing marks them for review by itself.
            int retried=swept.Kept-swept.KeptForReview;
            if(swept.MoreRemainThanAdmissionMayOpen)
            {
                // Kept for any reason or not opened, together they outnumber what the next admission may open, so the next
                // sign-in and every saved-session check refuse them all as needing review (SweepOwned remembers exactly this for
                // the handoff checks). The routine lines would promise a removal or a retry that refusal denies, so one line
                // says what holds instead.
                int remaining=swept.Kept+swept.Deferred;
                AddEvent($"{remaining} leftover private sign-in folder(s) remain in signin-browser-data, more than one sign-in may check, so sign-in waits until they are reviewed.");
                _signInFolderNote=(swept.KeptForReview>0?ReviewNote(swept.KeptForReview):"")
                    +$" {remaining} leftover private sign-in folder(s) remain in signin-browser-data, more than one sign-in may check, so sign-in and saved-session checks are refused until they are reviewed. Close Zenith Sessions and review signin-browser-data in the Zenith data folder.";
            }
            else
            {
                // Not opened because one run's budget ran out: nothing is wrong with them, and the next sign-in or start removes them.
                if(swept.Deferred>0) AddEvent($"{swept.Deferred} more leftover private sign-in folder(s) were left for the next sign-in or start to remove.");
                // The next sign-in or start tries held or busy folders again, so this is routine. Only data SweepOwned counts as
                // kept for review waits for someone to look at it.
                if(retried>0) AddEvent($"{retried} leftover private sign-in folder(s) were in use or could not be removed yet. The next sign-in or start retries them.");
                _signInFolderNote=(swept.KeptForReview>0?ReviewNote(swept.KeptForReview):"")
                    +(retried>0?$" {retried} leftover private sign-in folder(s) in signin-browser-data were kept at startup because they were in use or could not be removed yet. The next sign-in or start retries them.":"");
            }
            // The same rule SweepOwned remembered for the handoff checks: the review note and the sign-in failure record appear
            // exactly when the next sign-in or saved-session check would be refused as a cleanup failure.
            if(!swept.NeedsReview) return;
            AddEvent("Leftover private sign-in folders were kept for review. See Settings, Protected-file recovery.");
            _diagnostics.RecordFailure(ManagerSubsystem.SignIn);
            static string ReviewNote(int keptForReview)
                =>$" {keptForReview} leftover private sign-in folder(s) in signin-browser-data were kept for review because they were not recognized, were redirected or held more files than cleanup may check. Close Zenith Sessions and review signin-browser-data in the Zenith data folder.";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            _signInFolderNote=" Leftover private sign-in folders could not be checked at startup, so signin-browser-data was left unchanged. Close Zenith Sessions and review signin-browser-data in the Zenith data folder.";
            AddEvent("Leftover private sign-in folders could not be checked and were kept for review. See Settings, Protected-file recovery.");
            _diagnostics.RecordFailure(ManagerSubsystem.SignIn,ex.GetType());
        }
    }
    private LaunchAdmission ReadLaunchAdmission(IReadOnlyList<LaunchOperation> operations)
        => ReadLaunchAdmissionForBudget(operations,LaunchResourceBudget.FromSettings(Volatile.Read(ref _savedSettings).Capacity!),false);
    private LaunchResourceBudget ReadNewLaunchBudget(LaunchIntent _)
        => LaunchResourceBudget.FromSettings(Volatile.Read(ref _savedSettings).Capacity!);
    private LaunchAdmission ReadCandidateLaunchAdmission(LaunchOperation candidate,IReadOnlyList<LaunchOperation> operations)
        => candidate.ResourceBudget is { } budget?ReadLaunchAdmissionForBudget(operations,budget,LaunchDescriptions.AwaitsClientUpdate(candidate))
            :new(false,"The request has no recorded memory budget. Review it before starting.");
    /// <param name="heldForUpdate">The candidate already waits on a Roblox update: a fresh count of at least one running client held it.
    /// Without a trusted count it keeps that reason only for <see cref="ClientUpdateHold.UntrustedSampleGrace"/> after the newest trusted inventory.</param>
    private LaunchAdmission ReadLaunchAdmissionForBudget(IReadOnlyList<LaunchOperation> operations,LaunchResourceBudget budget,bool heldForUpdate)
    {
        // The public-network guard's hold comes first: it is the reason the user can act on, and it holds whether or not
        // monitoring is ready. Taken on Windows' thread the moment the network turns Public (ShellPublicNetworkGuardFeatures).
        if(Volatile.Read(ref _publicNetworkHeld)==1) return new(false,PublicNetworkPolicy.HoldExplanation,LaunchHoldReason.PublicNetwork);
        var observed=Volatile.Read(ref _resourceAdmission);
        if(observed is null || IsLocked || _shuttingDown) return new(false,"Monitoring is not ready or controls are locked.");
        var snapshot=observed.Desktop;var admission=observed.Admission;
        var limits=Volatile.Read(ref _savedSettings).Capacity!;var now=DateTimeOffset.UtcNow;
        var projection=LaunchReservationProjection.Evaluate(new(true,operations),snapshot,limits,now);
        NoteTrustedInventory(snapshot,projection);
        // Checked before headroom: it is the reason the user can act on, and it changes only when clients open or close.
        // Possible clients this queue started count as running; only a fresh complete inventory makes the count trustworthy.
        // A stale, incomplete or failed one refuses every start below anyway, so a row already held (on a fresh count of at
        // least one) stays held through a short run of them: deciding on each untrusted count would re-save the queue and
        // switch the row's text between two explanations on every such sample. Once the newest trusted inventory is older
        // than the grace, no count supports the claim that a client runs, so the row returns to the wait for fresh evidence
        // (O-C9-1). The comparison is still re-read, so it can end the hold meanwhile.
        // The selected row's comparison (and its file) is read only while a client may run: with none, nothing can close.
        int running=projection.FreshProcessInventory?projection.OccupiedForAdmission
            :heldForUpdate && ClientUpdateHold.KeepsHoldWithoutTrustedCount(new DateTimeOffset(Volatile.Read(ref _trustedInventoryUtcTicks),TimeSpan.Zero),now)?1:0;
        if(projection.ForecastsKnown && running>0
            && ClientUpdateHold.Evaluate(LaunchReleaseMatchForHold(),running) is {Hold:true} update)
            return new(false,update.Reason,LaunchHoldReason.ClientUpdate);
        var decision=CapacityPolicy.Evaluate(snapshot.Resources,limits,projection.Reservations,projection.OccupiedForAdmission,now,budget);
        return new(admission.Allowed && projection.ForecastsKnown && projection.FreshProcessInventory && decision.SamplesHealthy && decision.MemoryOnlySlots>0,
            !projection.ForecastsKnown||!projection.FreshProcessInventory?projection.Explanation:!decision.SamplesHealthy||decision.MemoryOnlySlots==0?decision.Explanation:admission.Explanation);
    }
    // Lock-free and allocation-free (it runs once per sample and once per admission): a plain compare-and-swap keeps the
    // newest observation whichever of the sampler or the queue notes it first.
    private void NoteTrustedInventory(DesktopSnapshot snapshot,LaunchReservationProjectionResult projection)
    {
        if(!projection.FreshProcessInventory || snapshot.ProcessObservedUtc is not { } observed) return;
        long ticks=observed.UtcTicks,seen=Volatile.Read(ref _trustedInventoryUtcTicks);
        while(ticks>seen)
        {
            long prior=Interlocked.CompareExchange(ref _trustedInventoryUtcTicks,ticks,seen);
            if(prior==seen) return;
            seen=prior;
        }
    }
    private IReadOnlySet<Guid> ReadObservedReservations(IReadOnlyList<LaunchOperation> operations)
    {
        var desktop=Volatile.Read(ref _latestDesktop);
        return desktop is null?System.Collections.Frozen.FrozenSet<Guid>.Empty:
            LaunchReservationProjection.Evaluate(new(true,operations),desktop,Volatile.Read(ref _savedSettings).Capacity!,DateTimeOffset.UtcNow).SafelyObservedOperationIds;
    }
    private async Task<bool> ValidateLaunchAsync(LaunchIntent intent,CancellationToken ct)
    {
        return await new LaunchAuthorizationValidator(_accounts,_vault).ValidateAsync(intent,ct).ConfigureAwait(false);
    }
    private void QueueChanged(LaunchQueueSnapshot snapshot)
    {
        var previous=Interlocked.Exchange(ref _latestQueue,snapshot);
        var old=previous.Operations.ToDictionary(row=>row.Intent.OperationId);
        if(snapshot.Operations.Any(row=>old.TryGetValue(row.Intent.OperationId,out var prior)&&row.Intent.AuthorizationGeneration!=prior.Intent.AuthorizationGeneration))
            Interlocked.Exchange(ref _nativeMetadataPending,1);
        // Every snapshot passes here (only the UI step below is coalesced), so a receipt the previous one lacked is a client
        // this queue's provider just started. Receipts restored at startup arrive before any installation is selected.
        if(snapshot.Operations.Any(row=>row.ProcessReceipt is {IsValid:true}
            &&!(old.TryGetValue(row.Intent.OperationId,out var prior)&&prior.ProcessReceipt is {IsValid:true})))
            NoteQueueClientStarted();
        Volatile.Write(ref _pendingQueue,snapshot);
        if(Interlocked.Exchange(ref _queueUiScheduled,1)!=0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            Interlocked.Exchange(ref _queueUiScheduled,0);
            var latest=Interlocked.Exchange(ref _pendingQueue,null);if(latest is null || _shuttingDown) return;
            _queuePaused=latest.Paused;Raise(nameof(QueuePauseLabel));
            // Say once why the queue paused itself; the footer keeps its fixed-width counts.
            if(latest.PauseReason!=_shownQueuePauseReason)
            {
                _shownQueuePauseReason=latest.PauseReason;
                if(latest.PauseReason!=LaunchQueuePauseReason.None) {Notice=LaunchDescriptions.ExplainPause(latest.PauseReason);AddEvent(Notice);}
            }
            var ids=latest.Operations.Select(o=>o.Intent.OperationId).ToHashSet();
            for(int i=Launches.Count-1;i>=0;i--) if(!ids.Contains(Launches[i].Id)) Launches.RemoveAt(i);
            var rows=Launches.ToDictionary(r=>r.Id);
            foreach(var operation in latest.Operations)
            {
                if(rows.TryGetValue(operation.Intent.OperationId,out var row)) row.Update(operation);
                else Launches.Add(new(operation) { Label=Accounts.FirstOrDefault(a=>a.Profile.Id==operation.Intent.ProfileId)?.Label??"Saved profile · "+operation.Intent.ProfileId.ToString("N")[..6] });
            }
            RecordOverviewQueue(latest);
            ObserveLaunchBatchAlerts(latest);
            RefreshVisibleQueueStatus();
            RefreshAccountLaunchStates();
            RefreshNativeTrialFeatures();
            RefreshLaunchEvidence();
            ShowLaunchReleaseStarted();
            if(Volatile.Read(ref _pendingQueue) is { } pending) QueueChanged(pending);
        }));
    }
    private void QueueFaulted(Exception error)
    {
        _diagnostics.RecordFailure(ManagerSubsystem.Queue,error.GetType());
        if(!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(()=>
        {
            if(_shuttingDown) return;
            QueueReady=_launchQueue.IsHealthy;
            if(!QueueReady) ShowQueueBlocker("Queue stopped after an error. Existing queue files were preserved; restart after resolving storage access.");
            RefreshOverview();
            Notice="A launch operation needs attention. Check the queue; no uncertain result is reported as running.";
        }));
    }
    // Names the kept file that blocks the queue and where to review it. Messages hold only fixed
    // file names from the data folder, never queue contents or exception text.
    private void ShowQueueBlocker(string unclassified)
    {
        QueueStatus=_queueStore.RecoveryBlocker switch
        {
            {Kind:LaunchQueueBlockerKind.InterruptedWrite} b=>$"Launch queue unavailable: an interrupted save left {b.FileName} next to the last completed save. Choose Set aside interrupted save to keep it under a new name"
                +(_launchQueue.Opened?", then restart Zenith Sessions to reopen the queue.":" and reopen the queue."),
            {Kind:LaunchQueueBlockerKind.OrphanEvidence} b=>$"Launch queue unavailable: {b.FileName} was found without a readable saved queue. It was kept; review it via More options › Open data folder.",
            {Kind:LaunchQueueBlockerKind.ConflictingDamagedCopy} b=>$"Launch queue unavailable: launch-queue.json is damaged and differs from the copy already kept as {b.FileName}. Both were kept; review them via More options › Open data folder.",
            {Kind:LaunchQueueBlockerKind.UnreadableQueue} b=>$"Launch queue unavailable: {b.FileName} could not be read and no usable backup was found. It was kept; review it via More options › Open data folder.",
            {Kind:LaunchQueueBlockerKind.UnreadableBackup} b=>$"Launch queue unavailable: the backup {b.FileName} could not be read or has an unknown format. It was kept; review it via More options › Open data folder.",
            {Kind:LaunchQueueBlockerKind.UnsupportedFormat} b=>$"Launch queue unavailable: {b.FileName} was saved by a newer version of Zenith Sessions. It was kept; open it with a compatible version.",
            _=>unclassified
        };
        Raise(nameof(CanSetAsideInterruptedQueueWrite));
    }
    // The view confirms first. The store renames (never deletes) the interrupted save only behind a
    // readable committed queue. A queue that never opened then opens paused, with nothing resumed;
    // one that was already running stays stopped until a restart.
    private async Task SetAsideInterruptedQueueWriteAsync()
    {
        if(_shuttingDown || !CanSetAsideInterruptedQueueWrite) return;
        if(IsLocked) {Notice="Unlock the dashboard before changing launch-queue files.";return;}
        string? kept;
        try
        {
            kept=await Task.Run(()=>_queueStore.SetAsideInterruptedWriteAsync(_lifetime.Token));
            await Task.Run(()=>_launchQueue.InitializeAsync(_lifetime.Token));
        }
        catch(Exception) when(!_launchQueue.IsHealthy && !_shuttingDown)
        {
            ShowQueueBlocker("Launch queue unavailable. Existing queue files were preserved; account profiles remain usable.");
            throw;
        }
        string setAside=kept is null?"No interrupted save was left to set aside.":$"The interrupted save was kept as {kept}.";
        if(!_launchQueue.IsHealthy)
        {
            // A save failed while the queue was already running; only a restart reopens it.
            QueueStatus=$"{setAside} Restart Zenith Sessions to reopen the launch queue.";Raise(nameof(CanSetAsideInterruptedQueueWrite));
            Notice=QueueStatus;AddEvent(QueueStatus);return;
        }
        QueueReady=true;
        if(_queueStore.RecoveryNotice is { } recovery) AddEvent(recovery);
        Notice=$"{setAside} The launch queue reopened paused from its last completed save; nothing resumed automatically.";AddEvent(Notice);
    }
    public async Task ConfirmLaunchClientClosedAsync(Guid id)
    { if(!IsLocked && QueueReady) await _launchQueue.AcknowledgeClientClosedAsync(id,_lifetime.Token); }
    private Task OwnProfileOperation(Func<Task> work, CancellationToken ct = default)
    {
        if(ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if(_shuttingDown) return Task.FromCanceled(new CancellationToken(true));
        if(!_ready) return Task.FromException(new InvalidOperationException("The local library has not opened successfully."));
        if(_profileOperations.Count>=32) return Task.FromException(new ArgumentException("Too many pending library operations. Wait for the current work to finish, then retry."));
        var operation=RunSerialProfileOperationAsync(work,ct); _profileOperations.Add(operation); return ObserveProfileOperationAsync(operation);
    }
    private async Task RunSerialProfileOperationAsync(Func<Task> work,CancellationToken ct)
    {
        using var waiting=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);
        await _profileSerial.WaitAsync(waiting.Token);
        try { await work(); } finally { _profileSerial.Release(); }
    }
    private async Task ObserveProfileOperationAsync(Task work)
    { try { await work; } finally { _profileOperations.Remove(work); } }
}
