mod arrange;
mod pair_lease;
mod publisher;
mod telemetry;

pub use arrange::{Placement, tile_owned_players};
pub use pair_lease::PairLease;
pub use telemetry::{PriorityAction, PriorityState, ProcessMetrics, ProcessSample};

use std::{
    fs::{File, OpenOptions},
    io::{self, Read},
    mem::size_of,
    os::windows::{ffi::OsStringExt, fs::OpenOptionsExt, io::AsRawHandle, process::CommandExt},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use ::windows::{
    Win32::{
        Foundation::{
            CloseHandle, ERROR_INVALID_PARAMETER, ERROR_NO_MORE_FILES, FILETIME, HANDLE, HLOCAL,
            HWND, LPARAM, LocalFree, WAIT_OBJECT_0, WAIT_TIMEOUT,
        },
        Security::Cryptography::{
            CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
        },
        Storage::FileSystem::FILE_SHARE_READ,
        System::{
            Diagnostics::ToolHelp::{
                CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
                TH32CS_SNAPPROCESS,
            },
            Registry::{HKEY_CURRENT_USER, RRF_RT_REG_SZ, RegGetValueW},
            SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX},
            Threading::{
                GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
                PROCESS_SYNCHRONIZE, WaitForSingleObject,
            },
        },
        UI::WindowsAndMessaging::{
            EnumWindows, GW_OWNER, GetWindow, GetWindowThreadProcessId, IsIconic, IsWindowVisible,
            SW_RESTORE, SetForegroundWindow, ShowWindowAsync,
        },
    },
    core::{BOOL, PCWSTR, w},
};
use serde::{Deserialize, Serialize};
use uuid::Uuid;
use zeroize::{Zeroize, Zeroizing};

use crate::{admission::MemorySample, auth::Credential};

const PROBE_ENTROPY: &[u8] = b"ZenithSessions/Rust/LaunchProbe/v1";
const MAX_BUNDLE: u64 = 128 * 1024;

#[derive(Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SavedIdentity {
    pub profile_id: Uuid,
    pub username: String,
    pub user_id: u64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Bundle {
    version: u8,
    profile_id: Uuid,
    username: String,
    user_id: u64,
    generation: u64,
    expires_unix_seconds: u64,
    cookie: Zeroizing<String>,
}

pub fn read_probe_credential(path: &Path, username: &str) -> Result<(u64, Credential), String> {
    let mut encrypted = Vec::new();
    File::open(path)
        .and_then(|file| file.take(MAX_BUNDLE + 1).read_to_end(&mut encrypted))
        .map_err(|_| "could not read the protected probe bundle")?;
    let (identity, credential) = open_saved_bundle(&mut encrypted)?;
    if !identity.username.eq_ignore_ascii_case(username) {
        return Err("saved sign-in does not match the requested account".into());
    }
    Ok((identity.user_id, credential))
}

pub fn open_saved_bundle(encrypted: &mut [u8]) -> Result<(SavedIdentity, Credential), String> {
    let clear = unseal_current_user(encrypted, PROBE_ENTROPY, MAX_BUNDLE)?;
    let bundle: Bundle =
        serde_json::from_slice(&clear).map_err(|_| "invalid protected probe bundle")?;
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "system clock is invalid")?;
    if bundle.version != 1
        || bundle.profile_id.is_nil()
        || bundle.user_id == 0
        || bundle.generation == 0
        || bundle.username.is_empty()
        || bundle.username.len() > 20
        || !bundle
            .username
            .bytes()
            .all(|value| value.is_ascii_alphanumeric() || value == b'_')
        || bundle.expires_unix_seconds <= now.as_secs()
    {
        return Err("saved sign-in is expired or does not match the requested account".into());
    }
    let mut credential = Credential::new(bundle.cookie).map_err(|error| error.to_string())?;
    credential.limit_expiry(bundle.expires_unix_seconds);
    Ok((
        SavedIdentity {
            profile_id: bundle.profile_id,
            username: bundle.username,
            user_id: bundle.user_id,
        },
        credential,
    ))
}

pub const MAX_LEGACY_MIGRATION_BYTES: usize = 16 * 1024 * 1024;
const LEGACY_MIGRATION_ENTROPY: &[u8] = b"ZenithSessions/Rust/LegacyMigration/v1";

