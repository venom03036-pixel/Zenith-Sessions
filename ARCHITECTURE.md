# MacroSuite — Architecture & Engineering Specification

> Canonical reference for all agents and engineers.
> Read AGENTS.md first for behavioral rules. This document covers architecture, component design, subsystem contracts, and phased build plan.
> Last updated: 2026-09-11 (v2 — expanded spec with licensing, SDK, plugin system, resolved design decisions)

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                   MacroSuite.App (WPF)                  │
│  ┌─────────────────────────┐ ┌───────────────────────┐  │
│  │   Visual Block Editor   │ │   Overlay Windows     │  │
│  │  (MVVM + Drag & Drop)   │ │(Region/Magnifier/HUD) │  │
│  └────────────┬────────────┘ └───────────┬───────────┘  │
└───────────────┼──────────────────────────┼──────────────┘
                │ Commands & State         │ UI Throttled (10 FPS)
┌───────────────▼──────────────────────────▼──────────────┐
│                   MacroSuite.Core                       │
│                                                         │
│  ┌─────────────────────────────────────────────────┐    │
│  │   MacroController & IMacroControl (State)       │    │
│  │         [Run / Pause / Stop / Panic]            │    │
│  └────────────────────────┬────────────────────────┘    │
│                           │                             │
│  ┌────────────────────────▼────────────────────────┐    │
│  │               MacroExecutor (Runner)            │    │
│  │   Walks MacroSteps with shared MacroContext     │    │
│  │   Enforces: MaxRuntime, MaxIterations, Panic    │    │
│  └──────┬────────────────────────────┬────────────┘    │
│         │                            │                  │
│  ┌──────▼──────────┐    ┌────────────▼───────────────┐  │
│  │ InputEngine     │    │ RuleEngine                 │  │
│  │ - Hook Channel  │    │ - WaitUntil / Event-driven │  │
│  │ - Compression   │    │ - Composable Conditions    │  │
│  └──────┬──────────┘    └────────────┬───────────────┘  │
│         │                            │                  │
│  ┌──────▼──────────┐    ┌────────────▼───────────────┐  │
│  │ Safety Sandbox  │    │ CaptureScheduler           │  │
│  │ - FeatureGate   │    │ - Region Merging           │  │
│  │ - Validator     │    │ - Adaptive Polling         │  │
│  │ - ApprovalGate  │    │ - Zero-copy fan-out        │  │
│  └──────┬──────────┘    └────────────┬───────────────┘  │
│         │                            │                  │
│  ┌──────▼──────────────────────────────────────────┐    │
│  │ Licensing / Usage / Plugin / Audit / Security   │    │
│  └──────┬──────────────────────────────────────────┘    │
└─────────┼──────────────────────────────────────────────┘
          │
┌─────────▼──────────────────────────────────────────────┐
│               MacroSuite.Infrastructure                 │
│  ┌────────────────────────┐  ┌───────────────────────┐  │
│  │ Win32 SendInput & Hooks│  │ Windows.Graphics.Cap  │  │
│  │ (WH_MOUSE/KEYBOARD_LL) │  │ (DXGI/D3D11 + GDI)   │  │
│  ├────────────────────────┤  ├───────────────────────┤  │
│  │ AI Vision Providers    │  │ OpenCvSharp4 Matcher  │  │
│  │ (Anthropic/OpenAI/Gem) │  │ (Cached Mats/Worker)  │  │
│  ├────────────────────────┤  ├───────────────────────┤  │
│  │ DPAPI SecretVault      │  │ SQLite (Audit+Usage)  │  │
│  ├────────────────────────┤  ├───────────────────────┤  │
│  │ LicenseService (local) │  │ Plugin Loader/Sandbox │  │
│  └────────────────────────┘  └───────────────────────┘  │
└─────────────────────────────────────────────────────────┘

MacroSuite.SDK (published NuGet — no Core/Infra references)
  Contracts / Steps / Conditions / AI / UI metadata
