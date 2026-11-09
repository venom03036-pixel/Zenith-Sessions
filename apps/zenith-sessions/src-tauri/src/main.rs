#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod library;
mod macro_catalog;
mod macro_playback;
mod panic_hotkey;
mod panic_stop;
mod recovery;
mod self_check;
mod server_history;
mod session;
mod session_monitor;
mod sign_in;
mod support_report;

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

use library::{
    AssignmentRecipient, AssignmentTarget, AssignmentView, LegacyView, Library, MigrationReport,
    PrivateServerReference, PrivateServerRow, SavedCredential,
};
use macro_catalog::{CatalogView, Choice as MacroChoice, Cursor as CatalogCursor, MacroCatalog};
use macro_playback::{MacroPlayback, MacroRunView, ReviewKey};
use panic_hotkey::PanicHotkey;
use panic_stop::{PanicStop, PanicView};
use serde::{Deserialize, Serialize};
use session::{
    ClientPerformance, FailureStage, LaunchState, PlaybackTarget, RetryRequest, Session,
    SessionOwner, WindowPlacement,
};
use session_monitor::SessionMonitor;
use support_report::SupportReports;
use tauri::ipc::{InvokeBody, Request};
use tauri::{AppHandle, Manager, State, WindowEvent, async_runtime::spawn_blocking};
use uuid::Uuid;
use zenith_session_launch::{
    admission::Admission,
    auth::RobloxAuth,
    directory::{Directory, Game, GameArt, GameDetails, ServerFilters, ServerPage, public_place},
    private_invite::PrivateInvite,
    profile::{Page, Person, PrivateProfile, Profile},
    target::Target,
    windows::{
        EfficiencyAction, EfficiencyState, MAX_LEGACY_MIGRATION_BYTES, PriorityAction,
        PriorityState, SavedIdentity, WindowVisibility, memory_sample,
    },
};
use zeroize::Zeroizing;

struct Workspace {
    library: Mutex<Library>,
    macro_catalog: MacroCatalog,
    macro_playback: MacroPlayback,
    owner: Arc<Mutex<SessionOwner>>,
    panic: Arc<PanicStop>,
    hotkey: Option<PanicHotkey>,
    directory: Directory,
    sign_in: Mutex<sign_in::SignInState>,
    sign_in_directory: PathBuf,
    logs: PathBuf,
    exiting: AtomicBool,
    monitor: Mutex<Option<SessionMonitor>>,
    support_reports: SupportReports,
}

#[tauri::command]
async fn saved_macro_catalog(
    state: State<'_, Arc<Workspace>>,
    cursor: Option<CatalogCursor>,
) -> Result<CatalogView, String> {
    if state.exiting.load(Ordering::Acquire) {
        return Err("Zenith is closing.".into());
    }
    let result = state
        .macro_catalog
        .page(cursor)
        .await
        .map_err(str::to_owned)?;
    if state.exiting.load(Ordering::Acquire) {
        return Err("Zenith is closing.".into());
    }
    Ok(result)
}

#[tauri::command]
async fn macro_assignments(
    state: State<'_, Arc<Workspace>>,
    offset: usize,
) -> Result<AssignmentView, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .assignments(offset)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[derive(Deserialize)]
#[serde(tag = "action", deny_unknown_fields)]
enum AssignmentEdit {
    Save { choice: MacroChoice },
    Remove,
}

