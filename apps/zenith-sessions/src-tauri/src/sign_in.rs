use std::sync::{Arc, atomic::Ordering};

use tauri::{
    AppHandle, Manager, State, Url, WebviewUrl, WebviewWindowBuilder,
    async_runtime::spawn_blocking, webview::NewWindowResponse,
};
use uuid::Uuid;
use zenith_session_launch::{
    auth::{Credential, RobloxAuth},
    windows::SavedIdentity,
};
use zeroize::Zeroizing;

use crate::{STATE_ERROR, Workspace};

const LOGIN: &str = "https://www.roblox.com/login";
const HOME: &str = "https://www.roblox.com/";

pub struct SignIn {
    pub attempt: Uuid,
    saving: bool,
}

fn label(attempt: Uuid) -> String {
    format!("roblox-sign-in-{attempt}")
}

fn official_navigation(url: &Url) -> bool {
    url.scheme() == "https"
        && url.port_or_known_default() == Some(443)
        && url.username().is_empty()
        && url.password().is_none()
        && url
            .host_str()
            .is_some_and(|host| host == "roblox.com" || host.ends_with(".roblox.com"))
}

#[tauri::command]
pub async fn begin_sign_in(
    app: AppHandle,
    state: State<'_, Arc<Workspace>>,
) -> Result<Uuid, String> {
    if state.exiting.load(Ordering::Acquire) {
        return Err("Zenith is closing".into());
    }
    let attempt = Uuid::new_v4();
    {
        let mut active = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        if active.is_some() {
            return Err("Finish or cancel the existing Roblox sign-in first".into());
        }
        *active = Some(SignIn {
            attempt,
            saving: false,
        });
    }
    let opened = WebviewWindowBuilder::new(
        &app,
        label(attempt),
        WebviewUrl::External(Url::parse(LOGIN).expect("fixed official login URL")),
    )
    .title("Sign in to Roblox · Zenith Sessions")
    .inner_size(900.0, 700.0)
    .incognito(true)
    .data_directory(state.sign_in_directory.clone())
    .devtools(false)
    .on_navigation(official_navigation)
    .on_new_window(|_, _| NewWindowResponse::Deny)
    .build();
    if opened.is_err() {
        state.clear_sign_in(attempt);
        return Err("Could not open the isolated Roblox sign-in window".into());
    }
    Ok(attempt)
}

#[tauri::command]
pub fn cancel_sign_in(
    app: AppHandle,
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
) -> Result<(), String> {
    state.clear_sign_in(attempt);
    if let Some(window) = app.get_webview_window(&label(attempt)) {
        window
            .close()
            .map_err(|_| "Close the Roblox sign-in window manually")?;
    }
    Ok(())
}

#[tauri::command]
pub async fn finish_sign_in(
    app: AppHandle,
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
) -> Result<SavedIdentity, String> {
    let state = Arc::clone(state.inner());
    {
        let mut active = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        let flow = active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
            .ok_or("Open a Roblox sign-in window first")?;
        if flow.saving {
            return Err("Zenith is already checking this sign-in".into());
        }
        flow.saving = true;
    }
    let worker_state = Arc::clone(&state);
    let result = spawn_blocking(move || capture_and_save(&app, &worker_state, attempt))
        .await
        .map_err(|_| STATE_ERROR.to_owned())
        .and_then(|result| result);
    if result.is_err()
        && let Ok(mut active) = state.sign_in.lock()
        && let Some(flow) = active.as_mut().filter(|flow| flow.attempt == attempt)
    {
        flow.saving = false;
    }
    result
}

fn capture_and_save(
    app: &AppHandle,
    state: &Workspace,
    attempt: Uuid,
) -> Result<SavedIdentity, String> {
    let window = app
        .get_webview_window(&label(attempt))
        .ok_or("The Roblox sign-in window closed")?;
    let cookies = window
        .cookies_for_url(Url::parse(HOME).expect("fixed official URL"))
        .map_err(|_| "Could not read the isolated Roblox sign-in")?;
    let mut matches = cookies.iter().filter(|cookie| {
        cookie.name() == ".ROBLOSECURITY"
            && cookie.http_only() == Some(true)
            && cookie.secure() == Some(true)
            && cookie.domain().is_none_or(|domain| {
                matches!(
                    domain.trim_start_matches('.'),
                    "roblox.com" | "www.roblox.com"
                )
            })
    });
    let cookie = matches
        .next()
        .ok_or("Finish signing in on Roblox, then select Save sign-in")?;
    if matches.next().is_some() {
        return Err("Roblox returned ambiguous sign-ins. Close the window and try again.".into());
    }
    let expires = cookie
        .expires_datetime()
        .map(|date| date.unix_timestamp().max(0) as u64)
        .unwrap_or(253_402_300_799);
    let mut credential = Credential::new(Zeroizing::new(cookie.value().to_owned()))
        .map_err(|error| error.to_string())?;
    drop(cookies);
    let (user_id, username) = RobloxAuth::new()
        .map_err(|error| error.to_string())?
        .signed_in_user(&mut credential)
        .map_err(|error| error.to_string())?;
    let identity = {
        let mut active = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        if state.exiting.load(Ordering::Acquire)
            || active.as_ref().is_none_or(|flow| flow.attempt != attempt)
        {
            return Err("The Roblox sign-in was cancelled".into());
        }
        let owner = state.owner.lock().map_err(|_| STATE_ERROR)?;
        let mut library = state.library.lock().map_err(|_| STATE_ERROR)?;
        if library
            .by_user_id(user_id)
            .is_some_and(|account| owner.uses(account.profile_id))
        {
            return Err("Stop this account's session before renewing its sign-in".into());
        }
        let identity = library.remember_sign_in(user_id, username, credential, expires)?;
        *active = None;
        identity
    };
    let _ = window.close();
    Ok(identity)
}

pub fn destroyed(state: &Workspace, window_label: &str) {
    if let Some(id) = window_label
        .strip_prefix("roblox-sign-in-")
        .and_then(|suffix| Uuid::parse_str(suffix).ok())
    {
        state.clear_sign_in(id);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sign_in_window_accepts_only_secure_first_party_origins() {
        assert!(official_navigation(&Url::parse(LOGIN).unwrap()));
        assert!(official_navigation(
            &Url::parse("https://auth.roblox.com/v1/example").unwrap()
        ));
        for url in [
            "http://www.roblox.com/login",
            "https://roblox.com.evil.test/",
            "https://evilroblox.com/",
            "https://user@www.roblox.com/login",
            "https://www.roblox.com:8443/login",
            "tauri://localhost/",
        ] {
            assert!(!official_navigation(&Url::parse(url).unwrap()));
        }
    }
}