```

---

## Solution Layout

```
MacroSuite/
├── MacroSuite.sln
├── src/
│   ├── MacroSuite.Core/                    # Pure domain & business logic. No WPF. No Win32.
│   │   ├── Abstractions/                   # IScreenCaptureService, IInputPlayer, IAIVisionProvider,
│   │   │                                   #   ILicenseService, IFeatureGate, ISecretVault,
│   │   │                                   #   IAuditLogger, IPluginHost
│   │   ├── Models/                         # InputEvent (struct), MacroModel, CaptureFrame,
│   │   │                                   #   MacroStep (base), AuditEntry, MatchResult
│   │   ├── Execution/                      # MacroExecutor, MacroContext, MacroVariables,
│   │   │                                   #   IMacroControl, MacroController
│   │   ├── Input/                          # InputRecorder, MouseCompressionEngine, InputPlayer
│   │   ├── Capture/                        # CaptureScheduler, ICaptureSubscriber, RegionMerger
│   │   ├── Vision/                         # ColorDetector, TemplateCache, MatchResult
│   │   ├── Rules/                          # ICondition, ColorCondition, ImageCondition,
│   │   │                                   #   AndCondition, OrCondition, NotCondition, RuleEngine
│   │   ├── Steps/                          # ClickStep, KeyPressStep, DelayStep, WaitUntilStep,
│   │   │                                   #   IfThenElseStep, LoopStep, AIStep, PluginStep
│   │   ├── AI/                             # AIAction, AIActionValidator, IApprovalGate,
│   │   │                                   #   ApprovalPolicy, AutonomousModeState
│   │   ├── Licensing/                      # ILicenseService, IFeatureGate, FeatureId,
│   │   │                                   #   FeatureNotLicensedException, LicensedFeatureGuard
│   │   ├── Usage/                          # UsageTracker, UsageRecord, SpendCap, IUsageRepository
│   │   ├── Extensibility/                  # IPluginDescriptor, IStepPlugin, IAIProviderPlugin,
│   │   │                                   #   PluginRegistry, PluginLifecycleManager
│   │   ├── Security/                       # ISecretVault
│   │   ├── Storage/                        # IMacroRepository, MacroJsonSerializer,
│   │   │                                   #   SchemaVersion, MacroMigrationEngine,
│   │   │                                   #   AppDataPathResolver
│   │   └── Diagnostics/                    # IAuditLogger, AuditEntry, AuditRetentionPolicy
│   │
│   ├── MacroSuite.Infrastructure/          # Hardware, OS & third-party integrations
│   │   ├── Windows/                        # Win32 P/Invoke: SetWindowsHookEx, SendInput,
│   │   │                                   #   GetDpiForMonitor, RegisterHotKey, GetForegroundWindow
│   │   ├── Input/                          # LowLevelHookService, Win32InputPlayer
│   │   ├── Capture/                        # WindowsGraphicsCaptureService (DXGI/WinRT),
│   │   │                                   #   GdiBitBltCaptureService, FallbackCaptureCoordinator
│   │   ├── OpenCV/                         # OpenCvMatcher, TemplateCache
│   │   ├── AIProviders/                    # AnthropicVisionProvider, OpenAiVisionProvider,
│   │   │                                   #   GoogleGeminiVisionProvider, ProviderHttpClientFactory
│   │   ├── Security/                       # DpapiSecretVault
│   │   ├── SQLite/                         # AsyncAuditLogger, UsageRepository,
│   │   │                                   #   AuditRetentionEnforcer
│   │   ├── Licensing/                      # LocalLicenseService (file-based, DPAPI-signed)
│   │   ├── Plugins/                        # PluginLoader, PluginSandbox, AssemblyIsolation
│   │   └── Storage/                        # FileSystemMacroRepository, AppDataDirectoryInitializer
│   │
│   ├── MacroSuite.App/                     # WPF UI (.NET 8-windows10.0.19041.0)
│   │   ├── App.xaml / App.xaml.cs          # DI composition root, lifecycle management
│   │   ├── Views/                          # MainWindow, MacroEditorView, SettingsView,
│   │   │                                   #   AuditLogView, LicensingView, UsageDashboardView
│   │   ├── ViewModels/                     # MainViewModel, MacroEditorViewModel, StepViewModels,
│   │   │                                   #   LicensingViewModel, UsageViewModel
│   │   ├── Controls/                       # StepBlockTemplateSelector, VisualBlockCanvas,
│   │   │                                   #   FeatureGatedControl
│   │   ├── Overlays/                       # RegionSelectorWindow, ColorPickerOverlay,
│   │   │                                   #   LiveWatchOverlay, AutonomousBanner,
│   │   │                                   #   ApprovalToastWindow
│   │   ├── Converters/                     # StatusToBrushConverter, ActionTypeToGlyphConverter,
│   │   │                                   #   FeatureEnabledConverter
│   │   ├── Services/                       # GlobalHotkeyService, SystemTrayService,
│   │   │                                   #   DpiCoordinateConverter
│   │   └── Themes/                         # DarkTheme.xaml, ModernControls.xaml
│   │
│   └── MacroSuite.SDK/                     # Public extensibility surface (published as NuGet)
│       ├── Steps/                          # IStepPlugin, StepDescriptor, StepEditorMetadata,
│       │                                   #   StepExecutionContext, StepValidationResult
│       ├── Conditions/                     # IConditionPlugin, ConditionDescriptor
│       ├── AI/                             # IAIProviderPlugin, AIProviderDescriptor,
│       │                                   #   ModelDescriptor, ProviderCapabilities
│       └── Contracts/                      # PluginManifest, PluginIdentity, IPluginServices
│                                           #   (access to MacroContext without engine internals)
│
└── tests/
    ├── MacroSuite.Core.Tests/              # xUnit: execution, compression, rules, licensing,
    │                                       #   approval, usage tracking, spend caps, plugins
    ├── MacroSuite.Infrastructure.Tests/    # xUnit+mocks: DPAPI, OpenCV, provider HTTP
    └── MacroSuite.SDK.Tests/               # xUnit: plugin contracts, step lifecycle, isolation
