# Zenith Sessions and the RoALT reference

This comparison describes the current Zenith implementation and its limits. The detailed earlier static comparison is retained in [round4/history/ROALT-COMPARISON.md](round4/history/ROALT-COMPARISON.md); its old completion claims are not current acceptance evidence. RoALT was a read-only source reference. No RoALT code or branding was imported.

The user's chosen direction is Zenith branding/themes, the existing left sidebar, avatar headshots, matching game media and an evenly tiled window overview. UI work remains separate; this completion pass makes only needed corrections/wiring.

| Capability observed in the reference | Current Zenith behavior | Acceptance still required |
|---|---|---|
| Saved account sign-ins | Roblox-hosted sign-in with isolated WebView2 lifecycle; identity-checked encrypted sessions, no OAuth dependency | Attended real provider sign-in, rotation, cancellation and cleanup |
| Separate account launches | Standard-player Official-page and Direct-ticket modes; one-use preparation and held executable identity; recorded qualification per actual mode/executable | Correct player/destination for each client; simultaneous-client compatibility without singleton workarounds |
| Launch after an executable update | Old verification is insufficient for changed bytes/identity | A new attended trial for the selected executable |
| Bootstrapper detection | Discovery and publisher inspection remain available | Launching is excluded from the first build by owner choice |
| Account protection/backup | Windows protection, optional password-wrapped vault key, fresh protected-state check on dashboard Unlock and password-sealed portable archive; fresh imported identities | Separate-Windows-account/PC import, migration interruption, password recovery |
| Player and game images | Public avatar headshots with bounded Pending retry; universe-correlated icons/media and navigation | Real thumbnail availability, pending/blocked media and matching game gallery |
| Public servers | Public pages, filters, pagination and exact reviewed destination | Live endpoint shapes, occupancy changes, unavailable/challenged results |
| Signed-in server preferences/private lists | Account-owned Recommended/latency/private listing and reviewed name-only rename | Attended account requests; no purchase/renewal/access edits |
| Private-server joining | Distinct, explicit single-account Official-page trial using a protected saved reference | Owner-attended private join, revoked link, emitted protocol and cancellation; no public fallback |
| Window management | Owned-client observation, focus/close/size/layout, tiled overview and bounded previews | Real window rendering, DPI/minimum sizes, interaction/restore and stale identity |
| Resources and process controls | CPU/priority/working-set/audio/upload controls use bounded evidence and explicit authority; startup CPU recovery reports possible caps rather than inferring an unreadable rate | Windows/client-specific behavior, foreign jobs, late elevation and recovery |
| Background macros/broadcast/keep-awake | Input ownership and keep-awake policy/runner have synthetic tests | Production target-directed transport is absent; R-instances-17/Z1702 require owner decision, then isolated input qualification |
| Automatic rejoin/restart | No production automatic-recovery coordinator | R-instances-13 policy decision; attended recovery qualification if implemented |
| Discord integration | Optional, explicitly configured alerts/presence with stop and privacy controls | Attended provider/pipe/webhook behavior; no messages sent in this completion pass |
| Public-network and shutdown controls | Saved opt-in network hold/lock; explicit shutdown scheduling/cancellation and cleanup ownership | Windows network changes and attended scheduled-shutdown/cancel trial in a disposable environment |
| Updates and installer | Signed staging, apply-time re-verification, opt-in owner, explicit next-start approval, per-user scripts and rollback | Production host/key/publisher decision plus clean-guest install/update/uninstall acceptance |
| Language support | English catalog with the owner-approved scoped migration and RTL/pseudo tests | Other page translations remain follow-up scope; no full multilingual claim |

Zenith does not duplicate installations per account as an assumed isolation mechanism. It starts native player processes with separate prepared account authority; correct live account selection and multiple concurrent clients must be demonstrated. A recorded trial is bounded evidence, not a guarantee about every later client.

The following reference techniques remain excluded: browser-secret scraping, injected or modified clients, FFlag/client-setting rewrites, singleton-enforcement workarounds, shared Roblox sign-in file locks, browser identity spoofing and unsupported sub-minimum window forcing. Moving the data folder and buying private servers are outside the agreed scope.

No claim of parity, speed superiority or working live compatibility follows from this table. The authoritative 93-ID state ledger is [ROUND4-STATUS.md](ROUND4-STATUS.md); exact trials/decisions are in [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md), and merged validation is tracked by [QUALITY_STATUS.md](QUALITY_STATUS.md).
