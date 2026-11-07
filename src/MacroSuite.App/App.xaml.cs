using System.IO;
using System.Windows;
using MacroSuite.App.Overlays;
using MacroSuite.App.Themes;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.Infrastructure.Capture;
using MacroSuite.Infrastructure.Diagnostics;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Licensing;
using MacroSuite.Infrastructure.Security;
using MacroSuite.Infrastructure.SQLite;
using MacroSuite.Infrastructure.Storage;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App;

/// <summary>
/// Application composition root configuring dependencies and launching the main shell.
/// </summary>
public partial class App : Application
{
    private DpapiSecretVault? _secretVault;
    private ProviderHttpClientFactory? _clientFactory;
    private LocalLicenseService? _licenseService;
    private FeatureGate? _featureGate;
    private SqliteUsageRepository? _usageRepo;
    private UsageDashboardViewModel? _usageDashboardVM;
    private UsageTracker? _usageTracker;
    private AsyncAuditLogger? _auditLogger;
    private FallbackCaptureCoordinator? _captureCoordinator;
    private SharedCaptureService? _captureService;
    private InputBackendResolution? _inputBackendResolution;
    private IInputPlayer? _inputPlayer;
    private LowLevelHookService? _inputRecorder;
    private OneShotLocationPicker? _locationPicker;
    private HostInputOperationArbiter? _inputOperations;
    private AIVisionRouter? _visionRouter;
    private ApprovalGate? _approvalGate;
    private MacroSuite.App.ViewModels.AutoClickerViewModel? _autoClickerVM;
    private MacroEditorViewModel? _editorVM;
    private LiveWatchOverlay? _detectionOverlay;
    private SettingsViewModel? _settingsVM;
#if DEVELOPER_MODE
    private Zenith.MacroHost.Windows.MacroCatalogPipeServer? _macroCatalogServer;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Initialize structured crash reporter with app diagnostic paths (Logs/crashes, Diagnostics/...)
        CrashReporter.Initialize(AppDataPathResolver.Default.RootDirectory, () => new CrashReportContext
        {
            Version = MacroSuite.App.Common.BuildEnvironment.Version,
            IsRecording = _editorVM?.IsRecording ?? false,
            IsPlaying = _editorVM?.IsRunning ?? false,
            SelectedMacro = _editorVM?.SelectedScript?.Name,
            EventsCaptured = _editorVM?.EventsCapturedCount ?? 0,
            QueueLength = _editorVM?.QueueLength ?? 0,
            RuntimeVersion = Environment.Version.ToString()
        });

        // Global unhandled exception handlers to log diagnostics and prevent silent drops (Rule 18)
        DispatcherUnhandledException += (s, args) =>
        {
            CrashReporter.RecordCrash(args.Exception, "WPF Dispatcher Unhandled Exception");

            _auditLogger?.Enqueue(new MacroSuite.Core.Audit.AuditEntry
            {
                ProviderId = "SYSTEM",
                ModelId = "DispatcherUnhandledException",
                Prompt = args.Exception.Message,
                Error = args.Exception.ToString()
            });
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashReporter.RecordCrash(ex, "AppDomain Unhandled Exception");

                _auditLogger?.Enqueue(new MacroSuite.Core.Audit.AuditEntry
                {
                    ProviderId = "SYSTEM",
                    ModelId = "UnhandledException",
                    Prompt = ex.Message,
                    Error = ex.ToString()
                });
            }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            CrashReporter.RecordCrash(args.Exception, "TaskScheduler Unobserved Task Exception");

