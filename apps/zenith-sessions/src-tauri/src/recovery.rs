use std::{collections::HashSet, fs, io::ErrorKind, path::PathBuf};

use serde::{Deserialize, Serialize};
use uuid::Uuid;
use zenith_session_launch::windows::{ProcessIdentity, ProcessStatus, observe_process};

use crate::library::{atomic_write, read_limit};

pub const MAX_ENTRIES: usize = 32;
const MAX_BYTES: usize = 32 * 1024;
const UNREADABLE: &str =
    "The launch journal is unreadable or from another version. Review it before launching.";
const WRITE_FAILED: &str =
    "Zenith could not save launch recovery information. New launches are held.";

#[derive(Clone, Copy, Deserialize, PartialEq, Eq, Serialize)]
#[serde(
    tag = "state",
    content = "process",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum Checkpoint {
    Preparing,
    Dispatching,
    Started(ProcessIdentity),
}

#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Record {
    pub attempt_id: Uuid,
    pub account_id: Uuid,
    pub username: String,
    pub place_id: String,
    pub requested_server: Option<Uuid>,
    pub checkpoint: Checkpoint,
}

impl Record {
    pub fn review(&self) -> ReviewState {
        match self.checkpoint {
            Checkpoint::Preparing => ReviewState::Interrupted,
            Checkpoint::Dispatching => ReviewState::Unknown,
            Checkpoint::Started(identity) => match observe_process(identity) {
                ProcessStatus::Running => ReviewState::Surviving,
                ProcessStatus::Ended => ReviewState::Ended,
                ProcessStatus::Unknown => ReviewState::Unknown,
            },
        }
    }

    pub fn view(&self) -> Review {
        Review {
            attempt_id: self.attempt_id,
            username: self.username.clone(),
            place_id: self.place_id.clone(),
            requested_server: self.requested_server,
            pid: match self.checkpoint {
                Checkpoint::Started(process) => Some(process.pid),
                _ => None,
            },
            state: self.review(),
        }
    }