```

---

## Data Directory Layout (Runtime)

```
%APPDATA%\MacroSuite\
├── Macros\             # .json macro files (versioned)
├── Templates\          # image template files (.png, .jpg)
│                       #   referenced by relative path within this root
├── Config\             # app settings (never API keys — those go in Security\)
│   └── settings.json
├── Security\           # DPAPI-encrypted credential blobs (never plaintext)
│   └── providers.bin
├── Audit\              # SQLite audit database
│   ├── audit.db
│   └── screenshots\    # thumbnails referenced by audit DB (not blobs)
├── Usage\              # SQLite usage/spend database
│   └── usage.db
├── Plugins\            # third-party plugin assemblies
│   └── {PluginId}\
│       ├── plugin.json (manifest)
│       └── *.dll
├── Cache\              # transient (safe to delete)
└── Logs\               # structured log output
```

**Design decisions:**
- All directories created automatically by `AppDataDirectoryInitializer` at startup.
- No user configuration required for normal operation.
- Template paths in macro files are relative to `%APPDATA%\MacroSuite\Templates\` — never absolute.
- The `Storage` abstraction accepts a root-path provider, enabling future portable/user-selected mode without changing the engine.

---

## Core Data Structures

### InputEvent — 24-byte Stack-Only Struct

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct InputEvent
{
    public readonly InputEventType Type;   // MouseMove, MouseDown, MouseUp, KeyDown, KeyUp, MouseWheel
    public readonly long TimestampTicks;   // Stopwatch.GetTimestamp() — high-res, no wall-clock drift
    public readonly int X;
    public readonly int Y;
    public readonly int Data;              // VirtualKey, MouseButton, or WheelDelta
    public readonly uint ExtraInfo;
}
// Total: 4 + 8 + 4 + 4 + 4 + 4 = 28 bytes with Pack=1
```

**Design constraints:**
- Zero heap allocations. Created on hook-callback stack, written to `Channel<InputEvent>` by value.
- `TimestampTicks` from `Stopwatch.GetTimestamp()`, not `DateTime.UtcNow`.

---

### CaptureFrame — Pooled, Zero-Copy Memory

```csharp
public sealed class CaptureFrame : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }      // Bgra32 from DXGI; Bgr24 from GDI
    public CaptureSource Source { get; }    // Dxgi | Gdi
    public ReadOnlySpan<byte> Span =>
        _memoryOwner.Memory.Span[..(Height * Stride)];
    private IMemoryOwner<byte>? _memoryOwner;
    public void Dispose() =>
        Interlocked.Exchange(ref _memoryOwner, null)?.Dispose();
}
```

- Rented from `MemoryPool<byte>.Shared`. No per-frame heap allocations.
- `Source` field lets consumers adapt if GDI fallback produces a different pixel layout.

---

### MacroModel — Versioned Macro File Schema

```csharp
public sealed class MacroModel
{
    public int SchemaVersion { get; init; } = MacroSchema.CurrentVersion;
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public MacroSettings Settings { get; init; } = new();
    public IReadOnlyList<MacroStep> Steps { get; init; } = [];
}

// Discriminated step serialization:
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ClickStep),       "click")]
[JsonDerivedType(typeof(KeyPressStep),    "keypress")]
[JsonDerivedType(typeof(DelayStep),       "delay")]
[JsonDerivedType(typeof(WaitUntilStep),   "waituntil")]
[JsonDerivedType(typeof(IfThenElseStep),  "ifthenelse")]
[JsonDerivedType(typeof(LoopStep),        "loop")]
[JsonDerivedType(typeof(AIStep),          "ai")]
[JsonDerivedType(typeof(PluginStep),      "plugin")]   // extensible: unknown types preserved as-is
public abstract class MacroStep
{
    public Guid StepId { get; init; } = Guid.NewGuid();
    public string? Label { get; init; }
    public bool IsEnabled { get; init; } = true;
}
```

**Schema migration:** `MacroMigrationEngine` applies sequential migration functions when loading a file with `SchemaVersion` < `CurrentVersion`. Unknown `$type` values are deserialized into `UnknownStep` (a passthrough) rather than failing.

---

### AIAction — Normalized, Provider-Agnostic

