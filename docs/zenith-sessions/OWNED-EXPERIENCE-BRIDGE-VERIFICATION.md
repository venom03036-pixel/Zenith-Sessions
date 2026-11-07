# Owned Roblox experience bridge

## What the backend can do

The Windows transport publishes a short-lived status or exact public-server move command to one configured universe. The Roblox server script accepts commands only for allowlisted players and places. Status reports a player currently present at the specified source. A move pins the player, source place and job, destination place and job, and one operation ID. The destination writes its receipt only after Roblox raises the player arrival event with matching source data and the same operation claim.

Publishing, receipt at the source, teleport initiation, and arrival are distinct results. An expired request, missing receipt, older operation, or ambiguous transport failure leaves arrival unconfirmed. An arrival receipt is evidence of a player at the destination server at the reported time. Windows process identity, sign-in, and permission to route local input need their own evidence. This bridge operates on connected players and cannot launch or reconnect a native client.

Each player has one receipt slot. An atomic Roblox storage update claims an operation before any move. One live operation occupies that slot for up to 45 seconds, including after a terminal receipt. Duplicate or out-of-order commands leave it alone. A late source update cannot downgrade a destination arrival. A server handles up to four operations at a time, with at most one deferred terminal callback per active player. Busy servers can leave a request without a receipt. The bridge exposes that as pending or expired.

The Windows transport allows one in-flight request, with at least one second between publishes and half a second between reads. Roblox throttling can extend those intervals. Each request has a five-second cancellation deadline, and a receipt body is limited to 4 KiB. There is no polling worker or automatic republication. A server may still finish a command after local cancellation if Roblox already received it. Receipt times come from the Roblox server's clock. The script accepts a command while that clock is up to two seconds behind the PC, so the Windows reader accepts a receipt time up to two seconds before the command's issue time or after the PC's current time.

The protocol keeps the existing numeric ID types and rejects IDs beyond Luau's exact integer range at this bridge boundary. It currently supports exact public jobs in the approved universe. Private and reserved access data are outside the command format.

## Setup boundary

The repository's server script ships with an empty allowlist and an unset universe. It exits in Studio and on private or reserved servers. To prepare a published acceptance trial, configure the creator-owned universe and approved place and player IDs in a private copy of the script. Install it in `ServerScriptService` on both source and destination places. Use Roblox's Creator Dashboard to provision separate API keys for publishing messages and reading receipts in the chosen universe. [Roblox messaging guide](https://create.roblox.com/docs/cloud/guides/usage-messaging) · [Roblox data store reference](https://create.roblox.com/docs/cloud/reference/features/storage)

The desktop adapter expects two callbacks that retrieve those separate keys from protected local storage. Connect those callbacks through the Personal host before enabling the bridge. Key setup and the live trial remain pending. The source tree, generated fixture and test outputs contain synthetic values. The script uses Roblox's documented server teleport and join-data mechanisms. [Roblox teleport guide](https://create.roblox.com/docs/projects/teleport) · [Roblox Player.GetJoinData](https://create.roblox.com/docs/reference/engine/classes/Player#GetJoinData)

## Executed checks

- The standalone Luau compiler compiled the shipped script. Ten executed server cases used a controlled Roblox service substitute and the same server script with synthetic allowlists. They covered disabled defaults, input validation, status, exact moves, duplicates, arrival ordering, failure callbacks, expiry, and a failed receipt write.
- The isolated Windows launch suite passed **603 checks twice** with zero build warnings or errors. Twenty-three new cases covered the two Open Cloud routes and key separation, stages and exact IDs, malformed and oversized responses, pacing, cancellation, and unavailable credentials.
- The staged app remains **0.50.0-preview**. The Windows code and Lua script were verified using isolated synthetic data. No live Roblox response, installed client, creator project, real account, or UI participated. Live native selection, exact arrival, and reconnect remain unverified.

The [verification record](../../artifacts/sessions-owned-bridge-verification-0.57.json) pins the tested backend, source, and results. Its SHA-256 is `2D23AA88754983D729ABE6AB96BD47FB3A48B3E8A4479AAFF174E79C30692172`.

Use the [local Luau verifier](../../scripts/Test-ZenithOwnedExperienceBridge.ps1) with the Windows binaries from the [official Luau 0.745 release](https://github.com/luau-lang/luau/releases/tag/0.745) to rerun the server fixture.
