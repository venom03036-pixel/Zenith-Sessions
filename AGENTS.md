# Zenith Macros — Engineering Constitution

> This file is the authoritative behavioral contract for all agents and engineers working in this repository.
> It is automatically loaded at the start of every agent session.
> These rules are **architectural requirements**, not style suggestions.
> When in doubt, follow the rule. When a rule conflicts with convenience, the rule wins.

---

## Project Overview

### Zenith Sessions requirement review

For Personal companion work, follow [the persistent review rule](.cursor/rules/zenith-sessions-requirements.mdc) and [the requirements ledger](docs/zenith-sessions/REQUIREMENTS-LEDGER.md). Consult all three user-supplied prompt inventories before each development/review cycle, check their fingerprints, and inspect the applicable original clauses. Update evidence after the cycle; an implemented feature is not automatically a tested or finished feature. These documents supplement the user's instructions and do not expand permission to access credentials, alter real sessions, or bypass platform protections.

**Zenith Macros** is a Windows desktop automation application written in **C# / .NET with WPF**.

### Core Subsystems

| Subsystem | Description |
|---|---|
| Input Recording | Captures keyboard/mouse via low-level Windows hooks |
| Input Playback | Replays recorded or scripted input |
| Global Hooks | Low-level keyboard/mouse hooks (WH_KEYBOARD_LL, WH_MOUSE_LL) |
| Screen Capture | Centralized, shared frame acquisition |
| Color Detection | Region-bounded pixel/color analysis |
| Image Recognition | Template matching via OpenCV |
| Conditional Rules | Boolean conditions that gate macro steps |
| Macro Execution | Deterministic step-by-step macro runner |
| WPF Visual Editor | Drag-and-drop macro authoring UI |
| AI Vision Providers | Pluggable AI backends (e.g., GPT-4V, Gemini Vision) |
| AI Action Validation | Parse -> Validate -> Safety -> Approval pipeline |
| User Approval Gate | Human-in-the-loop confirmation for AI actions |
| Audit Logging | Structured, async, machine-readable event log |
| API Key Storage | DPAPI-encrypted, never persisted in plaintext |
| Usage Tracking | Per-session and per-macro telemetry |
| Safety Controls | Panic stop, rate limits, region constraints |

---

## Architectural Layering

```
UI (WPF)
  |
Application / Controller
  |
Macro Engine
  |
Domain Interfaces
  |
Infrastructure (Windows APIs, OpenCV, AI Providers, SQLite, HTTP)
```

**Dependencies only flow downward.** The macro engine must never reference WPF types.
Infrastructure implementations must never be referenced directly from the engine — only through interfaces.

---

## Engineering Constitution (20 Rules)

### Rule 1 — UI / Engine Separation
The UI must never perform automation work directly.
All macro control, input simulation, screen capture, and AI interaction must go through the Application/Controller layer.

### Rule 2 — Engine / WPF Independence
The macro engine must never depend on WPF.
No `System.Windows`, `Dispatcher`, or WPF control types in engine or domain code.

### Rule 3 — Platform Isolation
Windows-specific APIs must be isolated behind infrastructure interfaces whenever practical.
P/Invoke, `user32.dll`, `gdi32.dll`, and Win32 structs belong in infrastructure, accessed via interfaces.

### Rule 4 — Single Input Pipeline
There must be one controlled input-simulation pipeline.
No subsystem may call `SendInput`, `mouse_event`, or `keybd_event` directly except through the designated input executor.

### Rule 5 — Single Screen Capture Pipeline
There must be one shared screen-capture pipeline.
Multiple consumers (color watchers, image matchers, AI vision) must share captured frames.
They must not independently start their own capture loops.

### Rule 6 — No Independent Watcher Capture Loops
Individual watchers must not create independent capture loops.
Watchers register interest and receive frames; they do not own capture infrastructure.

### Rule 7 — Allocation-Aware Hot Paths
High-frequency code must minimize allocations.
Avoid LINQ, closures, `string.Format`, boxing, and `params` arrays on hot paths.
Prefer object pools, stack allocation (`stackalloc`), `ArrayPool<T>`, and `Span<T>` where appropriate.

### Rule 8 — AI Output Is Untrusted
AI output is untrusted data and must pass through validation before execution.
The pipeline is always:

  AI Provider -> AI Response -> Parse -> Validate -> Safety Checks -> Approval Gate -> Action Executor -> Input Simulation

AI output must never bypass any stage.

Validate at minimum:
- Coordinates (within allowed region)
- Target window (matches expected)
- Action type (in allowed set)
- Text length (bounded)
- Execution mode (matches session policy)
- Safety limits (rate, region, repeatability)
- Runtime limits (total duration cap)

### Rule 9 — Replaceable AI Providers
AI providers must be replaceable without changing the macro engine.
All providers implement a common interface. Provider-specific types, API shapes, and error codes must not leak into shared code.

### Rule 10 — API Key Hygiene
API keys must never exist in:
- Macro files
- Log output
- Screenshots
- Source code
- Config files committed to version control

Keys are stored using **Windows DPAPI** encryption.
Decrypted keys live in memory only as long as necessary.
Keys are never passed as constructor arguments that might be captured in logs or dumps.

### Rule 11 — Panic / Emergency Stop Is Absolute
Panic / emergency termination must always take precedence over normal macro execution.
The emergency stop signal must be checked at every execution boundary.
It must never be maskable by a running macro, a wait, or an AI action.
Emergency stop and normal pause have clearly different semantics and implementations.

### Rule 12 — Explicit Cancellation and Shutdown
Every background operation must have an explicit cancellation and shutdown path.
Every `Task`, loop, timer, `Thread`, and background worker receives a `CancellationToken`.
Shutdown must be deterministic — no fire-and-forget without a tracked handle.
`async void` is forbidden except for framework-required event handlers.

