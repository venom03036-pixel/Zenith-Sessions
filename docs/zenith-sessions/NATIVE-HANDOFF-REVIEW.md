# Native ticket handoff evidence checkpoint

Reviewed November 5, 2026. This checkpoint concerns the saved Roblox-session flow, independently of OAuth. No authenticated request, ticket issuance/redemption, native process launch, reference executable/script, credential store or real account was exercised. Only this evidence document was added; no launch-ticket client or placeholder launch capability was implemented.

The three original prompt fingerprints match the requirements ledger. F §17.6 and A §17.6 explicitly permit a documented short-lived provider handoff while forbidding long-lived login material in launch arguments, URLs, environment, clipboard and diagnostics. They also require account binding and observed-session verification. Those clauses do not prohibit every native ticket mechanism; they require the mechanism to be established before use. The master prompt's supported-launch and no-protection-bypass restrictions remain applicable.

## Evidence obtained

The current public [Roblox Authentication API v1 schema](https://prod.docsiteassets.roblox.com/assets/en-us/cloud/legacy/auth/v1.json), retrieved through the web tool, declares HTTPS host `auth.roblox.com`. Its `/v1/client-assertion` GET has a successful object response containing a string `clientAssertion`, disallows additional properties in that response model, and documents an authorization-denied 401. The reviewed schema has no `authentication-ticket` path, including issuance or redemption. This is an omission in the reviewed public schema, not proof that the service endpoint is absent or prohibited. This bounded evidence establishes the assertion shape but does not establish the complete ticket transaction.

The supplied local `docs/research/fixtures/authlaunch-auth-openapi-v1-excerpt.json` agrees with that distinction. Its source metadata identifies the same official schema and describes an October 23 observation. The supplied `authlaunch-web-launch-uri-spec.json` identifies a first-party GameLaunch.js resource observed October 24, with placeholder example values. It describes a short-lived ticket in the native URI's `gameinfo` field and destination parameters, rather than a long-lived account cookie. That is reference-derived static research, not an independently retrieved current script or live compatibility result.

Source fixture fingerprints:

- `authlaunch-web-launch-uri-spec.json`: `02A2C1F7F84C05B4D15F38D127E6D98AB0BA64B06DF553903FEFD020235FE00D`.
- `authlaunch-auth-openapi-v1-excerpt.json`: `B8E1C53CB2FDD1BEE0A52E9A7A1980C5E850CB9528D222218E1B10636B403918`.

Both were read statically from the explicitly supplied checkout at `C:/Users/noelm/OneDrive/Documents/GitHub/RoALT/docs/research/fixtures`. No reference implementation was copied.

## Retrieval limits

The web tool could not open [the legacy docs route](https://auth.roblox.com/docs/json/v1) or [the exact first-party script named by the fixture](https://js.rbxcdn.com/813c4bdd4d89a033c33e45564ccfc9ef7a7013fba5db9cf6de4270dce4e72100-GameLaunch.js). Direct unauthenticated GET attempts to those hosts and the official schema host failed with DNS resolution errors in the current execution environment. No elevation or alternate account/session request was attempted. The final schema URL remained readable through the web tool; its parsed content was reviewed, but no byte-identical download/hash of that current response was available.

## Exact missing contract and next step

Before implementing `RobloxLaunchTicket` and `RobloxLaunchTicketClient`, independently obtain first-party documentation or the relevant current official shipped script text establishing:

1. Ticket issuance method, exact route, required session authentication, assertion JSON and required origin/referer headers, plus the response ticket carrier and size/type expectations.
2. A distinguishable CSRF rejection and whether one retry with the returned token is safe before issuance; challenges, denial, rate limits and uncertain issuance must not silently become retries.
3. Ticket one-use/expiry semantics and native delivery. Any additional tiny local deadline is an engineering policy, not a claimed server guarantee.
4. Session rotation handling across every response. A changed credential must be surfaced for same-account verification and durable generation-aware replacement, or stop the handoff; the old cookie cannot silently be replayed.

Once those facts are available, the smallest independent adapter can use exact HTTPS endpoints, no cookie jar/redirects, bounded response headers/body/deadline, one explicitly consumed redacted disposable ticket, and selected-account/generation context. Synthetic transport tests can then cover those documented branches before any separately authorized real issuance. That adapter would still not establish verified arrival, reconnect, or supported simultaneous players.

Current decision: retain native account-selecting launch as unavailable. This checkpoint records a specific missing ticket contract and retrieval limit; it does not treat OAuth limitations as a blanket objection to saved-session or app-owned browser approaches.

## Decision reversed for one mode (2026-11-08)

The owner decided (R-api-7, also covering R-launch-13) to build the ticket client this checkpoint deferred, as a selectable **Direct ticket** native launch mode beside the official-page mode, which stays the default until the live trial verifies the direct mode. `RobloxLaunchTicketClient` and `ProtectedLaunchTicket` follow the adapter sketched above: exact HTTPS endpoints (`GET /v1/client-assertion/`, `POST /v1/authentication-ticket/` on `auth.roblox.com`, observed web-client shapes rather than a documented contract), no cookie jar or redirects, a bounded body and deadline, one anti-forgery retry, typed outcomes for 401, 403, 429 and a human check, one explicitly consumed redacted ticket bound to the selected account and generation, and session rotation handed back for the same-player check. Zenith sends its own User-Agent and no Referer or Origin, so the four open items above (issuance route and headers, a distinguishable CSRF rejection, one-use and expiry semantics, rotation) are now answered by the live trial rather than by documentation. The adapter still does not establish verified arrival, reconnect or supported simultaneous players. Record in [HANDOFF.md](HANDOFF.md).
