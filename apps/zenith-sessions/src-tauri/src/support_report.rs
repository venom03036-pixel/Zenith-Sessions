use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    os::windows::{
        ffi::OsStrExt,
        fs::{MetadataExt, OpenOptionsExt},
    },
    path::{Path, PathBuf},
    sync::{Arc, Mutex, atomic::Ordering},
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use sha2::{Digest, Sha256};
use tauri::{State, async_runtime::spawn_blocking};
use uuid::Uuid;
use windows::{
    Win32::Storage::FileSystem::{
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        FILE_SHARE_READ, FILE_SHARE_WRITE, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    },
    core::PCWSTR,
};
use zenith_session_launch::{admission::Admission, windows::memory_sample};

use crate::{
    STATE_ERROR, Workspace, panic_hotkey::PanicHotkey, panic_stop::PanicDiagnostics,
    session::LaunchDiagnostics,
};

const SCHEMA_VERSION: u32 = 1;
const FOLDER: &str = "support-reports";
const SAVE_ERROR: &str = "Could not save the support report in Zenith's local data folder.";

pub struct SupportReports {
    folder: PathBuf,
    saving: Mutex<()>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SupportReport {
    schema_version: u32,
    captured_unix_seconds: Option<u64>,
    application_version: &'static str,
    architecture: &'static str,
    executable_sha256: Option<String>,
    memory_admission: Option<Admission>,
    launches: LaunchDiagnostics,
    panic: PanicDiagnostics,
}

#[derive(Serialize)]
pub struct SavedReport {
    path: String,
}

impl SupportReports {
    pub fn new(app_local_data: &Path) -> Self {
        Self {
            folder: app_local_data.join(FOLDER),
            saving: Mutex::new(()),
        }
    }

    fn write(&self, report: &SupportReport) -> Result<PathBuf, String> {
        let bytes = serde_json::to_vec_pretty(report).map_err(|_| SAVE_ERROR)?;
        write_document(&self.folder, Uuid::new_v4(), &bytes).map_err(|_| SAVE_ERROR.into())
    }
}

impl SupportReport {
    fn capture(launches: LaunchDiagnostics, panic: PanicDiagnostics) -> Self {
        Self {
            schema_version: SCHEMA_VERSION,
            captured_unix_seconds: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .ok()
                .map(|time| time.as_secs()),
            application_version: env!("CARGO_PKG_VERSION"),
            architecture: std::env::consts::ARCH,
            executable_sha256: std::env::current_exe()
                .ok()
                .and_then(|path| sha256(&path).ok()),
            memory_admission: memory_sample()
                .ok()
                .map(|sample| sample.admission(Instant::now())),
            launches,
            panic,
        }
    }
}

#[tauri::command]
pub async fn export_support_report(
    state: State<'_, Arc<Workspace>>,
) -> Result<SavedReport, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let _saving = state
            .support_reports
            .saving
            .try_lock()
            .map_err(|_| "A support report is already being saved.")?;
        if state.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        let (launches, panic) = {
            let owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            let shortcut = state.hotkey.as_ref().is_some_and(PanicHotkey::is_running);
            (
                owner.launch_diagnostics(Instant::now()),
                state.panic.view(&owner, shortcut).diagnostics(),
            )
        };
        let report = SupportReport::capture(launches, panic);
        if state.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        let path = state.support_reports.write(&report)?;
        Ok(SavedReport {
            path: path.to_string_lossy().into_owned(),
        })
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

fn sha256(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hash = Sha256::new();
    let mut buffer = [0; 16 * 1024];
    loop {
        let length = file.read(&mut buffer)?;
        if length == 0 {
            return Ok(format!("{:x}", hash.finalize()));
        }
        hash.update(&buffer[..length]);
    }
}

fn report_directory(folder: &Path) -> io::Result<File> {
    match fs::create_dir(folder) {
        Ok(()) => (),
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => (),
        Err(error) => return Err(error),
    }
    // Keep the directory entry open without delete sharing during publication. Windows needs
    // write sharing on the directory to rename the temporary child into its final name.
    let directory = OpenOptions::new()
        .read(true)
        .share_mode((FILE_SHARE_READ | FILE_SHARE_WRITE).0)
        .custom_flags((FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT).0)
        .open(folder)?;
    let metadata = directory.metadata()?;
    if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Redirected report directory.",
        ));
    }
    Ok(directory)
}

