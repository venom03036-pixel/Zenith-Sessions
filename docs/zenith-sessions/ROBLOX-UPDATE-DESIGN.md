# Roblox updates: the automatic check and the verified installer run

Settings > Roblox installation > Roblox updates adds two things Zenith lacked, both off until chosen. It is Zenith's own version
of a capability the RoALT reference has (it checks the Roblox version 3 s after start and every 30 minutes and can install the
official update once the installer is verified as signed by Roblox Corporation, refusing while clients or launches are active);
no RoALT code was used. The owner's decision of 2026-11-08 shaped it: the periodic check is an opt-in (default off; the on-demand
check from R-research-13 stays), and the install is offered from Roblox's own setup host: download to a temp folder, verify the
Authenticode signature and Roblox Corporation publisher with the existing trust code, warn when clients are running, then run it
only after the user confirms. This item (R-launch-9) also covers R-shell-19 (install or update the Roblox client from inside the
app). It reverses one recorded wording: the release summary used to say "Zenith never downloads or installs updates"; it now
says nothing is downloaded or installed unless the user chooses Download Roblox update and then confirms Install Roblox update.

## The automatic check (saved preference `CheckRobloxUpdates`, default off)

- **Schedule.** One one-shot `DispatcherTimer`, armed `RobloxClientUpdate.CheckDelay` (3 s) after the manager is ready when the
  saved preference is on, and re-armed `RobloxClientUpdate.CheckInterval` (30 min) after each check finishes, so a slow read never
  overlaps the next one and nothing polls while off (Rule 14). Saving the preference on arms it 3 s out; saving it off stops it.
- **What a check does.** The same anonymous read of Roblox's public version service that Refresh installation makes
  (`IPublicRobloxClientReleaseDirectory`, one GET, no cookie, 4 KiB cap). With no installation row selected it runs the full
  refresh (`RefreshInstallations`), so new version folders appear and the rows are compared with the answer. With a row selected
  (someone's trial installation, whose change would stop a native preparation and pause the queue) it only asks for the release,
  remembers it in `PublicClientReleaseCache` and fills the listed rows in place through the same path a native trial start uses
  (`PublishLaunchRelease`, which also signals the launch queue so a held request re-checks).
- **What it says.** The status line under the tick box reports the last check's time and outcome and that the next is in 30
  minutes. A pending update is announced once per release as a routine notice and an activity line ("Roblox update available:
  release …"); the same release is not announced again. A failed read keeps the rows and says the release could not be read.
- **When it stays quiet.** Locked, closing, the sample profile (the sample profile never reaches the network), a download or
  installation in progress, or an installation check, publisher check or native trial in progress: the check is skipped and
  re-armed. Lock idles the timer and Unlock arms it again 3 s out. Panic leaves the check scheduled: it is the same read as
  Refresh installation and starts, changes or closes nothing (Panic does cancel and discard the installer work, below).

## The update offer

`RefreshUpdateOffer` re-reads the listed rows after every release application (a refresh, a kept release filling the rows, the
automatic check) and after the list is cleared. An update is offered when a native (not bootstrapper-managed) player row reads
"Not current", no native row reads "Current release", and the stale player's file still exists (a player updated outside Zenith
leaves its old version folder gone; the refresh then re-lists). The release offered is the one that row was compared with, so
the installer downloaded is exactly the release the rows were measured against. A held installer for a release the rows no
longer point at (Roblox moved on, or the player is current now) is discarded with a status line saying so.

## Download Roblox update

`WindowsRobloxClientUpdateInstaller.DownloadAsync` (Windows layer, behind `IClientUpdateInstaller`):

1. The address is built only from a valid upload name (`RobloxClientRelease.TryInstallerUri`: `version-` and 16 lowercase hex
   digits) on `setup.rbxcdn.com`, Roblox's own setup host, under the fixed name `RobloxPlayerInstaller.exe`. Anything else is
   refused before any request.
2. One anonymous GET over an HTTP client with the public directory's handler settings (no redirects, no cookie jar, no
   credentials, the Windows system proxy) and no User-Agent, Referer or Origin. A 3xx is a refused redirect; any status other
   than 200 fails. A declared length over `MaximumInstallerBytes` (64 MB) is refused before reading; an undeclared body is capped
   at the same size while streaming; a body shorter than its declared length fails. The whole download must finish within
   `DownloadLimit` (5 min).
3. The body streams into a fresh folder `%TEMP%\ZenithSessions-RobloxUpdate-<guid>`, into a file opened exclusively
   (`FileShare.None`) while it is written, in 64 KiB pooled chunks with cancellation checked per chunk and progress reported
   (shown as "x of y MB").