```csharp
public sealed record AIAction(
    AIActionType Type,      // Click, Type, KeyPress, Scroll, NoAction
    Point? Position,
    string? Text,
    VirtualKey? Key,
    int ScrollDelta,
    string Reasoning,       // retained for audit; never executed
    string ProviderRef);    // provider name + request ID for audit correlation
```

---

### MacroContext — Execution Scope

```csharp
public sealed class MacroContext
{
    public Guid RunId { get; } = Guid.NewGuid();
    public IMacroControl Control { get; init; }
    public IScreenCaptureService CaptureService { get; init; }
    public IRuleEngine RuleEngine { get; init; }
    public IInputPlayer InputPlayer { get; init; }
    public IAIVisionProvider? AIProvider { get; init; }     // nullable — AI is optional (Rule 15)
    public IActionValidator ActionValidator { get; init; }
    public IApprovalGate ApprovalGate { get; init; }
    public IAuditLogger AuditLogger { get; init; }
    public IFeatureGate FeatureGate { get; init; }
    public UsageTracker UsageTracker { get; init; }
    public IPluginHost PluginHost { get; init; }
    public MacroVariables Variables { get; } = new();
    public DateTime StartedAtUtc { get; init; }
    public TimeSpan MaxRuntime { get; init; }
    public int MaxIterations { get; init; }
    public int MaxLoopDepth { get; init; }

    // Checked at every step boundary — Constitution Rule 11
    public bool IsAborted =>
        Control.PanicToken.IsCancellationRequested ||
        Control.StopToken.IsCancellationRequested ||
        (DateTime.UtcNow - StartedAtUtc) > MaxRuntime;
}
```

---

## Key Interfaces

### IMacroControl — Triple-Tier Control

```csharp
public interface IMacroControl
{
    // Tier 1: Pause — suspends at step boundaries, preserves state
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync();
    bool IsPaused { get; }

    // Tier 2: Stop — signals cancellation token, awaits clean shutdown
    Task StopAsync();
    CancellationToken StopToken { get; }

    // Tier 3: Panic — immediate, unconditional, releases held keys via SendInput(KEYUP)
    void Panic();
    CancellationToken PanicToken { get; }
}
```

| Tier | Mechanism | State After | Key Release |
|---|---|---|---|
| Pause | `SemaphoreSlim` gating step loop | Preserved | No |
| Stop | `CancellationTokenSource` | Reset | No |
| Panic | Separate `CancellationTokenSource` + `SendInput(KEYUP)` for all depressed keys | Cleared | **Yes — immediate** |

---

### IFeatureGate — Licensing Enforcement Boundary

```csharp
public interface IFeatureGate
{
    /// <summary>
    /// Returns true if the feature is licensed.
    /// Does NOT throw — use for UI state binding.
    /// </summary>
    bool IsAvailable(FeatureId feature);

    /// <summary>
    /// Throws FeatureNotLicensedException if not licensed.
    /// Called at every execution boundary that requires a license.
    /// </summary>
    void Require(FeatureId feature);
}

public enum FeatureId
{
    BasicMacroRecording,
    BasicPlayback,
    ColorDetection,
    ImageRecognition,
    AIVision,
    AutonomousMode,
    AdvancedConditions,
    PluginExecution,
    AuditLog,
    UsageTracking,
}
```

**Enforcement contract:**
- UI uses `IsAvailable()` to reflect entitlement state (show/hide/grey out features).
- UI restrictions are **not** the enforcement boundary.
- Every execution path that touches a licensed feature calls `Require()` before proceeding.
- Plugins are subject to `FeatureId.PluginExecution`. A plugin cannot bypass this.

---

### ILicenseService

```csharp
public interface ILicenseService
{
    LicenseInfo GetCurrentLicense();
    Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default);
    bool IsFeatureLicensed(FeatureId feature);
    event EventHandler<LicenseChangedEventArgs> LicenseChanged;
}
```

---

### IApprovalGate — 3-Policy Configurable

```csharp
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestApprovalAsync(
        AIAction action,
        ApprovalContext context,
        CancellationToken ct);
}

public enum ApprovalPolicy
{
    ManualApproval,     // Non-focus-stealing toast near target. Timeout → Reject.
    AutoApprove,        // Immediate approve. Requires explicit AutonomousMode.
    AutoReject,         // Immediate reject. Safe default for testing.
}

public sealed class ApprovalContext
{
    public ApprovalPolicy Policy { get; init; }
    public TimeSpan Timeout { get; init; }
    public CaptureFrame? Screenshot { get; init; }   // for preview in toast
    public Rectangle TargetRegion { get; init; }
    public IntPtr TargetHwnd { get; init; }
}
```

**Important invariants:**
- Autonomous Mode does NOT implicitly mean `AutoApprove`. It is a separate deliberate policy.
- `AutoApprove` requires both `AutonomousMode` to be active AND the user to have configured `ApprovalPolicy.AutoApprove`.
- Timeout never causes indefinite hang — always resolves to `ApprovalDecision.Rejected`.
- `ManualApproval` displays a non-focus-stealing topmost window near `TargetRegion`.

