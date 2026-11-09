use std::{collections::HashSet, io::ErrorKind, path::PathBuf};

use serde::{Deserialize, Serialize};
use uuid::Uuid;
use zenith_session_launch::{
    private_invite::PrivateInvite,
    target::Target,
    windows::{MAX_PRIVATE_SERVER_BYTES, open_private_servers, seal_private_servers},
};
use zeroize::Zeroizing;

use super::{Library, atomic_write, read_limit};

const MAX_RECORDS: usize = 100;
const UNREADABLE: &str =
    "The protected private-server list is unreadable. Zenith kept the original file.";
const STALE: &str = "This saved private invite was changed or removed. Select it again.";

#[derive(Clone, Copy, Deserialize, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct PrivateServerReference {
    pub id: Uuid,
    pub revision: Uuid,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PrivateServerRow {
    #[serde(flatten)]
    reference: PrivateServerReference,
    name: String,
    place_id: String,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Record {
    reference: PrivateServerReference,
    name: String,
    place: u64,
    invite: PrivateInvite,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct SavedServers {
    version: u8,
    records: Vec<Record>,
}

impl Record {
    fn row(&self) -> PrivateServerRow {
        PrivateServerRow {
            reference: self.reference,
            name: self.name.clone(),
            place_id: self.place.to_string(),
        }
    }
}

impl Library {
    fn private_servers_path(&self) -> PathBuf {
        self.root.join("private-servers.dpapi")
    }

    fn read_private_servers(&self) -> Result<SavedServers, String> {
        let mut bytes = match read_limit(&self.private_servers_path(), MAX_PRIVATE_SERVER_BYTES) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == ErrorKind::NotFound => {
                return Ok(SavedServers {
                    version: 1,
                    records: Vec::new(),
                });
            }
            Err(_) => return Err(UNREADABLE.into()),
        };
        let clear = open_private_servers(&mut bytes).map_err(|_| UNREADABLE)?;
        let store: SavedServers = serde_json::from_slice(&clear).map_err(|_| UNREADABLE)?;
        let mut ids = HashSet::new();
        if store.version != 1
            || store.records.len() > MAX_RECORDS
            || store.records.iter().any(|record| {
                record.reference.id.is_nil()
                    || record.reference.revision.is_nil()
                    || !ids.insert(record.reference.id)
                    || !valid_name(&record.name)
                    || record.invite.code_in_label(&record.name)
                    || !record.invite.valid_for(record.place)
            })
        {
            return Err(UNREADABLE.into());
        }
        Ok(store)
    }

    fn write_private_servers(&self, store: &SavedServers) -> Result<(), String> {
        let mut clear = Zeroizing::new(
            serde_json::to_vec(store).map_err(|_| "Could not encode the private-server list.")?,
        );
        let bytes = seal_private_servers(&mut clear)?;
        atomic_write(&self.private_servers_path(), &bytes)
    }

    pub fn private_servers(&self) -> Result<Vec<PrivateServerRow>, String> {
        Ok(self
            .read_private_servers()?
            .records
            .iter()
            .map(Record::row)
            .collect())
    }

    pub fn save_private_server(
        &self,
        name: String,
        place: u64,
        invite: PrivateInvite,
    ) -> Result<PrivateServerRow, String> {
        let name = checked_name(name, &invite)?;
        if !invite.valid_for(place) {
            return Err("The invite does not match this Roblox place.".into());
        }
        let mut store = self.read_private_servers()?;
        if store.records.len() >= MAX_RECORDS {
            return Err("The private-server list already has 100 invites.".into());
        }
        if store.records.iter().any(|record| record.invite == invite) {
            return Err("This private invite is already saved.".into());
        }
        let record = Record {
            reference: PrivateServerReference {
                id: Uuid::new_v4(),
                revision: Uuid::new_v4(),
            },
            name,
            place,
            invite,
        };
        let row = record.row();
        store.records.push(record);
        self.write_private_servers(&store)?;
        Ok(row)
    }

    pub fn rename_private_server(
        &self,
        reference: PrivateServerReference,
        name: String,
    ) -> Result<PrivateServerRow, String> {
        let mut store = self.read_private_servers()?;
        let record = store
            .records
            .iter_mut()
            .find(|record| record.reference == reference)
            .ok_or(STALE)?;
        record.name = checked_name(name, &record.invite)?;
        record.reference.revision = Uuid::new_v4();
        let row = record.row();
        self.write_private_servers(&store)?;
        Ok(row)
    }

    pub fn remove_private_server(&self, reference: PrivateServerReference) -> Result<(), String> {
        let mut store = self.read_private_servers()?;
        let index = store
            .records
            .iter()
            .position(|record| record.reference == reference)
            .ok_or(STALE)?;
        store.records.remove(index);
        self.write_private_servers(&store)
    }

    pub fn private_target(&self, reference: PrivateServerReference) -> Result<Target, String> {
        let record = self
            .read_private_servers()?
            .records
            .into_iter()
            .find(|record| record.reference == reference)
            .ok_or(STALE)?;
        Ok(Target::private(record.place, record.invite))
    }
}

fn valid_name(name: &str) -> bool {
    !name.trim().is_empty()
        && name.chars().count() <= 80
        && !name.chars().any(char::is_control)
        && !name.contains(['=', '?'])
        && !name.contains("://")
}

fn checked_name(name: String, invite: &PrivateInvite) -> Result<String, String> {
    let name = name.trim();
    if !valid_name(name) || invite.code_in_label(name) {
        return Err(
            "Use a short label for the private server. Put the URL in the invite field.".into(),
        );
    }
    Ok(name.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn private_invites_are_encrypted_and_edits_invalidate_the_old_reference() {
        let root = std::env::temp_dir().join(format!("zenith-private-{}", Uuid::new_v4()));
        let library = Library::open(root.clone()).unwrap();
        let code = "12345678901234567890123456789012";
        let invite = PrivateInvite::parse(&format!(
            "https://roblox.com/games/1818?privateServerLinkCode={code}"
        ))
        .unwrap();
        assert!(
            library
                .save_private_server(format!("Copied {code}"), 1818, invite.clone())
                .is_err()
        );
        let saved = library
            .save_private_server("Fixture".into(), 1818, invite)
            .unwrap();
        let encrypted = fs::read(library.private_servers_path()).unwrap();
        assert!(
            !encrypted
                .windows(code.len())
                .any(|bytes| bytes == code.as_bytes())
        );
        assert!(!serde_json::to_string(&saved).unwrap().contains(code));
        drop(library);
        let library = Library::open(root.clone()).unwrap();
        let renamed = library
            .rename_private_server(saved.reference, "Renamed".into())
            .unwrap();
        assert!(library.private_target(saved.reference).is_err());
        assert!(library.remove_private_server(saved.reference).is_err());
        assert!(
            library
                .private_target(renamed.reference)
                .unwrap()
                .is_private()
        );
        library.remove_private_server(renamed.reference).unwrap();
        assert!(library.private_servers().unwrap().is_empty());
        fs::write(library.private_servers_path(), b"damaged fixture").unwrap();
        assert!(library.private_servers().is_err());
        assert!(
            library
                .rename_private_server(renamed.reference, "No".into())
                .is_err()
        );
        assert_eq!(
            fs::read(library.private_servers_path()).unwrap(),
            b"damaged fixture"
        );
        drop(library);
        fs::remove_dir_all(root).unwrap();
    }
}