#[tauri::command]
async fn edit_macro_assignment(
    state: State<'_, Arc<Workspace>>,
    target: AssignmentTarget,
    stamp: String,
    offset: usize,
    edit: AssignmentEdit,
) -> Result<AssignmentView, String> {
    if state.exiting.load(Ordering::Acquire) {
        return Err("Zenith is closing.".into());
    }
    let selected = match edit {
        AssignmentEdit::Save { choice } => Some(state.macro_catalog.verify_choice(choice).await?),
        AssignmentEdit::Remove => None,
    };
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let library = state.library.lock().map_err(|_| STATE_ERROR)?;
        if state.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        library.edit_assignment(&stamp, target, selected, offset)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MacroTargetReview {
    target_name: String,
    macro_name: String,
    recipients: Vec<MacroRecipientReview>,
    playback_review: Option<Uuid>,
}

#[derive(Serialize)]
struct MacroRecipientReview {
    name: String,
    session: Option<PlaybackTarget>,
}

#[tauri::command]
async fn review_macro_targets(
    state: State<'_, Arc<Workspace>>,
    target: AssignmentTarget,
    stamp: String,
    choice: MacroChoice,
    epoch: String,
) -> Result<MacroTargetReview, String> {
    const STALE: &str =
        "The workspace stopped or changed. Review playback targets again after resuming.";
    let epoch = epoch.parse().map_err(|_| STALE)?;
    if !state.can_dispatch(epoch) {
        return Err(STALE.into());
    }
    let host = choice.host_session();
    let selected = state.macro_catalog.verify_choice(choice).await?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) {
            return Err(STALE.into());
        }
        let library = state.library.lock().map_err(|_| STATE_ERROR)?;
        let scope = library.review_assignment(&stamp, target, &selected)?;
        let recipients: Vec<_> = scope
            .members
            .iter()
            .map(|member| match member {
                AssignmentRecipient::Saved(account) => {
                    let session = owner.playback_target(account);
                    MacroRecipientReview {
                        name: account.username.clone(),
                        session: Some(session),
                    }
                }
                AssignmentRecipient::Unlinked(name) => MacroRecipientReview {
                    name: name.clone(),
                    session: None,
                },
            })
            .collect();
        if !state.can_dispatch(epoch) {
            return Err(STALE.into());
        }
        let pin = match (recipients.as_slice(), scope.members.as_slice()) {
            (
                [
                    MacroRecipientReview {
                        session: Some(PlaybackTarget::Ready { attempt_id, .. }),
                        ..
                    },
                ],
                [AssignmentRecipient::Saved(account)],
            ) => owner.macro_window(account, *attempt_id).ok().map(|window| {
                (
                    ReviewKey {
                        target,
                        stamp,
                        host,
                        macro_id: selected.id,
                        revision: selected.revision,
                        epoch,
                        attempt: *attempt_id,
                    },
                    window,
                )
            }),
            _ => None,
        };
        let playback_review = state.macro_playback.publish_review(pin)?;
        Ok(MacroTargetReview {
            target_name: scope.target_name,
            macro_name: selected.name,
            recipients,
            playback_review,
        })
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    macro_playback: Option<MacroRunView>,
    accounts: Vec<SavedIdentity>,
    sessions: Vec<Session>,
    server_history: server_history::ServerHistoryView,
    launch: LaunchState,
    recovery: recovery::RecoveryView,
    panic: PanicView,
    admission: Option<Admission>,
    sign_in_attempt: Option<Uuid>,
    sign_in_closing: Option<Uuid>,
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
        let (sessions, server_history, launch, recovery, panic) = {
            let owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            let shortcut = state.hotkey.as_ref().is_some_and(PanicHotkey::is_running);
            (
                owner.rows(),
                owner.server_history(),
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
            macro_playback: state.macro_playback.view(),
            accounts,
            sessions,
            server_history,
            launch,
            recovery,
            panic,
            admission,
            sign_in_attempt: sign_in.active.as_ref().map(|flow| flow.attempt),
            sign_in_closing: sign_in.closing,
            sign_in_error: sign_in.active.as_ref().and_then(|flow| flow.error.clone()),
            saved_sign_in: sign_in.saved.clone(),
        })
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn clear_public_server_history(state: State<'_, Arc<Workspace>>) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .clear_public_server_history()
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
async fn public_game_details(
    state: State<'_, Arc<Workspace>>,
    place: String,
) -> Result<GameDetails, String> {
    let place = public_place(&place)?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.directory.game_details(place))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn public_game_art(
    state: State<'_, Arc<Workspace>>,
    universe: String,
) -> Result<GameArt, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.directory.game_art(&universe))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn browse_public_servers(
    state: State<'_, Arc<Workspace>>,
    place: String,
    cursor: Option<String>,
    filters: ServerFilters,
) -> Result<ServerPage, String> {
    let place = public_place(&place)?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || state.directory.servers(place, cursor.as_deref(), filters))
        .await
        .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn private_servers(
    state: State<'_, Arc<Workspace>>,
) -> Result<Vec<PrivateServerRow>, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .private_servers()
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn save_private_server(
    state: State<'_, Arc<Workspace>>,
    name: String,
    invite: String,
    account: Option<Uuid>,
) -> Result<PrivateServerRow, String> {
    let invite = PrivateInvite::parse(&Zeroizing::new(invite))?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let place = match invite.direct_place() {
            Some(place) => place,
            None => state.read_saved_account(
                account.ok_or("Choose a saved account to check this Roblox share link.")?,
                |saved| {
                    let auth = RobloxAuth::new().map_err(|error| error.to_string())?;
                    auth.verify(&mut saved.credential, saved.identity.user_id)
                        .map_err(|error| error.to_string())?;
                    invite
                        .resolve(&auth, &mut saved.credential, &state.directory)
                        .map(|(place, _)| place)
                },
            )?,
        };
        let library = state.library.lock().map_err(|_| STATE_ERROR)?;
        if state.exiting.load(Ordering::Acquire) {
            return Err("Zenith is closing.".into());
        }
        library.save_private_server(name, place, invite)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn rename_private_server(
    state: State<'_, Arc<Workspace>>,
    reference: PrivateServerReference,
    name: String,
) -> Result<PrivateServerRow, String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .rename_private_server(reference, name)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn remove_private_server(
    state: State<'_, Arc<Workspace>>,
    reference: PrivateServerReference,
) -> Result<(), String> {
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .library
            .lock()
            .map_err(|_| STATE_ERROR)?
            .remove_private_server(reference)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

enum LaunchSource {
    Public(Target),
    Private(PrivateServerReference),
    Recovery(RetryRequest),
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LaunchPreferences {
    allow_pair: bool,
    recover: bool,
}

struct LaunchOptions {
    epoch: u64,
    try_one: bool,
    preferences: LaunchPreferences,
}

#[tauri::command]
async fn launch_account(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    place_id: String,
    job: Option<Uuid>,
    try_one: bool,
    options: LaunchPreferences,
    epoch: String,
) -> Result<(), String> {
    let place = public_place(&place_id)?;
    if job.is_some_and(|job| job.is_nil()) {
        return Err("Select a valid public server.".into());
    }
    launch_target(
        Arc::clone(state.inner()),
        id,
        LaunchSource::Public(Target::public(place, job)),
        try_one,
        options,
        epoch,
    )
    .await
}

#[tauri::command]
async fn launch_private_server(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    reference: PrivateServerReference,
    try_one: bool,
    epoch: String,
) -> Result<(), String> {
    launch_target(
        Arc::clone(state.inner()),
        id,
        LaunchSource::Private(reference),
        try_one,
        LaunchPreferences {
            allow_pair: false,
            recover: false,
        },
        epoch,
    )
    .await
}

async fn launch_target(
    state: Arc<Workspace>,
    id: Uuid,
    source: LaunchSource,
    try_one: bool,
    preferences: LaunchPreferences,
    epoch: String,
) -> Result<(), String> {
    let epoch = epoch
        .parse()
        .map_err(|_| "Refresh the launch state before starting Roblox.")?;
    spawn_blocking(move || {
        run_launch(
            &state,
            id,
            source,
            LaunchOptions {
                epoch,
                try_one,
                preferences,
            },
        )
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

fn run_launch(
    state: &Arc<Workspace>,
    id: Uuid,
    source: LaunchSource,
    options: LaunchOptions,
) -> Result<(), String> {
    let LaunchOptions {
        epoch,
        try_one,
        preferences,
    } = options;
    if !state.can_dispatch(epoch) {
        return Err("The launch state changed. Review it and choose Launch again.".into());
    }
    let (mut saved, attempt, target, reference) = {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) {
            return Err("Zenith stopped this launch request.".into());
        }
        let library = state.library.lock().map_err(|_| STATE_ERROR)?;
        let (target, reference, retry) = match source {
            LaunchSource::Public(target) => (target, None, None),
            LaunchSource::Private(reference) => {
                let target = library.private_target(reference).inspect_err(|_| {
                    owner.record_reservation_failure(FailureStage::PrivateInviteResolution);
                })?;
                (target, Some(reference), None)
            }
            LaunchSource::Recovery(retry) => {
                (Target::public(retry.place, retry.job), None, Some(retry))
            }
        };
        let saved = library.credential(id).inspect_err(|_| {
            owner.record_reservation_failure(FailureStage::SavedSignIn);
        })?;
        if !state.can_dispatch(epoch) {
            return Err("Zenith stopped this launch request.".into());
        }
        let attempt = match retry {
            Some(retry) => owner
                .reserve_retry(retry, &saved.identity, &target, Instant::now())
                .inspect_err(|_| {
                    owner.record_reservation_failure(FailureStage::LaunchAdmission);
                })?,
            None => {
                let attempt = owner
                    .reserve(&saved.identity, &target, preferences.allow_pair, try_one)
                    .inspect_err(|_| {
                        owner.record_reservation_failure(FailureStage::LaunchAdmission);
                    })?;
                if preferences.recover {
                    owner.arm_recovery(attempt, epoch, preferences.allow_pair)?;
                }
                attempt
            }
        };
        (saved, attempt, target, reference)
    };
    let launch_state = Arc::clone(state);
    let prepared = session::prepare(
        &saved.identity,
        &mut saved.credential,
        target,
        &state.directory,
        try_one,
        move || {
            launch_state.can_dispatch(epoch)
                && launch_state
                    .owner
                    .lock()
                    .is_ok_and(|owner| launch_state.can_dispatch(epoch) && owner.permits(attempt))
        },
    );
    let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
    let mut failed_at = FailureStage::FinalAuthorization;
    let result = (|| {
        if !owner.permits(attempt) || !state.can_dispatch(epoch) {
            return Err("The launch was cancelled or the first client stopped.".into());
        }
        let library = state.library.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) {
            return Err("Zenith stopped this launch request.".into());
        }
        library.finish_credential(&saved)?;
        if let Some(reference) = reference {
            // Hold the library lease through process creation so removal or rename cannot overtake dispatch.
            library.private_target(reference)?;
        } else {
            drop(library);
        }
        let prepared = prepared.map_err(|error| {
            failed_at = error.stage;
            error.message
        })?;
        owner
            .dispatch(prepared, attempt, try_one, || state.can_dispatch(epoch))
            .map_err(|error| {
                failed_at = error.stage;
                error.message
            })
    })();
    if let Err(error) = &result {
        owner.fail_preparation(
            attempt,
            error.clone(),
            !state.can_dispatch(epoch),
            failed_at,
        );
    }
    result
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
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .cancel_pending(attempt)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn close_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    let epoch = state.panic.permit()?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if state.macro_playback.is_active() {
            return Err(
                "Finish foreground macro playback before requesting a normal close.".into(),
            );
        }
        owner.request_close(attempt, || state.can_dispatch(epoch))
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn force_stop_session(state: State<'_, Arc<Workspace>>, attempt: Uuid) -> Result<(), String> {
    state.macro_playback.cancel_for(Some(attempt));
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        state
            .owner
            .lock()
            .map_err(|_| STATE_ERROR)?
            .force_stop(attempt)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
async fn stop_all_sessions(state: State<'_, Arc<Workspace>>) -> Result<(), String> {
    state.macro_playback.cancel_for(None);
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
async fn set_window_visibility(
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
    action: WindowVisibility,
) -> Result<(), String> {
    let epoch = state.panic.permit()?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let owner = state
            .owner
            .try_lock()
            .map_err(|_| "Zenith is updating the session. Try again.")?;
        if state.macro_playback.is_active() {
            return Err("Stop foreground macro playback before changing the Roblox window.".into());
        }
        owner.set_window_visibility(attempt, action, || state.can_dispatch(epoch))
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
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
async fn set_client_efficiency(
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
    action: EfficiencyAction,
) -> Result<EfficiencyState, String> {
    let epoch = state.panic.permit()?;
    let state = Arc::clone(state.inner());
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) {
            return Err("Zenith stopped this efficiency action.".into());
        }
        owner.set_client_efficiency(attempt, action, || state.can_dispatch(epoch))
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
            let state = Arc::new(Workspace {
                library: Mutex::new(library),
                macro_catalog: MacroCatalog::default(),
                macro_playback: MacroPlayback::default(),
                owner,
                panic,
                hotkey,
                directory,
                sign_in: Mutex::new(sign_in::SignInState::default()),
                sign_in_directory: root.join("isolated-sign-in"),
                logs,
                exiting: AtomicBool::new(false),
                monitor: Mutex::new(None),
                support_reports: SupportReports::new(&root),
            });
            *state
                .monitor
                .lock()
                .map_err(|_| std::io::Error::other(STATE_ERROR))? =
                Some(SessionMonitor::start(&state));
            app.manage(state);
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
                state.macro_playback.cancel_for(None);
                tauri::async_runtime::spawn(async move {
                    state.macro_playback.join().await;
                    let owner = Arc::clone(&state.owner);
                    let stopped = spawn_blocking(move || {
                        owner
                            .lock()
                            .is_ok_and(|mut owner| owner.stop_all(true).is_ok())
                    })
                    .await
                    .unwrap_or(false);
                    if stopped {
                        let monitor = state
                            .monitor
                            .lock()
                            .ok()
                            .and_then(|mut monitor| monitor.take());
                        if let Some(monitor) = monitor {
                            monitor.shutdown().await;
                        }
                        app.exit(0);
                    } else {
                        state.exiting.store(false, Ordering::Release);
                    }
                });
            }
        })
        .invoke_handler(tauri::generate_handler![
            workspace_snapshot,
            clear_public_server_history,
            support_report::export_support_report,
            saved_macro_catalog,
            macro_assignments,
            edit_macro_assignment,
            review_macro_targets,
            macro_playback::start_macro_playback,
            macro_playback::control_macro_playback,
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
            public_game_details,
            public_game_art,
            browse_public_servers,
            private_servers,
            save_private_server,
            rename_private_server,
            remove_private_server,
            launch_account,
            launch_private_server,
            dismiss_recovered_launch,
            archive_launch_journal,
            stop_session,
            close_session,
            force_stop_session,
            stop_all_sessions,
            panic_stop,
            resume_launches,
            focus_session,
            set_window_visibility,
            client_performance,
            set_client_priority,
            set_client_efficiency,
            tile_windows,
            sign_in::begin_sign_in,
            sign_in::finish_sign_in,
            sign_in::cancel_sign_in
        ])
        .run(context)
        .expect("could not start Zenith Sessions");
    ExitCode::SUCCESS
}
