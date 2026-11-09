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
    auth::{Credential, Failure, RobloxAuth},
    windows::SavedIdentity,
};
use zeroize::Zeroizing;

use crate::{STATE_ERROR, Workspace};

const LOGIN: &str = "https://www.roblox.com/login";
const HOME: &str = "https://www.roblox.com/";

enum SaveFailure {
    Busy,
    Provider(Failure),
    Local(String),
}

impl SaveFailure {
    fn message(&self) -> String {
        match self {
            Self::Busy => "Zenith is already checking this sign-in".into(),
            Self::Provider(error) => error.to_string(),
            Self::Local(message) => message.clone(),
        }
    }

    fn retry_delay(&self, retries: usize) -> Option<Duration> {
        match self {
            Self::Provider(
                Failure::Network(_) | Failure::Timeout(_) | Failure::SignInRequired(_),
            ) => [2, 5].get(retries).copied().map(Duration::from_secs),
            _ => None,
        }
    }
}

impl From<&str> for SaveFailure {
    fn from(message: &str) -> Self {
        Self::Local(message.into())
    }
}

impl From<String> for SaveFailure {
    fn from(message: String) -> Self {
        Self::Local(message)
    }
}

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
    let mut retries = 0;
    let mut next_check = Instant::now();
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
        if Instant::now() >= next_check && window.url().is_ok_and(|url| signed_in_landing(&url)) {
            match try_save(app, state, attempt) {
                Ok(Some(_)) => return,
                Ok(None) | Err(SaveFailure::Busy) => {}
                Err(error) => {
                    let Some(delay) = error.retry_delay(retries) else {
                        report_save_error(state, attempt, error.message());
                        return;
                    };
                    retries += 1;
                    next_check = Instant::now() + delay;
                }
            }
        }
        std::thread::sleep(Duration::from_secs(1));
    }
    report_save_error(
        state,
        attempt,
        "Automatic saving timed out. Finish signing in, then retry saving.".into(),
    );
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
    spawn_blocking(move || match try_save(&app, &state, attempt) {
        Ok(Some(identity)) => Ok(identity),
        Ok(None) => Err("Finish signing in in the Roblox window, then retry.".into()),
        Err(SaveFailure::Busy) => Err(SaveFailure::Busy.message()),
        Err(error) => {
            let message = error.message();
            report_save_error(&state, attempt, message.clone());
            Err(message)
        }
    })
    .await
    .map_err(|_| STATE_ERROR.to_owned())?
}

fn try_save(
    app: &AppHandle,
    state: &Workspace,
    attempt: Uuid,
) -> Result<Option<SavedIdentity>, SaveFailure> {
    {
        let mut sign_in = state.sign_in.lock().map_err(|_| STATE_ERROR)?;
        let flow = sign_in
            .active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
            .ok_or("Open a Roblox sign-in window first")?;
        if flow.saving {
            return Err(SaveFailure::Busy);
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
    }
    result
}

fn report_save_error(state: &Workspace, attempt: Uuid, message: String) {
    if let Ok(mut sign_in) = state.sign_in.lock()
        && let Some(flow) = sign_in
            .active
            .as_mut()
            .filter(|flow| flow.attempt == attempt)
    {
        flow.error = Some(message);
    }
}

fn capture_and_save(
    app: &AppHandle,
    state: &Workspace,
    attempt: Uuid,
) -> Result<Option<SavedIdentity>, SaveFailure> {
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
        .map_err(|error| SaveFailure::Local(error.to_string()))?;
    credential.limit_expiry(expires);
    drop(cookies);
    let (user_id, username) = RobloxAuth::new()
        .map_err(SaveFailure::Provider)?
        .signed_in_user(&mut credential)
        .map_err(SaveFailure::Provider)?;
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

    #[test]
    fn automatic_save_retries_temporary_validation_failures_with_a_fixed_limit() {
        for error in [
            Failure::Network("fixture"),
            Failure::Timeout("fixture"),
            Failure::SignInRequired("fixture"),
        ] {
            let failure = SaveFailure::Provider(error);
            assert_eq!(failure.retry_delay(0), Some(Duration::from_secs(2)));
            assert_eq!(failure.retry_delay(1), Some(Duration::from_secs(5)));
            assert_eq!(failure.retry_delay(2), None);
        }
        for failure in [
            SaveFailure::Provider(Failure::RateLimited("fixture", Some(15))),
            SaveFailure::Provider(Failure::Challenge("fixture")),
            SaveFailure::Provider(Failure::IdentityChanged),
            SaveFailure::Provider(Failure::UnstableSignIn),
            SaveFailure::Local("storage failure".into()),
        ] {
            assert_eq!(failure.retry_delay(0), None);
        }
    }
}