---

### IAIVisionProvider

```csharp
public interface IAIVisionProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool SupportsVision { get; }
    Task<AIAction> AnalyzeAsync(
        CaptureFrame frame,
        string prompt,
        AIProviderOptions options,
        CancellationToken ct);
}
```

---

### IUsageRepository — Persisted Across Restarts

```csharp
public interface IUsageRepository
{
    Task RecordAsync(UsageRecord record, CancellationToken ct = default);
    Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default);
}

public sealed class UsageRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; }
    public string ProviderId { get; init; }
    public string ModelId { get; init; }
    public string? ProviderRequestId { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCostUsd { get; init; }
    public FeatureId Feature { get; init; }
    public Guid? MacroId { get; init; }
    // Never contains API keys or secrets
}
```

---

### IPluginDescriptor — SDK Contract

```csharp
// In MacroSuite.SDK
public interface IPluginDescriptor
{
    string PluginId { get; }
    string DisplayName { get; }
    Version PluginVersion { get; }
    Version MinHostVersion { get; }
    IReadOnlyList<FeatureId> RequiredFeatures { get; }
}

public interface IStepPlugin : IPluginDescriptor
{
    StepDescriptor GetDescriptor();
    Task<StepResult> ExecuteAsync(
        PluginStep step,
        IPluginExecutionContext context,
        CancellationToken ct);
    ValidationResult Validate(PluginStep step);
}

public interface IAIProviderPlugin : IPluginDescriptor
{
    AIProviderDescriptor GetProviderDescriptor();
    IAIVisionProvider CreateProvider(IPluginServices services);
}
```

**Plugin isolation rules:**
- Plugins execute within their own `AssemblyLoadContext` (soft isolation).
- Plugins access the host only through `IPluginExecutionContext` and `IPluginServices` — no direct Core type references.
- A plugin exception is caught, logged, and treated as a step failure. It must not crash the executor.
- Plugins cannot bypass: licensing (`FeatureGate.Require` is always called before delegation), panic (token flows through `IPluginExecutionContext`), audit.
- Plugin manifests are validated before loading. Invalid manifests: skipped with a diagnostic log entry.

---

## Subsystem Engineering Details

### A. Input Recording

- **Hook callback budget: < 1ms.** Windows silently unhooks if exceeded.
- Callback constructs `InputEvent` on stack → `ChannelWriter<InputEvent>.TryWrite()` → return immediately.
- No allocations, no locks, no logging inside callback.
- Consumer task on dedicated `ThreadPriority.AboveNormal` background thread.

**Compression invariant:** The final `MouseMove` immediately before any `MouseDown` is **never** suppressed.

### B. High-Precision Playback

- `Task.Delay` resolution: ~15.6ms. Unacceptable for fast macros.
- Dedicated `ThreadPriority.AboveNormal` thread with `Stopwatch` spin-wait hybrid:
  - Gap ≤ 2ms → spin
  - Gap 2–15ms → `Thread.Sleep(1)` loop
  - Gap > 15ms → `Thread.Sleep(gap - 5ms)` then spin remainder
- Sequential events with identical timestamps batched into single `SendInput` call.

### C. Screen Capture & GDI Fallback

```
FallbackCaptureCoordinator
  Primary: WindowsGraphicsCaptureService (DXGI/WinRT)
    ↓ fails (protected frame, blank, init error)
  → GDI fallback enabled? (default: yes)
    → GdiBitBltCaptureService
    → Backoff: don't retry primary on every frame
       (attempt re-init every 30 seconds)
  → GDI fallback disabled?
    → Return CaptureError to consumer
```

- Consumers depend only on `IScreenCaptureService` — fallback is transparent.
- Application setting: `capture.allowGdiFallback` (default: `true`). When `false`, failures surface as errors.
- `CaptureFrame.Source` field records whether the frame came from DXGI or GDI.

### D. CaptureScheduler

- Subscribers register: `Rectangle`, `TimeSpan PollingInterval`, `SubscriberId`.
- Each tick: compute bounding union of all active rectangles → single capture → fan out sub-spans.
- **Zero subscribers = scheduler pauses completely** (no CPU at idle).
- Adaptive frequency: slowest subscriber interval sets the floor; fastest sets the ceiling.

### E. Color Detection

```csharp
// Integer arithmetic — zero allocations, no Color objects
static bool IsMatch(ReadOnlySpan<byte> bgra, int offset, byte r, byte g, byte b, int tolerance)
{
    int dist = Math.Abs(bgra[offset + 2] - r)
             + Math.Abs(bgra[offset + 1] - g)
             + Math.Abs(bgra[offset]     - b);
    return dist <= tolerance * 3;
}
```

