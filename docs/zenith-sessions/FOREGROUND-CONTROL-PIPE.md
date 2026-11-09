# Personal foreground control connection

The Rust Sessions client and Personal host now share a separate one-run foreground control protocol. The existing v1 catalog still serves saved-macro metadata. The foreground server is an explicit, short-lived object. It listens once, issues a 128-bit pairing code once, and expires after two minutes if pairing does not complete. A rejected attempt ends that listener. The desktop composition must create a new listener for another attempt.

The server uses a first-instance current-user pipe and checks the client process and Windows session reported by the pipe. The Rust client disables impersonation and verifies the pipe owner's SID before the handshake. The pairing code stays in memory and does not travel over the pipe. Fresh client and host nonces bind mutual HMAC-SHA-256 proofs. Each request and reply is authenticated with a session key, its direction, and its sequence number. [Windows named pipe access](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Pipe client process identity](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), [HMAC-SHA-256](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hmacsha256)

The first authenticated command carries the exact Personal session, macro ID and saved revision, plus the proposed window handle, process ID, and Windows creation time. The host binding requires an independent owned-window authorization callback and matches the callback's receipt against all three window identity fields. The binding uses the Stop generation captured when the grant was created. Once Start is accepted, Status, Pause, Resume, and Stop apply to that operation ID on that connection. Results contain fixed states and outcomes. There is no macro document, saved sign-in, path, raw input, or editable command text in the protocol.

The read loop detects a closed pipe while a separate command is awaiting preparation or a control transition. It revokes the run immediately and joins the actual operation and its cleanup. If an authorization result arrives after disconnect, the worker stops and joins that exact operation too. A six-second heartbeat deadline provides the same revocation when the connection stays open but stops sending. Rust exchanges have a three-second deadline and release the pipe when abandoned. A desktop worker must poll the host independently of React while the operation is active.

## Protocol v1

1. The client sends ASCII `ZMC1` followed by a 32-byte random nonce. The host returns a 32-byte random nonce and a 32-byte HMAC over role byte 1, the entire client hello, and the host nonce. The client replies with the same transcript under role byte 2. Both use role byte 3 to derive the per-connection key.
2. A frame is a little-endian unsigned 16-bit length, 1–96 body bytes, and a 32-byte HMAC. The MAC input is ASCII `C` or `S` for direction, the little-endian unsigned 64-bit sequence beginning at 1, and the body.
3. Start is command byte 1 followed by three RFC-order UUIDs, signed 64-bit window, signed 32-bit PID, and signed 64-bit FILETIME. The scalar integers are little-endian. Commands 2–5 are Status, Pause, Resume, and Stop, each followed by one RFC-order UUID. The reply contains the echoed command, state, outcome, accepted flag, and RFC-order operation UUID.

## Verification

From the integration checkout on Windows, run the focused .NET and Rust suites:

```powershell
dotnet test tests/Zenith.Sessions.Tests/Zenith.Sessions.Tests.csproj -c Release --no-restore -m:1 --filter 'FullyQualifiedName~ForegroundControlPipeTests|FullyQualifiedName~CompanionForegroundPlaybackTests|FullyQualifiedName~MacroHostPipeTests|FullyQualifiedName~SavedMacroPlanPreparerTests|FullyQualifiedName~CompanionMacroOperationsTests'
cargo test --locked --offline --manifest-path apps/zenith-sessions/src-tauri/Cargo.toml
$env:ZENITH_MACRO_FIXTURE = (Resolve-Path 'tools/Zenith.MacroHost.ProbeWorker/bin/Release/net8.0-windows10.0.19041.0/Zenith.MacroHost.ProbeWorker.dll').Path
cargo test --locked --offline --manifest-path apps/zenith-sessions/src-tauri/Cargo.toml dotnet_ -- --ignored
```

The final run passed 138 focused .NET checks, 29 Rust desktop checks, and two cross-runtime fixtures. The real Windows pipe tests exercised a wrong proof, stale operation ID, replayed frame, silent peer, disconnect during an unfinished control, and an authorization result arriving after disconnect. The Rust fixture paired with the .NET listener, started a synthetic operation, paused and resumed it, and verified that closing the connection made the host stop and join the operation. The v1 catalog fixture also passed. These checks used synthetic receipts and in-memory operations.

Formatting, Clippy, the frontend build, and the zero-warning Tauri Release build passed. The packaged desktop self-check passed all six stages twice. The build-check executable SHA-256 is `0A0C4D4F525157EA5DDE93E589DE6087748BCEBD3480E16A661E67CFD5CEF6B7`.

## Desktop integration

Preview 1.18 composes the pipe behind the Personal pairing button and drives it from the local Automation page. The paired Personal build embeds the exact Sessions executable SHA-256 at build time. Personal checks that hash against the local pipe peer's image and independently checks the direct child process, creation time, and native window. Sessions revalidates the protected assignment and a short-lived review of the exact live window before dispatch. The Rust worker maintains the heartbeat and closes its connection on Panic, session stop, or workspace exit. See [the paired preview and attended steps](RUST-DESKTOP-PREVIEW.md#preview-118-paired-foreground-playback).