            _auditLogger?.Enqueue(new MacroSuite.Core.Audit.AuditEntry
            {
                ProviderId = "SYSTEM",
                ModelId = "UnobservedTaskException",
                Prompt = args.Exception.Message,
                Error = args.Exception.ToString()
            });
            args.SetObserved();
        };

        try
        {
            // 1. Initialize %APPDATA%\MacroSuite\ directory hierarchy
            AppDataDirectoryInitializer.Initialize();
            StepTypeRegistry.ValidateKnownDiscriminators();
            AIActionCatalog.ThrowIfInconsistent();

            // 2. Core Infrastructure Services
            _secretVault = new DpapiSecretVault();
            _clientFactory = new ProviderHttpClientFactory();

            var openAi = new OpenAiVisionProvider(_clientFactory);
            var anthropic = new AnthropicVisionProvider(_clientFactory);
            var gemini = new GoogleGeminiVisionProvider(_clientFactory);

            _visionRouter = new AIVisionRouter(_secretVault, [openAi, anthropic, gemini]);

            string licenseFile = Path.Combine(MacroSuite.Core.Storage.AppDataPathResolver.GetLicenseDirectory(), "license.bin");
            bool isFirstLaunch = !File.Exists(licenseFile);
            _licenseService = new LocalLicenseService(
                trust: MacroSuite.App.Common.BuildEnvironment.IsDeveloperBuild
                    ? LicensePersistenceTrust.DeveloperAdminOrFree
                    : LicensePersistenceTrust.ProductionVerifiedOnly);
#if DEVELOPER_MODE
            if (isFirstLaunch)
            {
                var adminLicense = LicenseInfo.CreateAdministrator(
                    licenseKey: "ZENITH-ADMIN-MASTER-2026-UNLIMITED",
                    customerId: "Administrator",
                    email: "developer@zenithmacros.dev",
                    issued: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    expiration: DateTimeOffset.MaxValue);
                _licenseService.InstallLicense(adminLicense);
            }
#else
            // In public shipping builds, LocalLicenseService defaults to standard Free tier on first launch.
#endif
            _featureGate = new FeatureGate(_licenseService);

            _usageRepo = new SqliteUsageRepository();
            _usageTracker = new UsageTracker(_usageRepo, _featureGate);

            _auditLogger = new AsyncAuditLogger();
            // Rule 5/6: SharedCaptureService is the single runtime capture owner. It wraps
            // FallbackCaptureCoordinator (real DXGI primary, GDI fallback). CaptureScheduler is a
            // push fan-out that must wrap this service if used; it is not started here so
            // polling conditions do not fight a second capture loop.
            _captureCoordinator = new FallbackCaptureCoordinator();
            _captureService = new SharedCaptureService(_captureCoordinator);

            var settingsVM = new SettingsViewModel(_secretVault, _visionRouter);
            _settingsVM = settingsVM;
            ApplyAppearance();
            settingsVM.Appearance.Changed += ApplyAppearance;
            settingsVM.Appearance.Initialize();
            ImageScanBudget.Shared.SnapshotChanged += OnImageScanPerformanceChanged;
            _usageTracker.DailySpendCapUsd = settingsVM.DailySpendCapUsd;
            _usageTracker.MonthlySpendCapUsd = settingsVM.MonthlySpendCapUsd;
            _captureCoordinator.AllowGdiFallback = settingsVM.AllowGdiFallback;
            settingsVM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.AllowGdiFallback))
                    _captureCoordinator.AllowGdiFallback = settingsVM.AllowGdiFallback;
            };

            _inputBackendResolution = new InputBackendResolver(
                win32Fallback: new Win32InputBackend(foregroundGuard: new Win32ForegroundInputGuard()))
                .Resolve(settingsVM.PreferredInputBackendId);
            _inputPlayer = _inputBackendResolution.Player;
            settingsVM.InputBackendStatus = FormatInputBackendStatus(_inputBackendResolution);
            foreach (InputBackendDiagnostic diagnostic in _inputBackendResolution.Diagnostics)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Input backend [{diagnostic.Code}] {diagnostic.BackendId}: {diagnostic.Message}");
            }
            _inputRecorder = new LowLevelHookService();
            _inputOperations = new HostInputOperationArbiter();
            _locationPicker = new OneShotLocationPicker(_inputOperations);

            // 3. Human-in-the-Loop Approval Gate connecting to non-focus-stealing ApprovalToastWindow (Rule 8)
            _approvalGate = new ApprovalGate(ApprovalPolicy.ManualApproval, async (action, context, ct) =>
            {
                return await Dispatcher.InvokeAsync(async () =>
                {
                    var toast = new ApprovalToastWindow();
                    return await toast.WaitForDecisionAsync(context, ct);
                }).Task.Unwrap();
            });

            // 4. ViewModels
            var editorVM = new MacroEditorViewModel(
                _captureService,
                _inputPlayer,
                _visionRouter,
                _approvalGate,
                _featureGate,
                _usageTracker,
                _auditLogger,
                seedDemoData: MacroSuite.App.Common.BuildEnvironment.IsDeveloperBuild,
                inputRecorder: _inputRecorder,
                locationPicker: _locationPicker,
                // Scripts must survive restarts and rebuilds, not just developer builds.
                customMacrosDirectory: MacroSuite.Core.Storage.AppDataPathResolver.Default.MacrosDirectory,
                inputOperations: _inputOperations);
            _editorVM = editorVM;