pub fn open_legacy_migration(encrypted: &mut [u8]) -> Result<Zeroizing<Vec<u8>>, String> {
    unseal_current_user(
        encrypted,
        LEGACY_MIGRATION_ENTROPY,
        MAX_LEGACY_MIGRATION_BYTES as u64,
    )
}

pub fn seal_legacy_metadata(clear: &mut [u8]) -> Result<Vec<u8>, String> {
    seal_current_user(clear, LEGACY_MIGRATION_ENTROPY, MAX_LEGACY_MIGRATION_BYTES)
}

pub const MAX_ASSIGNMENT_BYTES: usize = 8 * 1024 * 1024;
const ASSIGNMENT_ENTROPY: &[u8] = b"ZenithSessions/Rust/MacroAssignments/v1";

pub fn open_assignment_metadata(encrypted: &mut [u8]) -> Result<Zeroizing<Vec<u8>>, String> {
    unseal_current_user(encrypted, ASSIGNMENT_ENTROPY, MAX_ASSIGNMENT_BYTES as u64)
}

pub fn seal_assignment_metadata(clear: &mut [u8]) -> Result<Vec<u8>, String> {
    seal_current_user(clear, ASSIGNMENT_ENTROPY, MAX_ASSIGNMENT_BYTES)
}

fn unseal_current_user(
    encrypted: &mut [u8],
    entropy: &[u8],
    limit: u64,
) -> Result<Zeroizing<Vec<u8>>, String> {
    if encrypted.is_empty() || encrypted.len() as u64 > limit {
        return Err("The protected file has an invalid size.".into());
    }
    let mut entropy = entropy.to_vec();
    let input = blob(encrypted);
    let entropy = blob(&mut entropy);
    let mut output = CRYPT_INTEGER_BLOB::default();
    // SAFETY: DPAPI owns the successful output. Wipe and release it before returning.
    unsafe {
        CryptUnprotectData(
            &input,
            None,
            Some(&entropy),
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|_| "Windows could not open this current-user protected file.")?;
        if output.pbData.is_null() {
            return Err("Windows returned an empty protected file.".into());
        }
        let bytes = std::slice::from_raw_parts_mut(output.pbData, output.cbData as usize);
        let copy = (bytes.len() as u64 <= limit).then(|| Zeroizing::new(bytes.to_vec()));
        bytes.zeroize();
        LocalFree(Some(HLOCAL(output.pbData.cast())));
        copy.ok_or_else(|| "The decoded protected file is too large.".into())
    }
}

fn blob(bytes: &mut [u8]) -> CRYPT_INTEGER_BLOB {
    CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_mut_ptr(),
    }
}

pub fn seal_saved_bundle(
    identity: &SavedIdentity,
    credential: &Credential,
    expires: u64,
) -> Result<Vec<u8>, String> {
    let expires = credential
        .expires_at()
        .map_or(expires, |limit| expires.min(limit));
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "System clock is invalid")?
        .as_secs();
    if expires <= now
        || identity.profile_id.is_nil()
        || identity.user_id == 0
        || identity.username.is_empty()
        || identity.username.len() > 20
        || !identity
            .username
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
    {
        return Err("The sign-in is expired or the account identity is invalid".into());
    }
    #[derive(Serialize)]
    #[serde(rename_all = "camelCase")]
    struct Payload<'a> {
        version: u8,
        #[serde(flatten)]
        identity: &'a SavedIdentity,
        generation: u64,
        expires_unix_seconds: u64,
        cookie: &'a str,
    }
    let mut clear = Zeroizing::new(
        serde_json::to_vec(&Payload {
            version: 1,
            identity,
            generation: now.max(1),
            expires_unix_seconds: expires,
            cookie: credential.value(),
        })
        .map_err(|_| "Could not encode the saved sign-in")?,
    );
    seal_current_user(&mut clear, PROBE_ENTROPY, MAX_BUNDLE as usize)
}

