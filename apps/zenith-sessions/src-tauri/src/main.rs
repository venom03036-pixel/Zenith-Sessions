#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod library;
mod self_check;
mod session;
mod sign_in;

use std::{
    path::PathBuf,
    process::ExitCode,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};

use library::Library;
use serde::Serialize;
use session::{LaunchState, Session, SessionOwner};
use tauri::{Manager, State, WindowEvent, async_runtime::spawn_blocking};
use uuid::Uuid;
use zenith_session_launch::{
    admission::Admission,
    directory::{Directory, Game, ServerPage, public_place},
    target::Target,
    windows::{SavedIdentity, memory_sample},
};

struct Workspace {
    library: Mutex<Library>,
    owner: Mutex<SessionOwner>,
    directory: Directory,
    sign_in: Mutex<Option<sign_in::SignIn>>,
    sign_in_directory: PathBuf,
    logs: PathBuf,
    exiting: AtomicBool,
}

impl Workspace {
    fn clear_sign_in(&self, attempt: Uuid) {
        if let Ok(mut active) = self.sign_in.lock()
            && active.as_ref().is_some_and(|flow| flow.attempt == attempt)
        {
            *active = None;
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    accounts: Vec<SavedIdentity>,
    sessions: Vec<Session>,
    launch: LaunchState,
    admission: Option<Admission>,
    sign_in_attempt: Option<Uuid>,
}

const STATE_ERROR: &str = "Zenith could not access the workspace state";

#[tauri::command]
async fn workspace_snapshot(state: State<'_, Arc<Workspace>>) -> Result<Snapshot, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let observation = state.owner.lock().map_err(|_| STATE_ERROR)?.observation();
        let evidence = observation.map(|sample| (sample, sample.read(&state.logs)));
        let (sessions, launch) = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            owner.reconcile(evidence);
            (owner.rows(), owner.launch_state())
        };
        let accounts = state.library.lock().map_err(|_| STATE_ERROR)?.accounts();
        let admission = memory_sample()
            .ok()
            .map(|sample| sample.admission(Instant::now()));
        let sign_in_attempt = state
            .sign_in
            .lock()
            .map_err(|_| STATE_ERROR)?
            .as_ref()
            .map(|flow| flow.attempt);
        Ok(Snapshot {
            accounts,
            sessions,
            launch,
            admission,
            sign_in_attempt,
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
async fn search_public_games(
    state: State<'_, Arc<Workspace>>,
    query: String,
) -> Result<Vec<Game>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.directory.search(&query))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn browse_public_servers(
    state: State<'_, Arc<Workspace>>,
    place: String,
    cursor: Option<String>,
) -> Result<ServerPage, String> {
    let place = public_place(&place)?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.directory.servers(place, cursor.as_deref()))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn launch_account(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    place_id: String,
    job: Option<Uuid>,
    try_one: bool,
    allow_pair: bool,
) -> Result<(), String> {
    let place = public_place(&place_id)?;
    if job.is_some_and(|job| job.is_nil()) {
        return Err("Select a valid public server.".into());
    }
    let target = Target { place, job };
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let (identity, credential, attempt) = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            if state.exiting.load(Ordering::Acquire) {
                return Err("Zenith is closing.".into());
            }
            let (identity, credential) = state
                .library
                .lock()
                .map_err(|_| STATE_ERROR)?
                .credential(id)?;
            let attempt = owner.reserve(&identity, &target, allow_pair, try_one)?;
            (identity, credential, attempt)
        };
        let prepared = session::prepare(identity, credential, target, try_one, || {
            !state.exiting.load(Ordering::Acquire)
                && state.owner.lock().is_ok_and(|owner| owner.permits(attempt))
        });
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        let result = prepared.and_then(|prepared| {
            owner.dispatch(prepared, attempt, try_one, || {
                !state.exiting.load(Ordering::Acquire)
            })
        });
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
        owner.stop(attempt)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn stop_all_sessions(state: State<'_, Arc<Workspace>>) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.owner.lock().map_err(|_| STATE_ERROR)?.stop_all(false))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
fn focus_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    let owner = state
        .owner
        .try_lock()
        .map_err(|_| "Zenith is updating the session. Try again.")?;
    owner.focus(attempt)
}

fn main() -> ExitCode {
    let context = tauri::generate_context!();
    if std::env::args_os()
        .nth(1)
        .is_some_and(|arg| arg == "--self-check")
    {
        return self_check::run(&context);
    }
    tauri::Builder::default()
        .setup(|app| {
            let root = app.path().app_local_data_dir()?;
            let library = Library::open(root.join("library")).map_err(std::io::Error::other)?;
            let logs = app.path().local_data_dir()?.join("Roblox").join("logs");
            app.manage(Arc::new(Workspace {
                library: Mutex::new(library),
                owner: Mutex::new(SessionOwner::default()),
                directory: Directory::new().map_err(std::io::Error::other)?,
                sign_in: Mutex::new(None),
                sign_in_directory: root.join("isolated-sign-in"),
                logs,
                exiting: AtomicBool::new(false),
            }));
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() != "main" {
                if matches!(event, WindowEvent::Destroyed) {
                    let state = window.state::<Arc<Workspace>>();
                    sign_in::destroyed(state.inner(), window.label());
                }
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
                        .is_ok_and(|mut owner| owner.stop_all(true).is_ok());
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
            search_public_games,
            browse_public_servers,
            launch_account,
            stop_session,
            stop_all_sessions,
            focus_session,
            sign_in::begin_sign_in,
            sign_in::finish_sign_in,
            sign_in::cancel_sign_in
        ])
        .run(context)
        .expect("could not start Zenith Sessions");
    ExitCode::SUCCESS
}