Licensed under `FeatureId.ColorDetection`.

### F. OpenCV Matching Pipeline

- `TemplateCache`: keyed by SHA-256 of file bytes. Stores `Mat` (BGRA), `Mat` (gray), `Mat` (0.5x gray).
- Matching channel: `Capacity: 2, FullMode: DropOldest`. Stale frames discarded automatically.
- Dedicated background worker with `CancellationToken`. Never runs on ThreadPool.

Licensed under `FeatureId.ImageRecognition`.

### G. AI Safety Pipeline

```
IFeatureGate.Require(AIVision)          // licensing boundary
IAIVisionProvider.AnalyzeAsync()       // provider-specific, normalized to AIAction
  ↓
AIActionParser                          // parse, reject malformed
  ↓
IActionValidator.Validate()            // bounds, window, type whitelist, text length
  ↓
Safety checks                          // rate limits, spend cap, region constraints
  ↓
UsageTracker.PreflightCheckAsync()     // check spend cap BEFORE sending request
  ↓
IApprovalGate.RequestApprovalAsync()   // policy-driven; timeout → reject
  ↓
IFeatureGate.Require(AIVision)         // re-check after potential delay
  ↓
IInputPlayer.PlayAsync()               // single input pipeline
  ↓
IAuditLogger.Enqueue()                 // async, never blocks
  ↓
UsageTracker.RecordAsync()             // persist actual usage
```

**Spend cap pre-flight is explicit:**
1. Check license.
2. Load persisted total from `IUsageRepository`.
3. Compare (estimated cost + running total) against configured cap.
4. Only then call the provider.

### H. Licensing Enforcement

```
Before any licensed operation:
  IFeatureGate.Require(FeatureId.X)
    ↓ not licensed → throw FeatureNotLicensedException
    ↓ licensed → proceed

FeatureNotLicensedException:
  - Caught at executor boundary
  - Surfaced to user via appropriate UX (not a crash)
  - Audit log entry: license denial
```

UI uses `IsAvailable()` for binding. UI state is **not** the enforcement boundary.

### I. Plugin Lifecycle

```
Discovery:     Scan %APPDATA%\MacroSuite\Plugins\*\plugin.json
Validation:    Check manifest schema, MinHostVersion compatibility
               Verify required FeatureIds
               Signature check (if enforced)
Loading:       AssemblyLoadContext per plugin (soft isolation)
Initialization: IStepPlugin.InitializeAsync(IPluginServices)
               → failure → mark plugin as faulted, skip
Registration:  PluginRegistry.Register(descriptor)
               MacroJsonSerializer registers plugin $type discriminators
Runtime:       MacroExecutor delegates PluginStep to PluginRegistry
               Timeout, cancellation, and panic tokens passed through
Shutdown:      IStepPlugin.ShutdownAsync() called on app exit
Error Isolation: Plugin exceptions caught, logged, step fails gracefully
```

### J. Audit Log