#if DEVELOPER_MODE
            try
            {
                _macroCatalogServer = new(editorVM.CompanionCatalog);
                // A stopped endpoint must not be silent: the companion would only say "not connected" with no recorded reason.
                _ = _macroCatalogServer.Completion.ContinueWith(static stopped => System.Diagnostics.Trace.TraceWarning(
                        "The optional Personal macro metadata endpoint stopped ({0}). Restart Personal to reconnect the companion; editing and saved macros remain available.",
                        stopped.Exception?.GetBaseException().GetType().Name),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception)
            {
                System.Diagnostics.Trace.TraceWarning("The optional Personal macro metadata endpoint could not start. Editing and saved macros remain available.");
            }
#endif
            editorVM.OwnedLocationPickPrompter = (parent, ct) => CoordinatePickOverlay.PromptAsync(_locationPicker!, parent, ct);
            editorVM.ExecutionStatusPublished = snapshot =>
            {
                if (!settingsVM.ShowDetectionOverlay)
                    return;
                _detectionOverlay ??= new LiveWatchOverlay();
                _detectionOverlay.SetStatus(snapshot);
            };
            editorVM.DetectionOverlayStopped = () => _detectionOverlay?.StopWatching();
            settingsVM.DetectionOverlayEnabledChanged += enabled =>
            {
                if (!enabled)
                    _detectionOverlay?.StopWatching();
            };

            _approvalGate.Policy = settingsVM.SelectedPolicy;
            settingsVM.ApprovalPolicyChanged += policy => _approvalGate.Policy = policy;
            var licensingVM = new LicensingViewModel(_licenseService);
            var usageVM = _usageDashboardVM = new UsageDashboardViewModel(_usageTracker, _usageRepo, settingsVM.TrySaveSpendCaps);
            _autoClickerVM = new MacroSuite.App.ViewModels.AutoClickerViewModel(_inputPlayer, _locationPicker, paths: AppDataPathResolver.Default, featureGate: _featureGate, inputOperations: _inputOperations);
            settingsVM.AttachAutoClickerPreferences(_autoClickerVM);
            _autoClickerVM.DisplayGeometryProvider = Win32VirtualDesktopGeometry.Query;
            _autoClickerVM.ShowOwnedPickOverlay = (picker, parent, ct) => CoordinatePickOverlay.PromptAsync(picker, parent, ct);

            var mainVM = new MainViewModel(
                editorVM,
                settingsVM,
                licensingVM,
                usageVM,
                _autoClickerVM,
                _licenseService,
                inputOperations: _inputOperations);

            // 5. Launch Main Window
            var mainWindow = new MainWindow(mainVM);
            mainWindow.CreateMacroAuthoringViewModel = () => new AIMacroAuthoringViewModel(
                new AIMacroAuthoringService(_visionRouter, _captureService,
                    new MacroSuite.Infrastructure.OpenCV.OpenCvImageEncoder(), _usageTracker, _featureGate), (macro, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!editorVM.AddGeneratedMacro(macro))
                    throw new InvalidOperationException("The macro could not be saved. Resolve pending edits or the library error and try again.");
                mainVM.CurrentViewModel = editorVM;
                return Task.CompletedTask;
            });
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            CrashReporter.RecordCrash(ex, "Fatal startup exception");
            try
            {
                var crashPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroSuite", "crash.txt");
                System.IO.File.WriteAllText(crashPath, ex.ToString());
            }
            catch { }
            throw;
        }
    }

    private void ApplyAppearance()
    {
        if (_settingsVM is null) return;
        if (Dispatcher.CheckAccess())
            ThemeManager.Apply(Resources, _settingsVM.Appearance.CurrentPalette, _settingsVM.Appearance.EffectivePanelOpacity);
        else
            Dispatcher.Invoke(ApplyAppearance);
    }

    private void OnImageScanPerformanceChanged(ImageScanPerformanceSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => _settingsVM?.Appearance.SetResourcePressure(snapshot.Enabled && snapshot.IsThrottled));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _inputOperations?.Close();
#if DEVELOPER_MODE
        // The source is a standalone metadata publisher, so joining never waits on the UI dispatcher.
        try
        {
            _macroCatalogServer?.SignalStop();
            if (_macroCatalogServer is { } catalog) catalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            System.Diagnostics.Trace.TraceWarning("The Personal macro metadata endpoint did not shut down cleanly.");
        }
#endif
        ImageScanBudget.Shared.SnapshotChanged -= OnImageScanPerformanceChanged;
        if (_settingsVM is not null)
        {
            _settingsVM.Appearance.Changed -= ApplyAppearance;
            _settingsVM.DetachRuntimeSettings();
        }
        bool inputUnjoined = _inputOperations?.Snapshot.IsBusy == true;
        try
        {
            if (inputUnjoined) _editorVM?.SignalEmergencyStop();
            else _editorVM?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Editor view-model dispose failed: {ex}");
        }

        _autoClickerVM?.Dispose();
        _detectionOverlay?.Close();
        _locationPicker?.Dispose();
        if (inputUnjoined || _inputOperations?.Snapshot.IsBusy == true)
        {
            // Forced process exit is not a cleanup receipt. Do not dispose resources beneath
            // a live/uncertain producer, and never block the exiting UI on its continuations.
            System.Diagnostics.Trace.TraceWarning("Input cleanup was not joined before process exit. Shared producer services were retained.");
        }
        else
        {
            _captureService?.Dispose();
            _inputBackendResolution?.Dispose();
            _inputRecorder?.Dispose();
            _usageDashboardVM?.Dispose();
            _usageRepo?.Dispose();
            _auditLogger?.Dispose();
            _clientFactory?.Dispose();
        }

        base.OnExit(e);
    }

    private static string FormatInputBackendStatus(InputBackendResolution resolution)
    {
        string selected = resolution.UsedFallback
            ? $"Active backend: {resolution.BackendId} (fell back from the preferred adapter)."
            : $"Active backend: {resolution.BackendId}.";
        string details = string.Join(" ", resolution.Diagnostics.Select(d => d.Message));
        return string.IsNullOrWhiteSpace(details) ? selected : $"{selected} {details}";
    }
}
