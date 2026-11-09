use std::{
    sync::{Arc, atomic::Ordering},
    time::{Duration, Instant},
};

use serde::Serialize;
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
    pub error: Option<String>,
    saving: bool,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedSignIn {
    attempt: Uuid,
    profile_id: Uuid,
}

#[derive(Default)]
pub struct SignInState {
    pub active: Option<SignIn>,
    pub saved: Option<SavedSignIn>,
}

impl SignInState {
    fn clear(&mut self, attempt: Uuid) {
        if self
            .active
            .as_ref()
            .is_some_and(|flow| flow.attempt == attempt)
        {
            self.active = None;
        }
    }
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

fn signed_in_landing(url: &Url) -> bool {
    official_navigation(url)
        && url.host_str() == Some("www.roblox.com")
        && matches!(url.path(), "/" | "/home" | "/home/")
        && !url.query_pairs().any(|(name, _)| {
            ["challenge", "returnurl", "login", "twoStepVerification"]
                .iter()
                .any(|key| key.eq_ignore_ascii_case(&name))
        })
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
        let mut sign_in = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        if sign_in.active.is_some() {
            return Err("Finish or cancel the existing Roblox sign-in first".into());
        }
        sign_in.active = Some(SignIn {
            attempt,
            saving: false,
            error: None,
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
        state
            .sign_in
            .lock()
            .map_err(|_| STATE_ERROR)?
            .clear(attempt);
        return Err("Could not open the isolated Roblox sign-in window".into());
    }
    let state = Arc::clone(state.inner());
    spawn_blocking(move || monitor(&app, &state, attempt));
    Ok(attempt)
}

fn monitor(app: &AppHandle, state: &Workspace, attempt: Uuid) {
    let deadline = Instant::now() + Duration::from_secs(15 * 60);
    while Instant::now() < deadline {
        if state.exiting.load(Ordering::Acquire)
            || !state.sign_in.lock().is_ok_and(|sign_in| {
                sign_in
                    .active
                    .as_ref()
                    .is_some_and(|flow| flow.attempt == attempt)
            })
        {
            return;
        }
        let Some(window) = app.get_webview_window(&label(attempt)) else {
            return;
        };
        if window.url().is_ok_and(|url| signed_in_landing(&url))
            && !matches!(try_save(app, state, attempt), Ok(None))
        {
            return;
        }
        std::thread::sleep(Duration::from_secs(1));
    }
    if let Ok(mut sign_in) = state.sign_in.lock()
        && let Some(flow) = sign_in
            .active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
    {
        flow.error =
            Some("Automatic saving timed out. Finish signing in, then retry saving.".into());
    }
}

#[tauri::command]
pub fn cancel_sign_in(
    app: AppHandle,
    state: State<'_, Arc<Workspace>>,
    attempt: Uuid,
) -> Result<(), String> {
    state
        .sign_in
        .lock()
        .map_err(|_| STATE_ERROR)?
        .clear(attempt);
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
    spawn_blocking(move || {
        try_save(&app, &state, attempt)?
            .ok_or_else(|| "Finish signing in in the Roblox window, then retry.".into())
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

fn try_save(
    app: &AppHandle,
    state: &Workspace,
    attempt: Uuid,
) -> Result<Option<SavedIdentity>, String> {
    {
        let mut sign_in = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        let flow = sign_in
            .active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
            .ok_or("Open a Roblox sign-in window first")?;
        if flow.saving {
            return Err("Zenith is already checking this sign-in".into());
        }
        flow.saving = true;
    }
    let result = capture_and_save(app, state, attempt);
    if let Ok(mut sign_in) = state.sign_in.lock()
        && let Some(flow) = sign_in
            .active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
    {
        flow.saving = false;
        if let Err(error) = &result {
            flow.error = Some(error.clone());
        }
    }
    result
}

fn capture_and_save(
    app: &AppHandle,
    state: &Workspace,
    attempt: Uuid,
) -> Result<Option<SavedIdentity>, String> {
    let window = app
        .get_webview_window(&label(attempt))
        .ok_or("The Roblox sign-in window closed")?;
    if !window.url().is_ok_and(|url| signed_in_landing(&url)) {
        return Ok(None);
    }
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
    let Some(cookie) = matches.next() else {
        return Ok(None);
    };
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
    if !window.url().is_ok_and(|url| signed_in_landing(&url)) {
        return Ok(None);
    }
    let identity = {
        let mut sign_in = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        if state.exiting.load(Ordering::Acquire)
            || sign_in
                .active
                .as_ref()
                .is_none_or(|flow| flow.attempt != attempt)
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
        sign_in.saved = Some(SavedSignIn {
            attempt,
            profile_id: identity.profile_id,
        });
        sign_in.clear(attempt);
        identity
    };
    let _ = window.close();
    Ok(Some(identity))
}

pub fn destroyed(state: &Workspace, window_label: &str) {
    if let Some(id) = window_label
        .strip_prefix("roblox-sign-in-")
        .and_then(|suffix| Uuid::parse_str(suffix).ok())
        && let Ok(mut sign_in) = state.sign_in.lock()
    {
        sign_in.clear(id);
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

    #[test]
    fn automatic_save_waits_for_a_completed_first_party_landing() {
        assert!(signed_in_landing(
            &Url::parse("https://www.roblox.com/home").unwrap()
        ));
        for url in [
            LOGIN,
            "https://www.roblox.com/home?challenge=fixture",
            "https://www.roblox.com/login/two-step-verification",
            "https://www.roblox.com/challenge",
            "https://auth.roblox.com/home",
            "https://www.roblox.com.evil.test/home",
        ] {
            assert!(!signed_in_landing(&Url::parse(url).unwrap()));
        }
        let mut sign_in = SignInState::default();
        let current = Uuid::new_v4();
        sign_in.active = Some(SignIn {
            attempt: current,
            saving: false,
            error: None,
        });
        sign_in.clear(Uuid::new_v4());
        assert_eq!(sign_in.active.as_ref().unwrap().attempt, current);
        sign_in.clear(current);
        assert!(sign_in.active.is_none());
    }
}
