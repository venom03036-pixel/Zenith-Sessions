# CPU-cap recovery record repair (0.72)

Reviewed baseline: 388792b0da4b16e6099b66aa931272b6547163ff. Scope: CAP-14-01, CAP-14-03 and PROD-QA-03. Source inventory fingerprints matched the reviewed ledger before this cycle.

## Findings and repaired behavior

A failed change from an existing CPU cap to another cap deleted the exact process recovery record even though Windows retained the original cap. Also, a held job with no current cap (after a failed first cap or a successful clear) was reused before saving a new record. If that late save failed, the process could remain capped with no record for the next Zenith start.

Every nonzero cap now requires its exact PID/start record before the native rate call, including a reused inert job. An I/O or capacity failure refuses the call. A failed native rate change removes the record only when a readback confirms no rate control; an old cap or unknown readback retains the record. Successful clear still removes it, and the same held job remains available for later reuse. No process termination or new native API was added.

The JSON ledger previously treated UTC minus uptime as a boot identity. A same-boot wall-clock correction over two minutes discarded records for still-running capped clients. The ledger now retains both stamped and legacy exact-instance records regardless of the clock estimate. Existing query-only PID/start/liveness reconciliation retires exited or reused instances. If that query is denied, uncertainty is reported and the record remains. The schema and optional bootUtc metadata remain compatible; bootUtc no longer authorizes discarding a record.

The prior test that interpreted a changed UTC estimate plus denied query as a proven reboot encoded the defect. It now expects a retained record and NotChecked. A separate restart control proves that an actually different process start is queried without mutation, retired, and removed from the on-disk ledger.

## Executed evidence

All files below are in artifacts/sessions-tests-0.72-cpuowners/ in the D3 worktree:

- cpu-owners-red.trx: **10 failed, 0 passed, 0 skipped** against unchanged production source. Covers existing cap retention, both inert-job origins with I/O/capacity refusal, record-before-native-call order, forward/backward clock changes and legacy records.
- cpu-owners-readback-red.trx: **3 failed, 0 passed, 0 skipped** against unchanged production source. Covers unknown readback retention, denied exact-instance query after a changed clock estimate, and mandatory actual-identity reconciliation after restart.
- cpu-owners-green.trx: **91 passed, 0 failed, 0 skipped** after the repair, including the full fake-native process-priority owner and presentation suites. The existing failed-first-cap/inert-job reuse, explicit restore, exit cleanup, foreign-job refusal and exact PID reuse controls remain exercised.

The build/test command used Release, --no-restore, -m:2 and -nr:false with only ClientProcessPriorityTests and ClientProcessPriorityPresentationTests selected. The paid cloud test was excluded. The TRX inventory was checked with Read-ZenithTestResults.ps1, and git diff --check passed.

## Scope and limits

The native API was a fake and ledger files used temporary synthetic fixtures. No real Roblox client, real process cap, cmd helper, account, sign-in, UAC, reboot, wall-clock change, installer or paid service ran. These checks prove the record ordering and reconciliation decisions, not live CPU performance or native compatibility. Root integration, immutable final full gates and attended client trials remain separate acceptance steps. Release readiness remains false.