- `IAuditLogger.Enqueue()` is fire-and-forget. Never blocks the macro.
- `Channel<AuditEntry>` → background SQLite writer task with `CancellationToken`.
- Screenshots stored as thumbnail files in `Audit\screenshots\`. Referenced by path in DB, never as blobs.
- Rolling quota: configurable (default 500MB for screenshots). Oldest files deleted when exceeded.
- Fields: `Timestamp, RunId, MacroId, StepId, Provider, Model, Prompt, ProposedAction, ValidationResult, ApprovalResult, ExecutedAction, ScreenshotPath, Error, InputTokens, OutputTokens, EstimatedCostUsd`
- **Never contains:** API keys, secrets, full-resolution raw frames.

---

## Performance Targets

| Metric | Target |
|---|---|
| Memory footprint at idle | < 150 MB |
| CPU at idle (no active macro) | < 1% |
| CPU during active screen capture | < 5% on modern hardware |
| Hook callback execution time | < 1 ms |
| Playback timing jitter | < 2 ms at 1x–5x speed |
| OpenCV match latency | < 50 ms per frame on dedicated worker |
| Approval toast display latency | < 100 ms from AI response |
| Plugin step dispatch overhead | < 1 ms (not counting step execution time) |

---

## Error Classification

| Class | Meaning | Macro Behavior |
|---|---|---|
| `OperationalFailure` | Expected, recoverable | Retry or skip per step config |
| `ConfigurationError` | Bad setup | Surface to user, halt feature |
| `FeatureNotLicensedException` | License gate | Surface upgrade prompt, halt step |
| `SpendCapExceededException` | Budget exceeded | Surface cap warning, halt AI step |
| `ProviderException` | AI provider error | Log, attempt fallback or skip |
| `CancellationException` | Expected shutdown | Do not log as error |
| `SafetyTermination` | Panic triggered | Log, halt all, notify |
| `ProgrammingError` | Bug | Assert in debug, log context, surface |

Never catch an exception merely to rethrow it. Add context or don't catch it.

---

## NuGet Dependencies (Rule 19 — Justified)

| Package | Purpose | Why Not Platform |
|---|---|---|
| `OpenCvSharp4` | Template matching, multi-scale | No NCC matching in .NET BCL |
| `Microsoft.Data.Sqlite` | Audit + Usage persistence | Atomic writes, WAL, concurrent access |
| `GongSolutions.WPF.DragDrop` | Block editor drag & drop | No nested container support in ListView |
| `System.Text.Json` | Polymorphic serialization | Inbox in .NET 8 |
| Windows SDK (inbox) | WinRT capture API bindings | Required for GraphicsCaptureItem |

**AI providers: No SDKs.** All three providers called via `HttpClient` with manually constructed JSON to avoid SDK lock-in (Rule 16). One `HttpClient` per provider, injected, reused.

---

## Build Phases

### Phase 1 — Solution Scaffold & Input Engine
**Goal:** Solution layout, data models, Win32 hooks, compression, high-precision playback.

Deliverables:
- `MacroSuite.sln` — 5 projects (Core, Infrastructure, App, SDK, Tests)
- `InputEvent` struct + `Channel<InputEvent>` producer/consumer
- `LowLevelHookService` (WH_MOUSE_LL + WH_KEYBOARD_LL)
- `MouseCompressionEngine` (distance/time/direction invariant)
- `Win32InputPlayer` (batched `SendInput` + Stopwatch hybrid timing)
- `AppDataDirectoryInitializer` (creates `%APPDATA%\MacroSuite\` tree)
- Unit tests: compression ratios, direction preservation, timing accuracy

### Phase 2 — Capture Engine & Color Detection
**Goal:** Zero-copy GPU capture, automatic GDI fallback with backoff, color matching.

Deliverables:
- `CaptureFrame` with `IMemoryOwner<byte>` + `Source` field
- `WindowsGraphicsCaptureService` (DXGI/WinRT)
- `GdiBitBltCaptureService`
- `FallbackCaptureCoordinator` (automatic fallback + 30s re-init backoff)
- Subscription-based `CaptureScheduler` with region-merging
- `ColorDetector` (integer byte-span arithmetic, zero allocations)
- Unit tests: buffer reuse, color tolerance, GDI fallback trigger logic

### Phase 3 — OpenCV & Rule Engine
**Goal:** Image recognition worker pipeline and composable, extensible conditions.

Deliverables:
- `TemplateCache` (SHA-256 keyed, 3 Mat variants)
- `OpenCvMatcher` (bounded channel, DropOldest, dedicated worker)
- `ICondition` hierarchy: `ColorCondition`, `ImageCondition`, `AndCondition`, `OrCondition`, `NotCondition`
- `RuleEngine.WaitUntilAsync()` using `TaskCompletionSource` + subscription
- Steps: `WaitUntilStep`, `IfThenElseStep`, `LoopStep` (with `MaxDepth` guard)
- Unit tests: condition evaluation, timeout handling, composability

### Phase 4 — Macro Format, Serialization & Execution Engine
**Goal:** Versioned macro lifecycle, polymorphic serialization with migration, safety state machine.

Deliverables:
- `MacroSchema.CurrentVersion` + `MacroMigrationEngine`
- Polymorphic JSON via `[JsonDerivedType]` + `UnknownStep` passthrough
- `MacroContext` + `MacroVariables`
- `IMacroControl` + `MacroController` (3 independent tiers)
- `MacroExecutor` (step iteration, `IsAborted` at every boundary, loop depth guard)
- Round-trip serialization tests for every built-in step type
- End-to-end simulated macro execution tests

### Phase 5 — Licensing & Usage Tracking
**Goal:** Feature gate enforcement, persisted spend cap, usage records.

Deliverables:
- `IFeatureGate` + `ILicenseService` + `FeatureId`
- `FeatureNotLicensedException`, `SpendCapExceededException`
- `LocalLicenseService` (DPAPI-signed local license file)
- `UsageTracker` with `PreflightCheckAsync()` and `RecordAsync()`
- `IUsageRepository` + `SqliteUsageRepository` (persisted across restarts)
- `UsageRecord` schema (no secrets)
- Unit tests: feature gating, spend cap enforcement, cap persistence simulation

### Phase 6 — SDK & Plugin System
**Goal:** MacroSuite.SDK published surface, plugin lifecycle, isolation, registration.

Deliverables:
- `MacroSuite.SDK` project with `IStepPlugin`, `IAIProviderPlugin`, `IPluginExecutionContext`, `IPluginServices`, `PluginManifest`
- `PluginLoader` with `AssemblyLoadContext` per plugin
- `PluginLifecycleManager` (discovery → validation → init → shutdown)
- `PluginRegistry` with `PluginStep` dispatch
- Error isolation: plugin exceptions caught, step fails gracefully
- `MacroJsonSerializer` integration: plugin `$type` discriminators registered at load time
- Unit tests: plugin loading, invalid manifest rejection, exception isolation, panic propagation

### Phase 7 — AI Vision, Safety Sandbox, Approval Gate & Audit Log
**Goal:** Multi-provider integration, DPAPI key storage, full safety pipeline, configurable approval, async audit.

Deliverables:
- `DpapiSecretVault` (`ProtectedData.CurrentUser`)
- `AnthropicVisionProvider`, `OpenAiVisionProvider`, `GoogleGeminiVisionProvider` (manual HTTP, no SDKs)
- `ProviderHttpClientFactory` (one `HttpClient` per provider, injected)
- `AIActionValidator` (bounds, window, type whitelist, text length, rate limits)
- `IApprovalGate` with 3-policy system + non-focus-stealing `ApprovalToastWindow`
- Configurable timeout → auto-reject
- `AutonomousMode` state: explicit setting, persistent banner, policy-separate from approval
- `AsyncAuditLogger` (`Channel<AuditEntry>` → SQLite background writer)
- Screenshot thumbnail writer + rolling quota enforcer
- Unit tests: spend cap pre-flight, approval policy variants, audit field coverage, key-in-log absence check

### Phase 8 — WPF UI, Block Editor & Overlays
**Goal:** Polished dark-mode Windows desktop experience.

Deliverables:
- Main Window: dark theme, status bar (provider, license state, FPS, macro status)
- Visual Macro Editor: block canvas with drag-and-drop + nesting (`GongSolutions.WPF.DragDrop`)
- `RegionSelectorWindow`: fullscreen transparent, rubber-band, DPI-aware coordinate output
- `ColorPickerOverlay`: real-time magnifier loupe, hex swatch
- `LiveWatchOverlay`: `WS_EX_TRANSPARENT | WS_EX_LAYERED`, 10 FPS via `DispatcherTimer` at 100ms
- `ApprovalToastWindow`: non-focus-stealing, topmost, near target coordinates
- `AutonomousBanner`: topmost, undismissable amber/red persistent warning
- `LicensingView` + `UsageDashboardView`: spend summary, cap configuration
- `FeatureGatedControl`: greys out unlicensed features; enforcement is at execution, not UI
- Rebindable global hotkeys (`RegisterHotKey`)
- System Tray: minimize-to-tray, quick actions

### Phase 9 — Hardening, Benchmarks & End-to-End Tests
**Goal:** Production validation.

Deliverables:
- BenchmarkDotNet: idle memory < 150MB, capture CPU < 5%, hook callback < 1ms
- Panic test: active AI loop → Esc → all keys released + all tasks cancelled < 100ms
- GDI fallback test: DXGI blank-frame trigger → automatic switch → backoff timer
- Multi-monitor DPI verification: 100%, 125%, 150%
- Plugin isolation test: faulting plugin → host continues
- Schema migration test: v0 macro files → v1 round-trip
- Spend cap persistence test: restart simulation → cap not reset

---

## Resolved Design Decisions

| Question | Decision |
|---|---|
| Macro file storage | `%APPDATA%\MacroSuite\` — automatic, no user config required |
| GDI fallback | Automatic on blank/protected/failed DXGI frame. 30s re-init backoff. User-disable setting. |
| Spend cap persistence | Persisted in SQLite across restarts. Never resets on launch. |
| Approval Gate UX | Non-focus-stealing topmost toast near target. 3-policy (Manual/AutoApprove/AutoReject). |
| Autonomous Mode | Explicit deliberate setting. Does NOT implicitly mean AutoApprove. Policy is separate. |
| Extensibility | In scope for v1. `MacroSuite.SDK` project. Plugin + custom AI provider support. |
| UsageTracker | Both informational AND enforcement (spend cap, license check). |
| Template paths | Relative to `%APPDATA%\MacroSuite\Templates\` — never absolute machine paths. |

---

## Golden Rules (Quick Reference)

- **One capture, many consumers.**
- **One controlled input pipeline.**
- **AI never directly controls SendInput.**
- **Untrusted AI output must be validated.**
- **Licensed features are enforced at the execution boundary — not merely hidden in the UI.**
- **No API keys in data except DPAPI-encrypted credential storage.**
- **No unnecessary polling. No unnecessary threads. No unnecessary allocations.**
- **Panic always wins.**
- **Plugins extend the application without destabilizing it.**
- **The application remains functional when AI or network services are unavailable.**
- **Measure performance rather than assuming it.**

---

*Last updated: 2026-09-11 (v2)*
*Source: Architecture & Engineering Specification, expanded with licensing, SDK, plugin system, and resolved design decisions*