fn seal_current_user(clear: &mut [u8], entropy: &[u8], limit: usize) -> Result<Vec<u8>, String> {
    if clear.is_empty() || clear.len() > limit {
        return Err("The file is too large to protect.".into());
    }
    let mut entropy = entropy.to_vec();
    let input = blob(clear);
    let entropy = blob(&mut entropy);
    let mut output = CRYPT_INTEGER_BLOB::default();
    // SAFETY: the inputs remain alive during DPAPI and the successful output is released here.
    unsafe {
        CryptProtectData(
            &input,
            PCWSTR::null(),
            Some(&entropy),
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|_| "Windows could not protect this file.")?;
        if output.pbData.is_null() {
            return Err("Windows returned an empty protected file.".into());
        }
        let size = output.cbData as usize;
        let bytes =
            (size <= limit).then(|| std::slice::from_raw_parts(output.pbData, size).to_vec());
        LocalFree(Some(HLOCAL(output.pbData.cast())));
        bytes.ok_or_else(|| "The protected file exceeds the size limit.".into())
    }
}

pub fn memory_sample() -> Result<MemorySample, String> {
    let mut state = MEMORYSTATUSEX {
        dwLength: size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    // SAFETY: the structure is writable and initialized with its ABI size.
    unsafe { GlobalMemoryStatusEx(&mut state) }
        .map_err(|_| "Windows resource sample is unavailable")?;
    Ok(MemorySample {
        sampled_at: Instant::now(),
        physical_total: state.ullTotalPhys,
        physical_available: state.ullAvailPhys,
        commit_total: state.ullTotalPageFile,
        commit_available: state.ullAvailPageFile,
    })
}

pub fn ensure_compatible_player_start(owned: &[ProcessIdentity]) -> Result<(), String> {
    // SAFETY: the snapshot is owned here and closed after enumeration on every result path.
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }
        .map_err(|_| "Could not check for existing Roblox clients")?;
    let result = (|| {
        let mut entry = PROCESSENTRY32W {
            dwSize: size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        unsafe { Process32FirstW(snapshot, &mut entry) }
            .map_err(|_| "Could not read the current process list")?;
        loop {
            let end = entry
                .szExeFile
                .iter()
                .position(|unit| *unit == 0)
                .unwrap_or(entry.szExeFile.len());
            let executable = String::from_utf16_lossy(&entry.szExeFile[..end]);
            if [
                "RobloxPlayerBeta.exe",
                "RobloxPlayerInstaller.exe",
                "RobloxPlayerLauncher.exe",
                "RoALT.exe",
                "Zenith.Sessions.App.exe",
            ]
            .iter()
            .any(|name| name.eq_ignore_ascii_case(&executable))
            {
                let pid = entry.th32ProcessID;
                let owned_player = executable.eq_ignore_ascii_case("RobloxPlayerBeta.exe")
                    && owned.iter().any(|identity| identity.pid == pid)
                    && current_process_identity(pid)
                        .is_some_and(|identity| is_owned_player(identity, owned));
                if !owned_player {
                    return Err(format!(
                        "Close the other {executable} before launching from Zenith"
                    ));
                }
            }
            match unsafe { Process32NextW(snapshot, &mut entry) } {
                Ok(()) => (),
                Err(error) if error.code() == ERROR_NO_MORE_FILES.to_hresult() => return Ok(()),
                Err(_) => return Err("Could not finish checking for other players".into()),
            }
        }
    })();
    let _ = unsafe { CloseHandle(snapshot) };
    result
}

fn is_owned_player(candidate: ProcessIdentity, owned: &[ProcessIdentity]) -> bool {
    owned.contains(&candidate)
}

pub struct VerifiedPlayer {
    path: PathBuf,
    _held_file: File,
}

impl VerifiedPlayer {
    pub fn registered() -> Result<Self, String> {
        let mut value = vec![0_u16; 32768];
        let mut bytes = (value.len() * size_of::<u16>()) as u32;
        // This is the official per-user install record, not the protocol handler or
        // a guess based on the most recently written version folder.
        let status = unsafe {
            RegGetValueW(
                HKEY_CURRENT_USER,
                w!("Software\\ROBLOX Corporation\\Environments\\roblox-player"),
                w!("clientExe"),
                RRF_RT_REG_SZ,
                None,
                Some(value.as_mut_ptr().cast()),
                Some(&mut bytes),
            )
        };
        if status.is_err() {
            return Err("Roblox's registered player is unavailable. Select the installed standard RobloxPlayerBeta.exe.".into());
        }
        let path = registered_path(
            value
                .get(..bytes as usize / size_of::<u16>())
                .ok_or("Roblox's registered player path is too long")?,
        )?;
        Self::open(&path)
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn open(path: &Path) -> Result<Self, String> {
        let path = path
            .canonicalize()
            .map_err(|_| "Roblox player file is unavailable")?;
        if !path
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.eq_ignore_ascii_case("RobloxPlayerBeta.exe"))
        {
            return Err("select the standard RobloxPlayerBeta.exe".into());
        }
        let file = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ.0)
            .open(&path)
            .map_err(|_| "could not hold the player executable for signature verification")?;
        publisher::verify(&path, HANDLE(file.as_raw_handle()))?;
        Ok(Self {
            path,
            _held_file: file,
        })
    }

    pub fn spawn(&self, uri: &Zeroizing<String>) -> Result<OwnedPlayer, String> {
        let child = Command::new(&self.path)
            .raw_arg(uri.as_str())
            .current_dir(
                self.path
                    .parent()
                    .expect("canonical executable has a directory"),
            )
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|error| {
                format!(
                    "native process start failed (Windows error {})",
                    error.raw_os_error().unwrap_or(0)
                )
            })?;
        Ok(OwnedPlayer {
            identity: process_identity(&child),
            child,
            managed_priority: false,
        })
    }
}

