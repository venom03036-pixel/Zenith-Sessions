# Startup activation and Panic review repair

Baseline: `c256e7533ba71a901b8464ea438b939cedbe085f`, following the [delayed settings-save repair](SETTINGS-STOP-REVIEW-REPAIRS.md). The follow-up traced the same activation boundary through startup rather than Save settings.

Initialization awaited the settings store, then later treated each saved opt-in as a fresh activation. Panic while the settings read was pending could be overwritten by startup callbacks. Four real-Shell regressions held the actual `SettingsStore` writer, began initialization, invoked Panic, released the read and completed initialization. They used saved opt-ins plus fake Roblox release, targeted-input and Discord transports. Before repair, all four failed: one public release check occurred, two F13 commands were accepted by the synthetic input transport, and the Discord alert and presence owners both restarted.

Startup activation now belongs to the shell's original settings-activation generation (zero), rather than acquiring new authority when the window's Loaded event arrives. A prior Panic/Lock invalidates startup's ability to clear the stopped state. Each affected activation boundary checks that authority. Roblox's automatic update timer is armed after the initial public-network guard, alongside the release updater's existing admission order. Account metadata, ordinary telemetry, settings loading and event-driven network observation still initialize. The keep-awake regression explicitly checks that the shell becomes ready and shows its synthetic monitored session after Panic.

Discord alerts retain their established distinction: Lock permits alert monitoring; Panic stops it. The callback therefore always reconciles the saved monitoring preference, but may clear a Panic stop only with current activation authority. Two held-startup/held-save Lock controls caught an overbroad callback fence in the intermediate repair; both passed after narrowing the fence to clearing Panic. Discord presence still respects its existing locked-state check and explicit Unlock behavior. Existing explicit Resume and later off/on settings actions remain available.

## Evidence

All runs are Release, `--no-restore -m:2 -nr:false`, with successful compilation.

| Artifact under `artifacts/sessions-tests-0.60-d9startepoch/` | Result |
| --- | --- |
| `start-stop-before.trx` | 4 failed, 0 passed, 0 skipped against `c256e75`; behavioral failures, not compiler failures |
| `lock-monitoring-control-before.trx` | 2 failed, 0 passed, 0 skipped against the intermediate activation fence; isolated the existing Lock-monitoring behavior that must remain |
| `start-stop-final.trx` | 344 passed, 0 failed, 0 skipped; affected update, keep-awake, sound/preview, Discord, settings-recovery, fail-safe stop, release-updater startup and public-network suites |

The final suite includes an additional startup boundary: Panic before `InitializeAsync`/Loaded begins also prevents the later automatic update check. Earlier exploratory green runs (`start-stop-after.trx`, 341 cases; `start-stop-boundaries.trx`, 5 cases) predate the two Lock controls and are superseded by the 344-case final run, not added to its total.

SHA-256:

- Startup red: `74DB07D00F8627030A573EF05BDA504C8A86D1A34CBCC752E621A7B078D42BB9`.
- Lock control red: `2B8E783E43DBCEA09B1EC763C7F6A15FC72AE1A0F87553920F9F1E28155175FD`.

Before/after checkpoint `0.60-d9startepoch` verifies all three original prompt fingerprints and scopes `PROD-QA-07`, `CAP-08-06`, `PROD-CORE-03`. Its after record hashes the three principal artifacts above.

Rules 11/12/18: no new worker, timer, network endpoint, dependency, persisted field or input path. The change only constrains existing owners' activation authority and preserves monitoring. No real Roblox client/account, external Discord communication, installer, administrator prompt or paid cloud test was used. Offline success does not prove native integration or release readiness.