4. The file is then verified by `WindowsClientExecutableTrust.PrepareInstallerAsync`: the existing selected-file verification
   (path safety, bounded size, SHA-256, cached-evidence Authenticode through WinVerifyTrust, Roblox Corporation publisher, repeated
   digest, re-open comparison), with the file-name rule swapped for "exactly RobloxPlayerInstaller.exe", so a player or launcher
   can never pass as an installer. On success the exact handle stays open sharing read access only, so the file cannot be
   rewritten, renamed or deleted before it runs: the bytes Windows verified are the bytes that run (tests prove the hold).
5. Anything but Verified deletes the folder: NotVerified (with the trust reason), Refused, Failed, cancellation (the partial file
   goes) and the time limit. A new download replaces a held one.

The shell shows the result (size, publisher, SHA-256) and enables Install Roblox update. A not-verified or failed download is an
error (`Fail(..., ManagerSubsystem.ClientUpdate, fixed text)`); only the exception type reaches diagnostics.

## Install Roblox update

The view asks `RobloxUpdateConfirmation` first: the release, that Windows verified the signature from Roblox Corporation, how many
Roblox clients Zenith can see running ("N Roblox clients are running: Roblox's installer closes every running Roblox client while
it updates, and anything unsaved in them is lost", or that none is running), and that Zenith only starts the official installer
and waits. Only a Yes runs `InstallAsync`: the held run re-reads the folder entry, starts the held file with no arguments from its
own folder (`UseShellExecute = false`, no channel, flag or address added) and waits for it to exit for at most
`InstallerWaitLimit` (10 min). The installer is never ended by Zenith: the wait limit, Cancel, Panic, Lock and exit only stop the
wait and say so. After an exit the folder is deleted (refused and left in Temp while the installer still runs from it) and, when
the installer exited normally, the installations are refreshed so the rows show what is installed now.

Download and Install are unavailable while an installation check, a publisher check or a native trial is in progress (the
installation check replaces the rows the offer came from; the others own the selected player), while locked, in the sample
profile, and while another download or run is in progress.

## Stopping

`StopClientUpdateWork` is a `Step` in `StopOperations`: Panic and Lock cancel a download, a verification or the wait for a started
installer and discard a held installer, so nothing verified before the stop is run after it; Lock also idles the automatic
check until Unlock. `DisposeClientUpdateFeaturesAsync` runs at exit before the directory features release the shared HTTP client:
it stops the timer, joins the automatic check and the download or install work, and disposes the installer component, which
cancels its own lifetime, releases a held file and deletes its folder.

## Settings, diagnostics and the sample profile

`SessionsSettings.CheckRobloxUpdates` is optional within schema 1 (files without it load unchanged; older builds ignore it) and
is found by Settings search ("Check for Roblox updates automatically"). Failures record only the exception type against the new
append-only `ManagerSubsystem.ClientUpdate` ("Roblox update"); the packaged layout self-check verifies the controls, their
bindings, the off default and that nothing is armed or offered in the sample profile. No temp path, address beyond the setup
host or exception text appears in a status line, notice, activity line, report or `ToString`.

## Rule 18

CPU and time: a check is one HTTP read (plus a bounded registry and file-metadata inventory when nothing is selected) every 30
minutes; a download streams at most 64 MB once, hashed twice as the existing verifier does. Memory: one 64 KiB pooled buffer per
download; the held file is a handle, not bytes. Threads: one dispatcher timer, no poller; the download runs on the HTTP client's
I/O and the verification on one pool worker, both owned. Cancellation: every path takes a token linked to the shell's lifetime;
exit joins the work. Errors: refusals and failures are reported with fixed text and recorded by type; cancellation is not an
error. Testability: `ClientUpdateInstallerTests` drive the real file handling over a fake HTTP handler, a synthetic Authenticode
policy and a fake process starter; `RobloxUpdateIntegrationTests` drive the shell over a fake installer, inventory, release
source and monitor. Security: no credentials, no AI, no client file or setting written; only Roblox's own installer, verified by
Windows, is ever run, and only after an explicit confirmation. Backwards compatibility: one optional settings field; no queue or
history format changed. No new dependency (System.Net.Http, System.IO, wintrust.dll as before).

## What the live trial must confirm

That `https://setup.rbxcdn.com/<clientVersionUpload>-RobloxPlayerInstaller.exe` serves the current installer with HTTP 200 (the
address shape follows Roblox's own bootstrapper and the RoALT research fixture; it was not fetched unattended), that Windows
verifies that file's signature from Roblox Corporation with cached evidence on the owner's PC, that running it with no arguments
updates the installed player (and what it opens afterwards), and that the wait and the post-run refresh behave with a real client
running and closed by the installer.