fn write_document(folder: &Path, id: Uuid, bytes: &[u8]) -> io::Result<PathBuf> {
    let _directory = report_directory(folder)?;
    let destination = folder.join(format!("zenith-support-{id}.json"));
    let temporary = folder.join(format!(".zenith-support-{id}.pending"));
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)?;
    let written = file.write_all(bytes).and_then(|()| file.sync_all());
    drop(file);
    let published = written.and_then(|()| publish(&temporary, &destination));
    if published.is_err() {
        let _ = fs::remove_file(temporary);
    }
    published.map(|()| destination)
}

fn publish(temporary: &Path, destination: &Path) -> io::Result<()> {
    let wide = |path: &Path| {
        path.as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>()
    };
    let temporary = wide(temporary);
    let destination = wide(destination);
    // SAFETY: these terminated buffers are alive for the call. No replacement or copy is requested.
    unsafe {
        MoveFileExW(
            PCWSTR(temporary.as_ptr()),
            PCWSTR(destination.as_ptr()),
            MOVEFILE_WRITE_THROUGH,
        )
    }
    .map_err(|_| io::Error::last_os_error())
}

pub fn self_check() -> Result<String, String> {
    use crate::{panic_stop::PanicStop, session::SessionOwner};

    let root = std::env::temp_dir().join(format!("zenith-report-check-{}", Uuid::new_v4()));
    fs::create_dir(&root).map_err(|_| "Could not create a disposable support directory.")?;
    let reports = SupportReports::new(&root);
    let owner = SessionOwner::default();
    let report = SupportReport::capture(
        owner.launch_diagnostics(Instant::now()),
        PanicStop::default().view(&owner, false).diagnostics(),
    );
    let result = reports.write(&report).and_then(|path| {
        let read = fs::read(&path).map_err(|_| "Could not reopen the disposable report.");
        let removed = fs::remove_file(&path).map_err(|_| "Could not remove the disposable report.");
        let value: serde_json::Value = serde_json::from_slice(&read?)
            .map_err(|_| "The disposable report was invalid JSON.")?;
        removed?;
        if value["schemaVersion"] != SCHEMA_VERSION
            || value["executableSha256"]
                .as_str()
                .is_none_or(|hash| hash.len() != 64)
            || value["launches"]["activeAttempts"] != 0
        {
            return Err("The disposable report did not contain the expected fields.".into());
        }
        Ok("Saved and reopened a report from a synthetic workspace.".into())
    });
    let _ = fs::remove_dir(&reports.folder);
    fs::remove_dir(&root).map_err(|_| "Could not clean up the disposable support directory.")?;
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reports_publish_without_overwriting_and_remove_failed_temporary_files() {
        let root = std::env::temp_dir().join(format!("zenith-report-test-{}", Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let folder = root.join(FOLDER);
        let id = Uuid::new_v4();
        let path = write_document(&folder, id, b"{\"schemaVersion\":1}").unwrap();
        assert!(write_document(&folder, id, b"would overwrite").is_err());
        assert_eq!(fs::read(&path).unwrap(), b"{\"schemaVersion\":1}");
        assert_eq!(fs::read_dir(&folder).unwrap().count(), 1);
        fs::remove_file(path).unwrap();
        fs::remove_dir(folder).unwrap();
        fs::remove_dir(root).unwrap();
    }

    #[test]
    fn rejects_an_existing_file_at_the_dedicated_directory_without_replacing_it() {
        let root = std::env::temp_dir().join(format!("zenith-report-test-{}", Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        let folder = root.join(FOLDER);
        fs::write(&folder, b"keep this").unwrap();
        assert!(write_document(&folder, Uuid::new_v4(), b"not a report").is_err());
        assert_eq!(fs::read(&folder).unwrap(), b"keep this");
        fs::remove_file(folder).unwrap();
        fs::remove_dir(root).unwrap();
    }
}
