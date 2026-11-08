# Fresh vault mode on dashboard Unlock

The dashboard Unlock command now reads the current protected vault mode before choosing a password prompt or reopening the dashboard. Previously it trusted the shell's cached mode: another vault owner could enable protection while the shell still showed Disabled, or disable protection while the shell still showed Locked. Actual credential access independently remained fail closed; this repair corrects the dashboard and prompt decision.

Scope: the two shell vault/command source files, a new offline regression file, and synchronization at existing test callers. No vault format, key handling, password migration, UI layout, launch behavior or network endpoint changed. The base is integration commit `643cf0e`.

## Ownership and behavior

- `Unlock` is an owned `AsyncCommand`. Its captured click epoch precedes `CanExecuteChanged`, so a reentrant Lock, Panic or cancellation in that notification invalidates the original click before it can admit work.
- The mode read uses `GetPasswordStatusAsync`, which refreshes the protected control under the existing vault writer lease. The read has one tracked owner, linked cancellation and a publication barrier. Repeated clicks and overlapping password operations cannot replace it.
- Lock, Panic, cancellation and exit invalidate the owner. The result is rechecked after state publication and after releasing Busy, before any prompt or dashboard unlock. Disposal joins the mode operation and the full command execution.
- Busy is released before raising the password prompt event. This preserves the real window's synchronous `ReviewVaultPassword` admission. A newer password owner admitted during publication takes precedence over the completed read.
- A fresh Locked mode requests Unlock; MigrationRequired requests the existing Enable/Resume path. Disabled or an already unlocked mode can reopen the dashboard. A fresh unavailable-control result retains the existing metadata/recovery route, while protected credential access stays unavailable. It does not reuse a stale Locked value in that recovery decision.

## Genuine failing-before evidence

Artifacts are retained under `wt/D4/artifacts/sessions-tests-0.74-vault-unlockmode/` and are intentionally outside tracked source. The new regression helper accepts both the old synchronous command and the new asynchronous command, so the initial baseline compiled and executed normally.

1. On unchanged production base `643cf0e`, `--filter FullyQualifiedName~DashboardUnlock` produced **8 failed, 1 passed, 0 skipped** in `unlock-mode-before.trx`. The external-enable case failed because `IsLocked` became false; the external-disable case failed because it remained true. Held-lease ownership, stale unavailable-mode presentation and Panic/cancel publication assertions also failed. The Lock publication control already passed; it is not claimed as a baseline failure.
2. On the initial asynchronous draft, before capturing the click epoch, `--filter FullyQualifiedName~DashboardUnlockStopAtCommandNotification` produced **3 failed, 0 passed, 0 skipped** in `unlock-command-before.trx`. Panic, Lock and cancel all ran in the command notification, but the old click admitted work afterward and reopened the dashboard. All three failed the intended `IsLocked` assertion. The admission snapshot and comparison were added afterward.

These are behavioral assertion failures, not compilation failures or documentation-only evidence. The unavailable-control regression was subsequently expanded from a single case to both previously protected and unprotected controls; the first artifact therefore records the earlier parameterless name.

| Artifact | SHA-256 |
| --- | --- |
| `unlock-mode-before.trx` | `87D368E99D75BB585A00E9B9E5958C8661D9D14B8F98BE0CA46B19182A439C5E` |
| `unlock-command-before.trx` | `BF0AAB9CC590F0BDCC9DB17462AD8711EDAC58758E7A711EC294957A89CC827C` |
| `unlock-mode-final.trx` | `00F91565C5C17D9CA61AA17BF894F9F7DE8C2EF9AE82940B4E0C71ECDD85FADF` |

## Final focused verification

The final affected-class run passed **873 tests, 0 failed, 0 skipped**, recorded in `unlock-mode-final.trx`. It covers all existing caller classes touched for asynchronous synchronization, the new Unlock regressions, `VaultMasterPasswordTests` and `StopOperationsFailSafeTests`. The exact 19-class filter is retained in `affected-filter.txt` beside the TRX. Command:

```powershell
$filter = (Get-Content artifacts/sessions-tests-0.74-vault-unlockmode/affected-filter.txt -Raw).Trim()
dotnet test tests/Zenith.Sessions.Tests/Zenith.Sessions.Tests.csproj -c Release --no-restore --filter $filter --logger 'trx;LogFileName=unlock-mode-final.trx' --results-directory artifacts/sessions-tests-0.74-vault-unlockmode -m:2 -nr:false
```

The new tests use actual temporary vault controls and synthetic passwords. They check external enable/disable, real prompt review-token admission, command-notification stops, publication stops, a held vault writer lease, duplicate-click refusal, unavailable-control recovery, exit cancellation/join, and a newer owner admitted during publication. Existing caller changes add exactly 43 `await Unlock.Execution` statements across 30 files; all original assertions and fixtures remain. `IdleLockTests` has two such awaits; `StopOperationsFailSafeTests` is unchanged.

Requirement checkpoints `before-0.74-vault-unlockmode.json` and `after-0.74-vault-unlockmode.json` cover `VA-07-03`, `VA-06-03` and `CAP-13-04`, with all three user-prompt fingerprints matched. The After record binds both failing artifacts and the final passing artifact. The scoped action is bounded to one asynchronous read; it adds no polling loop, dependency, worker thread or persistent state migration. Stop ownership and exception reporting remain explicit under constitution Rules 11–13 and 18.

No real account, credential, sign-in, Roblox process, external endpoint or paid AI test was used. This is focused offline regression evidence; the merged full-suite pair, packaging and attended live acceptance remain the integration owner's separate gates.
