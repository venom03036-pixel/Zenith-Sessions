use std::{
    path::Path,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use uuid::Uuid;
use zenith_session_launch::{
    auth::{Credential, RobloxAuth},
    evidence::Observed,
    target::{Target, new_tracker, player_uri},
    windows::{
        OwnedPlayer, SavedIdentity, VerifiedPlayer, ensure_isolated_player_start, memory_sample,
    },
};

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Phase {
    Preparing,
    Starting,
    Running,
    Unconfirmed,
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
}

impl Observation {
    pub fn read(self, logs: &Path) -> Option<Observed> {
        Observed::for_attempt(logs, self.tracker, self.launched)
    }
}

#[derive(Default)]
pub struct SessionOwner {
    row: Option<Session>,
    observation: Option<Observation>,
    player: Option<OwnedPlayer>,
    preparing: bool,
    cancelled: bool,
    closing: bool,
}

impl SessionOwner {
    pub fn row(&self) -> Option<Session> {
        self.row.clone()
    }

    pub fn require_attempt(&self, attempt: Uuid) -> Result<(), String> {
        if self
            .row
            .as_ref()
            .is_some_and(|row| row.attempt_id == attempt)
        {
            Ok(())
        } else {
            Err("The selected session has changed".into())
        }
    }

    pub fn reserve(&mut self, account: &SavedIdentity, place: u64) -> Result<Uuid, String> {
        if self.closing || self.preparing || self.player.is_some() {
            return Err("Stop the current Zenith session before starting another".into());
        }
        let id = Uuid::new_v4();
        self.row = Some(Session {
            attempt_id: id,
            account_id: account.profile_id,
            username: account.username.clone(),
            place_id: place.to_string(),
            phase: Phase::Preparing,
            pid: None,
            observed_server: None,
            detail: None,
        });
        self.preparing = true;
        self.cancelled = false;
        Ok(id)
    }

    pub fn permits(&self, attempt: Uuid) -> bool {
        self.preparing
            && !self.cancelled
            && !self.closing
            && self
                .row
                .as_ref()
                .is_some_and(|row| row.attempt_id == attempt)
    }

    pub fn fail_preparation(&mut self, attempt: Uuid, error: String) {
        if let Some(row) = self.row.as_mut().filter(|row| row.attempt_id == attempt) {
            self.preparing = false;
            row.phase = if self.cancelled {
                Phase::Stopped
            } else {
                Phase::Failed
            };
            row.detail = Some(if self.cancelled {
                "Cancelled before starting Roblox".into()
            } else {
                error
            });
        }
    }

    pub fn dispatch(
        &mut self,
        request: Prepared,
        attempt: Uuid,
        try_one: bool,
    ) -> Result<(), String> {
        if !self.permits(attempt) {
            return Err("The launch was cancelled".into());
        }
        check_memory(try_one)?;
        ensure_isolated_player_start()?;
        let launched = SystemTime::now();
        let timestamp = launched
            .duration_since(UNIX_EPOCH)
            .map_err(|_| "System clock is invalid")?;
        let tracker = new_tracker();
        let uri = player_uri(
            &request.ticket,
            &Target {
                place: request.place,
                job: None,
            },
            tracker,
            attempt,
            timestamp.as_millis(),
        );
        let player = request.player.spawn(&uri)?;
        let pid = player.pid();
        self.player = Some(player);
        self.observation = Some(Observation {
            attempt,
            tracker,
            launched,
            started: Instant::now(),
            expected_user: request.user_id,
            expected_place: request.place,
        });
        self.preparing = false;
        if let Some(row) = self.row.as_mut() {
            row.phase = Phase::Starting;
            row.pid = Some(pid);
        }
        Ok(())
    }

    pub fn observation(&self) -> Option<Observation> {
        self.observation
    }

    pub fn reconcile(&mut self, evidence: Option<(Observation, Option<Observed>)>) {
        let Some(player) = self.player.as_mut() else {
            return;
        };
        let Some(row) = self.row.as_mut() else {
            return;
        };
        match player.exited() {
            Ok(true) => {
                self.player = None;
                self.observation = None;
                row.phase = if row.phase == Phase::Starting {
                    Phase::Failed
                } else {
                    Phase::Stopped
                };
                row.detail = Some("The Roblox process exited".into());
                return;
            }
            Err(_) => {
                row.detail = Some("Could not query the owned Roblox process".into());
                return;
            }
            Ok(false) => (),
        }
        let Some((observation, facts)) = evidence.filter(|(observation, _)| {
            row.attempt_id == observation.attempt && row.phase == Phase::Starting
        }) else {
            return;
        };
        if let Some(facts) = facts {
            if facts
                .user_id
                .is_some_and(|actual| actual != observation.expected_user)
                || facts
                    .place_id
                    .is_some_and(|actual| actual != observation.expected_place)
            {
                self.observation = None;
                if player.stop().is_ok() {
                    self.player = None;
                    row.phase = Phase::Failed;
                    row.detail = Some("Roblox reported a different account or place. Zenith stopped the owned process.".into());
                } else {
                    row.phase = Phase::Unconfirmed;
                    row.detail = Some("Roblox reported a different account or place and the process could not be stopped".into());
                }
                return;
            }
            if facts.user_id == Some(observation.expected_user)
                && facts.place_id == Some(observation.expected_place)
                && facts.connected
                && facts.job_id.is_some()
                && player.has_window()
            {
                self.observation = None;
                row.phase = Phase::Running;
                row.observed_server = facts.job_id;
                row.detail = Some("Account and initial destination confirmed".into());
                return;
            }
        }
        if observation.started.elapsed() >= Duration::from_secs(65) {
            self.observation = None;
            row.phase = Phase::Unconfirmed;
            row.detail = Some(
                "Roblox started, but Zenith could not confirm the account and destination".into(),
            );
        }
    }

    pub fn stop(&mut self, closing: bool) -> Result<(), String> {
        self.closing |= closing;
        self.cancelled = true;
        self.observation = None;
        let Some(row) = self.row.as_mut() else {
            return Ok(());
        };
        if self.preparing {
            row.phase = Phase::Stopping;
            return Ok(());
        }
        if let Some(player) = self.player.as_mut() {
            row.phase = Phase::Stopping;
            if player.stop().is_err() {
                self.closing = false;
                row.phase = Phase::Unconfirmed;
                row.detail = Some("Could not stop the owned Roblox process".into());
                return Err("Could not stop the owned Roblox process".into());
            }
        }
        self.player = None;
        row.phase = Phase::Stopped;
        row.detail = None;
        Ok(())
    }

    pub fn focus(&self) -> Result<(), String> {
        if self
            .row
            .as_ref()
            .is_none_or(|row| row.phase != Phase::Running)
        {
            return Err("The account's Roblox window has not been confirmed".into());
        }
        self.player
            .as_ref()
            .ok_or("The owned Roblox process is unavailable")?
            .focus()
            .map_err(str::to_owned)
    }

    pub fn uses(&self, id: Uuid) -> bool {
        (self.preparing || self.player.is_some())
            && self.row.as_ref().is_some_and(|row| row.account_id == id)
    }
}

pub struct Prepared {
    player: VerifiedPlayer,
    ticket: zeroize::Zeroizing<String>,
    user_id: u64,
    place: u64,
}

pub fn prepare(
    identity: SavedIdentity,
    mut credential: Credential,
    place: u64,
    try_one: bool,
    permits: impl Fn() -> bool,
) -> Result<Prepared, String> {
    fn check(permits: &impl Fn() -> bool) -> Result<(), String> {
        if !permits() {
            return Err("The launch was cancelled".into());
        }
        Ok(())
    }
    check(&permits)?;
    check_memory(try_one)?;
    ensure_isolated_player_start()?;
    let player = VerifiedPlayer::registered()?;
    check(&permits)?;
    let auth = RobloxAuth::new().map_err(|error| error.to_string())?;
    auth.verify(&mut credential, identity.user_id)
        .map_err(|error| error.to_string())?;
    check(&permits)?;
    let ticket = auth
        .ticket(&mut credential)
        .map_err(|error| error.to_string())?;
    check(&permits)?;
    Ok(Prepared {
        player,
        ticket,
        user_id: identity.user_id,
        place,
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
    fn a_cancelled_attempt_cannot_dispatch_or_overwrite_a_new_session() {
        let mut owner = SessionOwner::default();
        let account = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "ZenithFixture".into(),
            user_id: 4242,
        };
        let first = owner.reserve(&account, 1818).unwrap();
        assert!(owner.reserve(&account, 1818).is_err());
        owner.stop(false).unwrap();
        assert!(!owner.permits(first));
        assert!(owner.reserve(&account, 1818).is_err());
        owner.fail_preparation(first, "cancelled worker".into());
        let second = owner.reserve(&account, 1919).unwrap();
        owner.fail_preparation(first, "late old failure".into());
        assert!(owner.permits(second));
        assert!(owner.require_attempt(first).is_err());
        assert_eq!(owner.row().unwrap().place_id, "1919");
    }
}
