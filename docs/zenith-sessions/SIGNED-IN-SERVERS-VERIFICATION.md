# D3 signed-in server browsing and owned-server rename

November 8, 2026; checkpoint `0.61-signedservers`. Scope: R-launch-12 / R-api-8 and the approved listing/rename portion of R-api-14. Private joining (R-launch-7) is a separate follow-up. This increment does not establish live API compatibility or release readiness.

## Behavior

The existing anonymous server directory stays anonymous. A collapsed Signed-in server options section offers three explicit requests for the selected account: Recommended, lowest reported latency, and owned private servers. Every fetch requests at most 25 rows and replaces the previous page. Paging is explicit, bound to the same account/place/order, rejects repeated cursors and stops at 100 cursor entries. Reported ping is sorted within the returned page, with unknown values last; it is not a measured client latency or worldwide ranking. Friend counts are optional and stay Unknown when absent. Selecting a row does not launch; Use pins its exact public job as a requested destination.

Private rows are included only when their owner ID equals the selected verified player. Names and occupancy stay in memory. Private-list `accessCode` is a different token from a checked share-link code: it, `joinCode` and `link` are deliberately not copied into metadata or treated as a public job ID. Joining will use independently checked protected saved-link records. This list does not save, purchase, renew, regenerate a code, change access, or alter subscriptions.

Rename requires one owned row, a short plain name and a single-use confirmation naming the account, server ID, old name and new name. Equal-looking forged reviews, changed accounts/generations/rows/names, stops and replay are refused. The HTTP body contains only `name`. A successful response says the request was accepted and asks for a fresh listing to confirm the remote name. Existing saved private-link labels are not rewritten. The UI explains that subscription/purchase actions must be performed on Roblox and offers the existing public game-page opener.

## Authority and bounds

The service refactors D2's request lifecycle into a private shared helper, preserving its tests. Each signed request pins a saved profile/user/generation, writes its durable SignInRequired fence before sending, verifies any returned replacement against the same player and conditionally commits before publishing data. Rename validates the actor before the mutation as well as afterwards. Another writer's newer authorization cannot be replaced. A directory 401 plus successful authenticated-user confirmation remains an unavailable directory answer, not invented expiry. Ambiguous failures leave authority fenced; observations can stale pending launch reviews because generations advance.

All HTTP endpoints are fixed Roblox hosts/paths. Production redirects, automatic cookie jars and ambient credentials are disabled. The user agent is `ZenithSessions/1.0`; no forged browser Origin/Referer is sent. Each call has a 20-second deadline, 16-KiB response-header limit, 256-KiB body cap and depth-12 JSON cap. Duplicate/ambiguous fields, wrong numeric types, excess rows, nonpublic public rows and unsupported response media are refused. Mutable transport/request buffers are cleared. Only one explicit CSRF refusal permits a mutation retry, and never after a challenge or cookie rotation. Retry-After and exhausted-window delays are carried through without shortening long provider deadlines. There is no automatic retry or polling worker.

Changing account, clearing selection, changing place, leaving Join a game, Cancel-and-clear, Disconnect, Lock, Panic and exit cancel/fence observations and clear private data. A temporary WPF selection reset while publishing refreshed account rows is distinguished from a real deselection. Late responses cannot repopulate a stopped owner. Shutdown/removal await the added commands before their client or vault is disposed. Added code uses existing .NET capabilities; no dependency or persistent format changed.

Rule 18: CPU and allocations are per explicit bounded request; one page and a bounded cursor set are retained; no extra timer/thread or capture loop; cancellation is shared with the existing sign-in owner; fixed errors and typed observations exclude secrets from diagnostics; synthetic clients exercise the behavior; anonymous browsing and existing account controls remain under regression coverage. Private results have no persistence/export contract. This is a narrow additive feature, with a private helper extraction rather than a new credential pipeline.

## Evidence

- `artifacts/sessions-tests-0.61-signedservers/primitive-red.trx`: two malformed numeric-field cases failed with an unexpected JSON primitive exception before the parser fix; one companion case passed. Earlier compile-only attempts exposed an internal name-rule reference and a test parenthesis typo; neither counts as a red behavioral run.
- `server-protocol.trx`: 61 passed, zero failures/skips.
- `server-binding.trx`: 107 passed, zero failures/skips.
- `server-ownership.trx`: 74 passed, zero failures/skips.
- `server-reviewed.trx`: 607 passed, zero failures/skips before the final extra guards.
- **`server-final.trx`: 616 passed, zero failures/skips**, 56 seconds. Includes saved-session/account-data/friendship regressions, server parsers/browser, cancellation/generation races, Shell ownership, and existing production-XAML account workspace checks. These use synthetic sessions/HTTP/process fixtures; no real sign-in, real Roblox client or account request ran. Paid cloud AI tests were explicitly excluded; dotnet used `-m:2 -nr:false --no-restore`.

Primary endpoint index: [Roblox games domain](https://create.roblox.com/docs/cloud/reference/domains/games) and [Roblox private-server features](https://create.roblox.com/docs/cloud/reference/features/private-servers), read November 8. The anonymous legacy games Swagger `/docs/json/v1` and `/v2` URLs both returned 404. The authorized local RoALT checkout was inspected for endpoint/query/response-field facts only; no source or UI text was copied. Its lenient private parsing was not adopted. Inference: exact current signed v2 ordering and private response shapes require attended verification, since primary documentation does not fully specify those responses.

## Remaining validation

An owner-attended trial must verify selected-account Recommended/BestLatency results, friend-count availability, private owner filtering, page cursors, account rotation/challenge/429 responses and a reversible rename of an owned test server. Inspect the actual private page size; a response exceeding 25 is refused rather than silently truncated. Confirm the new collapsed controls at real DPI/keyboard settings and the public external-browser link. Public/public-signed request isolation is fake-handler tested, not a live account trial. Private joining, integrated Release/self-check, full merged tests and final risk review remain separate work. **LiveTestReady=false.**
