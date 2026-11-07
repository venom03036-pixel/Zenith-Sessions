ZENITH VM TEST PREPARATION

This folder contains only Zenith's own configuration. No RoALT files have
been downloaded, copied, installed or executed on the host.

Offline-Inspection.wsb starts a disposable Windows Sandbox with networking,
host-folder mapping, clipboard sharing, camera, microphone, printers and
GPU sharing disabled. It does not download or start any program.

The Microsoft Sandbox feature must first be enabled by a Windows administrator.
scripts/Enable-ZenithSandbox.ps1 enables that feature with -NoRestart. If Windows
requires a restart, save your work and restart manually when convenient.

The first setup attempt on November 4 failed with 0x80073712 (Windows component
store corruption). The owner approved Microsoft's DISM repair and a setup retry.
scripts/Repair-ZenithSandboxPrerequisite.ps1 performs only that repair and the
Sandbox feature setup, without automatic reboot. It records current/terminal
state in artifacts/sandbox-repair-status.json and a Microsoft servicing log.
An in-progress status is not proof of repair or Sandbox availability.

The approved repair finished November 5: DISM CheckHealth reported Healthy,
and Containers-DisposableClientVM reached Enabled. Windows requires a restart.
No restart, VM launch, or RoALT retrieval was performed. The terminal report
is sandbox-repair-status.json; sandbox-restart-checkpoint.json records the
pre-restart boot identity and approved profile hash. Recheck actual boot time,
feature state and available Sandbox client after the owner restarts. Do not
assume the CLI or a running guest exists merely because the feature is enabled.

Before launch, run scripts/Test-ZenithSandboxProfile.ps1 -ProfilePath <this .wsb>.
It rejects changed isolation values, missing/duplicate/unknown/case-mismatched
settings, host mappings, startup commands and external XML entities. The local
Test-OfflineProfile.ps1 exercises these checks without starting a VM. A profile
check is not proof of hypervisor isolation or permission to run online malware.

The offline profile is for initial isolation checks. It cannot download a
program or prove online Roblox gameplay. A guest-only retrieval and controlled
runtime-test procedure must be established before any RoALT execution. Do not
copy RoALT out to the host or share a host folder to work around that limitation.
No existing host account data, browser profiles or saved credentials should be
made available to the test VM.

NOVEMBER 5: INTERNET AND 10 GB RUNTIME PROFILE

The owner explicitly requested internet access. Online-Inspection.wsb enables
guest networking; clipboard, GPU and host folders remain disabled. Its online
preflight requires -NetworkMode Online. The initial offline guest and replacement
online guest passed identity/network probes; HTTPS to GitHub returned 200 from
the online guest. Windows Sandbox 0.8.107.0 was installed through Microsoft's
first-use update. No RoALT release was downloaded or executed by the agent.

The owner then requested sharing their WebView2 engine and 10 GB RAM after the
Microsoft installer failed to create a temporary directory. The local host's
Microsoft-signed runtime 156.0.4314.58 is copied into ONE dedicated cache:
artifacts/sandbox-runtime-share (approximately 0.85 GiB, reused, no account data).
scripts/Prepare-ZenithRuntimeSandbox.ps1 prepares that cache, a credential-free
.NET Framework rendering check using existing Microsoft SDK libraries, and
Zenith-10GB-WebView2.wsb. The new profile specifies 10240 MB and exactly one
READ-ONLY host mapping to the trusted cache. It shares no Downloads folder,
browser profiles, credentials, clipboard, microphone, camera, printers or GPU.

InitializeRuntimeGuest.ps1 runs only as the Sandbox user, copies/hash-verifies
the engine into local guest storage, verifies Microsoft's signature, creates a
writable guest TEMP/TMP folder, sets the supported WebView2 SDK runtime-directory
override in the GUEST account, verifies assigned RAM and opens the local renderer
check. It does not run RoALT or install anything into the host. The manifest
covers 922 runtime/check files. No claim of usability is made until the live
renderer writes Ready with Rendered=True. Setup and rendering results are inside
the guest at C:\ZenithLab\SetupStatus.json and RuntimeCheck.txt.

The first attempt to start this new profile alongside the existing 4 GB guest
was rejected with CO_E_APPSINGLEUSE. The current guest was left untouched. The
owner was asked before closing it because its downloads/changes would be lost.
Sandbox is disposable: closing it does not preserve guest-installed runtimes.
Reopening this specific profile automatically reapplies the runtime setup.

The owner approved replacement. The new guest is recorded in
artifacts/sandbox-10gb-session.json. Actual measured memory was 10239 MiB (the
profile requests 10240). Its mapped trusted source was present, but the original
startup -File invocation did not execute setup. Setup was applied as inline
encoded, reviewed code without changing PowerShell policy; the saved profile
now contains that same tested code. A separate fresh-launch replay has not yet
been performed, because it would discard the owner's current guest again.

LIVE CHECKS PASSED: the copied engine rendered the local HTML page AND sent its
JavaScript readiness message; the actual working window was visually inspected;
the guest's temporary directory accepted writes; GitHub HTTPS returned 200;
creating a new file in the readable host-mapped cache was denied. The runtime
folder override is scoped only to the disposable GUEST account. No credentials,
browser profiles, RoALT files, or guest downloads were copied onto the host.
No claim of RoALT login/game compatibility follows from this runtime test.

Target behavior for Zenith: Roblox-hosted sign-in during account setup, with all
game sessions in the native Roblox app. A VM result is not a sixteen-client
performance result for the host. A client refusing a virtual environment is a
test limitation, not something to bypass.

NEXT STAGE REQUESTED BY THE OWNER

Only after the native sign-in/session capability is demonstrated, implement an
independent Zenith bootstrapper integrated into the companion. Froststrap is the
primary design reference, with the installed Fishstrap as a secondary reference.
The owner has authorized that later implementation once the prerequisite works.
No bootstrapper implementation, protocol-handler replacement, client change or
third-party installation was performed in this preparation stage.

Reference work so far is source text only: install/update serialization,
manifest-aware version decisions, cached package validation, bounded download
work, progress/cancellation, native launch and cleanup. A repository's feature
claims do not prove correct-account reconnect or background input isolation.
The eventual Zenith implementation must reuse verified runtime files rather
than extracting on each launch, stage/verify replacements before promotion,
retain bounded rollback, and preserve in-use clients and account data.

Froststrap's current new revisions identify MPL-2.0; Fishstrap identifies MIT.
This is an inspiration review, not copied source. Record file-specific origins
and preserve applicable notices if source is reused in a later implementation.

Primary references:
https://github.com/Froststrap/Froststrap
https://github.com/Froststrap/Froststrap/blob/main/Froststrap/Bootstrapper/Bootstrapper.cs
https://github.com/fishstrap/fishstrap/blob/main/Bloxstrap/Bootstrapper.cs
https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file
https://learn.microsoft.com/en-us/powershell/module/dism/repair-windowsimage
