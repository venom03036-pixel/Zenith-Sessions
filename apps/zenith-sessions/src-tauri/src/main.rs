#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod library;
mod panic_hotkey;
mod panic_stop;
mod recovery;
mod self_check;
mod session;
mod sign_in;

use std::{
    collections::HashMap,
    path::PathBuf,
    process::ExitCode,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};

use library::{LegacyView, Library, MigrationReport, SavedCredential};
use panic_hotkey::PanicHotkey;
use panic_stop::{PanicStop, PanicView};
use serde::Serialize;
use session::{ClientPerformance, LaunchState, Session, SessionOwner, WindowPlacement};
use tauri::ipc::{InvokeBody, Request};
use tauri::{AppHandle, Manager, State, WindowEvent, async_runtime::spawn_blocking};
use uuid::Uuid;
use zenith_session_launch::{
    admission::Admission,
    directory::{Directory, Game, ServerPage, public_place},
    profile::{Page, Person, PrivateProfile, Profile},
    target::Target,
    windows::{
        MAX_LEGACY_MIGRATION_BYTES, PriorityAction, PriorityState, SavedIdentity, memory_sample,
    },
};

struct Workspace {
    library: Mutex<Library>,
    owner: Arc<Mutex<SessionOwner>>,
    panic: Arc<PanicStop>,
    hotkey: Option<PanicHotkey>,
    directory: Directory,
    sign_in: Mutex<sign_in::SignInState>,
    sign_in_directory: PathBuf,
    logs: PathBuf,
    exiting: AtomicBool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    accounts: Vec<SavedIdentity>,
    sessions: Vec<Session>,
    launch: LaunchState,
    recovery: recovery::RecoveryView,
    panic: PanicView,
    admission: Option<Admission>,
    sign_in_attempt: Option<Uuid>,
    sign_in_error: Option<String>,
    saved_sign_in: Option<sign_in::SavedSignIn>,
}

const STATE_ERROR: &str = "Zenith could not access the workspace state";

impl Workspace {
    fn can_dispatch(&self, epoch: u64) -> bool {
        !self.exiting.load(Ordering::Acquire) && self.panic.permits(epoch)
    }

    fn read_saved_account<T>(
        &self,
        id: Uuid,
        read: impl FnOnce(&mut SavedCredential) -> Result<T, String>,
    ) -> Result<T, String> {
        if self.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        let mut saved = self
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .credential(id)?;
        let result = read(&mut saved);
        let library = self.library.lock().map_err(|_| STATE_ERROR)?;
        if self.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        library.finish_credential(&saved)?;
        result
    }
}

