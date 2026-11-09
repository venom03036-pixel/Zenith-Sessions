use std::{
    collections::HashSet,
    io::ErrorKind,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::library::{atomic_write, read_limit};

const MAX_VISITS: usize = 24;
const MAX_BYTES: usize = 8 * 1024;
const UNREADABLE: &str = "Saved server history is unreadable. Clear history to start a new list.";
const WRITE_FAILED: &str =
    "Zenith could not save recent servers. New visits are visible for this run.";

#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Visit {
    place_id: String,
    server_id: Uuid,
    observed_at: u64,
}

impl Visit {
    fn valid(&self) -> bool {
        self.place_id
            .parse::<i64>()
            .is_ok_and(|id| id > 0 && self.place_id == id.to_string())
            && !self.server_id.is_nil()
            && (1..=253_402_300_799).contains(&self.observed_at)
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Document {
    version: u8,
    visits: Vec<Visit>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerHistoryView {
    visits: Vec<Visit>,
    issue: Option<&'static str>,
}

#[derive(Default)]
pub struct ServerHistory {
    path: Option<PathBuf>,
    visits: Vec<Visit>,
    unreadable: bool,
    write_failed: bool,
}

impl ServerHistory {
    pub fn open(path: PathBuf) -> Self {
        let visits = match read_limit(&path, MAX_BYTES) {
            Ok(bytes) => Self::parse(&bytes),
            Err(error) if error.kind() == ErrorKind::NotFound => Some(Vec::new()),
            Err(_) => None,
        };
        Self {
            path: Some(path),
            unreadable: visits.is_none(),
            visits: visits.unwrap_or_default(),
            write_failed: false,
        }
    }

    fn parse(bytes: &[u8]) -> Option<Vec<Visit>> {
        let document: Document = serde_json::from_slice(bytes).ok()?;
        let mut seen = HashSet::new();
        if document.version != 1
            || document.visits.len() > MAX_VISITS
            || document
                .visits
                .iter()
                .any(|visit| !visit.valid() || !seen.insert((&visit.place_id, visit.server_id)))
        {
            return None;
        }
        Some(document.visits)
    }

    pub fn view(&self) -> ServerHistoryView {
        ServerHistoryView {
            visits: self.visits.clone(),
            issue: if self.unreadable {
                Some(UNREADABLE)
            } else {
                self.write_failed.then_some(WRITE_FAILED)
            },
        }
    }

    pub fn record(&mut self, place: u64, server: Uuid, observed: SystemTime) {
        let Ok(observed) = observed.duration_since(UNIX_EPOCH) else {
            return;
        };
        let visit = Visit {
            place_id: place.to_string(),
            server_id: server,
            observed_at: observed.as_secs(),
        };
        if !visit.valid() {
            return;
        }
        self.visits
            .retain(|old| old.place_id != visit.place_id || old.server_id != server);
        self.visits.insert(0, visit);
        self.visits.truncate(MAX_VISITS);
        if !self.unreadable {
            self.write_failed = self.save(&self.visits).is_err();
        }
    }

    pub fn clear(&mut self) -> Result<(), String> {
        self.save(&[])?;
        self.visits.clear();
        self.unreadable = false;
        self.write_failed = false;
        Ok(())
    }

    fn save(&self, visits: &[Visit]) -> Result<(), String> {
        #[derive(Serialize)]
        struct Saved<'a> {
            version: u8,
            visits: &'a [Visit],
        }
        if let Some(path) = &self.path {
            let bytes = serde_json::to_vec(&Saved { version: 1, visits })
                .map_err(|_| "Could not encode public server history.")?;
            atomic_write(path, &bytes).map_err(|_| "Could not save public server history.")?;
        }
        Ok(())
    }
}

pub fn self_check() -> Result<String, String> {
    let directory = std::env::temp_dir().join(format!("zenith-server-history-{}", Uuid::new_v4()));
    std::fs::create_dir(&directory)
        .map_err(|_| "Could not create a disposable history directory.")?;
    let path = directory.join("public-server-history.json");
    let result = (|| {
        let mut history = ServerHistory::open(path.clone());
        let server = Uuid::new_v4();
        history.record(1818, server, SystemTime::now());
        let mut reopened = ServerHistory::open(path.clone());
        if reopened.visits.as_slice() != history.visits.as_slice()
            || reopened.visits.len() != 1
            || reopened.visits[0].server_id != server
        {
            return Err("The synthetic server visit did not reopen.".into());
        }
        reopened.clear()?;
        let cleared = ServerHistory::open(path.clone());
        if !cleared.visits.is_empty() || cleared.view().issue.is_some() {
            return Err("The synthetic history did not clear.".into());
        }
        Ok("Saved, reopened, and cleared a synthetic public server visit.".into())
    })();
    let _ = std::fs::remove_file(path);
    std::fs::remove_dir(directory)
        .map_err(|_| "Could not remove the disposable history directory.")?;
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, time::Duration};

    struct Fixture(PathBuf);

    impl Fixture {
        fn new() -> Self {
            let path =
                std::env::temp_dir().join(format!("zenith-history-fixture-{}", Uuid::new_v4()));
            fs::create_dir(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> PathBuf {
            self.0.join("public-server-history.json")
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_file(self.path());
            let _ = fs::remove_dir(self.path());
            let _ = fs::remove_dir(&self.0);
        }
    }

    #[test]
    fn recent_visits_keep_exact_ids_deduplicate_and_survive_reopening() {
        let fixture = Fixture::new();
        let mut history = ServerHistory::open(fixture.path());
        let time = UNIX_EPOCH + Duration::from_secs(1_794_214_464);
        let place = 9_007_199_254_740_993;
        for sequence in 1..=30 {
            history.record(place, Uuid::from_u128(sequence), time);
        }
        assert_eq!(history.visits.len(), MAX_VISITS);
        assert_eq!(history.visits.last().unwrap().server_id, Uuid::from_u128(7));
        history.record(place, Uuid::from_u128(7), time + Duration::from_secs(5));
        let reopened = ServerHistory::open(fixture.path());
        assert_eq!(reopened.visits.len(), MAX_VISITS);
        let recent = &reopened.visits[0];
        assert_eq!(recent.place_id, place.to_string());
        assert_eq!(recent.server_id, Uuid::from_u128(7));
        assert_eq!(recent.observed_at, 1_794_214_469);
        assert!(reopened.view().issue.is_none());
    }

    #[test]
    fn unreadable_history_is_preserved_until_explicitly_cleared() {
        let fixture = Fixture::new();
        let original = br#"{"version":99,"visits":[]}"#;
        fs::write(fixture.path(), original).unwrap();
        let mut history = ServerHistory::open(fixture.path());
        assert_eq!(history.view().issue, Some(UNREADABLE));
        history.record(1818, Uuid::from_u128(1), SystemTime::now());
        assert_eq!(history.visits.len(), 1);
        assert_eq!(fs::read(fixture.path()).unwrap(), original);
        history.clear().unwrap();
        let reopened = ServerHistory::open(fixture.path());
        assert!(reopened.visits.is_empty());
        assert!(reopened.view().issue.is_none());
    }

    #[test]
    fn failed_writes_keep_recent_visits_in_memory_and_clear_is_transactional() {
        let fixture = Fixture::new();
        let mut history = ServerHistory::open(fixture.path());
        fs::create_dir(fixture.path()).unwrap();
        history.record(1818, Uuid::from_u128(1), SystemTime::now());
        assert_eq!(history.visits.len(), 1);
        assert_eq!(history.view().issue, Some(WRITE_FAILED));
        assert!(history.clear().is_err());
        assert_eq!(history.visits.len(), 1);
        fs::remove_dir(fixture.path()).unwrap();
        history.record(1819, Uuid::from_u128(2), SystemTime::now());
        let reopened = ServerHistory::open(fixture.path());
        assert_eq!(reopened.visits.len(), 2);
        assert!(reopened.view().issue.is_none());
    }
}