    fn valid(&self) -> bool {
        !self.attempt_id.is_nil()
            && !self.account_id.is_nil()
            && !self.username.is_empty()
            && self.username.len() <= 20
            && self
                .username
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'_')
            && self.place_id.bytes().all(|b| b.is_ascii_digit())
            && self.place_id.parse::<i64>().is_ok_and(|id| id > 0)
            && !self.requested_server.is_some_and(|id| id.is_nil())
            && match self.checkpoint {
                Checkpoint::Started(process) => process.pid > 0 && process.started_filetime > 0,
                _ => true,
            }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ReviewState {
    Interrupted,
    Unknown,
    Surviving,
    Ended,
}

impl ReviewState {
    pub fn holds(self) -> bool {
        matches!(self, Self::Unknown | Self::Surviving)
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Review {
    attempt_id: Uuid,
    username: String,
    place_id: String,
    requested_server: Option<Uuid>,
    pid: Option<u32>,
    state: ReviewState,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecoveryView {
    pub issue: Option<&'static str>,
    pub can_archive: bool,
    pub attempts: Vec<Review>,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Document {
    version: u8,
    attempts: Vec<Record>,
}

#[derive(Default)]
pub struct Journal {
    path: Option<PathBuf>,
    saved: Vec<Record>,
    unreadable: bool,
    write_failed: bool,
}

impl Journal {
    pub fn open(path: PathBuf) -> (Self, Vec<Record>) {
        let contents = match read_limit(&path, MAX_BYTES) {
            Ok(bytes) => Self::parse(&bytes),
            Err(error) if error.kind() == ErrorKind::NotFound => Some(Vec::new()),
            Err(_) => None,
        };
        let unreadable = contents.is_none();
        let recovered = contents.unwrap_or_default();
        (
            Self {
                path: Some(path),
                saved: recovered.clone(),
                unreadable,
                write_failed: false,
            },
            recovered,
        )
    }

    fn parse(bytes: &[u8]) -> Option<Vec<Record>> {
        let doc: Document = serde_json::from_slice(bytes).ok()?;
        (doc.version == 1 && valid_attempts(&doc.attempts)).then_some(doc.attempts)
    }

    pub fn issue(&self) -> Option<&'static str> {
        if self.unreadable {
            Some(UNREADABLE)
        } else {
            self.write_failed.then_some(WRITE_FAILED)
        }
    }

    pub fn can_archive(&self) -> bool {
        self.unreadable
    }

    pub fn save(&mut self, attempts: Vec<Record>) -> Result<(), String> {
        if self.unreadable {
            return Err(UNREADABLE.into());
        }
        if attempts == self.saved {
            self.write_failed = false;
            return Ok(());
        }
        if attempts.len() > MAX_ENTRIES {
            return Err("Dismiss a reviewed recovery entry before starting another launch.".into());
        }
        if !valid_attempts(&attempts) {
            self.write_failed = true;
            return Err(WRITE_FAILED.into());
        }
        let document = Document {
            version: 1,
            attempts,
        };
        if let Some(path) = &self.path {
            let bytes = serde_json::to_vec(&document).map_err(|_| WRITE_FAILED)?;
            if atomic_write(path, &bytes).is_err() {
                self.write_failed = true;
                return Err(WRITE_FAILED.into());
            }
        }
        self.saved = document.attempts;
        self.write_failed = false;
        Ok(())
    }

    pub fn archive_unreadable(&mut self) -> Result<(), String> {
        let path = self
            .path
            .as_ref()
            .filter(|_| self.unreadable)
            .ok_or("No unreadable launch journal needs archiving.")?;
        let destination =
            path.with_file_name(format!("launch-journal-archived-{}.json", Uuid::new_v4()));
        match fs::rename(path, destination) {
            Ok(()) => (),
            Err(error) if error.kind() == ErrorKind::NotFound => (),
            Err(_) => return Err("Could not archive the unreadable launch journal.".into()),
        }
        self.unreadable = false;
        Ok(())
    }
}

fn valid_attempts(attempts: &[Record]) -> bool {
    let mut ids = HashSet::new();
    attempts.len() <= MAX_ENTRIES
        && attempts
            .iter()
            .all(|record| record.valid() && ids.insert(record.attempt_id))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session::SessionOwner;

    struct Fixture(PathBuf);

    impl Fixture {
        fn new() -> Self {
            let root =
                std::env::temp_dir().join(format!("zenith-recovery-fixture-{}", Uuid::new_v4()));
            fs::create_dir(&root).unwrap();
            Self(root)
        }

        fn journal(&self) -> PathBuf {
            self.0.join("launch-journal.json")
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            if let Ok(files) = fs::read_dir(&self.0) {
                for file in files.flatten() {
                    let _ = fs::remove_file(file.path());
                }
            }
            let _ = fs::remove_dir(&self.0);
        }
    }

    fn record(checkpoint: Checkpoint) -> Record {
        Record {
            attempt_id: Uuid::new_v4(),
            account_id: Uuid::new_v4(),
            username: "ZenithFixture".into(),
            place_id: "1818".into(),
            requested_server: Some(Uuid::from_u128(42)),
            checkpoint,
        }
    }

    #[test]
    fn reopen_keeps_preparation_and_uncertain_dispatch_distinct_and_does_not_replay() {
        let fixture = Fixture::new();
        let interrupted = record(Checkpoint::Preparing);
        let uncertain = record(Checkpoint::Dispatching);
        let reused_pid = record(Checkpoint::Started(ProcessIdentity {
            pid: std::process::id(),
            started_filetime: 1,
        }));
        let (mut journal, _) = Journal::open(fixture.journal());
        journal
            .save(vec![interrupted.clone(), uncertain, reused_pid])
            .unwrap();
        let before = fs::read(fixture.journal()).unwrap();
        let mut owner = SessionOwner::open(fixture.journal());
        assert!(owner.rows().is_empty());
        assert_eq!(fs::read(fixture.journal()).unwrap(), before);
        let recovery = owner.recovery();
        let states: Vec<_> = recovery.attempts.iter().map(|entry| entry.state).collect();
        assert!(
            states
                == [
                    ReviewState::Interrupted,
                    ReviewState::Unknown,
                    ReviewState::Ended
                ]
        );
        owner.dismiss_recovered(interrupted.attempt_id).unwrap();
        drop(owner);
        let owner = SessionOwner::open(fixture.journal());
        assert_eq!(owner.recovery().attempts.len(), 2);
        assert!(owner.rows().is_empty());
    }

    #[test]
    fn unreadable_and_future_journals_stay_intact_until_explicit_archive() {
        let fixture = Fixture::new();
        for bytes in [
            b"{".as_slice(),
            br#"{"version":99,"attempts":[]}"#.as_slice(),
        ] {
            let path = fixture.journal();
            fs::write(&path, bytes).unwrap();
            let (mut journal, recovered) = Journal::open(path.clone());
            assert!(recovered.is_empty() && journal.can_archive());
            assert!(journal.save(vec![record(Checkpoint::Preparing)]).is_err());
            assert_eq!(fs::read(&path).unwrap(), bytes);
            journal.archive_unreadable().unwrap();
            assert!(journal.issue().is_none());
            assert!(!path.exists());
            let archives = fs::read_dir(&fixture.0).unwrap().flatten();
            assert!(
                archives
                    .into_iter()
                    .any(|entry| fs::read(entry.path()).is_ok_and(|data| data == bytes))
            );
        }
    }

    #[test]
    fn surviving_exact_process_blocks_dismissal_without_granting_control() {
        use windows::Win32::{
            Foundation::FILETIME,
            System::Threading::{GetCurrentProcess, GetProcessTimes},
        };
        let mut creation = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        // SAFETY: only query the current test process with writable output structures.
        unsafe {
            GetProcessTimes(
                GetCurrentProcess(),
                &mut creation,
                &mut exit,
                &mut kernel,
                &mut user,
            )
        }
        .unwrap();
        let exact = ProcessIdentity {
            pid: std::process::id(),
            started_filetime: (u64::from(creation.dwHighDateTime) << 32)
                | u64::from(creation.dwLowDateTime),
        };
        let entry = record(Checkpoint::Started(exact));
        let fixture = Fixture::new();
        let (mut journal, _) = Journal::open(fixture.journal());
        journal.save(vec![entry.clone()]).unwrap();
        let mut owner = SessionOwner::open(fixture.journal());
        assert!(owner.recovery().attempts[0].state == ReviewState::Surviving);
        assert!(owner.uses(entry.account_id));
        assert!(owner.dismiss_recovered(entry.attempt_id).is_err());
        assert!(owner.focus(entry.attempt_id).is_err());
        assert!(owner.stop(entry.attempt_id).is_err());
        assert!(owner.rows().is_empty());
    }

    #[test]
    fn journal_write_failure_keeps_the_last_committed_state_until_retry() {
        let fixture = Fixture::new();
        let parent = fixture.0.join("temporarily-unavailable");
        let path = parent.join("launch-journal.json");
        let (mut journal, recovered) = Journal::open(path.clone());
        assert!(recovered.is_empty());
        let entry = record(Checkpoint::Dispatching);
        assert!(journal.save(vec![entry.clone()]).is_err());
        assert_eq!(journal.issue(), Some(WRITE_FAILED));
        assert!(!path.exists());
        fs::create_dir(&parent).unwrap();
        journal.save(vec![entry.clone()]).unwrap();
        assert!(journal.issue().is_none());
        let (_, recovered) = Journal::open(path.clone());
        assert!(recovered == [entry]);
        fs::remove_file(path).unwrap();
        fs::remove_dir(parent).unwrap();
    }
}
