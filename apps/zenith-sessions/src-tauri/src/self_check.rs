use std::{
    fs,
    process::ExitCode,
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use tauri::{Context, Wry};
use uuid::Uuid;
use zenith_session_launch::{
    admission::Limit,
    auth::Credential,
    windows::{SavedIdentity, VerifiedPlayer, memory_sample, open_saved_bundle, seal_saved_bundle},
};
use zeroize::Zeroizing;

use crate::{
    recovery::{Checkpoint, Journal, Record},
    session::SessionOwner,
};

pub fn run(context: &Context<Wry>) -> ExitCode {
    let frontend = check_assets(context);
    let webview = tauri::webview_version()
        .map(|version| format!("WebView2 {version}"))
        .map_err(|_| "Windows WebView2 Runtime is unavailable.".into());
    let vault = check_dpapi();
    let resources = memory_sample().and_then(|sample| {
        let admission = sample.admission(Instant::now());
        match admission.limiting_resource {
            Some(Limit::Invalid | Limit::Stale) => {
                Err("Windows did not return a fresh memory sample.".into())
            }
            _ => Ok("Windows physical and commit measurements are available.".into()),
        }
    });
    let publisher = VerifiedPlayer::registered()
        .map(|_| "Registered standard player signature verified.".into());
    let mut failed = false;
    for (stage, result) in [
        ("Embedded interface", frontend),
        ("Browser runtime", webview),
        ("Current-user encryption", vault),
        ("Memory sampling", resources),
        ("Roblox installation", publisher),
        ("Launch recovery journal", check_journal()),
        ("Recent public servers", crate::server_history::self_check()),
        ("Local support report", crate::support_report::self_check()),
    ] {
        match result {
            Ok(detail) => println!("PASS {stage} · {detail}"),
            Err(error) => {
                failed = true;
                println!("FAIL {stage} · {error}");
            }
        }
    }
    if failed {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    }
}

fn check_journal() -> Result<String, String> {
    let directory = std::env::temp_dir().join(format!("zenith-self-check-{}", Uuid::new_v4()));
    fs::create_dir(&directory).map_err(|_| "Could not create a disposable recovery directory.")?;
    let path = directory.join("launch-journal.json");
    let result = (|| {
        let (mut journal, _) = Journal::open(path.clone());
        let attempt = Record {
            attempt_id: Uuid::new_v4(),
            account_id: Uuid::new_v4(),
            username: "ZenithSelfCheck".into(),
            place_id: "1818".into(),
            requested_server: None,
            private_server: false,
            checkpoint: Checkpoint::Dispatching,
        };
        journal.save(vec![attempt])?;
        let owner = SessionOwner::open(path.clone());
        let recovery = owner.recovery();
        if recovery.attempts.len() != 1 || recovery.issue.is_some() || !owner.rows().is_empty() {
            return Err("The journal did not reopen as a review-only attempt.".into());
        }
        Ok("A synthetic dispatch reopened for review without starting a client.".into())
    })();
    let _ = fs::remove_file(path);
    fs::remove_dir(directory).map_err(|_| "Could not remove the disposable recovery directory.")?;
    result
}

fn check_assets(context: &Context<Wry>) -> Result<String, String> {
    let assets = context.assets();
    let index = assets
        .get(&"index.html".into())
        .ok_or("The package does not contain index.html.")?;
    let html = std::str::from_utf8(&index).map_err(|_| "The embedded index is invalid UTF-8.")?;
    if !html.contains("id=\"root\"") {
        return Err("The embedded index does not have the React root.".into());
    }
    for (attribute, extension) in [("src", ".js"), ("href", ".css")] {
        let prefix = format!("{attribute}=\"/assets/");
        let name = html
            .split_once(&prefix)
            .and_then(|(_, value)| value.split_once('"'))
            .map(|(name, _)| name)
            .filter(|name| name.ends_with(extension))
            .ok_or_else(|| {
                format!("The embedded index does not reference its {extension} asset.")
            })?;
        let key = format!("assets/{name}");
        if assets.get(&key.into()).is_none_or(|bytes| bytes.is_empty()) {
            return Err(format!(
                "The package is missing the referenced {extension} asset."
            ));
        }
    }
    Ok("The index, React script and stylesheet are embedded.".into())
}

fn check_dpapi() -> Result<String, String> {
    let identity = SavedIdentity {
        profile_id: Uuid::new_v4(),
        username: "ZenithSelfCheck".into(),
        user_id: 1,
    };
    let expires = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "System clock is invalid.")?
        .as_secs()
        + 300;
    let credential = Credential::new(Zeroizing::new("SyntheticSelfCheck".into()))
        .map_err(|error| error.to_string())?;
    let mut bytes = seal_saved_bundle(&identity, &credential, expires)?;
    let (restored, _) = open_saved_bundle(&mut bytes)?;
    if restored != identity {
        return Err("The restored synthetic profile did not match.".into());
    }
    Ok("A synthetic sign-in was protected and restored in memory.".into())
}