### Rule 13 — Clear Resource Ownership
Every disposable or native resource must have a clear owner.
This applies to: `Mat` (OpenCV), bitmaps, screen-capture handles, file streams, SQLite connections,
HTTP clients, pooled buffers, Windows hooks, and event subscriptions.
The owner is responsible for disposal. Ownership transfers must be explicit.
Never return a pooled buffer to a pool while another component still holds a reference.

### Rule 14 — Prefer Event-Driven Over Polling
The system should prefer event-driven work over polling.
Use hooks, completion callbacks, `IObservable`, events, or `Channel<T>` before reaching for a polling loop.
When polling is necessary, use bounded sleep intervals, backpressure, and cancellation.

### Rule 15 — AI Is Optional
The application must remain fully usable when AI is unavailable.
AI features degrade gracefully — missing provider, quota exceeded, or network failure must not
crash or block the macro engine.

### Rule 16 — No Single Hard External Dependency
No single external provider should become a hard dependency of the architecture.
This applies to AI providers, cloud services, telemetry backends, and licensing servers.
Failure of any external service must have a defined fallback behavior.

### Rule 17 — Measurable Performance
Performance optimizations must be measurable and must not sacrifice correctness without an explicit
documented tradeoff.
Do not claim something is faster without benchmarks or a clear mechanical explanation.
Use profiling tools (dotTrace, PerfView, BenchmarkDotNet) for hot-path claims.

### Rule 18 — New Feature Checklist
Every new feature must be evaluated against:

- [ ] CPU usage — is it bounded?
- [ ] Memory usage — does it grow without bound?
- [ ] Allocations — are hot paths allocation-free?
- [ ] Thread usage — does it create unnecessary threads or timers?
- [ ] Cancellation — does every background path respect `CancellationToken`?
- [ ] Error handling — are failures surfaced correctly, not swallowed?
- [ ] Testability — can it be tested without a real screen, mouse, or keyboard?
- [ ] Security — does it touch keys, credentials, user data, or AI output?
- [ ] Backwards compatibility — does it break existing macros or saved state?

### Rule 19 — Justify New Dependencies
Before introducing a new NuGet or third-party dependency, evaluate whether the functionality can
be reasonably implemented with existing platform or .NET capabilities.
If a dependency is added, document: what it does, why the platform alternative was insufficient,
and what the removal/replacement path would be.

### Rule 20 — Refactor Migration Strategy Required
Before performing a large refactor, explain:
- What is changing and why
- The migration strategy (incremental vs. big-bang)
- Which existing behaviors might regress
- How regressions will be detected
- The rollback plan

No large refactor begins without this analysis being reviewed.

---

## Subsystem-Specific Rules

### Hook Callbacks (Input Recording)
Hook callbacks must be **extremely lightweight**.
Never perform inside a hook callback:
- UI work or `Dispatcher.Invoke`
- Disk I/O or logging
- Image processing
- Network calls
- Blocking waits or locks

Capture only the minimum data. Enqueue to a `Channel<T>` or lock-free queue. Return immediately.

### Screen Capture
- Capture is centralized. One pipeline, multiple consumers via subscription/frame distribution.
- Use region-based capture when full-screen is unnecessary.
- Reuse buffers. Avoid unnecessary copies.
- Use latest-frame semantics where historical frames provide no value.
- Adaptive capture frequency — slow down when no consumers need fresh frames.

### Image Recognition
- Cache decoded templates. Never reload from disk on every match attempt.
- Only perform matching when a macro step actually requires it.
- Use the smallest sufficient search region — never scan full screen for a small template.

### Color Detection
- Process only the required pixel region.
- Never scan millions of pixels when a single pixel or small rect is sufficient.

### Macro Execution
- Execution must be deterministic and easy to reason about.
- Safety controls live at the execution boundary, not scattered through steps.
- `Stop`, `Pause`, and `Emergency Stop` have distinct implementations and semantics.

### Audit Logging
- Never block macro execution on logging.
- Use async/background persistence (e.g., `Channel<T>` -> background writer).
- Logs are structured and machine-readable (JSON preferred).
- Logs never contain secrets, API keys, or raw AI responses containing sensitive data.

---

## Error Handling Standards

Every error must answer:
- What operation failed?
- Which subsystem failed?
- Why did it fail?
- Can the operation be retried?
- Should the macro stop?
- Should the user be notified?

| Error Class | Meaning |
|---|---|
| OperationalFailure | Expected, recoverable — retry or skip |
| ConfigurationError | Bad setup — surface to user, halt feature |
| ProgrammingError | Bug — log with full context, assert in debug |
| Cancellation | Expected shutdown — do not log as error |
| SafetyTermination | Emergency stop triggered — log, halt, notify |

Never catch an exception merely to rethrow it. Add context or don't catch it.

---

## Agent Behavioral Contract

Any agent working in this repository must:

1. **Read this file first** before making any changes.
2. **Name the rule** when rejecting or modifying a design based on a constitution rule.
3. **Apply the Rule 18 checklist** to every new feature, not as an afterthought.
4. **Require a migration strategy** (Rule 20) before beginning any large refactor.
5. **Never silently swallow exceptions** or skip error handling for brevity.
6. **Never hardcode, log, or expose API keys** under any circumstances.
7. **Push back** on designs that violate these rules even if the user requests them.
8. **Ask for clarification** before large changes rather than assume scope.
9. **Prefer the smallest correct change** over a comprehensive rewrite.
10. **Never claim code was tested when it was not.**

---

*Last updated: 2026-09-11*
*Maintained by: Engineering lead and all contributing agents.*