fn registered_path(value: &[u16]) -> Result<PathBuf, String> {
    let value = value
        .strip_suffix(&[0])
        .ok_or("Roblox's registered player path is invalid")?;
    let value = value
        .strip_prefix(&[b'"' as u16])
        .and_then(|unquoted| unquoted.strip_suffix(&[b'"' as u16]))
        .unwrap_or(value);
    if value.contains(&0) || value.contains(&(b'"' as u16)) {
        return Err("Roblox's registered player path is invalid".into());
    }
    let path = PathBuf::from(std::ffi::OsString::from_wide(value));
    if !path.is_absolute() {
        return Err("Roblox's registered player path is not absolute".into());
    }
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn coexistence_accepts_exact_process_birth_and_rejects_reused_pid() {
        let owned = ProcessIdentity {
            pid: 77,
            started_filetime: 11,
        };
        let reused = ProcessIdentity {
            pid: 77,
            started_filetime: 12,
        };
        assert!(is_owned_player(owned, &[owned]));
        assert!(!is_owned_player(reused, &[owned]));
        assert!(!is_owned_player(owned, &[]));
    }

    #[test]
    fn the_registered_player_is_a_path_and_never_a_command_line() {
        let path = r"C:\Roblox\Versions\version-test\RobloxPlayerBeta.exe";
        let quoted: Vec<_> = format!("\"{path}\"\0").encode_utf16().collect();
        assert_eq!(registered_path(&quoted).unwrap(), PathBuf::from(path));
        let command_line: Vec<_> = format!("\"{path}\" --unexpected\0")
            .encode_utf16()
            .collect();
        assert!(registered_path(&command_line).is_err());
    }

    fn fixture_player() -> OwnedPlayer {
        use ::windows::Win32::System::Threading::NORMAL_PRIORITY_CLASS;
        let windows = PathBuf::from(std::env::var_os("SystemRoot").unwrap());
        let child = Command::new(windows.join(r"System32\WindowsPowerShell\v1.0\powershell.exe"))
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Sleep -Seconds 20",
            ])
            .creation_flags(0x0800_0000 | NORMAL_PRIORITY_CLASS.0) // CREATE_NO_WINDOW
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        OwnedPlayer {
            identity: process_identity(&child),
            child,
            managed_priority: false,
        }
    }

    #[test]
    fn stopping_an_owned_process_leaves_a_different_process_running() {
        let mut owned = fixture_player();
        let mut other = fixture_player();
        let owned_identity = owned.identity();
        let other_identity = other.identity();
        let exact = owned_identity.unwrap();
        assert_eq!(observe_process(exact), ProcessStatus::Running);
        assert_eq!(
            observe_process(ProcessIdentity {
                started_filetime: exact.started_filetime - 1,
                ..exact
            }),
            ProcessStatus::Ended
        );
        let stop_result = owned.stop();
        let owned_exited = owned.exited();
        let other_exited = other.exited();
        let other_cleanup = other.stop();
        assert_ne!(owned_identity.unwrap().pid, other_identity.unwrap().pid);
        stop_result.unwrap();
        other_cleanup.unwrap();
        assert!(owned_exited.unwrap());
        assert!(!other_exited.unwrap());
        assert_eq!(observe_process(exact), ProcessStatus::Ended);
    }

    #[test]
    fn reads_native_metrics_and_restores_only_its_own_priority_change() {
        use telemetry::PriorityClass::{BelowNormal, Normal};

        let mut owned = fixture_player();
        let mut unrelated = fixture_player();
        let no_restore = owned.change_priority(PriorityAction::Restore);
        let sample = owned.performance_sample();
        let lowered = owned.change_priority(PriorityAction::Lower);
        let other_priority = unrelated.performance_sample();
        let restored = owned.change_priority(PriorityAction::Restore);
        let cleanup = owned.stop();
        let other_cleanup = unrelated.stop();
        cleanup.unwrap();
        other_cleanup.unwrap();

        assert!(no_restore.is_err());
        let metrics = sample.unwrap().metrics(None);
        assert!(metrics.working_set.is_some_and(|bytes| bytes > 0));
        assert!(metrics.private_commit.is_some_and(|bytes| bytes > 0));
        assert!(metrics.uptime_seconds.is_some());
        assert_eq!(metrics.priority.unwrap().class, Normal);
        let lowered = lowered.unwrap();
        assert_eq!(lowered.class, BelowNormal);
        assert!(lowered.can_restore);
        assert_eq!(
            other_priority
                .unwrap()
                .metrics(None)
                .priority
                .unwrap()
                .class,
            Normal
        );
        let restored = restored.unwrap();
        assert_eq!(restored.class, Normal);
        assert!(!restored.can_restore);
        assert!(owned.change_priority(PriorityAction::Lower).is_err());
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProcessIdentity {
    pub pid: u32,
    pub started_filetime: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProcessStatus {
    Running,
    Ended,
    Unknown,
}

pub fn observe_process(expected: ProcessIdentity) -> ProcessStatus {
    if expected.pid == 0 || expected.started_filetime == 0 {
        return ProcessStatus::Unknown;
    }
    // SAFETY: request only process-query and wait access. The handle is never inherited.
    let handle = match unsafe {
        OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            false,
            expected.pid,
        )
    } {
        Ok(handle) => handle,
        Err(error) if error.code() == ERROR_INVALID_PARAMETER.to_hresult() => {
            return ProcessStatus::Ended;
        }
        Err(_) => return ProcessStatus::Unknown,
    };
    let identity = process_identity_from_handle(handle, expected.pid);
    // SAFETY: handle is live and held until the non-blocking observation completes.
    let wait = unsafe { WaitForSingleObject(handle, 0) };
    let _ = unsafe { CloseHandle(handle) };
    match (identity, wait) {
        (_, WAIT_OBJECT_0) => ProcessStatus::Ended,
        (Some(identity), WAIT_TIMEOUT) if identity == expected => ProcessStatus::Running,
        (Some(_), WAIT_TIMEOUT) => ProcessStatus::Ended,
        _ => ProcessStatus::Unknown,
    }
}

pub struct OwnedPlayer {
    identity: Option<ProcessIdentity>,
    child: std::process::Child,
    managed_priority: bool,
}

impl OwnedPlayer {
    pub fn identity(&self) -> Option<ProcessIdentity> {
        self.identity
    }
    pub fn pid(&self) -> u32 {
        self.child.id()
    }

    pub fn exited(&mut self) -> io::Result<bool> {
        self.child.try_wait().map(|exit| exit.is_some())
    }

    pub fn has_window(&self) -> bool {
        self.window().is_some()
    }

    pub fn has_unique_visible_window(&self) -> bool {
        let Some(window) = self.find_matching_window(false, true) else {
            return false;
        };
        // SAFETY: recheck the candidate against the held process and current window state.
        unsafe {
            let mut pid = 0;
            GetWindowThreadProcessId(window, Some(&mut pid)) != 0
                && pid == self.pid()
                && IsWindowVisible(window).as_bool()
                && !IsIconic(window).as_bool()
                && GetWindow(window, GW_OWNER).is_err()
                && self.owns_current_pid()
        }
    }

    pub fn focus(&self) -> Result<(), &'static str> {
        let window = self
            .window()
            .ok_or("The owned Roblox window is unavailable")?;
        // SAFETY: the window was found for the exact process still held by this OwnedPlayer.
        unsafe {
            let mut pid = 0;
            GetWindowThreadProcessId(window, Some(&mut pid));
            if pid != self.pid() || !self.owns_current_pid() {
                return Err("The Roblox window changed");
            }
            if IsIconic(window).as_bool() {
                let _ = ShowWindowAsync(window, SW_RESTORE);
            }
            if !SetForegroundWindow(window).as_bool() {
                return Err("Windows declined focus. Select Roblox in the taskbar.");
            }
        }
        Ok(())
    }

    fn window(&self) -> Option<HWND> {
        self.find_window(false)
    }

    fn find_window(&self, layout_only: bool) -> Option<HWND> {
        self.find_matching_window(layout_only, layout_only)
    }

    fn find_matching_window(&self, layout_only: bool, require_unique: bool) -> Option<HWND> {
        let identity = self.identity?;
        let mut search = WindowSearch {
            pid: identity.pid,
            found: None,
            layout_only,
            require_unique,
        };
        // SAFETY: enumeration is synchronous. The callback borrows this stack value only during the call.
        let enumeration =
            unsafe { EnumWindows(Some(window_callback), LPARAM((&raw mut search) as isize)) };
        if require_unique && enumeration.is_err() {
            return None;
        }
        search.found.filter(|_| self.owns_current_pid())
    }

    fn owns_current_pid(&self) -> bool {
        self.identity
            .is_some_and(|identity| current_process_identity(identity.pid) == Some(identity))
    }

    pub fn stop(&mut self) -> io::Result<()> {
        if self.exited()? {
            return Ok(());
        }
        if let Err(error) = self.child.kill()
            && !self.exited()?
        {
            return Err(error);
        }
        let deadline = Instant::now() + Duration::from_secs(10);
        while !self.exited()? {
            if Instant::now() >= deadline {
                return Err(io::ErrorKind::TimedOut.into());
            }
            std::thread::sleep(Duration::from_millis(25));
        }
        Ok(())
    }
}

