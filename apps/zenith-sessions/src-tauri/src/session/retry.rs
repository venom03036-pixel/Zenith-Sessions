use std::time::{Duration, Instant};

use uuid::Uuid;
use zenith_session_launch::{target::Target, windows::SavedIdentity};

use super::{Attempt, FailureStage, Phase, SessionOwner};

const BACKOFF: [Duration; 2] = [Duration::from_secs(8), Duration::from_secs(16)];

#[derive(Clone, Copy, PartialEq, Eq)]
struct RecoveryPolicy {
    epoch: u64,
    allow_pair: bool,
    previous: [Option<Uuid>; BACKOFF.len()],
    retries: usize,
}

impl RecoveryPolicy {
    fn replacement(mut self, previous: Uuid) -> Option<Self> {
        *self.previous.get_mut(self.retries)? = Some(previous);
        self.retries += 1;
        Some(self)
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct RetryRequest {
    previous: Uuid,
    pub account: Uuid,
    user: u64,
    pub place: u64,
    pub job: Option<Uuid>,
    policy: RecoveryPolicy,
}

impl RetryRequest {
    pub fn epoch(self) -> u64 {
        self.policy.epoch
    }
}

#[derive(Default)]
pub(super) struct Recovery {
    policy: Option<RecoveryPolicy>,
    due: Option<Instant>,
}

impl Recovery {
    pub(super) fn clear_wait(&mut self) {
        self.due = None;
    }

    pub(super) fn includes(&self, id: Uuid) -> bool {
        self.policy
            .is_some_and(|policy| policy.previous.contains(&Some(id)))
    }
}

impl Attempt {
    fn retry_request(&self, now: Instant) -> Option<RetryRequest> {
        if self.row.phase != Phase::RetryWaiting || self.recovery.due? > now || self.cancelled {
            return None;
        }
        Some(RetryRequest {
            previous: self.row.attempt_id,
            account: self.row.account_id,
            user: self.user_id,
            place: self.row.place_id.parse().ok()?,
            job: self.row.requested_server,
            policy: self.recovery.policy?,
        })
    }

    pub(super) fn record_exit(&mut self, code: Option<u32>, now: Instant) {
        self.player = None;
        self.observation = None;
        self.row.pid = None;
        self.exit_status = code;
        let arrived = self.row.phase == Phase::Running;
        self.row.phase = if self.row.phase == Phase::Starting {
            Phase::Failed
        } else {
            Phase::Stopped
        };
        let fatal = code.filter(|code| is_fatal_exit(*code));
        if fatal.is_some() || self.row.phase == Phase::Failed {
            self.failure_stage = Some(FailureStage::ProcessExit);
        }
        if let Some(code) = fatal {
            self.row.phase = Phase::Failed;
            let detail = format!("Windows reported a fatal exit (0x{code:08X}).");
            if let Some(policy) = self.recovery.policy
                && arrived
                && !self.cancelled
            {
                if let Some(delay) = BACKOFF.get(policy.retries) {
                    self.row.phase = Phase::RetryWaiting;
                    self.recovery.due = Some(now + *delay);
                    self.row.detail = Some(format!(
                        "{detail} Restart {}/{} waits {} seconds.",
                        policy.retries + 1,
                        BACKOFF.len(),
                        delay.as_secs()
                    ));
                    return;
                }
                self.row.detail = Some(format!(
                    "{detail} Automatic restart limit reached. Review the session before launching again."
                ));
                return;
            }
            self.row.detail = Some(detail);
        } else {
            self.row.detail = Some(match code {
                Some(0) => "The Roblox process exited.".into(),
                Some(code) => format!("The Roblox process exited with status 0x{code:08X}."),
                None => "The Roblox process exited without a reported status.".into(),
            });
        }
    }
}

fn is_fatal_exit(code: u32) -> bool {
    matches!(
        code,
        0xC000_0005 | 0xC000_001D | 0xC000_0094 | 0xC000_00FD | 0xC000_0374 | 0xC000_0409
    )
}

impl SessionOwner {
    pub fn arm_recovery(&mut self, id: Uuid, epoch: u64, allow_pair: bool) -> Result<(), String> {
        let attempt = self.attempt_mut(id)?;
        if !attempt.row.private_server {
            attempt.recovery.policy = Some(RecoveryPolicy {
                epoch,
                allow_pair,
                previous: [None; BACKOFF.len()],
                retries: 0,
            });
        }
        Ok(())
    }

    pub fn retry_candidate(&self, now: Instant) -> Option<RetryRequest> {
        self.attempts
            .iter()
            .find_map(|attempt| attempt.retry_request(now))
    }

    pub fn reserve_retry(
        &mut self,
        request: RetryRequest,
        account: &SavedIdentity,
        target: &Target,
        now: Instant,
    ) -> Result<Uuid, String> {
        if self.attempt(request.previous)?.retry_request(now) != Some(request)
            || request.account != account.profile_id
            || request.user != account.user_id
            || target.is_private()
            || target.place != request.place
            || target.job() != request.job
        {
            return Err("The automatic restart was cancelled or the saved account changed.".into());
        }
        let policy = request
            .policy
            .replacement(request.previous)
            .ok_or("The automatic restart limit has been reached.")?;
        self.attempt_mut(request.previous)?.row.phase = Phase::Failed;
        match self.reserve(account, target, policy.allow_pair, false) {
            Ok(id) => {
                let replacement = self.attempt_mut(id)?;
                replacement.recovery.policy = Some(policy);
                replacement.row.automatic_restart = Some(policy.retries);
                Ok(id)
            }
            Err(error) => {
                let attempt = self.attempt_mut(request.previous)?;
                attempt.recovery.clear_wait();
                attempt.failure_stage = Some(FailureStage::AutomaticRestart);
                attempt.row.detail = Some(format!("Automatic restart stopped. {error}"));
                Err(error)
            }
        }
    }

    pub fn fail_retry(&mut self, request: RetryRequest, reason: &str) {
        if let Ok(attempt) = self.attempt_mut(request.previous)
            && attempt.retry_request(Instant::now()) == Some(request)
        {
            attempt.recovery.clear_wait();
            attempt.row.phase = Phase::Failed;
            attempt.failure_stage = Some(FailureStage::AutomaticRestart);
            attempt.row.detail = Some(format!("Automatic restart stopped. {reason}"));
        }
    }

    pub fn stop_target(&self, requested: Uuid) -> Uuid {
        self.active()
            .find(|attempt| attempt.recovery.includes(requested))
            .map_or(requested, |attempt| attempt.row.attempt_id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ACCESS_VIOLATION: u32 = 0xC000_0005;

    fn saved() -> SavedIdentity {
        SavedIdentity {
            profile_id: Uuid::from_u128(2),
            user_id: 42,
            username: "Fixture".into(),
        }
    }

    fn owned_attempt(owner: &mut SessionOwner, phase: Phase, recover: bool) -> Uuid {
        let target = Target::public(1818, Some(Uuid::from_u128(3)));
        let mut attempt = Attempt::new(&saved(), &target, None);
        attempt.row.phase = phase;
        attempt.preparing = false;
        let id = attempt.row.attempt_id;
        owner.attempts.push(attempt);
        if recover {
            owner.arm_recovery(id, 6, true).unwrap();
        }
        id
    }

    #[test]
    fn only_a_fatal_exit_after_confirmed_arrival_and_opt_in_can_schedule() {
        let now = Instant::now();
        for (phase, code, recover, waits) in [
            (Phase::Running, Some(0), true, false),
            (Phase::Running, Some(1), true, false),
            (Phase::Running, None, true, false),
            (Phase::Running, Some(ACCESS_VIOLATION), false, false),
            (Phase::Starting, Some(ACCESS_VIOLATION), true, false),
            (Phase::Unconfirmed, Some(ACCESS_VIOLATION), true, false),
            (Phase::Running, Some(ACCESS_VIOLATION), true, true),
        ] {
            let mut owner = SessionOwner::default();
            let id = owned_attempt(&mut owner, phase, recover);
            owner.attempt_mut(id).unwrap().record_exit(code, now);
            assert!(
                owner
                    .retry_candidate(now + Duration::from_secs(7))
                    .is_none()
            );
            let due = owner.retry_candidate(now + BACKOFF[0]);
            assert_eq!(due.is_some(), waits);
            if let Some(due) = due {
                assert_eq!(due.account, saved().profile_id);
                assert_eq!(due.epoch(), 6);
                assert_eq!((due.place, due.job), (1818, Some(Uuid::from_u128(3))));
                assert!(owner.recovery_records().is_empty());
            }
        }
    }

    #[test]
    fn backoff_doubles_then_the_breaker_requires_a_new_manual_launch() {
        let now = Instant::now();
        let mut owner = SessionOwner::default();
        let first = owned_attempt(&mut owner, Phase::Running, true);
        let original = owner.attempt(first).unwrap().recovery.policy.unwrap();
        let next_policy = original.replacement(first).unwrap();
        owner.attempts.clear();
        let second = owned_attempt(&mut owner, Phase::Running, false);
        let attempt = owner.attempt_mut(second).unwrap();
        attempt.recovery.policy = Some(next_policy);
        attempt.record_exit(Some(ACCESS_VIOLATION), now);
        assert!(
            owner
                .retry_candidate(now + Duration::from_secs(15))
                .is_none()
        );
        let retry = owner.retry_candidate(now + BACKOFF[1]).unwrap();
        let final_policy = retry.policy.replacement(second).unwrap();
        owner.attempts.clear();
        let third = owned_attempt(&mut owner, Phase::Running, false);
        let attempt = owner.attempt_mut(third).unwrap();
        attempt.recovery.policy = Some(final_policy);
        attempt.record_exit(Some(ACCESS_VIOLATION), now);
        assert!(
            owner
                .retry_candidate(now + Duration::from_secs(60))
                .is_none()
        );
        assert!(
            owner
                .attempt(third)
                .unwrap()
                .row
                .detail
                .as_deref()
                .unwrap()
                .contains("limit reached")
        );
    }

    #[test]
    fn cancelled_or_changed_recovery_cannot_reserve_and_stale_stop_tracks_only_its_chain() {
        let now = Instant::now();
        let mut owner = SessionOwner::default();
        let first = owned_attempt(&mut owner, Phase::Running, true);
        owner
            .attempt_mut(first)
            .unwrap()
            .record_exit(Some(ACCESS_VIOLATION), now);
        let when_due = now + BACKOFF[0];
        let retry = owner.retry_candidate(when_due).unwrap();
        assert!(
            owner
                .reserve_retry(retry, &saved(), &Target::public(1818, None), when_due)
                .is_err()
        );
        assert!(owner.retry_candidate(when_due).is_some());
        owner.stop(first).unwrap();
        let exact = Target::public(retry.place, retry.job);
        assert!(
            owner
                .reserve_retry(retry, &saved(), &exact, when_due)
                .is_err()
        );
        owner.attempts.clear();
        let replacement = owned_attempt(&mut owner, Phase::Preparing, false);
        let attempt = owner.attempt_mut(replacement).unwrap();
        attempt.preparing = true;
        attempt.recovery.policy = retry.policy.replacement(first);
        assert_eq!(owner.stop_target(first), replacement);
        owner.stop(first).unwrap();
        assert!(!owner.permits(replacement));
        owner.attempts.clear();
        let manual = owned_attempt(&mut owner, Phase::Running, false);
        assert!(owner.stop(first).is_err());
        assert!(owner.attempt(manual).unwrap().row.phase == Phase::Running);
    }

    #[test]
    fn panic_drain_cancels_waiting_recovery_before_resume() {
        let now = Instant::now();
        let panic = crate::panic_stop::PanicStop::default();
        let mut owner = SessionOwner::default();
        let attempt = owned_attempt(&mut owner, Phase::Running, true);
        owner
            .arm_recovery(attempt, panic.permit().unwrap(), false)
            .unwrap();
        owner
            .attempt_mut(attempt)
            .unwrap()
            .record_exit(Some(ACCESS_VIOLATION), now);
        let old = owner.retry_candidate(now + BACKOFF[0]).unwrap();
        let owner = std::sync::Mutex::new(owner);
        let epoch = panic.trip();
        panic.drain(epoch, &owner).unwrap();
        let owner = owner.lock().unwrap();
        panic.resume(epoch, &owner).unwrap();
        assert!(!panic.permits(old.epoch()));
        assert!(owner.retry_candidate(now + BACKOFF[1]).is_none());
    }
}
