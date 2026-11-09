#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod library;
mod session;

use std::{
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};

use library::Library;
use serde::Serialize;
use session::{Session, SessionOwner};
use tauri::{Manager, State, WindowEvent, async_runtime::spawn_blocking};
use uuid::Uuid;
use zenith_session_launch::{
    admission::Admission,
    windows::{SavedIdentity, memory_sample},
};

struct Workspace {
    library: Mutex<Library>,
    owner: Mutex<SessionOwner>,
    logs: PathBuf,
    exiting: AtomicBool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    accounts: Vec<SavedIdentity>,
    session: Option<Session>,
    admission: Option<Admission>,
}

const STATE_ERROR: &str = "Zenith could not access the workspace state";

#[tauri::command]
async fn workspace_snapshot(state: State<'_, Arc<Workspace>>) -> Result<Snapshot, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let observation = state.owner.lock().map_err(|_| STATE_ERROR)?.observation();
        let evidence = observation.map(|sample| (sample, sample.read(&state.logs)));
        let session = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            owner.reconcile(evidence);
            owner.row()
        };
        let accounts = state.library.lock().map_err(|_| STATE_ERROR)?.accounts();
        let admission = memory_sample()
            .ok()
            .map(|sample| sample.admission(Instant::now()));
        Ok(Snapshot {
            accounts,
            session,
            admission,
        })
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn import_account(
    state: State<'_, Arc<Workspace>>,
    encrypted: Vec<u8>,
) -> Result<SavedIdentity, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .import(encrypted)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn remove_account(state: State<'_, Arc<Workspace>>, id: Uuid) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if owner.uses(id) {
            return Err("Stop this account's session before removing it".into());
        }
        state.library.lock().map_err(|_| STATE_ERROR)?.remove(id)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn launch_account(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    place_id: String,
    try_one: bool,
) -> Result<(), String> {
    let place = place_id
        .parse::<u64>()
        .ok()
        .filter(|place| *place > 0 && *place <= i64::MAX as u64)
        .ok_or("Enter a valid Roblox place ID")?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let (identity, credential, attempt) = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            let (identity, credential) = state
                .library
                .lock()
                .map_err(|_| STATE_ERROR)?
                .credential(id)?;
            let attempt = owner.reserve(&identity, place)?;
            (identity, credential, attempt)
        };
        let prepared = session::prepare(identity, credential, place, try_one, || {
            state.owner.lock().is_ok_and(|owner| owner.permits(attempt))
        });
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        let result = prepared.and_then(|prepared| owner.dispatch(prepared, attempt, try_one));
        if let Err(error) = &result {
            owner.fail_preparation(attempt, error.clone());
        }
        result
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn stop_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        owner.require_attempt(attempt)?;
        owner.stop(false)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
fn focus_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    let owner = state
        .owner
        .try_lock()
        .map_err(|_| "Zenith is updating the session. Try again.")?;
    owner.require_attempt(attempt)?;
    owner.focus()
}

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let library = Library::open(app.path().app_local_data_dir()?.join("library"))
                .map_err(std::io::Error::other)?;
            let logs = app.path().local_data_dir()?.join("Roblox").join("logs");
            app.manage(Arc::new(Workspace {
                library: Mutex::new(library),
                owner: Mutex::new(SessionOwner::default()),
                logs,
                exiting: AtomicBool::new(false),
            }));
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() != "main" {
                return;
            }
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let state = Arc::clone(window.state::<Arc<Workspace>>().inner());
                if state.exiting.swap(true, Ordering::AcqRel) {
                    return;
                }
                let app = window.app_handle().clone();
                spawn_blocking(move || {
                    let stopped = state
                        .owner
                        .lock()
                        .is_ok_and(|mut owner| owner.stop(true).is_ok());
                    if stopped {
                        app.exit(0);
                    } else {
                        state.exiting.store(false, Ordering::Release);
                    }
                });
            }
        })
        .invoke_handler(tauri::generate_handler![
            workspace_snapshot,
            import_account,
            remove_account,
            launch_account,
            stop_session,
            focus_session
        ])
        .run(tauri::generate_context!())
        .expect("could not start Zenith Sessions");
}
