use std::{
    collections::{BTreeMap, HashMap, HashSet},
    io::ErrorKind,
};

use base64::{Engine, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use uuid::Uuid;
use zenith_session_launch::windows::{
    MAX_LEGACY_MIGRATION_BYTES, open_legacy_migration, seal_legacy_metadata,
};
use zeroize::Zeroizing;

use super::{
    Library, MAX_ACCOUNTS, MAX_BUNDLE, SavedIdentity, atomic_write, fs, open_saved_bundle,
    read_limit,
};

const ARCHIVE: &str = "legacy-workspace.zenith-migration";
const PENDING: &str = "legacy-workspace.pending";
const INVALID: &str = "This is not a supported Zenith workspace migration.";

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Migration {
    version: u8,
    workspace: Catalog,
    sign_ins: Vec<SignIn>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct SignIn {
    profile_id: Uuid,
    bundle: String,
}

#[derive(Deserialize, Serialize)]
pub(super) struct Catalog {
    protection: String,
    pub(super) profiles: Vec<LegacyProfile>,
    pub(super) groups: Vec<Value>,
    pub(super) bundles: Vec<Value>,
    pub(super) assignments: Vec<Value>,
    favorites: Vec<LegacyFavorite>,
    preferences: Value,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct LegacyProfile {
    pub(super) id: Uuid,
    label: String,
    username: String,
    groups: Vec<String>,
    priority: u8,
    preferred_place_id: Option<String>,
    legacy_user_id: Option<String>,
    pub(super) verified_user_id: Option<u64>,
    notes: String,
    is_favorite: bool,
    updated_utc: String,
    revision: i64,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct LegacyFavorite {
    id: Uuid,
    name: String,
    place_id: String,
    job_id: Option<Uuid>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LegacyView {
    protection: String,
    profile_count: usize,
    group_count: usize,
    bundle_count: usize,
    assignment_count: usize,
    archived_count: usize,
    archived: Vec<LegacyProfile>,
    linked: BTreeMap<Uuid, LegacyProfile>,
    favorites: Vec<LegacyFavorite>,
    theme: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrationReport {
    profiles: usize,
    added: usize,
    sign_ins_added: usize,
    existing_kept: usize,
}

impl Migration {
    fn open(bytes: &mut [u8]) -> Result<Self, String> {
        let clear = open_legacy_migration(bytes)?;
        let migration: Self = serde_json::from_slice(&clear).map_err(|_| INVALID)?;
        let catalog = &migration.workspace;
        if migration.version != 1
            || !matches!(
                catalog.protection.as_str(),
                "windows" | "password-protected" | "unavailable"
            )
            || catalog.profiles.len() > 10_000
            || catalog.groups.len() > 10_000
            || catalog.assignments.len() > 10_000
            || catalog.favorites.len() > 500
            || migration.sign_ins.len() > catalog.profiles.len()
            || catalog.protection != "windows" && !migration.sign_ins.is_empty()
        {
            return Err(INVALID.into());
        }
        let mut ids = HashSet::new();
        let mut verified_ids = HashSet::new();
        for profile in &catalog.profiles {
            if profile.id.is_nil()
                || !ids.insert(profile.id)
                || profile.label.trim().is_empty()
                || profile.label.len() > 400
                || profile.username.len() > 20
                || !profile
                    .username
                    .bytes()
                    .all(|value| value.is_ascii_alphanumeric() || value == b'_')
                || profile.priority > 100
                || profile.groups.len() > 32
                || profile.notes.len() > 16_000
                || profile.verified_user_id.is_some_and(|id| {
                    id == 0 || profile.username.is_empty() || !verified_ids.insert(id)
                })
                || profile
                    .preferred_place_id
                    .as_deref()
                    .is_some_and(|value| !positive_id(value))
            {
                return Err(INVALID.into());
            }
        }
        if catalog.favorites.iter().any(|favorite| {
            favorite.id.is_nil()
                || !positive_id(&favorite.place_id)
                || favorite.job_id.is_some_and(|id| id.is_nil())
        }) {
            return Err(INVALID.into());
        }
        Ok(migration)
    }

    fn bound_sign_ins(&self) -> Result<HashMap<Uuid, Vec<u8>>, String> {
        let profiles: HashMap<_, _> = self
            .workspace
            .profiles
            .iter()
            .map(|profile| (profile.id, profile))
            .collect();
        let mut checked = HashMap::new();
        let mut seen = HashSet::new();
        for entry in &self.sign_ins {
            let profile = profiles.get(&entry.profile_id).ok_or(INVALID)?;
            if !seen.insert(entry.profile_id)
                || entry.bundle.len() > MAX_BUNDLE.div_ceil(3) * 4
                || profile.verified_user_id.is_none()
            {
                return Err(INVALID.into());
            }
            let mut bundle = STANDARD.decode(&entry.bundle).map_err(|_| INVALID)?;
            // A protected file can expire between staging and importing. Keep the profile and let
            // the normal sign-in flow renew it. An unlocked file for a different identity is invalid.
            if let Ok((identity, _)) = open_saved_bundle(&mut bundle) {
                if identity.profile_id != profile.id
                    || Some(identity.user_id) != profile.verified_user_id
                    || identity.username != profile.username
                {
                    return Err("A protected sign-in belongs to a different profile.".into());
                }
                checked.insert(profile.id, bundle);
            }
        }
        Ok(checked)
    }
}

fn positive_id(value: &str) -> bool {
    !value.is_empty()
        && value.bytes().all(|byte| byte.is_ascii_digit())
        && value.parse::<i64>().is_ok_and(|id| id > 0)
}

impl Library {
    pub fn import_legacy(&mut self, mut bytes: Vec<u8>) -> Result<MigrationReport, String> {
        let archive = self.root.join(ARCHIVE);
        let pending = self.root.join(PENDING);
        if archive
            .try_exists()
            .map_err(|_| "Could not check the existing migration.")?
        {
            return Err("A legacy workspace has already been imported into this library.".into());
        }
        let mut migration = Migration::open(&mut bytes)?;
        let sign_ins = migration.bound_sign_ins()?;
        migration.sign_ins.clear();
        let mut metadata = Zeroizing::new(
            serde_json::to_vec(&migration)
                .map_err(|_| "Could not encode legacy workspace metadata.")?,
        );
        let protected_metadata = seal_legacy_metadata(&mut metadata)?;
        // Retries accept only the same source, without keeping a second credential backup.
        let fingerprint: [u8; 32] = Sha256::digest(&bytes).into();
        match read_limit(&pending, fingerprint.len()) {
            Ok(previous) if previous != fingerprint => {
                return Err(
                    "An interrupted migration is pending. Select the same migration file to retry."
                        .into(),
                );
            }
            Ok(_) => (),
            Err(error) if error.kind() == ErrorKind::NotFound => {
                atomic_write(&pending, &fingerprint)?
            }
            Err(_) => return Err("The pending migration could not be read.".into()),
        }

        let mut proposed = self.accounts.clone();
        let mut report = MigrationReport {
            profiles: migration.workspace.profiles.len(),
            added: 0,
            sign_ins_added: 0,
            existing_kept: 0,
        };
        for profile in migration.workspace.profiles {
            let Some(user_id) = profile.verified_user_id else {
                continue;
            };
            if proposed.values().any(|account| account.user_id == user_id) {
                report.existing_kept += 1;
                continue;
            }
            if proposed.len() == MAX_ACCOUNTS || proposed.contains_key(&profile.id) {
                continue;
            }
            let path = self.bundle_path(profile.id);
            match read_limit(&path, MAX_BUNDLE) {
                Ok(previous) if sign_ins.get(&profile.id) == Some(&previous) => (),
                Ok(_) => continue,
                Err(error) if error.kind() == ErrorKind::NotFound => {
                    if let Some(bundle) = sign_ins.get(&profile.id) {
                        atomic_write(&path, bundle)?;
                    }
                }
                Err(_) => return Err("Could not inspect an existing protected sign-in. Retry the same migration file.".into()),
            }
            report.sign_ins_added += usize::from(sign_ins.contains_key(&profile.id));
            proposed.insert(
                profile.id,
                SavedIdentity {
                    profile_id: profile.id,
                    username: profile.username,
                    user_id,
                },
            );
            report.added += 1;
        }
        self.write_index(&proposed)?;
        self.accounts = proposed;
        atomic_write(&archive, &protected_metadata).map_err(
            |_| "Profiles were saved. Retry the same file to finish archiving its metadata.",
        )?;
        let _ = fs::remove_file(&pending);
        Ok(report)
    }

    pub(super) fn legacy_catalog(&self) -> Result<Option<(Catalog, [u8; 32])>, String> {
        let mut encrypted = match read_limit(&self.root.join(ARCHIVE), MAX_LEGACY_MIGRATION_BYTES) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
            Err(_) => return Err("Could not read the imported legacy workspace.".into()),
        };
        let fingerprint = Sha256::digest(&encrypted).into();
        let migration = Migration::open(&mut encrypted)?;
        if !migration.sign_ins.is_empty() {
            return Err("The installed archive is not a metadata-only workspace.".into());
        }
        Ok(Some((migration.workspace, fingerprint)))
    }

    pub fn legacy_view(&self, offset: usize) -> Result<Option<LegacyView>, String> {
        let Some((catalog, _)) = self.legacy_catalog()? else {
            return Ok(None);
        };
        let mut view = LegacyView {
            protection: catalog.protection,
            profile_count: catalog.profiles.len(),
            group_count: catalog.groups.len(),
            bundle_count: catalog.bundles.len(),
            assignment_count: catalog.assignments.len(),
            archived_count: 0,
            archived: Vec::new(),
            linked: BTreeMap::new(),
            favorites: catalog.favorites,
            theme: catalog
                .preferences
                .get("theme")
                .and_then(Value::as_str)
                .map(str::to_owned),
        };
        let by_user: HashMap<_, _> = self
            .accounts
            .values()
            .map(|account| (account.user_id, account.profile_id))
            .collect();
        for profile in catalog.profiles {
            if let Some(id) = profile.verified_user_id.and_then(|id| by_user.get(&id)) {
                view.linked.insert(*id, profile);
            } else {
                if view.archived_count >= offset && view.archived.len() < 50 {
                    view.archived.push(profile);
                }
                view.archived_count += 1;
            }
        }
        Ok(Some(view))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use zenith_session_launch::auth::Credential;

    struct FixtureRoot(PathBuf);

    impl FixtureRoot {
        fn new() -> Self {
            let root =
                std::env::temp_dir().join(format!("zenith-migration-fixture-{}", Uuid::new_v4()));
            fs::create_dir(&root).unwrap();
            Self(root)
        }
    }

    impl Drop for FixtureRoot {
        fn drop(&mut self) {
            if let Ok(entries) = fs::read_dir(&self.0) {
                for entry in entries.flatten() {
                    let _ = fs::remove_file(entry.path());
                }
            }
            let _ = fs::remove_dir(&self.0);
        }
    }

    #[test]
    fn invalid_protected_migrations_leave_the_library_unchanged() {
        let root = FixtureRoot::new();
        let mut library = Library::open(root.0.clone()).unwrap();
        assert!(
            library
                .import_legacy(b"invalid workspace".to_vec())
                .is_err()
        );
        assert!(library.accounts().is_empty());
        assert!(!root.0.join(PENDING).exists());
        assert!(library.legacy_view(0).unwrap().is_none());
    }

    #[test]
    #[ignore = "Run with two explicitly staged synthetic C# workspace fixtures"]
    fn csharp_workspace_migrates_and_password_protection_is_preserved() {
        let clear_fixture =
            fs::read(std::env::var_os("ZENITH_MIGRATION_FIXTURE").unwrap()).unwrap();
        let password_fixture =
            fs::read(std::env::var_os("ZENITH_MIGRATION_PASSWORD_FIXTURE").unwrap()).unwrap();

        let fresh_root = FixtureRoot::new();
        // A previous attempt left a pending fingerprint. Opening the app does not replay it.
        fs::write(fresh_root.0.join(PENDING), Sha256::digest(&clear_fixture)).unwrap();
        let mut fresh = Library::open(fresh_root.0.clone()).unwrap();
        assert!(fresh.accounts().is_empty());
        assert!(fresh.import_legacy(password_fixture.clone()).is_err());
        let report = fresh.import_legacy(clear_fixture.clone()).unwrap();
        assert_eq!(
            (report.profiles, report.added, report.sign_ins_added),
            (3, 2, 1)
        );
        let id = fresh.by_user_id(4242).unwrap().profile_id;
        let expired = fresh.by_user_id(4243).unwrap().profile_id;
        assert!(fresh.credential(id).is_ok());
        assert!(fresh.credential(expired).is_err());
        drop(fresh);
        let mut fresh = Library::open(fresh_root.0.clone()).unwrap();
        let view = fresh.legacy_view(0).unwrap().unwrap();
        let mut archived = fs::read(fresh_root.0.join(ARCHIVE)).unwrap();
        assert!(Migration::open(&mut archived).unwrap().sign_ins.is_empty());
        assert!(!fresh_root.0.join(PENDING).exists());
        assert_eq!(view.linked[&id].notes, "Synthetic migration note");
        assert_eq!(view.linked[&id].groups, ["Testing", "Archive"]);
        assert_eq!(view.linked[&id].preferred_place_id.as_deref(), Some("1818"));
        assert_eq!(view.linked[&expired].notes, "Keep this expired profile");
        assert_eq!(view.archived_count, 1);
        assert_eq!(view.favorites[0].name, "Fixture game");
        assert_eq!(view.assignment_count, 1);
        let assignment_view = serde_json::to_value(fresh.assignments(0).unwrap()).unwrap();
        assert!(
            assignment_view["rows"]
                .as_array()
                .unwrap()
                .iter()
                .any(|row| row["editable"] == true
                    && row["target"]["id"] == id.to_string()
                    && row["assignment"].is_object())
        );
        fresh.remove(id).unwrap();
        assert!(fresh.import_legacy(clear_fixture.clone()).is_err());
        assert!(fresh.by_user_id(4242).is_none());

        let existing_root = FixtureRoot::new();
        let mut existing = Library::open(existing_root.0.clone()).unwrap();
        let credential =
            Credential::new(Zeroizing::new("synthetic-existing-sign-in".into())).unwrap();
        let saved = existing
            .remember_sign_in(4242, "ZenithFixture".into(), credential, 4_102_444_800)
            .unwrap();
        let path = existing.bundle_path(saved.profile_id);
        let before = fs::read(&path).unwrap();
        let report = existing.import_legacy(clear_fixture).unwrap();
        assert_eq!((report.existing_kept, report.sign_ins_added), (1, 0));
        assert_eq!(fs::read(path).unwrap(), before);
        assert_eq!(
            existing.legacy_view(0).unwrap().unwrap().linked[&saved.profile_id].label,
            "ZenithFixture"
        );

        let protected_root = FixtureRoot::new();
        let mut protected = Library::open(protected_root.0.clone()).unwrap();
        let report = protected.import_legacy(password_fixture).unwrap();
        assert_eq!(
            (report.profiles, report.added, report.sign_ins_added),
            (3, 0, 0)
        );
        let view = protected.legacy_view(0).unwrap().unwrap();
        assert_eq!(view.protection, "password-protected");
        assert_eq!(view.archived_count, 3);
        assert_eq!(view.assignment_count, 1);
        let assignment_view = serde_json::to_value(protected.assignments(0).unwrap()).unwrap();
        assert!(
            assignment_view["rows"]
                .as_array()
                .unwrap()
                .iter()
                .any(|row| row["editable"] == false && row["assignment"].is_object())
        );
    }
}
