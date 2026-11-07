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
    public string Identity => _identity;
    public string LinkedAccount => _linkedAccount;
    public void SetAuthorization(string state,string account,DateTimeOffset? expiresUtc=null) { _identity=state; _linkedAccount=account; _expiresUtc=expiresUtc; Raise(nameof(Identity)); Raise(nameof(LinkedAccount)); }
    public void SetCachedAuthorization(AuthorizationMetadata metadata)
    {
        if(_publicData is not null && _authorizationMetadata?.VerifiedUserId!=metadata.VerifiedUserId) ClearPublicPresentation();
        _authorizationMetadata=metadata;
        string state=metadata.Health!=AuthorizationHealth.Linked?"Sign in required":metadata.ExpiresUtc<=DateTimeOffset.UtcNow?"Expired link":"Saved link";
        SetAuthorization(state,$"Last-known linked ID {metadata.VerifiedUserId}. Authorization is checked before use.",metadata.ExpiresUtc);
        Raise(nameof(Label));Raise(nameof(Initial));Raise(nameof(Username));Raise(nameof(KnownUserId));Raise(nameof(AuthorizationGeneration));Raise(nameof(PowerLabel));Raise(nameof(PowerExplanation));
    }
    public void UpdateAuthorizationClock(DateTimeOffset now)
    { if(_identity is "Linked" or "Saved link" && _expiresUtc is { } expiry && now>=expiry) { _identity="Expired"; Raise(nameof(Identity));Raise(nameof(PowerLabel));Raise(nameof(PowerExplanation)); } }
    public string Destination => Profile.PreferredPlaceId switch
    {
        6564161046 => "Coloseum sword-fight", 110829983956014 => "Anime Card Clash", 2512643572 => "Bubble Gum Simulator",
        85896571713843 => "Bubble Gum Simulator INFINITY", null => "No game selected", _ => $"Place {Profile.PreferredPlaceId}"
    };
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
    public string State=>Operation.State switch {LaunchState.Preparing=>"Preparing account",LaunchState.Blocked when Operation.Problem==LaunchProblem.PreparationInterrupted=>"Review preparation",LaunchState.Arrived=>"Arrival verified",LaunchState.ReviewRequired=>"Review required",LaunchState.ClientStartedUnverified=>"Client started · unverified",_=>Operation.State.ToString()};
    public string Detail=>LaunchDescriptions.Explain(Operation.Problem);
    public string RequestedDestination=>$"Place {Operation.Intent.PlaceId}"+(Operation.Intent.RequestedJobId is { } job?$" · {job}":" · server not specified");
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
    private SessionLogView _log = SessionLogView.Unknown;
    public SessionLogView Log => _log;
    public void UpdateLog(SessionLogView value) { if (_log != value) { _log = value; Raise(nameof(Log)); } }
    public SessionObservation Observation { get; private set; } = observation;
    public string InstanceKey => Observation.InstanceKey;
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
    private sealed record ResourceAdmissionSnapshot(DesktopSnapshot Desktop,LaunchAdmission Admission);
    private ResourceAdmissionSnapshot? _resourceAdmission;
    private DesktopSnapshot? _latestDesktop;
    private string _queueStatus="Opening launch queue…";
    private bool _queueExpanded;
    public bool QueueExpanded { get=>_queueExpanded;set=>Set(ref _queueExpanded,value); }
    public string QueueStatus { get=>_queueStatus;private set=>Set(ref _queueStatus,value); }
    public bool QueueReady { get=>_queueReady;private set {if(Set(ref _queueReady,value)) {if(value) RefreshVisibleQueueStatus();RefreshOverview();}} }
    public string QueuePauseLabel=>EffectiveQueuePaused?"Resume queue":"Pause queue";
    private readonly ResourcePressureTracker _pressure=new();
    private CancellationTokenSource? _authorizationStop;
    private Guid? _linkingProfile;
    private bool _preserveStopNotice;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queryCancellation;
    private Task _queryTask = Task.CompletedTask, _monitorTask = Task.CompletedTask, _initializeTask = Task.CompletedTask;
    /// <summary>The owned debounced account query or busy-store retry; shutdown joins it.</summary>
    internal Task AccountQuery=>_queryTask;
    private Task? _disposeTask;
    private readonly HashSet<Task> _profileOperations=[];
    private readonly SemaphoreSlim _profileSerial=new(1,1);
    private bool _shuttingDown,_settingsNeedRepair,_libraryUnavailable;
    private Guid? _selectAfterReload;
    private bool _initializationStarted;
    private SessionsSettings _settings = new SessionsSettings().Validated();
    private SessionsSettings _savedSettings = new SessionsSettings().Validated();
    private readonly SemaphoreSlim _preferencesWriter = new(1, 1);
    private int _pendingSaves;
    private bool _saveFailed;
    private bool _panicRegistered;
    private string _pressureNotice="Waiting for a coherent resource sample.",_diagnosticsStatus="Diagnostics starting";
    private string _credentialCleanupStatus="Credential cleanup has not run.";
    public string CredentialCleanupStatus { get=>_credentialCleanupStatus; private set=>Set(ref _credentialCleanupStatus,value); }
    public string PressureNotice { get=>_pressureNotice; private set=>Set(ref _pressureNotice,value); }
    public string DiagnosticsStatus { get=>_diagnosticsStatus; private set=>Set(ref _diagnosticsStatus,value); }
    public string PanicLabel=>_panicRegistered?"Panic stop · F12":"Panic stop";
    public string PanicShortcutStatus=>IsSample?"Isolated sample previews do not register global shortcuts. F12 works while this preview is focused; Panic stop is also available.":_panicRegistered?"F12 is registered globally for this manager.":"Global F12 is unavailable, usually because Zenith already owns it. The Panic button and focused F12 remain available; existing shortcuts are preserved.";
    private int _queryGeneration, _page;
    private int _filteredTotal;
    private bool _updatingGroups;
    private bool _ready, _locked;
    private string _tab = "Overview", _search = "", _notice = "Starting local services…";
    private GroupFilter _group=GroupFilter.All;
    private AccountOrder _order;
    private AccountRow? _selected;
    private int _accountCount, _sessionCount;
    private string _physical = "Measuring…", _commit = "Measuring…", _cpu = "Measuring…", _capacity = "Waiting for measurements", _sampleTime = "—";
    private string _pageLabel = "0 accounts", _saveStatus = "Not loaded";
    public string DataRoot { get; }
    public bool IsSample { get; }
    public bool IsReady=>_ready;
    public string ProductLabel => IsSample ? "ISOLATED SAMPLE LIBRARY" : "PERSONAL · LOCAL STORAGE";
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    private SessionRow? _selectedSession;
    public SessionRow? SelectedSession { get => _selectedSession; set { if (Set(ref _selectedSession,value)) { Raise(nameof(HasSelectedSession)); Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates));RefreshTelemetryPresentation(); } } }
    public bool HasSelectedSession => SelectedSession is not null;
    public ObservableCollection<LaunchRow> Launches { get; }=[];
    public IReadOnlyList<GroupFilter> Groups { get; private set; } = [GroupFilter.All];
    public ObservableCollection<string> Events { get; } = [];
    public IReadOnlyList<ThemePreset> Themes => ThemeCatalog.Presets;
    public RecentClientHistoryViewModel RecentClients { get; }
    private string _processInventoryStatus="Process inventory freshness has not been established.";
    public string ProcessInventoryStatus {get=>_processInventoryStatus;private set=>Set(ref _processInventoryStatus,value);}
    public IReadOnlyList<ProviderCapability> Capabilities => ProviderCapabilities.Current;
    public IReadOnlyList<AccountOrder> Orders { get; } = Enum.GetValues<AccountOrder>();
    public IReadOnlyList<AccountSortChoice> SortChoices { get; } = [new("Name",AccountOrder.Name),new("Priority",AccountOrder.Priority),new("Recently updated",AccountOrder.RecentlyUpdated),new("Favorites first",AccountOrder.FavoritesFirst)];
    public AccountSortChoice SortChoice { get=>SortChoices.First(c=>c.Value==Order); set { if(value is not null) Order=value.Value; } }
    public string Tab { get => _tab; private set { if (Set(ref _tab, value)) {foreach (var name in new[] { nameof(IsOverview), nameof(IsAccounts), nameof(IsSessions),nameof(IsJoinGame),nameof(IsSessionWorkspace),nameof(IsWindows), nameof(IsAutomation), nameof(IsDiagnostics),nameof(IsSettings), nameof(IsGuide) }) Raise(name);if(IsDiagnostics) RefreshDiagnosticsView(true);RefreshClientPreviewContext();RefreshTelemetryPresentation();RefreshPublicImageActivity();RefreshJoinGameSources();if(IsOverview) RefreshOverview();if(IsJoinGame) ResetJoinFlow();} } }
    public bool IsOverview => Tab == "Overview"; public bool IsAccounts => Tab == "Accounts"; public bool IsSessions => Tab == "Sessions";
    public bool IsAutomation => Tab == "Automation"; public bool IsSettings => Tab == "Settings"; public bool IsGuide => Tab == "Guide";
    public bool IsJoinGame=>Tab=="JoinGame";
    public bool IsSessionWorkspace=>IsSessions || IsJoinGame;
    public bool IsDiagnostics=>Tab=="Diagnostics";
    public bool IsWindows=>Tab=="Windows";
    public bool IsLocked { get => _locked; private set { if (Set(ref _locked, value)) { Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates)); Raise(nameof(CanRefreshInstallations)); Raise(nameof(CanVerifyInstallationPublisher)); NotifyPerformanceTransfer(); RefreshDestinationCapacity(); GameDetails?.RefreshAvailability(); RecentClients?.RefreshAvailability();RefreshClientPreviewContext(); MacroHostCatalog?.RefreshAvailability(); MacroAssignments?.RefreshAvailability(); RefreshPublicImageActivity();RefreshOverview(); } } }
    private bool _compactLayout;
    public bool CompactLayout { get=>_compactLayout; set=>Set(ref _compactLayout,value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) ScheduleQuery(); } }
    public GroupFilter Group { get => _group; set { if(_updatingGroups && value is null) return;if (Set(ref _group, value ?? GroupFilter.All) && !_updatingGroups) ScheduleQuery(); } }
    public AccountOrder Order { get => _order; set { if (Set(ref _order, value)) { Raise(nameof(SortChoice)); ScheduleQuery(); } } }
    public AccountRow? SelectedAccount { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(HasSelection)); RefreshDestinationCapacity(); RefreshJoinGameSources(); } } }
    public bool HasSelection => SelectedAccount is not null;
    public bool CanNext => (_page + 1L) * 100 < _filteredTotal;
    public bool CanPrevious => _page > 0;
    public string EmptyAccountsText => !_ready?(_libraryUnavailable?"Library unavailable. Existing files were preserved; open Settings for recovery.":"Opening your local library…")
        :AccountCount == 0 ? "Add your first account profile to get started." : "No profiles match this search or group.";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string PageLabel { get => _pageLabel; private set => Set(ref _pageLabel, value); }
    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public int AccountCount { get => _accountCount; private set => Set(ref _accountCount, value); }
    public int SessionCount { get => _sessionCount; private set => Set(ref _sessionCount, value); }
    public string Physical { get => _physical; private set => Set(ref _physical, value); }
    public string Commit { get => _commit; private set => Set(ref _commit, value); }
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string Capacity { get => _capacity; private set => Set(ref _capacity, value); }
    public string SampleTime { get => _sampleTime; private set => Set(ref _sampleTime, value); }
    public bool Dense { get => _settings.DenseAccounts; set { if (_settings.DenseAccounts == value) return; _settings = _settings with { DenseAccounts = value }; Raise(); PreferencesEdited(); } }
    private string _spareRam = "3", _physicalPerClient = "2.5", _commitPerClient = "3", _maximumClients = "", _oauthClientId = "", _callbackPort="47635";
    public string SpareRam { get => _spareRam; set { if (Set(ref _spareRam, value)) PreferencesEdited(); } }
    public string PhysicalPerClient { get => _physicalPerClient; set { if (Set(ref _physicalPerClient, value)) PreferencesEdited(); } }
    public string CommitPerClient { get => _commitPerClient; set { if (Set(ref _commitPerClient, value)) PreferencesEdited(); } }
    public string MaximumClients { get => _maximumClients; set { if (Set(ref _maximumClients, value)) PreferencesEdited(); } }
    public string OAuthClientId { get => _oauthClientId; set { if (Set(ref _oauthClientId, value)) PreferencesEdited(); } }
    public string CallbackPort { get => _callbackPort; set { if (Set(ref _callbackPort,value)) PreferencesEdited(); } }
    public string CallbackUrl => int.TryParse(CallbackPort,out int port) ? $"http://localhost:{port}/zenith/oauth/" : "Enter a valid callback port";
    public bool IsAuthorizing => _authorizationStop is not null;
    public string ThemeName => ThemeCatalog.Resolve(_settings.Theme).Name;
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
    public AsyncCommand Refresh { get; }
    public AsyncCommand LinkAccount { get; }
    public AsyncCommand AddRobloxAccount { get; }
    public AsyncCommand RefreshAuthorization { get; }
    public AsyncCommand DisconnectAccount { get; }
    public AsyncCommand CleanupCredentials { get; }
    public Command CancelAuthorization { get; }
    public event Action<ThemePreset>? ThemeChanged;
    public event Action? PanicRequested;

    public ShellViewModel(string root, bool sample, Dispatcher dispatcher,IDesktopMonitor? monitor=null,
        IPublicRobloxDirectory? publicDirectory=null,IRobloxPublicServerDirectory? serverDirectory=null,IAccountRepository? accounts=null,
        IFavoriteDestinationStore? favoriteStore=null, IPerformanceSettingsTransferStore? performanceTransferStore=null,
        IClientInstallationDiscovery? installationDiscovery=null,IPublicRobloxGameDirectory? gameDirectory=null,
        ISessionHistoryStore? historyStore=null,IClientPreviewProvider? previewProvider=null,IRobloxSessionSignIn? signIn=null,
        IRobloxSessionClient? sessionClient=null,IClientExecutableTrustVerifier? executableTrust=null,IRecipientBundleStore? recipientBundleStore=null,
        IRobloxHandoffProbe? handoffProbe=null,WindowsConfiguredNativeLaunchProvider? nativeProvider=null,IZenithMacroCatalogClient? macroCatalogClient=null,
        IMacroAssignmentStore? macroAssignmentStore=null,bool enableAutomaticPublicImages=false,
        IClientProcessPriorityControl? clientPriority=null,IRobloxShareLinkClient? shareLinkClient=null,IPrivateServerStore? privateServerStore=null)
    {
        DataRoot = root; IsSample = sample; _dispatcher = dispatcher;_enableAutomaticPublicImages=enableAutomaticPublicImages;
        _monitor=monitor??new WindowsDesktopMonitor();
        _accounts = accounts??new AccountRepository(root); _settingsStore = new(root);
        _vault=new(root);
        _sessionClient=sessionClient??new RobloxSessionClient();
        _identityProjection=new(root,_accounts,_vault);
        _signIn=signIn??new OwnedRobloxSignIn(root,dispatcher);
        _shareLinkClient=shareLinkClient??new RobloxShareLinkClient();_privateServerStore=privateServerStore??new PrivateServerStore(root);
        _linkService=new(_accounts,_vault,_sessionClient,_signIn,_identityProjection,_shareLinkClient);
        _credentialCleanup=new(_accounts,_vault);
        _diagnostics=new(root);
        InitializeTelemetry();
        InitializeClientPreviews(previewProvider);
        RecentClients=new(historyStore??new RecentClientHistoryStore(root),CanUseDirectories,
            error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        _queueStore=new(root);
        _nativeCapture=new OwnedRobloxNativeHandoffCapture(root,dispatcher);
        // An injected development provider transfers ownership; it must remain Unverified.
        _nativeLaunch=nativeProvider??new(_linkService,_nativeCapture,_vault);
        _launchQueue=new(_queueStore,_nativeLaunch,ValidateLaunchAsync,ReadLaunchAdmission,observedReservations:ReadObservedReservations,
            budgetResolver:ReadNewLaunchBudget,candidateAdmission:ReadCandidateLaunchAdmission);
        _launchQueue.Changed+=QueueChanged;_launchQueue.Faulted+=QueueFaulted;
        InitializeDirectoryFeatures(publicDirectory,serverDirectory,gameDirectory);
        InitializeDestinationCapacity();
        _favoriteStore=favoriteStore??new FavoriteDestinationsStore(root);
        DestinationFavorites=new(_favoriteStore,ServerBrowser,CanUseDirectories);
        InitializeRecipientBundles(recipientBundleStore);
        InitializePerformanceFeatures();
        InitializePerformanceTransfer(performanceTransferStore);
        InitializeInstallationFeatures(installationDiscovery,executableTrust);
        InitializeDiagnosticsFeatures();
        InitializeClientWindowFeatures();
        InitializeClientPriorityFeatures(clientPriority);
        InitializeMacroHostCatalog(macroCatalogClient);
        InitializeMacroAssignments(macroAssignmentStore);
        Navigate = new(p => { if (!IsLocked && p is string tab && new[] { "Overview", "Accounts", "Sessions", "JoinGame", "Windows", "Automation", "Diagnostics", "Settings", "Guide" }.Contains(tab)) Tab = tab; });
        OpenFolder = new(_ => { try { Process.Start(new ProcessStartInfo { FileName = DataRoot, UseShellExecute = true }); } catch(System.ComponentModel.Win32Exception ex) { Fail(ex); } });
        Panic = new(_ => StopOperations("Panic stop: automation paused. Roblox clients remain open."));
        Lock = new(_ => LockDashboard()); Unlock = new(_ => { IsLocked = false; ReloadFavoritesAfterUnlock(); Notice = "Dashboard unlocked. Automation remains stopped until explicitly started."; });
        SaveSettings = new(_ => SavePreferencesAsync(), Fail);
        ApplyTheme = new(async p => { if (_ready && p is string id) { _settings = _settings with { Theme = ThemeCatalog.Resolve(id).Id }; ThemeChanged?.Invoke(ThemeCatalog.Resolve(id)); Raise(nameof(ThemeName)); await SaveCurrentSettingsAsync(); } }, Fail);
        NextPage = new(async _ => { if ((_page + 1L) * 100 < _filteredTotal) { _page++; await LoadAccountsAsync(_lifetime.Token); } }, Fail);
        PreviousPage = new(async _ => { if (_page > 0) { _page--; await LoadAccountsAsync(_lifetime.Token); } }, Fail);
        FocusSession = new(async p => { if (!IsLocked && p is SessionRow session) Notice = await _monitor.FocusAsync(session.Observation, _lifetime.Token) ? "Roblox window activated." : "Window changed or Windows denied activation. Select its taskbar button to bring it forward."; }, Fail);
        MinimizeSession=new(p=>ChangeWindowAsync(p,ClientWindowAction.Minimize),Fail);
        RestoreSession=new(p=>ChangeWindowAsync(p,ClientWindowAction.Restore),Fail);
        ToggleFavorite=new(async _=> { if(!IsLocked && SelectedAccount is { } row) await SaveAccountAsync(row.Profile with { IsFavorite=!row.Profile.IsFavorite }); },Fail);
        QueueSelected=new(async _=>
        {
            if(IsLocked || !QueueReady || SelectedAccount is not { } selected) return;
            var binding=selected.KnownUserId is >0 && selected.AuthorizationGeneration is >0?new LaunchAccountBinding(selected.KnownUserId.Value,selected.AuthorizationGeneration.Value):null;
            var operation=await _launchQueue.EnqueueAsync(selected.Profile,_lifetime.Token,binding);
            Notice=operation.State==LaunchState.Blocked?LaunchDescriptions.Explain(operation.Problem):"Launch request queued. It is not a running client.";
            QueueExpanded=true;
        },Fail);
        ToggleQueuePause=new(_=>ToggleQueuePausePresentationAsync(),Fail);
        CancelLaunch=new(async p=> { if(!IsLocked && QueueReady && p is LaunchRow row) await _launchQueue.CancelAsync(row.Id,_lifetime.Token); },Fail);
        Refresh = new(async _ => { if(!_ready) throw new AccountLinkException("Resolve the startup error and restart Zenith Sessions before refreshing the library."); await LoadAccountsAsync(_lifetime.Token); await SampleAsync(_lifetime.Token,forcePresentation:true); Notice = "Latest available observations shown. Measurement times are listed."; }, Fail);
        LinkAccount=new(_=>AuthorizeSelectedAsync(false),Fail);
        AddRobloxAccount=new(_=>AddRobloxAccountAsync(),Fail);
        RefreshAuthorization=new(_=>AuthorizeSelectedAsync(true),Fail);
        DisconnectAccount=new(_=>DisconnectSelectedAsync(),Fail);
        CancelAuthorization=new(_=>_authorizationStop?.Cancel());
        InitializeAccountHealthFeatures();
        CleanupCredentials=new(async _=> { if(!_ready) throw new AccountLinkException("Resolve the library startup error before retrying credential cleanup."); await CleanupCredentialsAsync(); },Fail);
        InitializeHandoffProbe(handoffProbe);
        InitializeNativeTrialFeatures();
        InitializeSettingsSearch();
    }

    public Task InitializeAsync()
    { if(!_initializationStarted) { _initializationStarted=true; _initializeTask=InitializeCoreAsync(); } return _initializeTask; }
    private async Task InitializeCoreAsync()
    {
        try
        {
            var loaded = await Task.Run(() => _settingsStore.LoadAsync(_lifetime.Token)); _settings = _savedSettings = loaded.Settings;
            _settingsNeedRepair=loaded.Notice is not null;
            var limits = _settings.Capacity!;
            SpareRam = limits.SparePhysicalGiB.ToString(CultureInfo.CurrentCulture); PhysicalPerClient = limits.NewClientPhysicalGiB.ToString(CultureInfo.CurrentCulture);
            CommitPerClient = limits.NewClientCommitGiB.ToString(CultureInfo.CurrentCulture); MaximumClients = limits.UserMaximum?.ToString(CultureInfo.CurrentCulture) ?? ""; OAuthClientId = _settings.OAuthClientId;
            CallbackPort=_settings.OAuthCallbackPort.ToString(CultureInfo.InvariantCulture);
            LoadPerformanceDraft(_settings.Performance!);ApplyPerformance(_settings.Performance!);
            LoadClientLayoutDraft(_settings.WindowLayout!);
            foreach (string field in new[] { nameof(SpareRam), nameof(PhysicalPerClient), nameof(CommitPerClient), nameof(MaximumClients), nameof(OAuthClientId), nameof(CallbackPort),nameof(CallbackUrl), nameof(Dense), nameof(ThemeName) }) Raise(field);
            ThemeChanged?.Invoke(ThemeCatalog.Resolve(_settings.Theme));
            await Task.Run(() => _accounts.InitializeAsync(_lifetime.Token));
            await CleanupCredentialsAsync();
            string? projectionNotice=null;
            try
            {
                var projection=await Task.Run(()=>_identityProjection.ReplayAsync(_lifetime.Token));
                if(projection.Pending>0) projectionNotice="Linked account names need reconciliation. Pending metadata journals were retained; account authorization is checked separately.";
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException || ex is OperationCanceledException && !_lifetime.IsCancellationRequested)
            {
                projectionNotice="Linked-name recovery needs attention. Existing metadata journals were preserved; your account library is still available.";
                _diagnostics.Record(ManagerEventKind.OperationFailed,ex.GetType());
            }
            if(projectionNotice is not null) AddEvent(projectionNotice);
            string? queueRecoveryNotice=null;
            try
            {
                await Task.Run(()=>_launchQueue.InitializeAsync(_lifetime.Token));QueueReady=true;
                queueRecoveryNotice=_queueStore.RecoveryNotice;
                if(queueRecoveryNotice is not null)AddEvent(queueRecoveryNotice);
            }
            catch(Exception ex) when(ex is not OperationCanceledException) {QueueStatus="Launch queue unavailable. Existing queue files were preserved; account profiles remain usable.";_diagnostics.Record(ManagerEventKind.OperationFailed,ex.GetType());}
            if (IsSample)
            {
                var initial = await Task.Run(() => _accounts.ReadAsync(new(), _lifetime.Token));
                if (initial.Total == 0) for (int i = 1; i <= 16; i++)
                    await Task.Run(() => _accounts.SaveAsync(AccountProfile.Create($"Sample account {i:00}") with { Group = i <= 8 ? "Card Clash" : "Bubble Gum", PreferredPlaceId = i <= 8 ? 110829983956014 : 2512643572 }, _lifetime.Token));
            }
            await RecentClients.InitializeAsync();
            await LoadAccountsAsync(_lifetime.Token); _ready = true; RefreshOverview(); Raise(nameof(IsReady)); Raise(nameof(EmptyAccountsText)); Raise(nameof(CanRefreshInstallations)); NotifyPerformanceTransfer(); GameDetails.RefreshAvailability(); RecentClients.RefreshAvailability();RefreshClientPreviewContext();MacroHostCatalog.RefreshAvailability();MacroAssignments.RefreshAvailability();RefreshPublicImageActivity();
            PreferencesEdited(); Notice = loaded.Notice ?? queueRecoveryNotice ?? projectionNotice ?? "Ready. Account metadata stays local; running processes require verified identity before automation.";
            AddEvent(IsSample ? "Isolated sample profile library opened; no sample session is shown as a live client." : "Local account library opened.");
            _diagnostics.Record(ManagerEventKind.Started);
            // Start the owned diagnostic worker only after initialization succeeds. Isolated
            // sample/injected-monitor tests never inspect unrelated desktop logs.
            if (!IsSample && _monitor is WindowsDesktopMonitor) _logCorrelator = new();
            _monitorTask = MonitorLoopAsync(_lifetime.Token);
            await DestinationFavorites.LoadAsync(_lifetime.Token);
            await RecipientBundles.LoadAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _ready=false; _libraryUnavailable=true; RefreshOverview(); Raise(nameof(IsReady)); Raise(nameof(EmptyAccountsText)); SaveStatus="Startup needs attention"; Fail(ex); }
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
        catch (Exception ex) { Fail(ex); }
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
            catch(Exception ex) when(ex is InvalidDataException or IOException or UnauthorizedAccessException)
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
                    catch(Exception itemError) when(itemError is InvalidDataException or IOException or UnauthorizedAccessException) { unavailable.Add(ids[i]); }
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
        Accounts.Clear(); foreach (var row in rows)
        {
            // Attach cached images only at final UI publication, after any Lock/Panic clear.
            if(!IsLocked && row.KnownUserId is { } userId && _publicPresentation.TryGetCached(userId,out var presentation) && presentation is not null && _publicPresentation.IsCurrent(presentation))
                row.SetPublicPresentation(presentation);
            Accounts.Add(row);
        }
        RefreshAccountLaunchStates();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Profile.Id == selected);
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
        catch (Exception ex) { Fail(ex); }
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
        if(!result.Applied) _diagnostics.Record(ManagerEventKind.OperationFailed);
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
            _diagnostics.Record(ManagerEventKind.OperationFailed,ex.GetType());
            return;
        }
        Notice = "Profile saved. Entered usernames are metadata until official account linking verifies them."; AddEvent("Profile saved.");
    }
    public Task DeleteAccountsAsync(IReadOnlyList<AccountRow> rows)=>OwnProfileOperation(()=>DeleteAccountsCoreAsync(rows));
    private async Task DeleteAccountsCoreAsync(IReadOnlyList<AccountRow> rows)
    {
        string queueStopped=StopQueueForSavedStateRemoval("Trial stopped because a profile was removed. Review any process-start receipt before retrying; existing Roblox clients remain open.");
        if(QueueReady)
        {
            foreach(var row in rows)await _launchQueue.InvalidateAuthorizationAsync(row.Profile.Id,_lifetime.Token);
        }
        await _launchQueue.WaitForActiveCleanupAsync(_lifetime.Token);
        if(_linkingProfile is { } linking && rows.Any(r=>r.Profile.Id==linking))
        { _authorizationStop?.Cancel(); await Task.WhenAll(AddRobloxAccount.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,InspectNativeHandoff.Execution); }
        await Task.Run(() => _accounts.DeleteManyAsync(rows.Select(r => r.Profile).ToArray(), _lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token); await CleanupCredentialsAsync();
        Notice = $"Removed {rows.Count} profile(s). Roblox clients were left open. {CredentialCleanupStatus}{queueStopped}"; AddEvent("Selected profiles removed.");
        _diagnostics.Record(ManagerEventKind.ProfilesRemoved);
    }
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
                    writer.WriteStartObject(); writer.WriteNumber("Version",3); writer.WritePropertyName("Profiles"); writer.WriteStartArray();
                    int exported=0;
                    for(int page=0;;page++)
                    {
                        var batch=await _accounts.ReadAsync(new(Page:page),_lifetime.Token);
                        if(batch.Total>100000) throw new InvalidDataException("Export at most 100,000 profiles per operation.");
                        foreach(var profile in batch.Items) { _lifetime.Token.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer,profile); }
                        await writer.FlushAsync(_lifetime.Token);
                        if(file.Length>16*1024*1024-64) throw new InvalidDataException("The export exceeds 16 MiB. Shorten notes or export a smaller library. The existing destination was preserved.");
                        exported+=batch.Items.Count; if(exported>=batch.Total) break;
                    }
                    writer.WriteEndArray();writer.WriteEndObject();await writer.FlushAsync(_lifetime.Token);await file.FlushAsync(_lifetime.Token);file.Flush(true);
                }
                _lifetime.Token.ThrowIfCancellationRequested(); File.Move(temporary,path,true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        Notice = "Profiles and group memberships exported. Saved empty groups, colors, group order, authorization and credentials are excluded.";
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
            return ProfileMetadataImport.Decode(bytes, _lifetime.Token);
        });
        var validated = imported.Select(profile => profile with { Id = Guid.NewGuid(), Revision = 0, RobloxUserId = null }).ToArray();
        await Task.Run(() => _accounts.CreateManyAsync(validated, _lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token); Notice = $"Imported {validated.Length} profiles. Sign-in is required separately."; AddEvent("Profile metadata imported.");
        _diagnostics.Record(ManagerEventKind.ProfilesImported);
    }
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await SampleAsync(ct);
                // One owned loop, no catch-up bursts. Essential native measurements stay 1 Hz.
                await Task.Delay(Math.Min(1000,Volatile.Read(ref _activePerformance).ProcessSampleMs),ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { await _dispatcher.InvokeAsync(() => {MarkOverviewMonitorStopped();Fail(ex);}); }
    }
    private async Task SampleAsync(CancellationToken ct,bool forcePresentation=false)
    {
        await _sampleSerial.WaitAsync(ct);
        try
        {
        var snapshot = await _monitor.SampleAsync(ct);
        // One bounded, pure reducer on the existing sample lane; no row-owned task/timer.
        await Task.Run(()=>_telemetryHistory.Observe(snapshot,DateTimeOffset.UtcNow),ct);
        _logCorrelator?.Offer(snapshot);
        var memory = snapshot.Resources;var now=DateTimeOffset.UtcNow;
        var limits=Volatile.Read(ref _savedSettings).Capacity!;
        var projection=LaunchReservationProjection.Evaluate(Volatile.Read(ref _latestQueue),snapshot,limits,now);
        var capacity=CapacityPolicy.Evaluate(memory,limits,projection.Reservations,projection.OccupiedForAdmission,now);
        bool previousHold=_lastPressure.Held;
        // Cached half-second calls must not reset a one-second safety sample's recovery history.
        var systemHealth=CapacityPolicy.EvaluateSystemPressure(memory,limits,now);
        if(_pressureObserved!=memory || !systemHealth.SamplesHealthy)
        {_lastPressure=_pressure.Observe(memory,systemHealth,now);_pressureObserved=memory;}
        var pressure=_lastPressure;
        // Cache pressure/sample health only. Each start recomputes remaining reservations,
        // excluding its own not-yet-emitted forecast, at the final native boundary.
        // Publish the sample and its health together. Readers must never combine a new
        // critical-memory/CPU/GPU observation with the previous sample's healthy decision.
        Volatile.Write(ref _resourceAdmission,new ResourceAdmissionSnapshot(snapshot,new(systemHealth.SamplesHealthy && !pressure.Held,pressure.Explanation)));
        Volatile.Write(ref _latestDesktop,snapshot);
        ObserveClientPreviewContext(snapshot);
        _launchQueue.Signal();
        if(!PresentationDue(Volatile.Read(ref _activePerformance),forcePresentation || previousHold!=pressure.Held)) return;
        await _dispatcher.InvokeAsync(() =>
        {
            Physical = memory.IsValid ? $"{memory.AvailablePhysical / (double)CapacityPolicy.GiB:0.0} / {memory.TotalPhysical / (double)CapacityPolicy.GiB:0.0} GiB free" : "Unavailable";
            Commit = memory.IsValid ? $"{memory.CommitUsed / (double)CapacityPolicy.GiB:0.0} / {memory.CommitLimit / (double)CapacityPolicy.GiB:0.0} GiB used" : "Unavailable";
            Cpu = memory.CpuPercent is { } percent ? $"{percent:0}%" : "Warming up";
            Capacity=(projection.ForecastsKnown?capacity.Explanation+" ":"")+projection.Explanation;
            RefreshNativeTrialFeatures();
            PressureNotice=pressure.Explanation;RecordOverviewSample(snapshot,capacity,projection);
            DiagnosticsStatus=_diagnostics.Failure??(_diagnostics.DroppedEvents>0?$"Diagnostics busy: {_diagnostics.DroppedEvents} routine events skipped.":"Diagnostics active · bounded to three log files");
            RefreshDiagnosticsView();
            RefreshDestinationCapacity();
            foreach(var account in Accounts) account.UpdateAuthorizationClock(DateTimeOffset.UtcNow);
            SessionCount = snapshot.Sessions.Count; SampleTime = $"Updated {memory.ObservedUtc.LocalDateTime:T}";
            ProcessInventoryStatus=snapshot.Problem is null && snapshot.ProcessInventoryComplete && snapshot.ProcessObservedUtc is { } at && at<=now.AddSeconds(1) && now-at<=RecentClientHistoryReducer.MaximumInventoryAge
                ? $"Process inventory sampled {at.LocalDateTime:T} · complete scan. Account identity remains unverified."
                : "Process scan incomplete, stale or unavailable. Displayed rows may be last-known; missing clients are not confirmed exits.";
            // Replace only observations whose measurements changed; profile views are not rebuilt by telemetry.
            bool clientRowsChanged=false;
            for (int i = Sessions.Count - 1; i >= 0; i--) if (!snapshot.Sessions.Any(s => s.InstanceKey == Sessions[i].InstanceKey))
            { Sessions[i].PropertyChanged-=SessionSelectionChanged; Sessions.RemoveAt(i);clientRowsChanged=true; }
            foreach (var session in snapshot.Sessions)
            {
                int index = -1; for (int i = 0; i < Sessions.Count; i++) if (Sessions[i].InstanceKey == session.InstanceKey) { index = i; break; }
                if (index < 0) { var row=new SessionRow(session);row.PropertyChanged+=SessionSelectionChanged;Sessions.Add(row);clientRowsChanged=true; } else Sessions[index].Update(session);
            }
            _logProjection.Update(snapshot.Sessions, _logCorrelator?.Snapshot, now);
            RefreshLaunchEvidence();
            RecentClients.Observe(snapshot,_logProjection.EvidenceSnapshot(now),now);
            var logViews = _logProjection.Snapshot(now);
            foreach (var row in Sessions)
                row.UpdateLog(logViews.TryGetValue(new(null, row.Observation.ProcessId, row.Observation.ProcessStartUtcTicks), out var log)
                    ? log : SessionLogView.Unknown);
            if (IsJoinGame) RefreshRecentlyPlayed();
            if (SelectedSession is not null && !Sessions.Contains(SelectedSession)) SelectedSession = null;
            if(clientRowsChanged) NotifyClientSelection();
            RefreshTelemetryPresentation();
        }, DispatcherPriority.Background, ct);
        }
        finally {_sampleSerial.Release();}
    }
    private async Task SavePreferencesAsync()
    {
        if(!_ready) throw new AccountLinkException("Local services have not opened successfully. Resolve the startup error before changing preferences.");
        var culture = CultureInfo.CurrentCulture;
        if (!double.TryParse(SpareRam, NumberStyles.Float, culture, out double spare) || !double.TryParse(PhysicalPerClient, NumberStyles.Float, culture, out double physical)
            || !double.TryParse(CommitPerClient, NumberStyles.Float, culture, out double commit)) throw new ArgumentException("Enter valid memory amounts in GiB.");
        int? maximum = null;
        if (!string.IsNullOrWhiteSpace(MaximumClients)) { if (!int.TryParse(MaximumClients, out int value) || value < 1) throw new ArgumentException("The optional client limit must be a positive whole number."); maximum = value; }
        var limits = (_savedSettings.Capacity! with { SparePhysicalGiB = spare, NewClientPhysicalGiB = physical, NewClientCommitGiB = commit, UserMaximum = maximum }).Validated();
        var performance=ReadPerformanceDraft();
        // Pin this explicit request before the asynchronous profile check. Failed requests
        // must never become the source for an unrelated appearance-only save.
        var requested=(_savedSettings with { Capacity=limits,Performance=performance,WindowLayout=ReadClientLayoutDraft() }).Validated();
        if(performance.MainAccountProfileId is { } main && await Task.Run(()=>_accounts.FindAsync(main,_lifetime.Token)) is null)
            throw new ArgumentException("The chosen main account no longer exists. Choose another saved profile or clear the selection before saving.");
        await SaveCurrentSettingsAsync(requested);
    }
    private void PreferencesEdited()
    {
        if (!_ready) return;
        var limits = _savedSettings.Capacity!;
        bool unchanged = double.TryParse(SpareRam, out double spare) && spare == limits.SparePhysicalGiB
            && double.TryParse(PhysicalPerClient, out double physical) && physical == limits.NewClientPhysicalGiB
            && double.TryParse(CommitPerClient, out double commit) && commit == limits.NewClientCommitGiB
            && MaximumClients.Trim() == (limits.UserMaximum?.ToString(CultureInfo.CurrentCulture) ?? "")
            && _settings.DenseAccounts == _savedSettings.DenseAccounts && _settings.Theme == _savedSettings.Theme
            && TryReadPerformanceDraft(out var performance) && performance==_savedSettings.Performance
            && TryReadClientLayoutDraft(out var windows) && windows==_savedSettings.WindowLayout;
        Raise(nameof(CallbackUrl));
        SaveStatus = _pendingSaves > 0 ? "Saving…" : _saveFailed ? "Save failed — retry" : !unchanged?"Unsaved settings":_settingsNeedRepair?"Recovered settings — save to repair":"Settings saved";
    }
    private async Task SaveCurrentSettingsAsync(SessionsSettings? requested=null)
    {
        string theme=_settings.Theme;bool dense=_settings.DenseAccounts;
        _pendingSaves++; PreferencesEdited();
        try
        {
            await _preferencesWriter.WaitAsync(_lifetime.Token);
            try
            {
                // Appearance autosaves merge with the latest successful explicit save,
                // even when they waited behind it. Never persist uncommitted field drafts.
                var snapshot=(requested??_savedSettings) with { Theme=theme,DenseAccounts=dense };
                await Task.Run(() => _settingsStore.SaveAsync(snapshot, _lifetime.Token));
                ApplyPerformance(snapshot.Performance!);
                _savedSettings = snapshot; _saveFailed = false; _settingsNeedRepair=false;
                _settings=snapshot with { Theme=_settings.Theme,DenseAccounts=_settings.DenseAccounts };
                _diagnostics.Record(ManagerEventKind.SettingsSaved);
                Notice = "Preferences saved to your writable settings folder.";
            }
            finally { _preferencesWriter.Release(); }
        }
        catch { _saveFailed = true; throw; }
        finally { _pendingSaves--; PreferencesEdited(); }
    }
    public async Task RequestCloseAsync(SessionObservation session)
    {
        var requestedAt=DateTimeOffset.UtcNow;
        bool requested=await _monitor.RequestCloseAsync(session,_lifetime.Token);
        if(requested) RecentClients.RecordCloseRequest(new(null,session.ProcessId,session.ProcessStartUtcTicks),requestedAt);
        Notice=requested?"Normal close requested. The request does not prove that the process exited.":"Close was not sent because the window changed or Windows rejected the request.";
    }
    public void LockDashboard() { StopOperations("Controls locked. Monitoring continues; automation requires explicit restart.",ManagerEventKind.ControlsLocked);ClearSettingsSearch.Execute(null); IsLocked = true; }
    private void StopOperations(string reason,ManagerEventKind kind=ManagerEventKind.Panic) { HoldQueuePresentation();MacroAssignments.CancelWork(); MacroHostCatalog.CancelWork(); Interlocked.Increment(ref _accountSetupEpoch);_preserveStopNotice=true;FenceNativeTrial("Trial stopped by Panic or Lock. Review possible clients before retrying.");CancelOverviewFocus();ClientPreviews.Fence("Previews stopped by Panic or Lock. Start explicitly to resume."); _authorizationStop?.Cancel();HandoffInspectionStatus="Inspection stopped by Panic or Lock. No native dispatch was permitted; review sign-in before retrying."; _launchQueue.SignalPanic();StopClientWindowOperations();StopClientPriorityOperations();StopGroupChanges();CancelPerformanceTransfers();CancelInstallationWork();CancelDirectoryFeatures(kind==ManagerEventKind.ControlsLocked);_queuePaused=true;Raise(nameof(QueuePauseLabel));QueueStatus="Queue paused. Pending or uncertain requests require review before retrying.";RefreshOverview();PanicRequested?.Invoke(); Notice = reason; AddEvent(reason); _diagnostics.Record(kind); }
    private void AddEvent(string text) { Events.Insert(0, $"{DateTime.Now:T}  {text}"); while (Events.Count > 100) Events.RemoveAt(Events.Count - 1); }
    public void Fail(Exception exception)
    {
        Notice = exception is JsonException?"The selected file is not a valid profile export. Existing profiles were preserved."
            :exception is ArgumentException or InvalidDataException or RevisionConflictException or AccountLinkException or RobloxSessionException or LaunchQueueException ? exception.Message
            : "The operation could not finish. Your existing data was preserved. Check the data folder permissions and retry.";
        AddEvent($"Operation failed ({exception.GetType().Name}).");
        _diagnostics.Record(ManagerEventKind.OperationFailed,exception.GetType());
    }
    public void Inform(string message) => Notice = message;
    public void SetPanicRegistration(bool registered) { _panicRegistered=registered && !IsSample; Raise(nameof(PanicLabel)); Raise(nameof(PanicShortcutStatus)); }
    public ValueTask DisposeAsync()=>new(_disposeTask??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _shuttingDown=true;
        StopClientPriorityOperations();
        DestinationSearch?.CancelWork(clearInput:true);
        MacroAssignments.CancelWork();
        MacroHostCatalog.CancelWork();
        DisposeSettingsSearch();
        RefreshClientPreviewContext();
        CancelPerformanceTransfers();
        CancelInstallationWork();
        RecipientBundles.CancelWork();
        _launchQueue.SignalPanic();
        _lifetime.Cancel(); _queryCancellation?.Cancel(); _authorizationStop?.Cancel();
        await DisposeClientPriorityFeaturesAsync();
        await _initializeTask;
        await DisposeMacroAssignmentsAsync();
        await MacroHostCatalog.DisposeAsync();
        try{await DisposeHandoffProbeAsync();}catch(Exception ex){Fail(ex);}
        await AddRobloxAccount.Execution;
        await Task.WhenAll(ImportPerformanceSettings.Execution, ExportPerformanceSettings.Execution);
        await Task.WhenAll(RefreshInstallations.Execution, VerifyInstallationPublisher.Execution);
        await _executableTrust.DisposeAsync();
        try { await Task.WhenAll(_queryTask, _monitorTask, SaveSettings.Execution, ApplyTheme.Execution,
            NextPage.Execution, PreviousPage.Execution, Refresh.Execution, FocusSession.Execution,FocusOverviewClient.Execution,MinimizeSession.Execution,RestoreSession.Execution,ToggleFavorite.Execution,QueueSelected.Execution,ToggleQueuePause.Execution,CancelLaunch.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,DisconnectAccount.Execution,CleanupCredentials.Execution,ExportDiagnostics.Execution); } catch (OperationCanceledException) { } catch(Exception ex) { Fail(ex); }
        try { await Task.WhenAll(_profileOperations.ToArray()); } catch(OperationCanceledException) { } catch(Exception ex) { Fail(ex); }
        await DisposeRecipientBundlesAsync();
        await StopDirectoryFeaturesAsync();
        await RecentClients.DisposeAsync();
        try {await ClientPreviews.DisposeAsync();} catch(Exception ex) {Fail(ex);}
        await DisposeClientWindowFeaturesAsync();
        await _favoritesReloadTask;
        await DestinationFavorites.DisposeAsync();
        await Task.WhenAll(DestinationFavorites.Refresh.Execution,DestinationFavorites.SaveCurrent.Execution,DestinationFavorites.Rename.Execution,DestinationFavorites.Delete.Execution);
        (_favoriteStore as IDisposable)?.Dispose();
        if (_logCorrelator is not null)
            try { await _logCorrelator.DisposeAsync(); }
            catch (Exception ex) { Fail(ex); } // Optional diagnostics must not prevent vault/queue/native owner cleanup.
        await _launchQueue.DisposeAsync();_launchQueue.Changed-=QueueChanged;_launchQueue.Faulted-=QueueFaulted;_queueStore.Dispose();
        await DisposeNativeTrialFeaturesAsync();
        _queryCancellation?.Dispose(); _preferencesWriter.Dispose(); _profileSerial.Dispose(); _credentialCleanup.Dispose(); await _linkService.DisposeAsync();
        if(_signIn is IAsyncDisposable signInOwner) await signInOwner.DisposeAsync();
        _identityProjection.Dispose(); _vault.Dispose(); (_sessionClient as IDisposable)?.Dispose();
        (_accounts as IDisposable)?.Dispose(); (_monitor as IDisposable)?.Dispose(); _settingsStore.Dispose();_sampleSerial.Dispose();
        _diagnostics.Record(ManagerEventKind.Shutdown); await _diagnostics.DisposeAsync(); _lifetime.Dispose();
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
            _diagnostics.Record(ManagerEventKind.AuthorizationLinked);
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested)
        { if(!_lifetime.IsCancellationRequested) { await LoadAccountsAsync(_lifetime.Token); if(!_preserveStopNotice) Notice="Account sign-in cancelled. Existing profiles were preserved."; } }
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
        _preserveStopNotice=false;
        string queueStopped=StopQueueForSavedStateRemoval("Trial stopped because a saved sign-in was removed. Review any process-start receipt before retrying; existing Roblox clients remain open.");
        if(QueueReady) await _launchQueue.InvalidateAuthorizationAsync(selected.Profile.Id,_lifetime.Token);
        await _launchQueue.WaitForActiveCleanupAsync(_lifetime.Token);
        _authorizationStop?.Cancel(); await Task.WhenAll(AddRobloxAccount.Execution,LinkAccount.Execution,RefreshAuthorization.Execution,CheckCheckedSignIns.Execution,InspectNativeHandoff.Execution);
        await Task.Run(()=>_linkService.DisconnectAsync(selected.Profile.Id,_lifetime.Token));
        await LoadAccountsAsync(_lifetime.Token);
        if(!_preserveStopNotice && !IsLocked && !_shuttingDown) Notice="Saved sign-in removed from Zenith. The account profile and running clients were preserved. This does not sign out Roblox on other devices."+queueStopped;
        _diagnostics.Record(ManagerEventKind.AuthorizationDisconnected);
    }
    private async Task CleanupCredentialsAsync()
    {
        var result=await Task.Run(()=>_credentialCleanup.RunAsync(_lifetime.Token));
        CredentialCleanupStatus=result.Remaining==0?"No pending credential cleanup."
            :result.ErrorType==nameof(InvalidDataException)?$"{result.Remaining} unused protected record(s) await cleanup. A saved sign-in file from a removed profile is damaged, so Zenith left it in place instead of guessing what to delete."
            :$"{result.Remaining} unused protected record(s) await cleanup. Check storage access and choose Retry credential cleanup.";
        if(result.ErrorType is not null) { AddEvent("Unused protected-file cleanup needs attention; the durable cleanup journal was retained."); _diagnostics.Record(ManagerEventKind.OperationFailed); }
    }
    private LaunchAdmission ReadLaunchAdmission(IReadOnlyList<LaunchOperation> operations)
        => ReadLaunchAdmissionForBudget(operations,LaunchResourceBudget.FromSettings(Volatile.Read(ref _savedSettings).Capacity!));
    private LaunchResourceBudget ReadNewLaunchBudget(LaunchIntent _)
        => LaunchResourceBudget.FromSettings(Volatile.Read(ref _savedSettings).Capacity!);
    private LaunchAdmission ReadCandidateLaunchAdmission(LaunchOperation candidate,IReadOnlyList<LaunchOperation> operations)
        => candidate.ResourceBudget is { } budget?ReadLaunchAdmissionForBudget(operations,budget)
            :new(false,"The request has no recorded memory budget. Review it before starting.");
    private LaunchAdmission ReadLaunchAdmissionForBudget(IReadOnlyList<LaunchOperation> operations,LaunchResourceBudget budget)
    {
        var observed=Volatile.Read(ref _resourceAdmission);
        if(observed is null || IsLocked || _shuttingDown) return new(false,"Monitoring is not ready or controls are locked.");
        var snapshot=observed.Desktop;var admission=observed.Admission;
        var limits=Volatile.Read(ref _savedSettings).Capacity!;var now=DateTimeOffset.UtcNow;
        var projection=LaunchReservationProjection.Evaluate(new(true,operations),snapshot,limits,now);
        var decision=CapacityPolicy.Evaluate(snapshot.Resources,limits,projection.Reservations,projection.OccupiedForAdmission,now,budget);
        return new(admission.Allowed && projection.ForecastsKnown && projection.FreshProcessInventory && decision.SamplesHealthy && decision.MemoryOnlySlots>0,
            !projection.ForecastsKnown||!projection.FreshProcessInventory?projection.Explanation:!decision.SamplesHealthy||decision.MemoryOnlySlots==0?decision.Explanation:admission.Explanation);
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
        var oldGenerations=previous.Operations.ToDictionary(row=>row.Intent.OperationId,row=>row.Intent.AuthorizationGeneration);
        if(snapshot.Operations.Any(row=>oldGenerations.TryGetValue(row.Intent.OperationId,out long prior)&&row.Intent.AuthorizationGeneration!=prior))
            Interlocked.Exchange(ref _nativeMetadataPending,1);
        Volatile.Write(ref _pendingQueue,snapshot);
        if(Interlocked.Exchange(ref _queueUiScheduled,1)!=0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            Interlocked.Exchange(ref _queueUiScheduled,0);
            var latest=Interlocked.Exchange(ref _pendingQueue,null);if(latest is null || _shuttingDown) return;
            _queuePaused=latest.Paused;Raise(nameof(QueuePauseLabel));
            var ids=latest.Operations.Select(o=>o.Intent.OperationId).ToHashSet();
            for(int i=Launches.Count-1;i>=0;i--) if(!ids.Contains(Launches[i].Id)) Launches.RemoveAt(i);
            var rows=Launches.ToDictionary(r=>r.Id);
            foreach(var operation in latest.Operations)
            {
                if(rows.TryGetValue(operation.Intent.OperationId,out var row)) row.Update(operation);
                else Launches.Add(new(operation) { Label=Accounts.FirstOrDefault(a=>a.Profile.Id==operation.Intent.ProfileId)?.Label??"Saved profile · "+operation.Intent.ProfileId.ToString("N")[..6] });
            }
            RecordOverviewQueue(latest);
            RefreshVisibleQueueStatus();
            RefreshAccountLaunchStates();
            RefreshNativeTrialFeatures();
            RefreshLaunchEvidence();
            if(Volatile.Read(ref _pendingQueue) is { } pending) QueueChanged(pending);
        }));
    }
    private void QueueFaulted(Exception error)
    {
        _diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());
        if(!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(()=>
        {
            if(_shuttingDown) return;
            QueueReady=_launchQueue.IsHealthy;
            if(!QueueReady) QueueStatus="Queue stopped after an error. Existing queue files were preserved; restart after resolving storage access.";
            RefreshOverview();
            Notice="A launch operation needs attention. Check the queue; no uncertain result is reported as running.";
        }));
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
