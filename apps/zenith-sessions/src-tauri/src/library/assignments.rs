use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    io::ErrorKind,
};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;
use windows::Win32::Globalization::{CSTR_EQUAL, CompareStringOrdinal};
use zenith_session_launch::windows::{
    MAX_ASSIGNMENT_BYTES, SavedIdentity, open_assignment_metadata, seal_assignment_metadata,
};
use zeroize::Zeroizing;

use super::{Library, atomic_write, migration::LegacyProfile, read_limit};
use crate::macro_catalog::VerifiedMacro;

const FILE: &str = "macro-assignments.dpapi";
const MAX_CHANGES: usize = 10_000;
const PAGE: usize = 25;
const MAX_RECIPIENTS: usize = 32;
const STALE: &str = "Assignments or targets changed. Refresh before editing again.";
const DAMAGED: &str = "The saved assignment file is unreadable. The existing file was preserved.";

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum TargetKind {
    Account,
    Group,
    Bundle,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AssignmentTarget {
    kind: TargetKind,
    id: Uuid,
}

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MacroRef {
    id: Uuid,
    name: String,
    saved_revision: Option<Uuid>,
}

impl MacroRef {
    fn valid(&self) -> bool {
        !self.id.is_nil() && !self.saved_revision.is_some_and(|id| id.is_nil()) && label(&self.name)
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LegacyAssignment {
    target: LegacyTarget,
    target_name: String,
    macro_id: Uuid,
    macro_name: String,
}

#[derive(Deserialize)]
struct LegacyTarget {
    kind: u8,
    id: Uuid,
}

#[derive(Deserialize)]
struct NamedTarget {
    id: Uuid,
    name: String,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Change {
    target: AssignmentTarget,
    #[serde(deserialize_with = "Option::deserialize")]
    assignment: Option<MacroRef>,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Disk {
    version: u8,
    changes: Vec<Change>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    target: AssignmentTarget,
    name: String,
    editable: bool,
    assignment: Option<MacroRef>,
    inherited: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AssignmentView {
    stamp: String,
    total: usize,
    offset: usize,
    previous: Option<usize>,
    next: Option<usize>,
    unreadable_legacy: usize,
    rows: Vec<Row>,
}

pub struct AssignmentRecipients {
    pub target_name: String,
    pub members: Vec<AssignmentRecipient>,
}

pub enum AssignmentRecipient {
    Saved(SavedIdentity),
    Unlinked(String),
}

struct Context {
    stamp: String,
    source: Sha256,
    targets: BTreeMap<AssignmentTarget, String>,
    profiles: Vec<LegacyProfile>,
    bundles: HashMap<Uuid, Vec<Uuid>>,
    inherited: BTreeMap<AssignmentTarget, MacroRef>,
    archived: Vec<Row>,
    changes: BTreeMap<AssignmentTarget, Option<MacroRef>>,
    unreadable_legacy: usize,
}

fn label(text: &str) -> bool {
    !text.trim().is_empty()
        && text.encode_utf16().count() <= 200
        && !text
            .chars()
            .any(|c| c.is_control() || matches!(c, '\u{2028}' | '\u{2029}'))
}

fn revision(source: &Sha256, ciphertext: &[u8]) -> String {
    let mut stamp = source.clone();
    stamp.update(Sha256::digest(ciphertext));
    format!("{:x}", stamp.finalize())
}

impl Context {
    fn assigned(&self, target: AssignmentTarget) -> Option<&MacroRef> {
        self.changes
            .get(&target)
            .map_or_else(|| self.inherited.get(&target), Option::as_ref)
    }

    fn legacy_members(
        &self,
        target: AssignmentTarget,
        name: &str,
    ) -> Result<Vec<Option<&LegacyProfile>>, String> {
        let members = match target.kind {
            TargetKind::Group => {
                let name: Vec<u16> = name.encode_utf16().collect();
                if self.targets.iter().any(|(other, label)| {
                    other.kind == TargetKind::Group && *other != target && same_group(&name, label)
                }) {
                    return Err("Multiple imported groups have this name.".into());
                }
                self.profiles
                    .iter()
                    .filter(|profile| profile.groups.iter().any(|group| same_group(&name, group)))
                    .take(MAX_RECIPIENTS + 1)
                    .map(Some)
                    .collect::<Vec<_>>()
            }
            TargetKind::Bundle => {
                let ids = self
                    .bundles
                    .get(&target.id)
                    .ok_or("Imported bundle membership is unavailable.")?;
                if ids.len() > MAX_RECIPIENTS {
                    return Err("Review supports up to 32 recipients.".into());
                }
                let mut seen = BTreeSet::new();
                if ids.iter().any(|id| id.is_nil() || !seen.insert(*id)) {
                    return Err("Imported bundle membership is invalid.".into());
                }
                ids.iter()
                    .map(|id| self.profiles.iter().find(|profile| profile.id == *id))
                    .collect()
            }
            TargetKind::Account => {
                return Err("This target is not an imported group or bundle.".into());
            }
        };
        if members.len() > MAX_RECIPIENTS {
            return Err("Review supports up to 32 recipients.".into());
        }
        Ok(members)
    }

    fn view(self, offset: usize) -> AssignmentView {
        let mut rows = self.archived;
        for (target, name) in self.targets {
            let change = self.changes.get(&target);
            let assignment =
                change.map_or_else(|| self.inherited.get(&target).cloned(), Clone::clone);
            rows.push(Row {
                target,
                name,
                editable: true,
                inherited: change.is_none() && assignment.is_some(),
                assignment,
            });
        }
        rows.sort_by(|a, b| {
            (!a.editable, a.target.kind, &a.name, a.target.id).cmp(&(
                !b.editable,
                b.target.kind,
                &b.name,
                b.target.id,
            ))
        });
        let total = rows.len();
        let offset = offset.min(total.saturating_sub(1) / PAGE * PAGE);
        AssignmentView {
            stamp: self.stamp,
            total,
            offset,
            previous: offset.checked_sub(PAGE),
            next: (offset + PAGE < total).then_some(offset + PAGE),
            unreadable_legacy: self.unreadable_legacy,
            rows: rows.into_iter().skip(offset).take(PAGE).collect(),
        }
    }
}

fn same_group(name: &[u16], candidate: &str) -> bool {
    let candidate: Vec<_> = candidate.encode_utf16().take(name.len() + 1).collect();
    // SAFETY: both slices are live, bounded UTF-16 buffers passed with their lengths.
    name.len() == candidate.len()
        && unsafe { CompareStringOrdinal(name, &candidate, true) == CSTR_EQUAL }
}

impl Library {
    fn assignment_context(&self) -> Result<Context, String> {
        let encoded = match read_limit(&self.root.join(FILE), MAX_ASSIGNMENT_BYTES) {
            Ok(bytes) => Some(bytes),
            Err(error) if error.kind() == ErrorKind::NotFound => None,
            Err(_) => return Err(DAMAGED.into()),
        };
        let overlay_hash = Sha256::digest(encoded.as_deref().unwrap_or_default());
        let mut changes = BTreeMap::new();
        if let Some(mut encoded) = encoded {
            let clear = open_assignment_metadata(&mut encoded).map_err(|_| DAMAGED)?;
            let disk: Disk = serde_json::from_slice(&clear).map_err(|_| DAMAGED)?;
            if disk.version != 1 || disk.changes.len() > MAX_CHANGES {
                return Err(DAMAGED.into());
            }
            for change in disk.changes {
                if change.target.id.is_nil()
                    || change
                        .assignment
                        .as_ref()
                        .is_some_and(|item| !item.valid() || item.saved_revision.is_none())
                    || changes.insert(change.target, change.assignment).is_some()
                {
                    return Err(DAMAGED.into());
                }
            }
        }
        let mut source = Sha256::new();
        source.update(Sha256::digest(
            serde_json::to_vec(&self.accounts).map_err(|_| DAMAGED)?,
        ));
        let mut targets: BTreeMap<_, _> = self
            .accounts
            .values()
            .map(|account| {
                (
                    AssignmentTarget {
                        kind: TargetKind::Account,
                        id: account.profile_id,
                    },
                    account.username.clone(),
                )
            })
            .collect();
        let mut inherited = BTreeMap::new();
        let mut profiles = Vec::new();
        let mut bundles = HashMap::new();
        let mut archived = Vec::new();
        let mut unreadable_legacy = 0;
        if let Some((archive, fingerprint)) = self.legacy_catalog()? {
            source.update(fingerprint);
            for (kind, entries) in [
                (TargetKind::Group, archive.groups),
                (TargetKind::Bundle, archive.bundles),
            ] {
                for entry in entries {
                    let Ok(target) = NamedTarget::deserialize(&entry) else {
                        continue;
                    };
                    if !target.id.is_nil() && label(&target.name) {
                        let key = AssignmentTarget {
                            kind,
                            id: target.id,
                        };
                        if targets.insert(key, target.name).is_some() {
                            return Err("Imported assignment targets conflict.".into());
                        }
                        if kind == TargetKind::Bundle
                            && let Some(value) = entry.get("profileIds")
                            && let Ok(ids) = Vec::<Uuid>::deserialize(value)
                        {
                            bundles.insert(key.id, ids);
                        }
                    }
                }
            }
            let active: HashMap<_, _> = self
                .accounts
                .values()
                .map(|account| (account.user_id, account.profile_id))
                .collect();
            profiles = archive.profiles;
            let by_legacy: HashMap<_, _> = profiles
                .iter()
                .filter_map(|profile| Some((profile.id, *active.get(&profile.verified_user_id?)?)))
                .collect();
            let mut legacy_keys = BTreeSet::new();
            for value in archive.assignments {
                let Ok(record) = serde_json::from_value::<LegacyAssignment>(value) else {
                    unreadable_legacy += 1;
                    continue;
                };
                let kind = match record.target.kind {
                    1 => TargetKind::Account,
                    2 => TargetKind::Group,
                    3 => TargetKind::Bundle,
                    _ => {
                        unreadable_legacy += 1;
                        continue;
                    }
                };
                let original = AssignmentTarget {
                    kind,
                    id: record.target.id,
                };
                let assignment = MacroRef {
                    id: record.macro_id,
                    name: record.macro_name,
                    saved_revision: None,
                };
                if original.id.is_nil() || !assignment.valid() || !label(&record.target_name) {
                    unreadable_legacy += 1;
                    continue;
                }
                if !legacy_keys.insert(original) {
                    return Err("Imported assignment references conflict.".into());
                }
                let linked = match kind {
                    TargetKind::Account => by_legacy
                        .get(&original.id)
                        .map(|id| AssignmentTarget { kind, id: *id }),
                    _ => targets.contains_key(&original).then_some(original),
                };
                if let Some(target) = linked {
                    if inherited.insert(target, assignment).is_some() {
                        return Err("Imported assignment references conflict.".into());
                    }
                } else {
                    archived.push(Row {
                        target: original,
                        name: record.target_name,
                        editable: false,
                        assignment: Some(assignment),
                        inherited: true,
                    });
                }
            }
        }
        for (&target, assignment) in &changes {
            if !targets.contains_key(&target)
                && let Some(assignment) = assignment
            {
                archived.push(Row {
                    target,
                    name: format!("Unavailable target {}", target.id),
                    editable: false,
                    assignment: Some(assignment.clone()),
                    inherited: false,
                });
            }
        }
        let mut stamp = source.clone();
        stamp.update(overlay_hash);
        Ok(Context {
            stamp: format!("{:x}", stamp.finalize()),
            source,
            targets,
            profiles,
            bundles,
            inherited,
            archived,
            changes,
            unreadable_legacy,
        })
    }

    pub fn assignments(&self, offset: usize) -> Result<AssignmentView, String> {
        if !offset.is_multiple_of(PAGE) {
            return Err("Select a valid assignment page.".into());
        }
        Ok(self.assignment_context()?.view(offset))
    }

    pub fn review_assignment(
        &self,
        expected: &str,
        target: AssignmentTarget,
        selected: &VerifiedMacro,
    ) -> Result<AssignmentRecipients, String> {
        let context = self.assignment_context()?;
        if context.stamp != expected {
            return Err(
                "Assignments or targets changed. Refresh assignments before reviewing.".into(),
            );
        }
        let name = context
            .targets
            .get(&target)
            .ok_or("This assignment target is unavailable.")?;
        let assignment = context
            .assigned(target)
            .ok_or("Assign a saved macro before reviewing.")?;
        if assignment.id != selected.id || assignment.saved_revision != Some(selected.revision) {
            return Err(
                "Save this exact macro revision as the assignment before reviewing.".into(),
            );
        }
        if !selected.enabled {
            return Err("This saved macro is disabled in the Personal editor.".into());
        }
        let members = if target.kind == TargetKind::Account {
            vec![AssignmentRecipient::Saved(self.account(target.id)?)]
        } else {
            context
                .legacy_members(target, name)?
                .into_iter()
                .map(|profile| {
                    if let Some(profile) = profile {
                        if let Some(account) =
                            profile.verified_user_id.and_then(|id| self.by_user_id(id))
                        {
                            return AssignmentRecipient::Saved(account);
                        }
                        if label(&profile.label) {
                            return AssignmentRecipient::Unlinked(profile.label.clone());
                        }
                    }
                    AssignmentRecipient::Unlinked("Unavailable imported profile".into())
                })
                .collect()
        };
        Ok(AssignmentRecipients {
            target_name: name.clone(),
            members,
        })
    }

    pub fn edit_assignment(
        &self,
        expected: &str,
        target: AssignmentTarget,
        selected: Option<VerifiedMacro>,
        offset: usize,
    ) -> Result<AssignmentView, String> {
        if !offset.is_multiple_of(PAGE) {
            return Err("Select a valid assignment page.".into());
        }
        let mut context = self.assignment_context()?;
        if expected != context.stamp {
            return Err(STALE.into());
        }
        if !context.targets.contains_key(&target) {
            return Err("This assignment target is unavailable.".into());
        }
        let next = selected.map(|item| MacroRef {
            id: item.id,
            name: item.name,
            saved_revision: Some(item.revision),
        });
        if next.as_ref().is_some_and(|item| !item.valid()) {
            return Err("The saved macro name is unsuitable for assignment.".into());
        }
        let current = context.assigned(target);
        if current == next.as_ref() {
            return Ok(context.view(offset));
        }
        if next.is_some() || context.inherited.contains_key(&target) {
            context.changes.insert(target, next);
        } else {
            context.changes.remove(&target);
        }
        if context.changes.len() > MAX_CHANGES {
            return Err("The assignment store has reached its supported size.".into());
        }
        let disk = Disk {
            version: 1,
            changes: context
                .changes
                .iter()
                .map(|(target, assignment)| Change {
                    target: *target,
                    assignment: assignment.clone(),
                })
                .collect(),
        };
        let mut clear = Zeroizing::new(
            serde_json::to_vec(&disk).map_err(|_| "Could not encode macro assignments.")?,
        );
        let protected = seal_assignment_metadata(&mut clear)?;
        atomic_write(&self.root.join(FILE), &protected)?;
        context.stamp = revision(&context.source, &protected);
        Ok(context.view(offset))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::{fs, path::PathBuf};
    use zenith_session_launch::{auth::Credential, windows::seal_legacy_metadata};

    struct Fixture(PathBuf);
    impl Fixture {
        fn open() -> (Self, Library) {
            let path =
                std::env::temp_dir().join(format!("zenith-assignment-fixture-{}", Uuid::new_v4()));
            let library = Library::open(path.clone()).unwrap();
            (Self(path), library)
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

    fn account(library: &mut Library, user: u64, username: &str) -> AssignmentTarget {
        let credential =
            Credential::new(Zeroizing::new("synthetic-assignment-fixture".into())).unwrap();
        let account = library
            .remember_sign_in(user, username.into(), credential, 4_102_444_800)
            .unwrap();
        AssignmentTarget {
            kind: TargetKind::Account,
            id: account.profile_id,
        }
    }

    #[test]
    fn edits_are_protected_stamp_bound_and_durable() {
        let (fixture, mut library) = Fixture::open();
        let target = account(&mut library, 101, "FixtureAccount");
        let initial = library.assignments(0).unwrap();
        let macro_id = Uuid::new_v4();
        let revision = Uuid::new_v4();
        let updated = library
            .edit_assignment(
                &initial.stamp,
                target,
                Some(VerifiedMacro {
                    id: macro_id,
                    revision,
                    name: "Fixture catalog title".into(),
                    enabled: true,
                }),
                0,
            )
            .unwrap();
        assert_ne!(initial.stamp, updated.stamp);
        assert!(
            library
                .edit_assignment(&initial.stamp, target, None, 0)
                .is_err()
        );
        let path = fixture.0.join(FILE);
        let ciphertext = fs::read(&path).unwrap();
        assert!(serde_json::from_slice::<Disk>(&ciphertext).is_err());
        drop(library);
        let library = Library::open(fixture.0.clone()).unwrap();
        let reopened = library.assignments(0).unwrap();
        let assignment = reopened.rows[0].assignment.as_ref().unwrap();
        assert_eq!(assignment.id, macro_id);
        assert_eq!(assignment.saved_revision, Some(revision));
        let removed = library
            .edit_assignment(&reopened.stamp, target, None, 0)
            .unwrap();
        assert!(removed.rows[0].assignment.is_none());
        fs::write(&path, []).unwrap();
        assert!(
            library
                .edit_assignment(&removed.stamp, target, None, 0)
                .is_err()
        );
        assert!(fs::read(path).unwrap().is_empty());
    }

    #[test]
    fn legacy_references_require_verified_account_mapping_and_keep_the_archive_intact() {
        let (fixture, mut library) = Fixture::open();
        let linked = account(&mut library, 42, "CurrentUsername");
        let unrelated = account(&mut library, 43, "OldUsername");
        let legacy = Uuid::new_v4();
        let archived = Uuid::new_v4();
        let group = Uuid::new_v4();
        let bundle = Uuid::new_v4();
        let malformed_bundle = Uuid::new_v4();
        let missing = Uuid::new_v4();
        let macro_id = Uuid::new_v4();
        let profile = |id: Uuid, verified: Option<u64>| {
            json!({ "id": id, "label": "Legacy account", "username": "OldUsername",
            "groups": ["fIXTURE äLTS"], "priority": 10, "preferredPlaceId": null, "legacyUserId": "42", "verifiedUserId": verified,
            "notes": "", "isFavorite": false, "updatedUtc": "2026-11-01T00:00:00Z", "revision": 1 })
        };
        let reference = |id: Uuid, kind: u8| {
            json!({ "target": { "id": id, "kind": kind }, "targetName": "Legacy target",
            "macroId": macro_id, "macroName": "Legacy macro" })
        };
        let migration = json!({ "version": 1, "signIns": [], "workspace": {
            "protection": "windows", "profiles": [profile(legacy, Some(42)), profile(archived, None)],
            "groups": [{ "id": group, "name": "Fixture Älts" }],
            "bundles": [{ "id": bundle, "name": "Fixture bundle", "profileIds": [archived, missing, legacy] },
                { "id": malformed_bundle, "name": "Invalid bundle", "profileIds": [legacy, legacy] }],
            "assignments": [reference(legacy, 1), reference(archived, 1), reference(group, 2), reference(bundle, 3)],
            "favorites": [], "preferences": {} } });
        let protected = seal_legacy_metadata(&mut serde_json::to_vec(&migration).unwrap()).unwrap();
        library.import_legacy(protected).unwrap();
        let archive = fixture.0.join("legacy-workspace.zenith-migration");
        let before = fs::read(&archive).unwrap();
        let view = library.assignments(0).unwrap();
        let row = |target| view.rows.iter().find(|row| row.target == target).unwrap();
        assert_eq!(row(linked).assignment.as_ref().unwrap().id, macro_id);
        assert!(row(unrelated).assignment.is_none());
        assert!(
            !row(AssignmentTarget {
                kind: TargetKind::Account,
                id: archived
            })
            .editable
        );
        let group = AssignmentTarget {
            kind: TargetKind::Group,
            id: group,
        };
        let bundle = AssignmentTarget {
            kind: TargetKind::Bundle,
            id: bundle,
        };
        assert!(row(group).editable && row(group).inherited);
        assert!(row(bundle).editable && row(bundle).inherited);

        let revision = Uuid::new_v4();
        let macro_choice = || VerifiedMacro {
            id: macro_id,
            name: "Current saved macro".into(),
            revision,
            enabled: true,
        };
        assert!(
            library
                .review_assignment(&view.stamp, group, &macro_choice())
                .is_err()
        );
        let invalid = AssignmentTarget {
            kind: TargetKind::Bundle,
            id: malformed_bundle,
        };
        let original_stamp = view.stamp.clone();
        let mut view = view;
        for target in [linked, group, bundle, invalid] {
            view = library
                .edit_assignment(&view.stamp, target, Some(macro_choice()), 0)
                .unwrap();
        }
        assert!(
            library
                .review_assignment(&original_stamp, group, &macro_choice())
                .is_err()
        );
        assert!(
            library
                .review_assignment(&view.stamp, invalid, &macro_choice())
                .is_err()
        );
        let mut changed = macro_choice();
        changed.revision = Uuid::new_v4();
        assert!(
            library
                .review_assignment(&view.stamp, group, &changed)
                .is_err()
        );
        let mut disabled = macro_choice();
        disabled.enabled = false;
        assert!(
            library
                .review_assignment(&view.stamp, group, &disabled)
                .is_err()
        );
        let linked_scope = library
            .review_assignment(&view.stamp, linked, &macro_choice())
            .unwrap();
        assert!(matches!(linked_scope.members.as_slice(),
            [AssignmentRecipient::Saved(account)] if account.profile_id == linked.id));
        let group_scope = library
            .review_assignment(&view.stamp, group, &macro_choice())
            .unwrap();
        assert_eq!(group_scope.target_name, "Fixture Älts");
        assert!(matches!(group_scope.members.as_slice(),
            [AssignmentRecipient::Saved(account), AssignmentRecipient::Unlinked(_)] if account.profile_id == linked.id));
        let bundle_scope = library
            .review_assignment(&view.stamp, bundle, &macro_choice())
            .unwrap();
        assert!(matches!(bundle_scope.members.as_slice(),
            [AssignmentRecipient::Unlinked(unlinked), AssignmentRecipient::Unlinked(missing), AssignmentRecipient::Saved(account)]
            if unlinked == "Legacy account" && missing == "Unavailable imported profile" && account.profile_id == linked.id));

        let view = library
            .edit_assignment(&view.stamp, group, None, 0)
            .unwrap();
        assert!(
            view.rows
                .iter()
                .find(|row| row.target == group)
                .unwrap()
                .assignment
                .is_none()
        );
        drop(library);
        let library = Library::open(fixture.0.clone()).unwrap();
        let reopened = library.assignments(0).unwrap();
        assert!(
            reopened
                .rows
                .iter()
                .find(|row| row.target == group)
                .unwrap()
                .assignment
                .is_none()
        );
        assert_eq!(fs::read(archive).unwrap(), before);
    }
}
