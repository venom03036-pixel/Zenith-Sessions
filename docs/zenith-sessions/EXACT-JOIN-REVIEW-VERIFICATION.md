# Exact join review and reported server verification

Zenith Sessions Personal 0.48 development preview. LiveTestReady and ReleaseReady are false.

## What changed

- Choose server in Accounts opens Join a game even if the profile has no preferred game. It preserves the destination, current text and queue. Use preferred game remains in the account menu.
- Review reported server and Copy reported server link require a fresh complete process inventory, one matching PID and start time, and an applicable fresh log correlation and lease. The registry's existing five-minute lifetime for the reported place/job event still applies. Review fills the request field for Search / Use. The source remains diagnostic evidence.
- The selected-account trial review is next to the destination. It shows the exact expected signed-in Roblox user, place, job and selected installation. The confirmation is used once. Changes or pending destination text require a new review, and durable profile revision and selected grant metadata are checked before queueing.
- The Guide follows the updated routes. The existing Zenith sidebar, player headshots, ordered experience gallery and in-app Windows tiles remain covered by the manager regression suite.

## Verification

- **4,072 passed** in the source build and **4,072 passed** against the exact staged first-party binaries. All 66 new cases passed, including 24 trial review, 23 log projection, nine reported destination handoff and ten Accounts navigation cases.
- **453 affected cases passed twice**. **42 packaged checks passed twice**. The forced source and package builds reported zero warnings and errors.
- All 29 staged files were accounted for by the manifest and the manifest file itself. SHA-256 and filename checks found no mismatch or private data filenames. This audit does not replace full content review.
- The initial staging directory was busy or unwritable and its files were preserved. Packaging used the candidate directory. An outdated path in the verification script interrupted the first attempt at the exact-binary retest, so the script was corrected and the retest rerun against the candidate.
- Staged app assembly SHA-256: 45EAE151A7DF55CC8BC59F62D3D28E5396AE6309952486FCFE3FFC42875D7805.
- Machine evidence: artifacts/sessions-manager-verification-0.48.json. SHA-256: 53111F4353A363D9E1F4E5483412E7DAD4AF5C092BB2FBB8683C4D888226D299.

## Remaining acceptance

The app keeps reported local client data separate from account verification and confirmed arrival. This cycle tested no real Roblox account or client. Background input and images, verified account-specific reconnect and arrival, full desktop interaction and performance, and the sixteen-client and competitive benchmarks remain open. No installed edition or real session changed.

[Design](EXACT-JOIN-REVIEW-DESIGN.md)