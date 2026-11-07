# Unified game search verification

Cycle0.40, November6,2026. Personal development preview only; **LiveTestReady=false, ReleaseReady=false**. No installed edition, real account, Roblox client, owner identity, saved authorization, Linux environment or published game changed. No reference executable was downloaded or executed, and no RoALT/Froststrap code was copied.

## Implemented behavior

The existing destination field now accepts a plain game name as well as supported Roblox links and positive Int64 place IDs. Name lookup presents selectable public results with root-place/universe IDs, creator/count/promotion facts where available, and explicit unknowns where absent. No result is selected automatically. Choosing one uses the existing browser boundary and loads the existing details/gallery. Direct subplace and public JobId requests retain their exact target. Account metadata/preferences are not silently changed.

One optional Core search interface and a Windows partial adapter reuse the existing anonymous HTTP owner, two shared request slots,64-KiB JSON limit, depth8, duplicate-field validation and cooldown. At most50 results are retained from at most1,024 parsed game entries; truncation is explicit. One user action requests one page, with exact query/session/token context. The presentation permits at most ten manually requested pages and fences repeated/cycling continuations. No retry, polling, enrichment fan-out, new cache, timer or dependency was added.

The controller owns one tracked operation. Typing, replacement, leaving the page, hiding the window, Lock, Panic and shutdown invalidate stale work; they do not start a request. Cancellation reaches a search-owned details request, while cancellation of a wait for separately owned manual details does not cancel that unrelated request. Disposal still joins actual work before releasing borrowed HTTP owners. Completion publishes live control availability and stopped wording even when a provider ignores cancellation.

The production layout uses the existing Zenith theme/resources and recycled rows. After choosing a destination, empty result controls collapse so details/pictures are nearer the top. An empty page with a continuation still exposes **More results**. The current request and status remain visible, and the redundant destination label was removed.

## Exact final evidence

- Final App SHA256: `08399F2F6E6B94EAE79B293306905ADAA4579E7518756D0AC8D6F7481BDB49F3`.
- Machine evidence: `artifacts/sessions-manager-verification-0.40.json`, SHA256 `BD6FFF2380E1B68231C12D4889C6B7C454AF2ECA26EA6A2178AD10768F1ABF0D`.
- **3,806/3,806** source tests and **3,806/3,806** against the exact staged first-party assemblies.
- **381/381** affected staged checks twice; the earlier209-case source check passed after the shutdown correction.
- **42/42** packaged checks twice, including the actual packaged search parser under a synthetic anonymous transport. The final repeat took7,998ms wall time; the existing budgets were not enlarged.
- **135 new tests:**88 contract/transport and47 controller, Shell and presentation cases. Tests cover wide IDs, missing creator/count, malformed/oversized/duplicate/contradictory data, shared slots/cooldowns, private-input isolation, stale/foreign selection, direct subplace/job preservation, actual details calls, cancellation/Lock/Panic/hide/disposal, real WPF button bindings, token cycles and the manual-page budget.
- Final forced builds produced zero warnings/errors. The28 staged files match the27 manifest entries plus the manifest, with no extra/reparse/private-filename matches. This is a filename/hash audit, not a full content/security audit.

The exact staged directory performed anonymous live searches for **Fisch** and **Anime Card Clash**. Each first page returned40 results; one explicit second Fisch page returned40 results with zero repeated universe IDs. The expected first root-place IDs were16732694052 and110829983956014. The parser retained39 unknown creators per observed first page instead of inventing them. These point-in-time observations are recorded in `artifacts/sessions-search-0.40/staged-public-search.json` and pinned by machine evidence. They are public search evidence, not sign-in, account-specific launch, access, arrival, compatibility or performance proof.

The first-party endpoint is named in Roblox's [legacy endpoint migration announcement](https://devforum.roblox.com/t/official-list-of-deprecated-web-endpoints/62889/59). Its current grouped response/paging schema is directly observed; a stable published schema contract was not verified. A separate full-body observation also parsed the complete response at depth8 without retaining its body. No proxy or authenticated fallback was added.

## Rendered checks and retained corrections

Thirty-two final renders use the actual staged production BAML and Shell search owner with a synthetic directory and isolated16-profile library: results/chosen states, Graphite Mint/Amber Night,1320×840/960×640 logical units, and1/1.25/1.5/2 render scales. The root inspected final wide results and chosen layouts, plus the narrow Amber baseline. These are offscreen renders, not physical-monitor/DPI or full interactive/performance acceptance. No computer-control actions were attempted this cycle.

Independent source review caught completion-binding, repeated-page, cancellation-feedback and borrowed-owner issues. Root review caught a destination reference-equality error: the expected values must match semantically before pinning the browser's actual object across awaits. The successful-path tests now assert real metadata requests, not just an assigned target.

The first compile failed because a new test fixture lacked its HTTP namespace import; this was corrected without a product change. The first full regression passed3,804/3,805 and caught delayed shutdown cancellation. The new operation was fenced immediately but its token was not cancelled until other owners finished. A synchronous cancel before the first shutdown await fixed that defect; the later joined disposal remains. The failing TRX is retained. Rendering then revealed wasted empty search controls after selection; the before image/report and passing pre-compaction test/package evidence are retained, followed by the full final verification above.

## Remaining work

[The local Froststrap/RoALT launch audit](FROSTSTRAP-LAUNCH-AUDIT.md) separates ordinary native handoff from excluded shared-login replacement, singleton takeover and handle mechanisms. Zenith already has an explicit guarded native trial; the inspected artifacts did not establish a failed real launch. No new launcher was chosen on that assumption.

Protected private/share/reserved destinations and their end-to-end providers remain unavailable. Their links are never sent as game names or converted into public fallback targets. Clearer Sessions/launch navigation remains partial because the field still lives in the existing Automation page. The old empty-gallery placeholder can still tell users to load details after an empty result; this is a named next UI repair. Real account/PID association, native handoff, verified arrival/reconnect, per-account background macros/broadcasting, sixteen-client operation, competitive measurements, complete desktop acceptance and protected final packaging remain unfinished. Historical diagnostics tiny-file drain stress latency is still unresolved evidence. These items are not treated as completed by this increment.
