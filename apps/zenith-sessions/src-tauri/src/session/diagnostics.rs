use std::time::Instant;

use serde::Serialize;

use super::{Phase, SessionOwner};
use crate::recovery::ReviewState;

#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum FailureStage {
    LaunchAdmission,
    SavedSignIn,
    ResourceAdmission,
    PlayerTrust,
    AccountVerification,
    PrivateInviteResolution,
    LaunchTicket,
    FinalAuthorization,
    NativeStart,
    ArrivalEvidence,
    ProcessExit,
    AutomaticRestart,
}

pub struct PreparationFailure {
    pub stage: FailureStage,
    pub message: String,
}

impl FailureStage {
    pub(super) fn failure(self, message: impl Into<String>) -> PreparationFailure {
        PreparationFailure {
            stage: self,
            message: message.into(),
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "snake_case")]
enum DestinationKind {
    PublicPlace,
    PublicServer,
    PrivateInvite,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AttemptSummary {
    phase: Phase,
    destination_kind: DestinationKind,
    process_receipt_present: bool,
    server_evidence_present: bool,
    automatic_restart: Option<usize>,
    failure_stage: Option<FailureStage>,
    native_exit_status: Option<u32>,
}

#[derive(Serialize)]
#[serde(rename_all = "snake_case")]
enum JournalStatus {
    Available,
    Unreadable,
    WriteFailed,
}

#[derive(Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReviewCounts {
    interrupted: usize,
    unknown: usize,
    surviving: usize,
    ended: usize,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchDiagnostics {
    active_attempts: usize,
    paired_mode: bool,
    scheduler_hold: bool,
    journal_status: JournalStatus,
    last_reservation_failure: Option<FailureStage>,
    recovery: ReviewCounts,
    recent_attempts: Vec<AttemptSummary>,
}

impl SessionOwner {
    pub fn launch_diagnostics(&self, now: Instant) -> LaunchDiagnostics {
        let mut recovery = ReviewCounts::default();
        for record in &self.recovered {
            match record.review() {
                ReviewState::Interrupted => recovery.interrupted += 1,
                ReviewState::Unknown => recovery.unknown += 1,
                ReviewState::Surviving => recovery.surviving += 1,
                ReviewState::Ended => recovery.ended += 1,
            }
        }
        let journal_status = if self.journal.can_archive() {
            JournalStatus::Unreadable
        } else if self.journal.issue().is_some() {
            JournalStatus::WriteFailed
        } else {
            JournalStatus::Available
        };
        LaunchDiagnostics {
            active_attempts: self.active().count(),
            paired_mode: self.pair.is_some(),
            scheduler_hold: self.hold(now, self.pair.is_some()).is_some(),
            journal_status,
            last_reservation_failure: self.last_reservation_failure,
            recovery,
            recent_attempts: self
                .attempts
                .iter()
                .rev()
                .map(|attempt| {
                    let row = &attempt.row;
                    let destination_kind = if row.private_server {
                        DestinationKind::PrivateInvite
                    } else if row.requested_server.is_some() {
                        DestinationKind::PublicServer
                    } else {
                        DestinationKind::PublicPlace
                    };
                    AttemptSummary {
                        phase: row.phase,
                        destination_kind,
                        process_receipt_present: row.pid.is_some(),
                        server_evidence_present: row.observed_server.is_some(),
                        automatic_restart: row.automatic_restart,
                        failure_stage: attempt.failure_stage,
                        native_exit_status: attempt.exit_status,
                    }
                })
                .collect(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        recovery::{Checkpoint, Record},
        session::Attempt,
    };
    use uuid::Uuid;
    use zenith_session_launch::{target::Target, windows::SavedIdentity};

    #[test]
    fn allowlist_drops_all_identifiers_and_free_text_from_attempts_and_recovery() {
        let account = SavedIdentity {
            profile_id: Uuid::from_u128(184),
            user_id: 1999988888,
            username: "PRIVATE_FIXTURE_NAME".into(),
        };
        let target = Target::public(7878787899, Some(Uuid::from_u128(123)));
        let mut attempt = Attempt::new(&account, &target, None);
        let id = attempt.row.attempt_id;
        attempt.row.pid = Some(232323);
        attempt.row.observed_server = Some(Uuid::from_u128(993));
        let mut owner = SessionOwner::default();
        owner.record_reservation_failure(FailureStage::SavedSignIn);
        owner.attempts.push(attempt);
        owner.fail_preparation(
            id,
            "http://private.fixture/.ROBLOSECURITY=TOKEN".into(),
            false,
            FailureStage::LaunchTicket,
        );
        owner.recovered.push(Record {
            attempt_id: Uuid::from_u128(124),
            account_id: account.profile_id,
            username: account.username.clone(),
            place_id: "7878787899".into(),
            requested_server: target.job(),
            private_server: false,
            checkpoint: Checkpoint::Dispatching,
        });
        let report = serde_json::to_value(owner.launch_diagnostics(Instant::now())).unwrap();
        assert_eq!(
            report["recentAttempts"][0],
            serde_json::json!({
                "phase": "failed",
                "destinationKind": "public_server",
                "processReceiptPresent": true,
                "serverEvidencePresent": true,
                "automaticRestart": null,
                "failureStage": "launch_ticket",
                "nativeExitStatus": null,
            })
        );
        assert_eq!(report["recovery"]["unknown"], 1);
        assert_eq!(report["lastReservationFailure"], "saved_sign_in");
        let text = report.to_string();
        for value in [
            "PRIVATE_FIXTURE_NAME",
            "TOKEN",
            "ROBLOSECURITY",
            "232323",
            "7878787899",
            "1999988888",
            &id.to_string(),
            &account.profile_id.to_string(),
            &target.job().unwrap().to_string(),
        ] {
            assert!(!text.contains(value));
        }
    }
}
