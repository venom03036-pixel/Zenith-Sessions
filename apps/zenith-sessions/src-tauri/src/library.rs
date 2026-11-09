use std::{
    collections::{BTreeMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    os::windows::fs::OpenOptionsExt,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

use uuid::Uuid;
use zenith_session_launch::{
    auth::Credential,
    windows::{SavedIdentity, open_saved_bundle, seal_saved_bundle},
};

mod assignments;
mod migration;
mod private_servers;
pub use assignments::{AssignmentRecipient, AssignmentTarget, AssignmentView};
pub use migration::{LegacyView, MigrationReport};
pub use private_servers::{PrivateServerReference, PrivateServerRow};

const MAX_BUNDLE: usize = 128 * 1024;
const MAX_ACCOUNTS: usize = 128;

pub struct Library {
    root: PathBuf,
    accounts: BTreeMap<Uuid, SavedIdentity>,
    credential_uses: Arc<Mutex<HashSet<Uuid>>>,
    _lease: File,
}

pub struct SavedCredential {
    pub identity: SavedIdentity,
    pub credential: Credential,
    original: Vec<u8>,
    _claim: CredentialClaim,
}

struct CredentialClaim {
    id: Uuid,
    uses: Arc<Mutex<HashSet<Uuid>>>,
}

impl Drop for CredentialClaim {
    fn drop(&mut self) {
        self.uses
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&self.id);
    }
}

impl Library {
    pub fn open(root: PathBuf) -> Result<Self, String> {
        fs::create_dir_all(&root).map_err(|_| "Could not create Zenith's local library")?;
        let lease = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .share_mode(0)
            .open(root.join("library.lock"))
            .map_err(|_| "This Zenith library is already open or inaccessible")?;
        let index = root.join("accounts.json");
        let records: Vec<SavedIdentity> = match read_limit(&index, MAX_BUNDLE) {
            Ok(bytes) => {
                serde_json::from_slice(&bytes).map_err(|_| "The account index is unreadable")?
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Vec::new(),
            Err(_) => return Err("Could not read the account index".into()),
        };
        let mut accounts = BTreeMap::new();
        if records.len() > MAX_ACCOUNTS {
            return Err("The account index exceeds the supported size".into());
        }
        for record in records {
            if record.profile_id.is_nil()
                || record.user_id == 0
                || record.username.is_empty()
                || accounts
                    .values()
                    .any(|item: &SavedIdentity| item.user_id == record.user_id)
                || accounts.insert(record.profile_id, record).is_some()
            {
                return Err("The account index contains conflicting identities".into());
            }
        }
        Ok(Self {
            root,
            accounts,
            credential_uses: Arc::default(),
            _lease: lease,
        })
    }

    pub fn accounts(&self) -> Vec<SavedIdentity> {
        self.accounts.values().cloned().collect()
    }

    pub fn account(&self, id: Uuid) -> Result<SavedIdentity, String> {
        self.accounts
            .get(&id)
            .cloned()
            .ok_or_else(|| "Select an account from the library".into())
    }

    pub fn import(&mut self, mut encrypted: Vec<u8>) -> Result<SavedIdentity, String> {
        let (identity, credential) = open_saved_bundle(&mut encrypted)?;
        drop(credential);
        if let Some(existing) = self.accounts.get(&identity.profile_id) {
            if *existing != identity {
                return Err("The imported file belongs to a different identity".into());
            }
            return Err(
                "This account is already imported. Remove it before importing a renewed sign-in."
                    .into(),
            );
        }
        if self
            .accounts
            .values()
            .any(|existing| existing.user_id == identity.user_id)
        {
            return Err("This Roblox identity already has a saved profile".into());
        }
        self.insert(identity, &encrypted)
    }

    pub fn by_user_id(&self, user_id: u64) -> Option<SavedIdentity> {
        self.accounts
            .values()
            .find(|value| value.user_id == user_id)
            .cloned()
    }

    pub fn remember_sign_in(
        &mut self,
        user_id: u64,
        username: String,
        credential: Credential,
        expires: u64,
    ) -> Result<SavedIdentity, String> {
        let previous = self.by_user_id(user_id);
        let identity = previous.clone().unwrap_or_else(|| SavedIdentity {
            profile_id: Uuid::new_v4(),
            user_id,
            username,
        });
        let encrypted = seal_saved_bundle(&identity, &credential, expires)?;
        if previous.is_some() {
            atomic_write(&self.bundle_path(identity.profile_id), &encrypted)?;
            return Ok(identity);
        }
        self.insert(identity, &encrypted)
    }

    fn insert(
        &mut self,
        identity: SavedIdentity,
        encrypted: &[u8],
    ) -> Result<SavedIdentity, String> {
        if self.accounts.len() >= MAX_ACCOUNTS {
            return Err("The library already has 128 accounts".into());
        }

        let file = self.bundle_path(identity.profile_id);
        atomic_write(&file, encrypted)?;
        self.accounts.insert(identity.profile_id, identity.clone());
        if let Err(error) = self.save_index() {
            self.accounts.remove(&identity.profile_id);
            let _ = fs::remove_file(file);
            return Err(error);
        }
        Ok(identity)
    }

    pub fn credential(&self, id: Uuid) -> Result<SavedCredential, String> {
        let expected = self
            .accounts
            .get(&id)
            .ok_or("Select an account from the library")?;
        if !self
            .credential_uses
            .lock()
            .map_err(|_| "Could not claim this saved sign-in")?
            .insert(id)
        {
            return Err("Another operation is checking this account. Retry shortly.".into());
        }
        let claim = CredentialClaim {
            id,
            uses: Arc::clone(&self.credential_uses),
        };
        let mut bytes = read_limit(&self.bundle_path(id), MAX_BUNDLE)
            .map_err(|_| "The saved sign-in needs renewal")?;
        let (actual, credential) = open_saved_bundle(&mut bytes)?;
        if &actual != expected {
            return Err("The saved sign-in does not match this profile".into());
        }
        Ok(SavedCredential {
            identity: actual,
            credential,
            original: bytes,
            _claim: claim,
        })
    }

    pub fn finish_credential(&self, saved: &SavedCredential) -> Result<(), String> {
        let id = saved.identity.profile_id;
        let path = self.bundle_path(id);
        if self.accounts.get(&id) != Some(&saved.identity)
            || !read_limit(&path, MAX_BUNDLE).is_ok_and(|current| current == saved.original)
        {
            return Err("The saved sign-in changed while Roblox was responding. Retry with the current account.".into());
        }
        if saved.credential.has_verified_update(saved.identity.user_id) {
            let expires = saved
                .credential
                .expires_at()
                .ok_or("The saved sign-in expiry is missing")?;
            let encrypted = seal_saved_bundle(&saved.identity, &saved.credential, expires)?;
            atomic_write(&path, &encrypted)?;
        }
        Ok(())
    }

    pub fn remove(&mut self, id: Uuid) -> Result<(), String> {
        let identity = self
            .accounts
            .get(&id)
            .cloned()
            .ok_or("Account is not saved")?;
        match fs::remove_file(self.bundle_path(id)) {
            Ok(()) => (),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
            Err(_) => return Err("Could not remove the saved sign-in".into()),
        }
        self.accounts.remove(&id);
        if let Err(error) = self.save_index() {
            self.accounts.insert(id, identity);
            return Err(format!(
                "Sign-in removed. The profile could not be removed: {error}"
            ));
        }
        Ok(())
    }

    fn save_index(&self) -> Result<(), String> {
        self.write_index(&self.accounts)
    }

    fn write_index(&self, accounts: &BTreeMap<Uuid, SavedIdentity>) -> Result<(), String> {
        let json = serde_json::to_vec(&accounts.values().collect::<Vec<_>>())
            .map_err(|_| "Could not encode the account index")?;
        atomic_write(&self.root.join("accounts.json"), &json)
    }

    fn bundle_path(&self, id: Uuid) -> PathBuf {
        self.root.join(format!("{id}.dpapi"))
    }
}

pub(crate) fn read_limit(path: &Path, max: usize) -> std::io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    File::open(path)?
        .take(max as u64 + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() > max {
        return Err(std::io::ErrorKind::InvalidData.into());
    }
    Ok(bytes)
}

pub(crate) fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let temporary = path.with_extension(format!("{}.tmp", Uuid::new_v4()));
    let result = (|| {
        let mut file = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result.map_err(|_| "Could not save the protected local library".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn claim_is_per_account_and_late_reads_cannot_overwrite_renewal_or_removal() {
        use zeroize::Zeroizing;
        let root = std::env::temp_dir().join(format!("zenith-library-fixture-{}", Uuid::new_v4()));
        let mut library = Library::open(root.clone()).unwrap();
        let fixture = |value: &str| Credential::new(Zeroizing::new(value.into())).unwrap();
        let first = library
            .remember_sign_in(42, "FixtureOne".into(), fixture("initial"), 4_102_444_800)
            .unwrap();
        let other = library
            .remember_sign_in(43, "FixtureTwo".into(), fixture("other"), 4_102_444_800)
            .unwrap();

        let mut active = library.credential(first.profile_id).unwrap();
        assert!(library.credential(first.profile_id).is_err());
        assert!(library.credential(other.profile_id).is_ok());
        active.credential = fixture("unverified");
        library.finish_credential(&active).unwrap();
        assert!(
            read_limit(&library.bundle_path(first.profile_id), MAX_BUNDLE).unwrap()
                == active.original
        );
        library
            .remember_sign_in(
                42,
                "FixtureOne".into(),
                fixture("explicit-renewal"),
                4_102_444_800,
            )
            .unwrap();
        let renewed = read_limit(&library.bundle_path(first.profile_id), MAX_BUNDLE).unwrap();
        assert_ne!(renewed, active.original);
        assert!(library.finish_credential(&active).is_err());
        drop(active);
        let active = library.credential(first.profile_id).unwrap();
        assert!(active.original == renewed);
        library.remove(first.profile_id).unwrap();
        assert!(library.finish_credential(&active).is_err());
        assert!(!library.bundle_path(first.profile_id).exists());
        drop(active);
        library.remove(other.profile_id).unwrap();
        drop(library);
        fs::remove_file(root.join("accounts.json")).unwrap();
        fs::remove_file(root.join("library.lock")).unwrap();
        fs::remove_dir(root).unwrap();
    }

    #[test]
    fn a_new_first_party_sign_in_and_renewal_keep_one_profile() {
        use zeroize::Zeroizing;
        let root = std::env::temp_dir().join(format!("zenith-library-fixture-{}", Uuid::new_v4()));
        let mut library = Library::open(root.clone()).unwrap();
        let expires = 4_102_444_800_u64;
        let first = Credential::new(Zeroizing::new("local-test-first".into())).unwrap();
        let first = library
            .remember_sign_in(4242, "ZenithFixture".into(), first, expires)
            .unwrap();
        let initial_cipher = fs::read(library.bundle_path(first.profile_id)).unwrap();
        let renewal = Credential::new(Zeroizing::new("local-test-renewal".into())).unwrap();
        let renewed = library
            .remember_sign_in(4242, "ZenithFixture".into(), renewal, expires)
            .unwrap();
        let replaced = fs::read(library.bundle_path(first.profile_id)).unwrap() != initial_cipher;
        drop(library);
        let mut reopened = Library::open(root.clone()).unwrap();
        let retained =
            reopened.accounts().len() == 1 && reopened.credential(first.profile_id).is_ok();
        reopened.remove(first.profile_id).unwrap();
        drop(reopened);
        fs::remove_file(root.join("accounts.json")).unwrap();
        fs::remove_file(root.join("library.lock")).unwrap();
        fs::remove_dir(root).unwrap();
        assert!(first == renewed && replaced && retained);
    }

    #[test]
    fn a_damaged_sign_in_does_not_remove_its_saved_profile() {
        let root = std::env::temp_dir().join(format!("zenith-library-fixture-{}", Uuid::new_v4()));
        let identity = SavedIdentity {
            profile_id: Uuid::new_v4(),
            username: "ZenithFixture".into(),
            user_id: 4242,
        };
        let library = Library::open(root.clone()).unwrap();
        let bundle = library.bundle_path(identity.profile_id);
        drop(library);
        fs::write(&bundle, b"invalid protected record").unwrap();
        fs::write(
            root.join("accounts.json"),
            serde_json::to_vec(&[&identity]).unwrap(),
        )
        .unwrap();
        let library = Library::open(root.clone()).unwrap();
        let retained = library.accounts().contains(&identity);
        let credential_refused = library.credential(identity.profile_id).is_err();
        drop(library);
        for file in [
            bundle,
            root.join("accounts.json"),
            root.join("library.lock"),
        ] {
            fs::remove_file(file).unwrap();
        }
        fs::remove_dir(root).unwrap();
        assert!(retained);
        assert!(credential_refused);
    }

    #[test]
    fn protected_import_survives_reopen_and_is_bound_to_the_saved_identity() {
        use windows::{
            Win32::{
                Foundation::{HLOCAL, LocalFree},
                Security::Cryptography::{
                    CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData,
                },
            },
            core::PCWSTR,
        };
        let root = std::env::temp_dir().join(format!("zenith-library-fixture-{}", Uuid::new_v4()));
        let id = Uuid::new_v4();
        let mut clear = serde_json::to_vec(&serde_json::json!({
            "version": 1, "profileId": id, "username": "ZenithFixture", "userId": 4242,
            "generation": 1, "expiresUnixSeconds": 4_102_444_800_u64, "cookie": "local-test-credential"
        })).unwrap();
        let mut entropy = b"ZenithSessions/Rust/LaunchProbe/v1".to_vec();
        let input = CRYPT_INTEGER_BLOB {
            cbData: clear.len() as u32,
            pbData: clear.as_mut_ptr(),
        };
        let entropy = CRYPT_INTEGER_BLOB {
            cbData: entropy.len() as u32,
            pbData: entropy.as_mut_ptr(),
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        // SAFETY: input and entropy are initialized and writable. Copy and release the DPAPI-owned output.
        let protected = unsafe {
            CryptProtectData(
                &input,
                PCWSTR::null(),
                Some(&entropy),
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
            .unwrap();
            let protected =
                std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
            LocalFree(Some(HLOCAL(output.pbData.cast())));
            protected
        };
        let mut library = Library::open(root.clone()).unwrap();
        let saved = library.import(protected.clone()).unwrap();
        let refused_duplicate = library.import(protected).is_err();
        drop(library);
        let mut library = Library::open(root.clone()).unwrap();
        let reopened = library.credential(id).unwrap().identity;
        library.remove(id).unwrap();
        let removed = library.accounts().is_empty() && library.credential(id).is_err();
        drop(library);
        fs::remove_file(root.join("accounts.json")).unwrap();
        fs::remove_file(root.join("library.lock")).unwrap();
        fs::remove_dir(root).unwrap();
        assert!(saved == reopened && saved.profile_id == id && saved.user_id == 4242);
        assert!(refused_duplicate);
        assert!(removed);
    }
}
