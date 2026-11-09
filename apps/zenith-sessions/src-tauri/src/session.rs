use std::{
    path::{Path, PathBuf},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use crate::recovery::{Checkpoint, Journal, MAX_ENTRIES, Record, RecoveryView, ReviewState};

mod diagnostics;
mod retry;
pub use diagnostics::{FailureStage, LaunchDiagnostics, PreparationFailure};
use retry::Recovery;
pub use retry::RetryRequest;

use serde::Serialize;
use uuid::Uuid;
use zenith_session_launch::{
    auth::{Credential, RobloxAuth},
    directory::Directory,
    evidence::Observed,
    target::{Target, new_tracker, player_uri},
    windows::{
        EfficiencyAction, EfficiencyState, OwnedPlayer, PairLease, Placement, PriorityAction,
        PriorityState, ProcessIdentity, ProcessMetrics, ProcessSample, SavedIdentity,
        VerifiedPlayer, WindowVisibility, ensure_compatible_player_start, memory_sample,
        tile_owned_players,
    },
};

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Phase {
    Preparing,
    Starting,
    Running,
    Unconfirmed,
    RetryWaiting,
    Closing,
    Stopping,
    Stopped,
    Failed,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Session {
    attempt_id: Uuid,
    pub account_id: Uuid,
    username: String,
    place_id: String,
    phase: Phase,
    pid: Option<u32>,
    requested_server: Option<Uuid>,
    private_server: bool,
    automatic_restart: Option<usize>,
    observed_server: Option<Uuid>,
    detail: Option<String>,
}

#[derive(Clone, Copy)]
pub struct Observation {
    pub attempt: Uuid,
    tracker: u64,
    launched: SystemTime,
    started: Instant,
    expected_user: u64,
    expected_place: u64,
    expected_job: Option<Uuid>,
    private_server: bool,
}

impl Observation {
    pub fn read(self, logs: &Path) -> Option<Observed> {
        Observed::for_attempt(logs, self.tracker, self.launched)
    }

    fn confirms(self, facts: &Observed) -> bool {
        !self.private_server
            && self.matches_account_and_place(facts)
            && facts
                .job_id
                .is_some_and(|job| self.expected_job.is_none_or(|expected| expected == job))
    }

    fn matches_account_and_place(self, facts: &Observed) -> bool {
        facts.user_id == Some(self.expected_user)
            && facts.place_id == Some(self.expected_place)
            && facts.connected
    }

    fn mismatch(self, facts: &Observed) -> Option<&'static str> {
        if facts
            .user_id
            .is_some_and(|actual| actual != self.expected_user)
            || facts
                .place_id
                .is_some_and(|actual| actual != self.expected_place)
        {
            Some("a different account or place")
        } else if facts.connected
            && let (Some(expected), Some(actual)) = (self.expected_job, facts.job_id)
            && expected != actual
        {
            Some("a different server")
        } else {
            None
        }
    }
}

const MAX_ACTIVE: usize = 2;
const MAX_ROWS: usize = 32;
const STABILIZATION: Duration = Duration::from_secs(20);

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchState {
    active: usize,
    pair_enabled: bool,
    hold: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowPlacement {
    attempt_id: Uuid,
    placement: Placement,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPerformance {
    attempt_id: Uuid,
    username: String,
    pid: u32,
    metrics: Option<ProcessMetrics>,
}

#[derive(Serialize, Debug, PartialEq, Eq)]
#[serde(
    tag = "status",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum PlaybackTarget {
    NoSession,
    ArrivalUnconfirmed,
    WindowUnavailable,
    Ready { attempt_id: Uuid, pid: u32 },
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct MacroWindow {
    pub handle: isize,
    pub process: ProcessIdentity,
}

#[derive(Default)]
pub struct SessionOwner {
    attempts: Vec<Attempt>,
    pair: Option<PairLease>,
    closing: bool,
    last_start: Option<Instant>,
    journal: Journal,
    recovered: Vec<Record>,
    last_reservation_failure: Option<FailureStage>,
}

impl SessionOwner {
    pub fn open(path: PathBuf) -> Self {
        let (journal, recovered) = Journal::open(path);
        Self {
            journal,
            recovered,
            ..Self::default()
        }
    }

    pub fn rows(&self) -> Vec<Session> {
        self.attempts
            .iter()
            .rev()
            .map(|attempt| attempt.row.clone())
            .collect()
    }

    pub fn record_reservation_failure(&mut self, stage: FailureStage) {
        self.last_reservation_failure = Some(stage);
    }

    pub fn launch_state(&self) -> LaunchState {
        LaunchState {
            active: self.active().count(),
            pair_enabled: self.pair.is_some(),
            hold: self.hold(Instant::now(), self.pair.is_some()),
        }
    }

    pub fn recovery(&self) -> RecoveryView {
        RecoveryView {
            issue: self.journal.issue(),
            can_archive: self.journal.can_archive(),
            attempts: self.recovered.iter().map(Record::view).collect(),
        }
    }

    fn recovery_records(&self) -> Vec<Record> {
        self.recovered
            .iter()
            .cloned()
            .chain(self.attempts.iter().filter_map(Attempt::recovery_record))
            .collect()
    }

    fn sync_journal(&mut self) -> Result<(), String> {
        let records = self.recovery_records();
        self.journal.save(records)
    }

    pub fn dismiss_recovered(&mut self, id: Uuid) -> Result<(), String> {
        let record = self
            .recovered
            .iter()
            .find(|record| record.attempt_id == id)
            .ok_or("That recovered attempt is no longer listed.")?;
        match record.review() {
            ReviewState::Surviving => {
                return Err(
                    "Close the surviving Roblox client before dismissing this attempt.".into(),
                );
            }
            ReviewState::Unknown => ensure_compatible_player_start(&[])?,
            ReviewState::Interrupted | ReviewState::Ended => (),
        }
        let mut records = self.recovery_records();
        records.retain(|record| record.attempt_id != id);
        self.journal.save(records)?;
        self.recovered.retain(|record| record.attempt_id != id);
        Ok(())
    }

    pub fn archive_unreadable_journal(&mut self) -> Result<(), String> {
        ensure_compatible_player_start(&[])?;
        self.journal.archive_unreadable()
    }

    fn active(&self) -> impl Iterator<Item = &Attempt> {
        self.attempts.iter().filter(|attempt| attempt.active())
    }

    pub fn has_active_attempts(&self) -> bool {
        self.active().next().is_some()
    }

    fn hold(&self, now: Instant, pair_enabled: bool) -> Option<String> {
        if self.closing {
            return Some("Zenith is closing.".into());
        }
        if let Some(issue) = self.journal.issue() {
            return Some(issue.into());
        }
        if self.recovered.iter().any(|record| record.review().holds()) {
            return Some(
                "Review the interrupted launch in Sessions before starting Roblox.".into(),
            );
        }
        let active: Vec<_> = self
            .active()
            .filter(|attempt| attempt.row.phase != Phase::RetryWaiting)
            .collect();
        if self.recovered.len() + active.len() >= MAX_ENTRIES {
            return Some(
                "Dismiss a reviewed recovery entry before starting another launch.".into(),
            );
        }
        if active
            .iter()
            .any(|attempt| attempt.row.phase != Phase::Running)
        {
            return Some("Wait for the current launch to finish. Stop an unconfirmed client before starting another.".into());
        }
        if active.is_empty() {
            return None;
        }
        if !pair_enabled {
            return Some(
                "Stop the current client and enable the two-account trial before starting a pair."
                    .into(),
            );
        }
        if active.len() >= MAX_ACTIVE {
            return Some("Both preview client slots are in use.".into());
        }
        if let Some(start) = self.last_start
            && let Some(remaining) = STABILIZATION.checked_sub(now.saturating_duration_since(start))
            && !remaining.is_zero()
        {
            return Some(format!(
                "Waiting {} seconds for the first client to stabilize.",
                remaining.as_secs().saturating_add(1)
            ));
        }
        None
    }

    fn owned_processes(&self) -> Vec<ProcessIdentity> {
        self.attempts
            .iter()
            .filter_map(|attempt| attempt.player.as_ref()?.identity())
            .collect()
    }

    pub fn reserve(
        &mut self,
        account: &SavedIdentity,
        target: &Target,
        allow_pair: bool,
        try_one: bool,
    ) -> Result<Uuid, String> {
        if self.active().any(|attempt| {
            attempt.row.account_id == account.profile_id || attempt.user_id == account.user_id
        }) {
            return Err("This Roblox account already has an active session.".into());
        }
        if let Some(hold) = self.hold(Instant::now(), self.pair.is_some()) {
            return Err(hold);
        }
        let peer = self.active().next().map(|attempt| attempt.row.attempt_id);
        if target.is_private() && (peer.is_some() || allow_pair) {
            return Err("A private invite trial requires one client at a time.".into());
        }
        if peer.is_some() && (!allow_pair || try_one) {
            return Err(
                "A second client requires the two-account trial and standard memory headroom."
                    .into(),
            );
        }
        check_memory(try_one)?;
        ensure_compatible_player_start(&self.owned_processes())?;
        if peer.is_none() && allow_pair && self.pair.is_none() {
            self.pair = Some(PairLease::acquire()?);
        }
        let attempt = Attempt::new(account, target, peer);
        let id = attempt.row.attempt_id;
        let mut records = self.recovery_records();
        records.extend(attempt.recovery_record());
        if let Err(error) = self.journal.save(records) {
            self.release_idle_pair();
            return Err(error);
        }
        self.attempts
            .retain(|attempt| attempt.row.account_id != account.profile_id);
        if self.attempts.len() >= MAX_ROWS
            && let Some(oldest) = self.attempts.iter().position(|attempt| !attempt.active())
        {
            self.attempts.remove(oldest);
        }
        self.attempts.push(attempt);
        self.last_reservation_failure = None;
        Ok(id)
    }

    pub fn permits(&self, id: Uuid) -> bool {
        !self.closing
            && self.attempts.iter().any(|attempt| {
                attempt.row.attempt_id == id
                    && attempt.preparing
                    && !attempt.cancelled
                    && attempt.peer.is_none_or(|peer| {
                        self.active().any(|other| {
                            other.row.attempt_id == peer && other.row.phase == Phase::Running
                        })
                    })
            })
    }

    pub fn fail_preparation(
        &mut self,
        id: Uuid,
        error: String,
        stopped: bool,
        stage: FailureStage,
    ) {
        if let Some(attempt) = self
            .attempts
            .iter_mut()
            .find(|attempt| attempt.row.attempt_id == id && attempt.preparing)
        {
            attempt.cancelled |= stopped;
            if !attempt.cancelled {
                attempt.failure_stage = Some(stage);
            }
            attempt.fail_preparation(error);
        }
        self.release_idle_pair();
        let _ = self.sync_journal();
    }

    pub fn dispatch(
        &mut self,
        request: Prepared,
        id: Uuid,
        try_one: bool,
        can_dispatch: impl Fn() -> bool,
    ) -> Result<(), String> {
        if !self.permits(id) {
            return Err("The launch was cancelled or the first client stopped.".into());
        }
        if !can_dispatch() {
            return Err("Zenith stopped this launch request.".into());
        }
        // Commit intent before any native process creation. A crash between this write and the
        // receipt must remain uncertain, even if preparation and the OS call were milliseconds apart.
        self.attempt_mut(id)?.dispatching = true;
        self.sync_journal()?;
        let peer = self.attempt(id)?.peer;
        if let Some(peer) = peer {
            let player = self
                .attempt_mut(peer)?
                .player
                .as_mut()
                .ok_or("The first client is no longer available.")?;
            if player.exited().unwrap_or(true) || !player.has_window() {
                return Err("The first client is no longer available.".into());
            }
        }
        check_memory(try_one)?;
        ensure_compatible_player_start(&self.owned_processes())?;
        self.attempt_mut(id)?.dispatch(request, &can_dispatch)?;
        self.last_start = Some(Instant::now());
        let recorded = self.sync_journal();
        if !can_dispatch() {
            self.stop(id)?;
            return Err("Zenith stopped the owned client during native startup.".into());
        }
        recorded
    }

    pub fn observation(&self) -> Option<Observation> {
        self.attempts.iter().find_map(|attempt| attempt.observation)
    }

    pub fn reconcile(&mut self, mut evidence: Option<(Observation, Option<Observed>)>) {
        let now = Instant::now();
        for attempt in &mut self.attempts {
            let sample = if evidence
                .as_ref()
                .is_some_and(|(sample, _)| sample.attempt == attempt.row.attempt_id)
            {
                evidence.take()
            } else {
                None
            };
            attempt.reconcile(sample, now);
        }
        self.release_idle_pair();
        let _ = self.sync_journal();
    }

    pub fn stop(&mut self, id: Uuid) -> Result<(), String> {
        self.stop_exact(self.stop_target(id))
    }

    pub fn cancel_pending(&mut self, id: Uuid) -> Result<(), String> {
        let target = self.stop_target(id);
        if !matches!(
            self.attempt(target)?.row.phase,
            Phase::Preparing | Phase::RetryWaiting
        ) {
            return Err("This launch request changed. Refresh the session before acting.".into());
        }
        self.stop_exact(target)
    }

    pub fn force_stop(&mut self, id: Uuid) -> Result<(), String> {
        let attempt = self.attempt(id)?;
        if !attempt.active() || attempt.player.is_none() {
            return Err("The selected client has already exited or changed.".into());
        }
        self.stop_exact(id)
    }

    fn stop_exact(&mut self, id: Uuid) -> Result<(), String> {
        let result = self.attempt_mut(id)?.stop();
        self.release_idle_pair();
        let saved = self.sync_journal();
        result.and(saved)
    }

    pub fn request_close(&mut self, id: Uuid, permitted: impl Fn() -> bool) -> Result<(), String> {
        if self.closing {
            return Err("Zenith is closing.".into());
        }
        let attempt = self.attempt_mut(id)?;
        if !matches!(attempt.row.phase, Phase::Running | Phase::Unconfirmed) {
            return Err("The selected client is not ready for a normal close.".into());
        }
        attempt
            .player
            .as_ref()
            .ok_or("The owned Roblox process is unavailable.")?
            .request_window_close(permitted)?;
        attempt.begin_close(Instant::now());
        Ok(())
    }

    pub fn stop_all(&mut self, closing: bool) -> Result<(), String> {
        self.closing |= closing;
        let all_stopped = std::thread::scope(|scope| {
            let workers: Vec<_> = self
                .attempts
                .iter_mut()
                .filter(|attempt| attempt.active())
                .map(|attempt| scope.spawn(|| attempt.stop()))
                .collect();
            workers.into_iter().fold(true, |all_stopped, worker| {
                let stopped = worker.join().is_ok_and(|result| result.is_ok());
                all_stopped && stopped
            })
        });
        self.release_idle_pair();
        let saved = self.sync_journal();
        if all_stopped {
            return if closing { Ok(()) } else { saved };
        }
        self.closing = false;
        Err("Could not stop every owned Roblox client.".into())
    }

    pub fn focus(&self, id: Uuid) -> Result<(), String> {
        self.attempt(id)?.focus()
    }

    pub fn set_window_visibility(
        &self,
        id: Uuid,
        visibility: WindowVisibility,
        permitted: impl Fn() -> bool,
    ) -> Result<(), String> {
        if self.closing {
            return Err("Zenith is closing.".into());
        }
        let attempt = self.attempt(id)?;
        if attempt.cancelled || attempt.row.phase != Phase::Running {
            return Err("The account's Roblox window has not been confirmed.".into());
        }
        attempt
            .player
            .as_ref()
            .ok_or("The owned Roblox process is unavailable.")?
            .set_window_visibility(visibility, permitted)
            .map_err(str::to_owned)
    }

    pub fn client_performance(&mut self) -> Vec<ClientPerformance> {
        self.attempts
            .iter_mut()
            .filter(|attempt| attempt.row.phase == Phase::Running)
            .filter_map(|attempt| {
                let player = attempt.player.as_mut()?;
                let sample = player.performance_sample();
                let metrics = sample
                    .as_ref()
                    .map(|sample| sample.metrics(attempt.performance_baseline.as_ref()));
                attempt.performance_baseline = sample;
                Some(ClientPerformance {
                    attempt_id: attempt.row.attempt_id,
                    username: attempt.row.username.clone(),
                    pid: player.pid(),
                    metrics,
                })
            })
            .collect()
    }

    pub fn set_client_priority(
        &mut self,
        id: Uuid,
        action: PriorityAction,
    ) -> Result<PriorityState, String> {
        self.performance_player(
            id,
            "Confirm this client's arrival before changing priority.",
        )?
        .change_priority(action)
        .map_err(str::to_owned)
    }

    pub fn set_client_efficiency(
        &mut self,
        id: Uuid,
        action: EfficiencyAction,
        permitted: impl Fn() -> bool,
    ) -> Result<EfficiencyState, String> {
        self.performance_player(
            id,
            "Confirm this client's arrival before changing efficiency.",
        )?
        .change_efficiency(action, permitted)
        .map_err(str::to_owned)
    }

    fn performance_player(
        &mut self,
        id: Uuid,
        unconfirmed: &'static str,
    ) -> Result<&mut OwnedPlayer, String> {
        if self.closing {
            return Err("Zenith is closing.".into());
        }
        let attempt = self.attempt_mut(id)?;
        if attempt.row.phase != Phase::Running {
            return Err(unconfirmed.into());
        }
        attempt
            .player
            .as_mut()
            .ok_or_else(|| "The owned Roblox process is unavailable.".into())
    }

    pub fn tile_windows(
        &self,
        ids: &[Uuid],
        anchor: isize,
        cancelled: impl Fn() -> bool,
    ) -> Result<Vec<WindowPlacement>, String> {
        if self.closing || cancelled() {
            return Err("Zenith is closing.".into());
        }
        let targets: Vec<_> = self
            .attempts
            .iter()
            .filter(|attempt| ids.contains(&attempt.row.attempt_id))
            .collect();
        if ids.is_empty() || targets.len() != ids.len() {
            return Err("The selected sessions changed. Refresh and try again.".into());
        }
        let players: Option<Vec<_>> = targets
            .iter()
            .map(|attempt| {
                if attempt.row.phase == Phase::Running {
                    attempt.player.as_ref()
                } else {
                    None
                }
            })
            .collect();
        let placements = tile_owned_players(
            anchor,
            &players.ok_or("Only confirmed owned Roblox windows can be tiled.")?,
            cancelled,
        )?;
        Ok(targets
            .into_iter()
            .zip(placements)
            .map(|(attempt, placement)| WindowPlacement {
                attempt_id: attempt.row.attempt_id,
                placement,
            })
            .collect())
    }

    pub fn uses(&self, id: Uuid) -> bool {
        self.active().any(|attempt| attempt.row.account_id == id)
            || self
                .recovered
                .iter()
                .any(|record| record.account_id == id && record.review().holds())
    }

    pub fn playback_target(&mut self, account: &SavedIdentity) -> PlaybackTarget {
        let mut active = self
            .attempts
            .iter_mut()
            .filter(|attempt| attempt.active() && attempt.row.account_id == account.profile_id);
        let Some(attempt) = active.next() else {
            return PlaybackTarget::NoSession;
        };
        if active.next().is_some()
            || attempt.user_id != account.user_id
            || attempt.cancelled
            || attempt.row.phase == Phase::Stopping
        {
            return PlaybackTarget::WindowUnavailable;
        }
        if attempt.row.phase != Phase::Running {
            return PlaybackTarget::ArrivalUnconfirmed;
        }
        let Some(player) = attempt.player.as_mut() else {
            return PlaybackTarget::WindowUnavailable;
        };
        if !player.exited().is_ok_and(|exited| !exited) || !player.has_unique_visible_window() {
            return PlaybackTarget::WindowUnavailable;
        }
        PlaybackTarget::Ready {
            attempt_id: attempt.row.attempt_id,
            pid: player.pid(),
        }
    }

    pub fn macro_window(
        &mut self,
        account: &SavedIdentity,
        attempt: Uuid,
    ) -> Result<MacroWindow, String> {
        if !matches!(self.playback_target(account), PlaybackTarget::Ready { attempt_id, .. } if attempt_id == attempt)
        {
            return Err(
                "The reviewed Roblox session is no longer eligible. Review playback targets again."
                    .into(),
            );
        }
        let player = self
            .attempt(attempt)?
            .player
            .as_ref()
            .ok_or("The Roblox process exited.")?;
        Ok(MacroWindow {
            handle: player
                .unique_visible_window_handle()
                .ok_or("The reviewed Roblox window changed.")?,
            process: player
                .identity()
                .ok_or("The reviewed Roblox process changed.")?,
        })
    }

    pub fn focus_macro_window(
        &mut self,
        account: &SavedIdentity,
        attempt: Uuid,
        expected: MacroWindow,
    ) -> Result<(), String> {
        if self.macro_window(account, attempt)? != expected {
            return Err("The reviewed Roblox window changed.".into());
        }
        self.attempt(attempt)?
            .player
            .as_ref()
            .ok_or("The Roblox process exited.")?
            .focus_macro_window(expected.handle)
            .map_err(str::to_owned)
    }

    pub fn macro_process_is_live(&mut self, attempt: Uuid, expected: ProcessIdentity) -> bool {
        self.attempt_mut(attempt).is_ok_and(|row| {
            row.row.phase == Phase::Running
                && !row.cancelled
                && row.player.as_mut().is_some_and(|player| {
                    player.identity() == Some(expected)
                        && player.exited().is_ok_and(|exited| !exited)
                })
        })
    }

    fn attempt(&self, id: Uuid) -> Result<&Attempt, String> {
        self.attempts
            .iter()
            .find(|attempt| attempt.row.attempt_id == id)
            .ok_or_else(|| "The selected session has changed".into())
    }

    fn attempt_mut(&mut self, id: Uuid) -> Result<&mut Attempt, String> {
        self.attempts
            .iter_mut()
            .find(|attempt| attempt.row.attempt_id == id)
            .ok_or_else(|| "The selected session has changed".into())
    }

    fn release_idle_pair(&mut self) {
        if self.active().next().is_none() {
            self.pair = None;
        }
    }
}

struct Attempt {
    row: Session,
    user_id: u64,
    peer: Option<Uuid>,
    observation: Option<Observation>,
    player: Option<OwnedPlayer>,
    performance_baseline: Option<ProcessSample>,
    preparing: bool,
    dispatching: bool,
    cancelled: bool,
    recovery: Recovery,
    close_request: Option<CloseRequest>,
    failure_stage: Option<FailureStage>,
    exit_status: Option<u32>,
}

struct CloseRequest {
    previous: Phase,
    until: Instant,
}

impl Attempt {
    fn active(&self) -> bool {
        !matches!(self.row.phase, Phase::Stopped | Phase::Failed)
    }

    fn recovery_record(&self) -> Option<Record> {
        if !self.active()
            || self.row.phase == Phase::RetryWaiting
            || self.preparing && self.cancelled
        {
            return None;
        }
        let checkpoint = match self.player.as_ref().and_then(OwnedPlayer::identity) {
            Some(process) => Checkpoint::Started(process),
            None if self.dispatching => Checkpoint::Dispatching,
            None => Checkpoint::Preparing,
        };
        Some(Record {
            attempt_id: self.row.attempt_id,
            account_id: self.row.account_id,
            username: self.row.username.clone(),
            place_id: self.row.place_id.clone(),
            requested_server: self.row.requested_server,
            private_server: self.row.private_server,
            checkpoint,
        })
    }

    fn new(account: &SavedIdentity, target: &Target, peer: Option<Uuid>) -> Self {
        Self {
            row: Session {
                attempt_id: Uuid::new_v4(),
                account_id: account.profile_id,
                username: account.username.clone(),
                place_id: target.place.to_string(),
                phase: Phase::Preparing,
                pid: None,
                requested_server: target.job(),
                private_server: target.is_private(),
                automatic_restart: None,
                observed_server: None,
                detail: None,
            },
            user_id: account.user_id,
            peer,
            observation: None,
            player: None,
            performance_baseline: None,
            preparing: true,
            dispatching: false,
            cancelled: false,
            recovery: Recovery::default(),
            close_request: None,
            failure_stage: None,
            exit_status: None,
        }
    }

    fn fail_preparation(&mut self, error: String) {
        self.preparing = false;
        self.row.phase = if self.cancelled {
            Phase::Stopped
        } else {
            Phase::Failed
        };
        self.row.detail = Some(if self.cancelled {
            "Cancelled before starting Roblox".into()
        } else {
            error
        });
    }

    fn dispatch(
        &mut self,
        request: Prepared,
        can_dispatch: impl Fn() -> bool,
    ) -> Result<(), String> {
        let attempt = self.row.attempt_id;
        let launched = SystemTime::now();
        let timestamp = launched
            .duration_since(UNIX_EPOCH)
            .map_err(|_| "System clock is invalid")?;
        let tracker = new_tracker();
        let uri = player_uri(
            &request.ticket,
            &request.target,
            tracker,
            attempt,
            timestamp.as_millis(),
        )?;
        if !can_dispatch() {
            return Err("Zenith stopped this launch request.".into());
        }
        let player = request.player.spawn(&uri)?;
        let pid = player.pid();
        self.player = Some(player);
        self.observation = Some(Observation {
            attempt,
            tracker,
            launched,
            started: Instant::now(),
            expected_user: request.user_id,
            expected_place: request.target.place,
            expected_job: request.target.job(),
            private_server: request.target.is_private(),
        });
        self.preparing = false;
        self.row.phase = Phase::Starting;
        self.row.pid = Some(pid);
        Ok(())
    }

    fn reconcile(&mut self, evidence: Option<(Observation, Option<Observed>)>, now: Instant) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let row = &mut self.row;
        match player.exit_status() {
            Ok(Some(exit)) => {
                self.record_exit(exit.code().map(|code| code as u32), now);
                return;
            }
            Err(_) => {
                row.detail = Some("Could not query the owned Roblox process".into());
                return;
            }
            Ok(None) => (),
        }
        if self.close_request.is_some() {
            self.check_close_grace(now);
            return;
        }
        let Some((observation, facts)) = evidence.filter(|(observation, _)| {
            row.attempt_id == observation.attempt && row.phase == Phase::Starting
        }) else {
            return;
        };
        if let Some(facts) = facts {
            if let Some(mismatch) = observation.mismatch(&facts) {
                self.failure_stage = Some(FailureStage::ArrivalEvidence);
                self.observation = None;
                if player.stop().is_ok() {
                    self.player = None;
                    row.phase = Phase::Failed;
                    row.detail = Some(format!(
                        "Roblox reported {mismatch}. Zenith stopped the owned process."
                    ));
                } else {
                    row.phase = Phase::Unconfirmed;
                    row.detail = Some(format!(
                        "Roblox reported {mismatch} and the process could not be stopped."
                    ));
                }
                return;
            }
            if observation.confirms(&facts) && player.has_window() {
                self.observation = None;
                row.phase = Phase::Running;
                row.observed_server = facts.job_id;
                row.detail = Some("Account and initial destination confirmed".into());
                return;
            }
            if observation.private_server
                && observation.matches_account_and_place(&facts)
                && player.has_window()
            {
                self.observation = None;
                row.phase = Phase::Unconfirmed;
                row.observed_server = facts.job_id;
                row.detail =
                    Some("Account and place confirmed. Check the private server in Roblox.".into());
                return;
            }
        }
        if observation.started.elapsed() >= Duration::from_secs(65) {
            self.failure_stage = Some(FailureStage::ArrivalEvidence);
            self.observation = None;
            row.phase = Phase::Unconfirmed;
            row.detail = Some(
                "Roblox started, but Zenith could not confirm the account and destination".into(),
            );
        }
    }

    fn stop(&mut self) -> Result<(), String> {
        if !self.active() {
            return Ok(());
        }
        self.cancelled = true;
        self.close_request = None;
        self.observation = None;
        self.recovery.clear_wait();
        let row = &mut self.row;
        if self.preparing {
            row.phase = Phase::Stopping;
            return Ok(());
        }
        if let Some(player) = self.player.as_mut() {
            row.phase = Phase::Stopping;
            if player.stop().is_err() {
                row.phase = Phase::Unconfirmed;
                row.detail = Some("Could not stop the owned Roblox process".into());
                return Err("Could not stop the owned Roblox process".into());
            }
        }
        self.player = None;
        row.phase = Phase::Stopped;
        row.pid = None;
        row.detail = None;
        Ok(())
    }

    fn focus(&self) -> Result<(), String> {
        if !matches!(self.row.phase, Phase::Running | Phase::Closing) {
            return Err("The account's Roblox window has not been confirmed".into());
        }
        self.player
            .as_ref()
            .ok_or("The owned Roblox process is unavailable")?
            .focus()
            .map_err(str::to_owned)
    }

    fn begin_close(&mut self, now: Instant) {
        self.close_request = Some(CloseRequest {
            previous: self.row.phase,
            until: now + Duration::from_secs(5),
        });
        self.observation = None;
        self.recovery = Recovery::default();
        self.row.phase = Phase::Closing;
        self.row.detail = Some("Close requested. Waiting for Roblox to exit.".into());
    }

    fn check_close_grace(&mut self, now: Instant) {
        if let Some(request) = &self.close_request
            && now >= request.until
        {
            self.row.phase = request.previous;
            self.close_request = None;
            self.row.detail = Some(
                "Roblox is still running. Confirm any leave prompt in Roblox, request close again, or choose Force stop Roblox. Automatic restart is off for this session.".into()
            );
        }
    }
}

pub struct Prepared {
    player: VerifiedPlayer,
    ticket: zeroize::Zeroizing<String>,
    user_id: u64,
    target: Target,
}

pub fn prepare(
    identity: &SavedIdentity,
    credential: &mut Credential,
    mut target: Target,
    directory: &Directory,
    try_one: bool,
    permits: impl Fn() -> bool + Send + Sync + 'static,
) -> Result<Prepared, PreparationFailure> {
    fn check(permits: &impl Fn() -> bool) -> Result<(), PreparationFailure> {
        if !permits() {
            return Err(FailureStage::FinalAuthorization
                .failure("The launch was cancelled or the first client stopped."));
        }
        Ok(())
    }
    check(&permits)?;
    check_memory(try_one).map_err(|error| FailureStage::ResourceAdmission.failure(error))?;
    let player =
        VerifiedPlayer::registered().map_err(|error| FailureStage::PlayerTrust.failure(error))?;
    check(&permits)?;
    let auth = RobloxAuth::for_launch(permits)
        .map_err(|error| FailureStage::AccountVerification.failure(error.to_string()))?;
    auth.verify(credential, identity.user_id)
        .map_err(|error| FailureStage::AccountVerification.failure(error.to_string()))?;
    target
        .resolve(&auth, credential, directory)
        .map_err(|error| FailureStage::PrivateInviteResolution.failure(error))?;
    let ticket = auth
        .ticket(credential)
        .map_err(|error| FailureStage::LaunchTicket.failure(error.to_string()))?;
    Ok(Prepared {
        player,
        ticket,
        user_id: identity.user_id,
        target,
    })
}

fn check_memory(try_one: bool) -> Result<(), String> {
    let decision = memory_sample()?.admission(Instant::now());
    if decision.limiting_resource.is_none() || (try_one && decision.can_try_one) {
        return Ok(());
    }
    Err(format!(
        "Memory hold. {:.2} / {:.2} GiB physical available / required. {:.2} / {:.2} GiB commit available / required.",
        gib(decision.physical_available),
        gib(decision.physical_required),
        gib(decision.commit_available),
        gib(decision.commit_required)
    ))
}

fn gib(bytes: u64) -> f64 {
    bytes as f64 / (1_u64 << 30) as f64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn playback_targets_require_the_exact_account_and_a_confirmed_owned_window() {
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "Fixture".into(),
            user_id: 42,
        };
        let target = Target::public(1818, None);
        let mut owner = SessionOwner::default();
        assert_eq!(owner.playback_target(&account), PlaybackTarget::NoSession);
        owner.attempts.push(Attempt::new(&account, &target, None));
        for phase in [Phase::Preparing, Phase::Starting, Phase::Unconfirmed] {
            owner.attempts[0].row.phase = phase;
            assert_eq!(
                owner.playback_target(&account),
                PlaybackTarget::ArrivalUnconfirmed
            );
        }
        owner.attempts[0].row.phase = Phase::Running;
        assert_eq!(
            owner.playback_target(&account),
            PlaybackTarget::WindowUnavailable
        );
        let foreign = SavedIdentity {
            user_id: 43,
            ..account.clone()
        };
        owner.attempts[0].row.phase = Phase::Unconfirmed;
        assert_eq!(
            owner.playback_target(&foreign),
            PlaybackTarget::WindowUnavailable
        );
        owner.attempts.push(Attempt::new(&account, &target, None));
        assert_eq!(
            owner.playback_target(&account),
            PlaybackTarget::WindowUnavailable
        );
        owner.attempts[0].row.phase = Phase::Stopped;
        owner.attempts[1].row.phase = Phase::Failed;
        assert_eq!(owner.playback_target(&account), PlaybackTarget::NoSession);
    }

    #[test]
    fn panic_holds_resume_until_the_cancelled_preparation_reports_completion() {
        use crate::panic_stop::PanicStop;
        use std::sync::Mutex;

        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            user_id: 42,
            username: "Fixture".into(),
        };
        let attempt = Attempt::new(&account, &Target::public(1818, None), None);
        let id = attempt.row.attempt_id;
        let mut sessions = SessionOwner::default();
        sessions.attempts.push(attempt);
        let owner = Mutex::new(sessions);
        let panic = PanicStop::default();
        let epoch = panic.trip();
        panic.drain(epoch, &owner).unwrap();
        let mut sessions = owner.lock().unwrap();
        assert!(!sessions.permits(id));
        assert!(sessions.has_active_attempts());
        assert!(panic.resume(epoch, &sessions).is_err());
        sessions.fail_preparation(
            id,
            "Synthetic late response".into(),
            true,
            FailureStage::FinalAuthorization,
        );
        assert!(sessions.attempts[0].row.phase == Phase::Stopped);
        panic.resume(epoch, &sessions).unwrap();
        assert!(!sessions.permits(id));
    }

    #[test]
    fn arrangement_rejects_unknown_duplicate_and_unconfirmed_attempts() {
        let mut owner = SessionOwner::default();
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "Fixture".into(),
            user_id: 42,
        };
        let mut attempt = Attempt::new(&account, &Target::public(1818, None), None);
        attempt.row.phase = Phase::Unconfirmed;
        let id = attempt.row.attempt_id;
        owner.attempts.push(attempt);
        assert!(owner.tile_windows(&[Uuid::new_v4()], 0, || false).is_err());
        assert!(owner.tile_windows(&[id, id], 0, || false).is_err());
        assert!(owner.tile_windows(&[id], 0, || false).is_err());
    }

    #[test]
    fn window_visibility_rejects_unconfirmed_cancelled_and_stale_attempts() {
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "Fixture".into(),
            user_id: 42,
        };
        let mut owner = SessionOwner::default();
        let mut attempt = Attempt::new(&account, &Target::public(1818, None), None);
        let id = attempt.row.attempt_id;
        attempt.row.phase = Phase::Unconfirmed;
        owner.attempts.push(attempt);
        let change = |owner: &SessionOwner| {
            owner.set_window_visibility(id, WindowVisibility::Minimize, || unreachable!())
        };
        assert!(
            change(&owner)
                .unwrap_err()
                .contains("has not been confirmed")
        );
        owner.attempts[0].row.phase = Phase::Running;
        assert!(
            change(&owner)
                .unwrap_err()
                .contains("process is unavailable")
        );
        owner.attempts[0].cancelled = true;
        assert!(
            change(&owner)
                .unwrap_err()
                .contains("has not been confirmed")
        );
        owner.attempts.clear();
        assert!(change(&owner).unwrap_err().contains("session has changed"));
    }

    #[test]
    fn performance_requires_a_confirmed_attempt_with_its_owned_child() {
        let mut owner = SessionOwner::default();
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "Fixture".into(),
            user_id: 42,
        };
        let mut attempt = Attempt::new(&account, &Target::public(1818, None), None);
        let id = attempt.row.attempt_id;
        attempt.row.phase = Phase::Unconfirmed;
        owner.attempts.push(attempt);
        assert!(owner.client_performance().is_empty());
        assert!(
            owner
                .set_client_priority(id, PriorityAction::Lower)
                .is_err()
        );
        assert!(
            owner
                .set_client_efficiency(id, EfficiencyAction::Request, || true)
                .is_err()
        );
        owner.attempts[0].row.phase = Phase::Running;
        assert!(owner.client_performance().is_empty());
        assert!(
            owner
                .set_client_priority(id, PriorityAction::Lower)
                .is_err()
        );
        assert!(
            owner
                .set_client_efficiency(id, EfficiencyAction::Request, || true)
                .is_err()
        );
        assert!(
            owner
                .set_client_priority(Uuid::new_v4(), PriorityAction::Lower)
                .is_err()
        );
    }

    #[test]
    fn a_different_server_at_the_same_place_cannot_confirm_arrival() {
        let mut launch = Observation {
            attempt: Uuid::new_v4(),
            tracker: 42,
            launched: SystemTime::now(),
            started: Instant::now(),
            expected_user: 42,
            expected_place: 1818,
            expected_job: Some(Uuid::from_u128(1)),
            private_server: false,
        };
        let mut facts = Observed {
            user_id: Some(42),
            place_id: Some(1818),
            job_id: Some(Uuid::from_u128(2)),
            connected: true,
        };
        assert!(!launch.confirms(&facts));
        assert_eq!(launch.mismatch(&facts), Some("a different server"));
        facts.job_id = launch.expected_job;
        assert!(launch.confirms(&facts));
        facts.connected = false;
        assert!(!launch.confirms(&facts));
        facts.connected = true;
        launch.expected_job = None;
        assert!(launch.confirms(&facts));
        launch.private_server = true;
        assert!(launch.matches_account_and_place(&facts));
        assert!(!launch.confirms(&facts));
    }

    #[test]
    fn a_cancelled_attempt_cannot_dispatch_or_overwrite_a_new_session() {
        let mut owner = SessionOwner::default();
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "ZenithFixture".into(),
            user_id: 4242,
        };
        let initial = Target::public(1818, None);
        let pending = Attempt::new(&account, &initial, None);
        let first = pending.row.attempt_id;
        owner.attempts.push(pending);
        assert!(owner.reserve(&account, &initial, false, false).is_err());
        let duplicate = SavedIdentity {
            profile_id: Uuid::new_v4(),
            ..account.clone()
        };
        assert!(owner.reserve(&duplicate, &initial, false, false).is_err());
        owner.stop(first).unwrap();
        assert!(!owner.permits(first));
        assert!(owner.hold(Instant::now(), false).is_some());
        owner.fail_preparation(
            first,
            "cancelled worker".into(),
            false,
            FailureStage::FinalAuthorization,
        );
        let job = Uuid::from_u128(42);
        let replacement = Attempt::new(&account, &Target::public(1919, Some(job)), None);
        let second = replacement.row.attempt_id;
        owner.attempts.clear();
        owner.attempts.push(replacement);
        owner.fail_preparation(
            first,
            "late old failure".into(),
            false,
            FailureStage::LaunchTicket,
        );
        assert!(owner.permits(second));
        assert!(owner.stop(first).is_err());
        let row = &owner.attempt(second).unwrap().row;
        assert_eq!(row.place_id, "1919");
        assert_eq!(row.requested_server, Some(job));
    }

    #[test]
    fn a_pair_requires_confirmed_arrival_and_stabilization() {
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "FirstFixture".into(),
            user_id: 42,
        };
        let now = Instant::now();
        let mut owner = SessionOwner {
            last_start: Some(now),
            ..SessionOwner::default()
        };
        let mut first = Attempt::new(&account, &Target::public(1818, None), None);
        first.row.phase = Phase::Starting;
        first.preparing = false;
        owner.attempts.push(first);
        assert!(owner.hold(now + STABILIZATION, true).is_some());
        owner.attempts[0].row.phase = Phase::Unconfirmed;
        assert!(owner.hold(now + STABILIZATION, true).is_some());
        owner.attempts[0].row.phase = Phase::Running;
        assert!(
            owner
                .hold(now + STABILIZATION - Duration::from_millis(1), true)
                .is_some()
        );
        assert!(owner.hold(now + STABILIZATION, false).is_some());
        assert!(owner.hold(now + STABILIZATION, true).is_none());
    }

    #[test]
    fn stopping_the_first_client_fences_an_inflight_second_attempt() {
        let mut owner = SessionOwner::default();
        let target = Target::public(1818, None);
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "FirstFixture".into(),
            user_id: 42,
        };
        let mut first = Attempt::new(&account, &target, None);
        first.row.phase = Phase::Running;
        first.preparing = false;
        let first_id = first.row.attempt_id;
        owner.attempts.push(first);
        let other = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "OtherFixture".into(),
            user_id: 43,
        };
        let second = Attempt::new(&other, &target, Some(first_id));
        let second_id = second.row.attempt_id;
        owner.attempts.push(second);
        assert!(owner.permits(second_id));
        owner.stop(first_id).unwrap();
        assert!(!owner.permits(second_id));
        owner.fail_preparation(
            second_id,
            "First client stopped".into(),
            false,
            FailureStage::FinalAuthorization,
        );
        assert!(!owner.uses(account.profile_id));
        assert!(!owner.uses(other.profile_id));
        assert!(owner.hold(Instant::now(), true).is_none());
    }
}