#[tauri::command]
async fn workspace_snapshot(state: State<'_, Arc<Workspace>>) -> Result<Snapshot, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let observation = state.owner.lock().map_err(|_| STATE_ERROR)?.observation();
        let evidence = observation.map(|sample| (sample, sample.read(&state.logs)));
        let (sessions, launch, recovery, panic) = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            owner.reconcile(evidence);
            let shortcut = state.hotkey.as_ref().is_some_and(PanicHotkey::is_running);
            (
                owner.rows(),
                owner.launch_state(),
                owner.recovery(),
                state.panic.view(&owner, shortcut),
            )
        };
        let accounts = state.library.lock().map_err(|_| STATE_ERROR)?.accounts();
        let admission = memory_sample()
            .ok()
            .map(|sample| sample.admission(Instant::now()));
        let sign_in = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        Ok(Snapshot {
            accounts,
            sessions,
            launch,
            recovery,
            panic,
            admission,
            sign_in_attempt: sign_in.active.as_ref().map(|flow| flow.attempt),
            sign_in_error: sign_in.active.as_ref().and_then(|flow| flow.error.clone()),
            saved_sign_in: sign_in.saved.clone(),
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
async fn import_legacy(
    state: State<'_, Arc<Workspace>>,
    request: Request<'_>,
) -> Result<MigrationReport, String> {
    let InvokeBody::Raw(bytes) = request.body() else {
        return Err("Select a protected Zenith legacy workspace file.".into());
    };
    if bytes.is_empty() || bytes.len() > MAX_LEGACY_MIGRATION_BYTES {
        return Err("The migration file has an invalid size.".into());
    }
    let bytes = bytes.clone();
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .import_legacy(bytes)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn legacy_workspace(
    state: State<'_, Arc<Workspace>>,
    offset: usize,
) -> Result<Option<LegacyView>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .legacy_view(offset)
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
async fn account_headshots(
    state: State<'_, Arc<Workspace>>,
) -> Result<HashMap<Uuid, String>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let accounts = state.library.lock().map_err(|_| STATE_ERROR)?.accounts();
        let ids: Vec<_> = accounts
            .into_iter()
            .map(|account| (account.profile_id, account.user_id))
            .collect();
        state.directory.headshots(&ids)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn account_profile(state: State<'_, Arc<Workspace>>, id: Uuid) -> Result<Profile, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let account = state.library.lock().map_err(|_| STATE_ERROR)?.account(id)?;
        state.directory.public_profile(account.user_id)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn account_favorites(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    cursor: Option<String>,
) -> Result<Page<Game>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let account = state.library.lock().map_err(|_| STATE_ERROR)?.account(id)?;
        state
            .directory
            .favorites(account.user_id, cursor.as_deref())
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn account_private_profile(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
) -> Result<PrivateProfile, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state.read_saved_account(id, |saved| {
            state
                .directory
                .private_profile(saved.identity.user_id, &mut saved.credential)
        })
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn account_friends(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    cursor: String,
) -> Result<Page<Person>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state.read_saved_account(id, |saved| {
            state
                .directory
                .friends(saved.identity.user_id, &mut saved.credential, &cursor)
        })
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
    epoch: String,
) -> Result<(), String> {
    let place = public_place(&place_id)?;
    if job.is_some_and(|job| job.is_nil()) {
        return Err("Select a valid public server.".into());
    }
    let target = Target { place, job };
    let epoch = epoch
        .parse()
        .map_err(|_| "Refresh the launch state before starting Roblox.")?;
    if !state.can_dispatch(epoch) {
        return Err("The launch state changed. Review it and choose Launch again.".into());
    }
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let (mut saved, attempt) = {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            if !state.can_dispatch(epoch) {
                return Err("Zenith stopped this launch request.".into());
            }
            let saved = state
                .library
                .lock()
                .map_err(|_| STATE_ERROR)?
                .credential(id)?;
            if !state.can_dispatch(epoch) {
                return Err("Zenith stopped this launch request.".into());
            }
            let attempt = owner.reserve(&saved.identity, &target, allow_pair, try_one)?;
            (saved, attempt)
        };
        let launch_state = Arc::clone(&state);
        let prepared = session::prepare(
            &saved.identity,
            &mut saved.credential,
            target,
            try_one,
            move || {
                launch_state.can_dispatch(epoch)
                    && launch_state.owner.lock().is_ok_and(|owner| {
                        launch_state.can_dispatch(epoch) && owner.permits(attempt)
                    })
            },
        );
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        let result = (|| {
            if !owner.permits(attempt) || !state.can_dispatch(epoch) {
                return Err("The launch was cancelled or the first client stopped.".into());
            }
            {
                let library = state.library.lock().map_err(|_| STATE_ERROR)?;
                if !state.can_dispatch(epoch) {
                    return Err("Zenith stopped this launch request.".into());
                }
                library.finish_credential(&saved)?;
            }
            let prepared = prepared?;
            owner.dispatch(prepared, attempt, try_one, || state.can_dispatch(epoch))
        })();
        if let Err(error) = &result {
            owner.fail_preparation(attempt, error.clone(), !state.can_dispatch(epoch));
        }
        result
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn dismiss_recovered_launch(
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .dismiss_recovered(attempt)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn archive_launch_journal(state: State<'_, Arc<Workspace>>) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .archive_unreadable_journal()
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
async fn panic_stop(state: State<'_, Arc<Workspace>>) -> Result<(), String> {
    let epoch = state.panic.trip();
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.panic.drain(epoch, &state.owner))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn resume_launches(state: State<'_, Arc<Workspace>>, epoch: String) -> Result<(), String> {
    let epoch = epoch
        .parse()
        .map_err(|_| "Refresh the Panic stop state before resuming.")?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if state.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        state.panic.resume(epoch, &owner)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
fn focus_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    let epoch = state.panic.permit()?;
    let owner = state
        .owner
        .try_lock()
        .map_err(|_| "Zenith is updating the session. Try again.")?;
    if !state.can_dispatch(epoch) {
        return Err("Zenith stopped this window action.".into());
    }
    owner.focus(attempt)
}

#[tauri::command]
async fn client_performance(
    state: State<'_, Arc<Workspace>>,
) -> Result<Vec<ClientPerformance>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        Ok(state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .client_performance())
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn set_client_priority(
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
    action: PriorityAction,
) -> Result<PriorityState, String> {
    let epoch = state.panic.permit()?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) {
            return Err("Zenith stopped this priority action.".into());
        }
        owner.set_client_priority(attempt, action)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn tile_windows(
    app: AppHandle,
    state: State<'_, Arc<Workspace>>,
    attempts: Vec<Uuid>,
) -> Result<Vec<WindowPlacement>, String> {
    if attempts.is_empty() || attempts.len() > 16 {
        return Err("Select between one and sixteen confirmed windows.".into());
    }
    let epoch = state.panic.permit()?;
    let anchor = app
        .get_webview_window("main")
        .ok_or("The Zenith window is unavailable.")?
        .hwnd()
        .map_err(|_| "The Zenith window is unavailable.")?
        .0 as isize;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .tile_windows(&attempts, anchor, || !state.can_dispatch(epoch))
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
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
            let directory = Directory::new().map_err(std::io::Error::other)?;
            let owner = Arc::new(Mutex::new(SessionOwner::open(
                root.join("library").join("launch-journal.json"),
            )));
            let panic = Arc::new(PanicStop::default());
            let hotkey_owner = Arc::clone(&owner);
            let hotkey_panic = Arc::clone(&panic);
            let hotkey = PanicHotkey::start(
                u32::from(windows::Win32::UI::Input::KeyboardAndMouse::VK_P.0),
                move || {
                    let epoch = hotkey_panic.trip();
                    if let Err(error) = hotkey_panic.drain(epoch, &hotkey_owner) {
                        eprintln!("{error}");
                    }
                },
            );
            app.manage(Arc::new(Workspace {
                library: Mutex::new(library),
                owner,
                panic,
                hotkey,
                directory,
                sign_in: Mutex::new(sign_in::SignInState::default()),
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
            import_legacy,
            legacy_workspace,
            remove_account,
            account_headshots,
            account_profile,
            account_favorites,
            account_private_profile,
            account_friends,
            search_public_games,
            browse_public_servers,
            launch_account,
            dismiss_recovered_launch,
            archive_launch_journal,
            stop_session,
            stop_all_sessions,
            panic_stop,
            resume_launches,
            focus_session,
            client_performance,
            set_client_priority,
            tile_windows,
            sign_in::begin_sign_in,
            sign_in::finish_sign_in,
            sign_in::cancel_sign_in
        ])
        .run(context)
        .expect("could not start Zenith Sessions");
    ExitCode::SUCCESS
}
