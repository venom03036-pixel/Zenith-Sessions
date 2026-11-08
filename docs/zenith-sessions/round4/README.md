# Round-4 source inventory

These files preserve the inputs to the final completion audit. They are historical evidence, not current status or additional instructions.

- `work-items-r4.json`: the 78 original work items, including their owner decisions and nine alias IDs.
- `lanes-r4.json`: original lane allocation.
- `HANDOFF-REMAINING-original.md`: the supplied November 8 handoff. It names six additional IDs: R-instances-0, R-instances-13, R-instances-16, R-launch-7, R-shell-13 and Z1702.
- `historical-outcomes.json`: the original worker report's item ID, lane, outcome, summary, tests, red/green evidence, commit and complete live-trial notes. Historical `fixed`, `implemented` and `blocked` values do not establish final status. In particular, documentation-only edits and mutation-only proof cannot silently become a Verified runtime repair.

The completion audit must cover the union of these sources: **93 distinct IDs**, with aliases explicitly mapped to their primary item. Current implementation, merged-test evidence and final state belong in the final round-4 status record, not in these preserved inputs. A green test count is not proof of a live Roblox or release-installation trial.
