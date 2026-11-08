# Round-4 remaining work

This replaces the historical handoff's “24 blocked / 19 implemented” snapshot. Original inputs are preserved at [round4/HANDOFF-REMAINING-original.md](round4/HANDOFF-REMAINING-original.md), [work-items-r4.json](round4/work-items-r4.json) and [lanes-r4.json](round4/lanes-r4.json).

The complete **93-ID** ledger is [ROUND4-STATUS.md](ROUND4-STATUS.md) / [ROUND4-STATUS.json](ROUND4-STATUS.json). Exact live steps and owner questions are in [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md). No original item disappears.

Offline acceptance checklist (verify the external records for this commit):

1. Inspect the repair records and [final review](ROUND4-FINAL-REVIEW.md), including settings/startup stop precedence and elevated-helper ownership. Each important repair requires genuine failing-before evidence and merged passing results.
2. Check both merged evidence audits; M-L1a-6's historical replay is complete, and every Verified item's named test must pass on the final merged build.
3. Verify the independent whole-change review and immutable patch-risk assessment identify the final commit.
4. Confirm every lane is merged, promote the final reviewed candidate to main and pass all six suites, zero-warning Release builds and all 43 packaged stages **twice on that exact commit**.
5. List any remaining minor findings and owner decisions. Only then give the owner the staged build and precise attended trial sequence.

Final item states are 40 Verified, 40 Built-needs-live-trial, 3 Excluded and 10 Needs-decision. Offline acceptance additionally requires the evidence above. No real account/client, paid cloud AI test, installer or release publication is authorized by this offline handoff.

Read [HANDOFF.md](HANDOFF.md) for paths/commands, [QUALITY_STATUS.md](QUALITY_STATUS.md) for acceptance rules, and [ROALT-COMPARISON.md](ROALT-COMPARISON.md) for actual capability boundaries.
