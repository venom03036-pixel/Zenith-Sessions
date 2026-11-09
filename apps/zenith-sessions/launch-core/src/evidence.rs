use std::{fs, io::Read, path::Path, time::SystemTime};

use serde::Serialize;
use uuid::Uuid;
use zeroize::Zeroizing;

const LOG_LIMIT: u64 = 2 * 1024 * 1024;
const MAX_FILES: usize = 128;

#[derive(Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Observed {
    pub user_id: Option<u64>,
    pub place_id: Option<u64>,
    pub job_id: Option<Uuid>,
    pub connected: bool,
}

impl Observed {
    pub fn for_attempt(directory: &Path, tracker: u64, started_at: SystemTime) -> Option<Self> {
        let candidates = fs::read_dir(directory)
            .ok()?
            .filter_map(|entry| {
                let entry = entry.ok()?;
                if !entry.file_type().ok()?.is_file() || entry.path().extension()? != "log" {
                    return None;
                }
                let metadata = entry.metadata().ok()?;
                if metadata.created().ok()? < started_at {
                    return None;
                }
                Some(entry.path())
            })
            .take(MAX_FILES + 1)
            .collect::<Vec<_>>();
        if candidates.len() > MAX_FILES {
            return None;
        }
        let mut matched = None;
        for path in candidates {
            let mut bytes = Zeroizing::new(Vec::new());
            let Ok(file) = fs::File::open(path) else {
                continue;
            };
            if file.take(LOG_LIMIT).read_to_end(&mut bytes).is_err() {
                continue;
            }
            let body = match std::str::from_utf8(&bytes) {
                Ok(text) => text,
                Err(error) => std::str::from_utf8(&bytes[..error.valid_up_to()]).ok()?,
            };
            if let Some(facts) = parse(body, tracker) {
                if matched.is_some() {
                    return None;
                }
                matched = Some(facts);
            }
        }
        matched
    }
}

fn parse(log: &str, tracker: u64) -> Option<Observed> {
    let mut matched = false;
    let mut facts = Observed::default();
    for line in log
        .split_inclusive('\n')
        .filter(|line| line.ends_with('\n'))
    {
        let Some((_, tagged)) = line.split_once(" [") else {
            continue;
        };
        let Some((tag, body)) = tagged.split_once("] ") else {
            continue;
        };
        if tag == "FLog::LogWin32BTId" {
            if let Some(id) = number_after(body, "websiteBTId is ")
                .or_else(|| number_after(body, "BTID is overriden to "))
            {
                if id != tracker {
                    return None;
                }
                matched = true;
            }
        } else if !matched {
            continue;
        } else if tag == "FLog::Output"
            && let Some(join) = body.strip_prefix("! Joining game '")
            && let Some((job, place)) = join.split_once("' place ")
        {
            // This probe reports the initial arrival. A later teleport starts a new join.
            if facts.job_id.is_some() {
                break;
            }
            facts.job_id = Uuid::parse_str(job).ok().filter(|value| !value.is_nil());
            facts.place_id = leading_number(place);
        } else if tag == "FLog::GameJoinLoadTime" && body.starts_with("Report game_join_loadtime:")
        {
            if let (Some(place), Some(reported)) = (facts.place_id, number_after(body, "placeid:"))
                && place != reported
            {
                return None;
            }
            if let Some(user) = number_after(body, "userid:") {
                if facts.user_id.is_some_and(|previous| previous != user) {
                    return None;
                }
                facts.user_id = Some(user);
            }
        } else if tag == "FLog::Network"
            && facts.job_id.is_some()
            && (body.starts_with("Replicator created") || body.starts_with("serverId:"))
        {
            facts.connected = true;
        }
    }
    matched.then_some(facts)
}

fn number_after(line: &str, marker: &str) -> Option<u64> {
    let (_, rest) = line.split_once(marker)?;
    leading_number(rest)
}

fn leading_number(rest: &str) -> Option<u64> {
    let end = rest
        .find(|c: char| !c.is_ascii_digit())
        .unwrap_or(rest.len());
    rest[..end].parse::<u64>().ok().filter(|id| *id > 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    const LOG: &str = concat!(
        "2026-10-19T09:08:03.437Z,0.437181,0a50,6,Warning [FLog::LogWin32BTId] LogWin32BTId, App, web launch, websiteBTId is 12345678\n",
        "2026-10-19T09:08:04.514Z,1.514491,1434,6 [FLog::Output] ! Joining game 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' place 1818 at 192.0.2.1\n",
        "2026-10-19T09:08:04.514Z,1.514491,1434,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:1818, userid:42,\n",
        "2026-10-19T09:08:04.514Z,1.514491,1434,6 [FLog::Network] Replicator created\n",
    );

    #[test]
    fn tracker_and_native_identity_and_arrival_are_separate_facts() {
        assert!(parse(LOG, 12345679).is_none());
        let facts = parse(LOG, 12345678).unwrap();
        assert_eq!(facts.user_id, Some(42));
        assert_eq!(facts.place_id, Some(1818));
        assert!(facts.connected);
    }

    #[test]
    fn output_lines_cannot_masquerade_as_account_observations() {
        let spoof = LOG.replace(
            "[FLog::GameJoinLoadTime]",
            "[FLog::Output] [FLog::GameJoinLoadTime]",
        );
        assert_eq!(parse(&spoof, 12345678).unwrap().user_id, None);
        assert!(parse(&LOG.replace("placeid:1818", "placeid:999"), 12345678).is_none());
    }

    #[test]
    fn later_teleport_cannot_erase_first_arrival() {
        let log = format!(
            "{LOG}2026-10-19T09:10:04.514Z,121.5,1434,6 [FLog::Output] ! Joining game 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' place 1919 at 192.0.2.2\n2026-10-19T09:10:04.514Z,121.5,1434,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:1919, userid:42,\n"
        );
        let first_arrival =
            parse(&log, 12345678).expect("first completed join must survive later travel");
        assert_eq!(first_arrival.place_id, Some(1818));
        assert!(first_arrival.connected);
    }
}