fn process_identity(child: &std::process::Child) -> Option<ProcessIdentity> {
    process_identity_from_handle(HANDLE(child.as_raw_handle()), child.id())
}

fn current_process_identity(pid: u32) -> Option<ProcessIdentity> {
    // SAFETY: this opens a read-only process handle. The handle is released before returning.
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }.ok()?;
    let identity = process_identity_from_handle(handle, pid);
    let _ = unsafe { CloseHandle(handle) };
    identity
}

fn process_identity_from_handle(handle: HANDLE, pid: u32) -> Option<ProcessIdentity> {
    let mut creation = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: the caller keeps its process handle alive and all outputs are writable.
    unsafe { GetProcessTimes(handle, &mut creation, &mut exit, &mut kernel, &mut user) }.ok()?;
    let time = (u64::from(creation.dwHighDateTime) << 32) | u64::from(creation.dwLowDateTime);
    (time != 0).then_some(ProcessIdentity {
        pid,
        started_filetime: time,
    })
}

struct WindowSearch {
    pid: u32,
    found: Option<HWND>,
    layout_only: bool,
    require_unique: bool,
}

unsafe extern "system" fn window_callback(window: HWND, param: LPARAM) -> BOOL {
    // SAFETY: caller supplies the live WindowSearch pointer to EnumWindows.
    let search = unsafe { &mut *(param.0 as *mut WindowSearch) };
    let mut pid = 0;
    unsafe {
        GetWindowThreadProcessId(window, Some(&mut pid));
        if pid == search.pid
            && IsWindowVisible(window).as_bool()
            && GetWindow(window, GW_OWNER).is_err()
            && (!search.layout_only || arrange::normal_window(window))
        {
            if search.found.is_some() {
                search.found = None;
                return BOOL(0);
            }
            search.found = Some(window);
        }
    }
    BOOL::from(search.require_unique || search.found.is_none())
}
