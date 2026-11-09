use std::{
    pin::pin,
    sync::{
        Arc, Mutex, TryLockError,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

use futures_util::future::{Either, select};
use serde::{Deserialize, Serialize};
use tauri::{
    State,
    async_runtime::{spawn, spawn_blocking},
};
use tokio::sync::{Notify, mpsc};
use uuid::Uuid;
use zenith_session_launch::windows::SavedIdentity;
use zenith_sessions_desktop::foreground_control::{
    Error, ForegroundControl, Outcome, Start, State as HostState, Status,
};
use zeroize::Zeroizing;

use crate::{
    STATE_ERROR, Workspace,
    library::{AssignmentRecipient, AssignmentTarget},
    macro_catalog::Choice,
    session::MacroWindow,
};

#[derive(Default)]
pub struct MacroPlayback(Mutex<PlaybackState>);

#[derive(Default)]
struct PlaybackState {
    current: Option<Arc<Run>>,
    review: Option<ReviewPermit>,
}

#[derive(PartialEq, Eq)]
pub struct ReviewKey {
    pub target: AssignmentTarget,
    pub stamp: String,
    pub host: Uuid,
    pub macro_id: Uuid,
    pub revision: Uuid,
    pub epoch: u64,
    pub attempt: Uuid,
}

struct ReviewPermit {
    id: Uuid,
    key: ReviewKey,
    window: MacroWindow,
    created: Instant,
}

impl PlaybackState {
    fn consume_review(&mut self, id: Uuid, key: &ReviewKey, window: MacroWindow) -> bool {
        if !self.review.as_ref().is_some_and(|permit| {
            permit.id == id
                && &permit.key == key
                && permit.window == window
                && permit.created.elapsed() < Duration::from_secs(120)
        }) {
            return false;
        }
        self.review = None;
        true
    }
}

struct Run {
    id: Uuid,
    attempt: Uuid,
    name: String,
    account: SavedIdentity,
    window: MacroWindow,
    status: Mutex<RunStatus>,
    controls: mpsc::Sender<Action>,
    cancelled: AtomicBool,
    cancel_notice: Notify,
    done_notice: Notify,
}

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum Phase {
    Connecting,
    Preparing,
    Running,
    Paused,
    Stopping,
    Finished,
    Disconnected,
}

#[derive(Clone, Serialize)]
struct RunStatus {
    phase: Phase,
    detail: String,
}

impl RunStatus {
    fn finished(&self) -> bool {
        matches!(self.phase, Phase::Finished | Phase::Disconnected)
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MacroRunView {
    id: Uuid,
    name: String,
    account: String,
    #[serde(flatten)]
    status: RunStatus,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Action {
    Pause,
    Resume,
    Stop,
}

impl MacroPlayback {
    fn current(&self) -> Option<Arc<Run>> {
        self.0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .current
            .clone()
    }

    pub fn publish_review(
        &self,
        review: Option<(ReviewKey, MacroWindow)>,
    ) -> Result<Option<Uuid>, String> {
        let mut state = self.0.lock().map_err(|_| STATE_ERROR)?;
        state.review = review.map(|(key, window)| ReviewPermit {
            id: Uuid::new_v4(),
            key,
            window,
            created: Instant::now(),
        });
        Ok(state.review.as_ref().map(|permit| permit.id))
    }

    pub fn view(&self) -> Option<MacroRunView> {
        self.current().map(|run| MacroRunView {
            id: run.id,
            name: run.name.clone(),
            account: run.account.username.clone(),
            status: run.status(),
        })
    }

    pub fn cancel_for(&self, attempt: Option<Uuid>) {
        if let Some(run) = self
            .current()
            .filter(|run| attempt.is_none_or(|id| id == run.attempt))
        {
            run.cancelled.store(true, Ordering::Release);
            run.cancel_notice.notify_waiters();
        }
    }

    pub async fn join(&self) {
        if let Some(run) = self.current() {
            let notice = run.done_notice.notified();
            tokio::pin!(notice);
            notice.as_mut().enable();
            if !run.status().finished() {
                notice.await;
            }
        }
    }
}

impl Run {
    fn status(&self) -> RunStatus {
        self.status
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone()
    }

    fn show(&self, phase: Phase, detail: impl Into<String>) {
        *self
            .status
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner) = RunStatus {
            phase,
            detail: detail.into(),
        };
        if matches!(phase, Phase::Finished | Phase::Disconnected) {
            self.done_notice.notify_waiters();
        }
    }

    async fn wait_cancelled(&self) {
        let notice = self.cancel_notice.notified();
        tokio::pin!(notice);
        notice.as_mut().enable();
        if !self.cancelled.load(Ordering::Acquire) {
            notice.await;
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlaybackRequest {
    target: AssignmentTarget,
    stamp: String,
    choice: Choice,
    attempt: Uuid,
    epoch: String,
    code: String,
    review_id: Uuid,
}

#[tauri::command]
pub async fn start_macro_playback(
    state: State<'_, Arc<Workspace>>,
    request: PlaybackRequest,
) -> Result<Uuid, String> {
    let PlaybackRequest {
        target,
        stamp,
        choice,
        attempt,
        epoch,
        code,
        review_id,
    } = request;
    const STALE: &str = "Review the assignment again after resuming.";
    let code = Zeroizing::new(code);
    let epoch = epoch.parse().map_err(|_| STALE)?;
    if !state.can_dispatch(epoch) {
        return Err(STALE.into());
    }
    let host_session = choice.host_session();
    let selected = state.macro_catalog.verify_choice(choice).await?;
    let state = Arc::clone(state.inner());
    spawn_blocking({
        let state = Arc::clone(&state);
        move || {
            let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
            if !state.can_dispatch(epoch) {
                return Err(STALE.into());
            }
            let library = state.library.lock().map_err(|_| STATE_ERROR)?;
            let scope = library.review_assignment(&stamp, target, &selected)?;
            let [AssignmentRecipient::Saved(account)] = scope.members.as_slice() else {
                return Err("Foreground playback requires exactly one linked recipient.".into());
            };
            let window = owner.macro_window(account, attempt)?;
            let request = Start {
                host_session,
                macro_id: selected.id,
                revision: selected.revision,
                window: window.handle as i64,
                process: i32::try_from(window.process.pid).map_err(|_| STATE_ERROR)?,
                creation_file_time: i64::try_from(window.process.started_filetime)
                    .map_err(|_| STATE_ERROR)?,
            };
            let mut current = state.macro_playback.0.lock().map_err(|_| STATE_ERROR)?;
            if current.current.as_ref().is_some_and(|run| !run.status().finished()) {
                return Err("Another macro operation is still active.".into());
            }
            if !state.can_dispatch(epoch) {
                return Err(STALE.into());
            }
            let key = ReviewKey { target, stamp, host: host_session, macro_id: selected.id, revision: selected.revision, epoch, attempt };
            if !current.consume_review(review_id, &key, window) {
                return Err("The reviewed window or assignment changed or expired. Review playback targets again.".into());
            }
            let (controls, commands) = mpsc::channel(2);
            let run = Arc::new(Run {
                id: Uuid::new_v4(),
                attempt,
                name: selected.name,
                account: account.clone(),
                window,
                status: Mutex::new(RunStatus {
                    phase: Phase::Connecting,
                    detail: "Connecting to Personal…".into(),
                }),
                controls,
                cancelled: AtomicBool::new(false),
                cancel_notice: Notify::new(),
                done_notice: Notify::new(),
            });
            let id = run.id;
            current.current = Some(Arc::clone(&run));
            spawn(run_worker(
                Arc::clone(&state),
                run,
                epoch,
                request,
                code,
                commands,
            ));
            Ok(id)
        }
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

#[tauri::command]
pub fn control_macro_playback(
    state: State<'_, Arc<Workspace>>,
    id: Uuid,
    action: Action,
) -> Result<(), String> {
    let run = state
        .macro_playback
        .current()
        .filter(|run| run.id == id && !run.status().finished())
        .ok_or("This macro operation has ended.")?;
    let phase = run.status().phase;
    if matches!(action, Action::Stop) && phase == Phase::Connecting {
        run.cancelled.store(true, Ordering::Release);
        run.cancel_notice.notify_waiters();
        return Ok(());
    }
    if matches!(action, Action::Pause) && phase != Phase::Running
        || matches!(action, Action::Resume) && phase != Phase::Paused
    {
        return Err("The macro changed state. Use the current controls.".into());
    }
    run.controls
        .try_send(action)
        .map_err(|_| "The current macro control is still being handled.".into())
}

async fn run_worker(
    state: Arc<Workspace>,
    run: Arc<Run>,
    epoch: u64,
    request: Start,
    code: Zeroizing<String>,
    commands: mpsc::Receiver<Action>,
) {
    let result = match select(
        pin!(select(
            pin!(state.panic.wait_revoked(epoch)),
            pin!(run.wait_cancelled())
        )),
        pin!(connected(&state, &run, epoch, request, code, commands)),
    )
    .await
    {
        Either::Left((Either::Left(_), _)) => Err(
            "Panic closed the connection. Personal stops the run and releases its held input."
                .into(),
        ),
        Either::Left((Either::Right(_), _)) => {
            Err("Session control closed the connection. Personal stops the run.".into())
        }
        Either::Right((result, _)) => result,
    };
    match result {
        Ok(status) => run.show(Phase::Finished, outcome_message(status.outcome)),
        Err(message) => run.show(Phase::Disconnected, message),
    }
}

async fn connected(
    state: &Arc<Workspace>,
    run: &Arc<Run>,
    epoch: u64,
    request: Start,
    code: Zeroizing<String>,
    mut commands: mpsc::Receiver<Action>,
) -> Result<Status, String> {
    let mut client = ForegroundControl::connect(code)
        .await
        .map_err(connection_error)?;
    focus(state, run, epoch).await?;
    if !state.can_dispatch(epoch) || run.cancelled.load(Ordering::Acquire) {
        return Err("Playback was stopped.".into());
    }
    let mut status = client.start(request).await.map_err(connection_error)?;
    let mut heartbeat = tokio::time::interval(Duration::from_secs(1));
    heartbeat.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut refused = None;
    loop {
        let (phase, detail) = match status.state {
            HostState::Rejected | HostState::Finished => return Ok(status),
            HostState::Preparing => (
                Phase::Preparing,
                "Personal is checking the saved macro and window.",
            ),
            HostState::Running => (Phase::Running, "Foreground playback is running."),
            HostState::Paused => (
                Phase::Paused,
                "Paused. Resume focuses the same Roblox window.",
            ),
            HostState::Stopping => (Phase::Stopping, "Waiting for Personal to finish cleanup."),
        };
        run.show(phase, refused.take().unwrap_or_else(|| detail.to_owned()));
        status = match select(pin!(commands.recv()), pin!(heartbeat.tick())).await {
            Either::Left((action, _)) => {
                match action {
                    Some(Action::Pause) => {
                        let reply = client.pause().await.map_err(connection_error)?;
                        if !reply.accepted {
                            refused = Some("Personal could not pause at this stage.".into());
                        }
                        reply
                    }
                    Some(Action::Resume) => {
                        if let Err(error) = focus(state, run, epoch).await {
                            refused = Some(error);
                            client.status().await.map_err(connection_error)?
                        } else {
                            let reply = client.resume().await.map_err(connection_error)?;
                            if !reply.accepted {
                                refused = Some("Focus the Roblox window and release held keys before resuming.".into());
                            }
                            reply
                        }
                    }
                    Some(Action::Stop) => client.stop().await.map_err(connection_error)?,
                    None => return Err("The control connection closed.".into()),
                }
            }
            Either::Right(_) => {
                let live = match state.owner.try_lock() {
                    Ok(mut owner) => owner.macro_process_is_live(run.attempt, run.window.process),
                    Err(TryLockError::WouldBlock) => true,
                    Err(TryLockError::Poisoned(_)) => false,
                };
                if !live && phase != Phase::Stopping {
                    client.stop().await.map_err(connection_error)?
                } else {
                    client.status().await.map_err(connection_error)?
                }
            }
        };
    }
}

async fn focus(state: &Arc<Workspace>, run: &Arc<Run>, epoch: u64) -> Result<(), String> {
    let state = Arc::clone(state);
    let run = Arc::clone(run);
    spawn_blocking(move || {
        let mut owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        if !state.can_dispatch(epoch) || run.cancelled.load(Ordering::Acquire) {
            return Err("Playback was stopped.".into());
        }
        owner.focus_macro_window(&run.account, run.attempt, run.window)
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

fn connection_error(error: Error) -> String {
    match error {
        Error::PairingCode => "Personal rejected the pairing code. Open a new pairing in the matching editor.",
        Error::Unavailable => "Personal pairing is unavailable. Open Pair Sessions in the matching editor.",
        _ => "The Personal connection ended. Personal stops any operation it owned on that connection.",
    }.into()
}

fn outcome_message(outcome: Outcome) -> &'static str {
    match outcome {
        Outcome::Completed => "Macro completed.",
        Outcome::Stopped => "Macro stopped and cleanup finished.",
        Outcome::Busy => "Personal is busy with another input activity.",
        Outcome::PreparationRejected => {
            "Personal could not prepare this exact saved revision. Use an Active external window macro with supported keys and delays."
        }
        Outcome::TargetUnavailable => {
            "Personal could not authorize this Roblox window for the paired Sessions build."
        }
        Outcome::InputHeld => "Release held keys and mouse buttons, then pair and try again.",
        Outcome::LicenseRequired => "Activate Personal before running this macro.",
        Outcome::CleanupFailed => {
            "Personal could not confirm input cleanup. Its input activities remain held."
        }
        _ => "Personal refused or could not finish the macro.",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use zenith_session_launch::windows::ProcessIdentity;

    #[test]
    fn playback_review_rejects_a_replacement_window_a_stale_epoch_and_reuse() {
        let id = Uuid::new_v4();
        let key = |epoch| ReviewKey {
            target: serde_json::from_value(
                serde_json::json!({"kind": "Account", "id": Uuid::from_u128(21)}),
            )
            .unwrap(),
            stamp: "fixture".into(),
            host: Uuid::from_u128(31),
            macro_id: Uuid::from_u128(41),
            revision: Uuid::from_u128(51),
            epoch,
            attempt: Uuid::from_u128(61),
        };
        let window = MacroWindow {
            handle: 1,
            process: ProcessIdentity {
                pid: 12,
                started_filetime: 34,
            },
        };
        let mut state = PlaybackState {
            review: Some(ReviewPermit {
                id,
                key: key(2),
                window,
                created: Instant::now(),
            }),
            ..Default::default()
        };
        assert!(!state.consume_review(
            id,
            &key(2),
            MacroWindow {
                handle: 2,
                ..window
            }
        ));
        assert!(!state.consume_review(id, &key(4), window));
        assert!(state.consume_review(id, &key(2), window));
        assert!(!state.consume_review(id, &key(2), window));
    }
}
