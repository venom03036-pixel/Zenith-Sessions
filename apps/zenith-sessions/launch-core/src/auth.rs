use std::{
    fmt,
    io::Read,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use cookie::Cookie;
use reqwest::{
    Method, StatusCode,
    blocking::{Client, Response},
    header::{ACCEPT, COOKIE, HeaderValue, ORIGIN, REFERER, RETRY_AFTER, SET_COOKIE},
};
use serde::Deserialize;
use serde_json::Value;
use zeroize::Zeroizing;

const IDENTITY: &str = "https://users.roblox.com/v1/users/authenticated";
const ASSERTION: &str = "https://auth.roblox.com/v1/client-assertion/";
const TICKET: &str = "https://auth.roblox.com/v1/authentication-ticket/";
const COOKIE_NAME: &str = ".ROBLOSECURITY";
const CSRF: &str = "x-csrf-token";
const MAX_JSON: u64 = 64 * 1024;

pub struct Credential {
    value: Zeroizing<String>,
    verified_user: Option<u64>,
    updated: bool,
    expires: Option<u64>,
}

impl Credential {
    pub fn new(value: Zeroizing<String>) -> Result<Self, Failure> {
        if value.is_empty()
            || value.len() > 16384
            || !value
                .bytes()
                .all(|b| (b'!'..=b'~').contains(&b) && !b";,".contains(&b))
        {
            return Err(Failure::SignInRequired("saved sign-in"));
        }
        Ok(Self {
            value,
            verified_user: None,
            updated: false,
            expires: None,
        })
    }

    fn header(&self) -> HeaderValue {
        let mut raw = Zeroizing::new(String::with_capacity(
            COOKIE_NAME.len() + 1 + self.value.len(),
        ));
        raw.push_str(COOKIE_NAME);
        raw.push('=');
        raw.push_str(&self.value);
        let mut header = HeaderValue::from_str(&raw).expect("credential character set checked");
        header.set_sensitive(true);
        header
    }

    pub(crate) fn value(&self) -> &str {
        &self.value
    }

    pub fn expires_at(&self) -> Option<u64> {
        self.expires
    }

    pub fn limit_expiry(&mut self, expires: u64) {
        self.expires = Some(self.expires.map_or(expires, |current| current.min(expires)));
    }

    pub fn has_verified_update(&self, user_id: u64) -> bool {
        self.updated && self.verified_user == Some(user_id)
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum Failure {
    Cancelled,
    Network(&'static str),
    Timeout(&'static str),
    SignInRequired(&'static str),
    IdentityChanged,
    UnstableSignIn,
    Challenge(&'static str),
    RateLimited(&'static str, Option<u64>),
    Refused(&'static str, u16),
    InvalidResponse(&'static str),
}

impl fmt::Display for Failure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Cancelled => f.write_str("Zenith stopped this launch request."),
            Self::Network(stage) => write!(f, "{stage}: could not contact Roblox"),
            Self::Timeout(stage) => write!(f, "{stage}: Roblox response timed out"),
            Self::SignInRequired(stage) => write!(f, "{stage}: sign in to this account again"),
            Self::IdentityChanged => {
                f.write_str("saved sign-in resolved to a different Roblox account")
            }
            Self::UnstableSignIn => f.write_str("Roblox replaced the sign-in again during verification. Sign in to this account again."),
            Self::Challenge(stage) => write!(f, "{stage}: Roblox requires an account check"),
            Self::RateLimited(stage, Some(seconds)) => write!(
                f,
                "{stage}: Roblox rate limited this request; retry after {seconds}s"
            ),
            Self::RateLimited(stage, None) => write!(
                f,
                "{stage}: Roblox rate limited this request; wait before retrying"
            ),
            Self::Refused(stage, code) => write!(f, "{stage}: Roblox returned HTTP {code}"),
            Self::InvalidResponse(stage) => {
                write!(f, "{stage}: Roblox returned an unexpected response")
            }
        }
    }
}

impl std::error::Error for Failure {}

pub struct RobloxAuth {
    http: Client,
    identity_url: String,
    assertion_url: String,
    ticket_url: String,
    launch_permit: Option<Box<dyn Fn() -> bool + Send + Sync>>,
}

#[derive(Deserialize)]
struct Identity {
    id: u64,
    #[serde(default)]
    name: String,
}

impl RobloxAuth {
    pub(crate) fn read_profile<T: serde::de::DeserializeOwned>(
        &self,
        stage: &'static str,
        credential: &mut Credential,
        url: &str,
    ) -> Result<T, Failure> {
        let response = self.request(stage, credential, Method::GET, url, None, None)?;
        read_json(accepted(stage, response)?, stage)
    }

    pub fn new() -> Result<Self, Failure> {
        let http = Client::builder()
            .user_agent("ZenithSessions/0.1")
            .connect_timeout(Duration::from_secs(8))
            .timeout(Duration::from_secs(15))
            .redirect(reqwest::redirect::Policy::none())
            .retry(reqwest::retry::never())
            .build()
            .map_err(|_| Failure::Network("HTTP setup"))?;
        Ok(Self {
            http,
            identity_url: IDENTITY.into(),
            assertion_url: ASSERTION.into(),
            ticket_url: TICKET.into(),
            launch_permit: None,
        })
    }

    pub fn for_launch(permits: impl Fn() -> bool + Send + Sync + 'static) -> Result<Self, Failure> {
        let mut auth = Self::new()?;
        auth.launch_permit = Some(Box::new(permits));
        Ok(auth)
    }

    fn check_launch(&self) -> Result<(), Failure> {
        if self
            .launch_permit
            .as_ref()
            .is_some_and(|permits| !permits())
        {
            return Err(Failure::Cancelled);
        }
        Ok(())
    }

    pub fn verify(&self, credential: &mut Credential, user_id: u64) -> Result<(), Failure> {
        credential.verified_user = None;
        let (user, rotation) = self.identity(credential)?;
        if user.id != user_id || user_id == 0 {
            return Err(Failure::IdentityChanged);
        }
        self.verify_replacement(credential, user.id, rotation)
    }

    pub fn signed_in_user(&self, credential: &mut Credential) -> Result<(u64, String), Failure> {
        credential.verified_user = None;
        let (user, rotation) = self.identity(credential)?;
        if user.id == 0
            || user.name.is_empty()
            || user.name.len() > 20
            || !user
                .name
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
        {
            return Err(Failure::InvalidResponse("account validation"));
        }
        self.verify_replacement(credential, user.id, rotation)?;
        Ok((user.id, user.name))
    }

    fn identity(&self, credential: &Credential) -> Result<(Identity, Option<Credential>), Failure> {
        const STAGE: &str = "account validation";
        let response = accepted(
            STAGE,
            self.send(
                STAGE,
                credential,
                Method::GET,
                &self.identity_url,
                None,
                None,
            )?,
        )?;
        let rotation = replacement(&response, credential, STAGE)?;
        Ok((read_json(response, STAGE)?, rotation))
    }

    fn verify_replacement(
        &self,
        credential: &mut Credential,
        user_id: u64,
        rotation: Option<Credential>,
    ) -> Result<(), Failure> {
        if let Some(mut candidate) = rotation {
            if candidate.value != credential.value {
                let (confirmed, successor) = self.identity(&candidate)?;
                if confirmed.id != user_id {
                    return Err(Failure::IdentityChanged);
                }
                if let Some(successor) = successor {
                    if successor.value != candidate.value {
                        return Err(Failure::UnstableSignIn);
                    }
                    candidate = successor;
                }
            }
            candidate.updated = true;
            *credential = candidate;
        }
        credential.verified_user = Some(user_id);
        Ok(())
    }

    pub fn ticket(&self, credential: &mut Credential) -> Result<Zeroizing<String>, Failure> {
        let assertion = self.request(
            "client assertion",
            credential,
            Method::GET,
            &self.assertion_url,
            None,
            None,
        )?;
        let body: Value = match assertion.status() {
            StatusCode::NOT_FOUND | StatusCode::METHOD_NOT_ALLOWED
                if !assertion.headers().contains_key("rblx-challenge-id") =>
            {
                serde_json::json!({})
            }
            _ => read_json(accepted("client assertion", assertion)?, "client assertion")?,
        };
        const STAGE: &str = "launch ticket";
        let first = self.request(
            STAGE,
            credential,
            Method::POST,
            &self.ticket_url,
            Some(&body),
            None,
        )?;
        let response = if first.status() == StatusCode::FORBIDDEN
            && !first.headers().contains_key("rblx-challenge-id")
            && let Some(token) = first.headers().get(CSRF).filter(|token| !token.is_empty())
        {
            let mut token = token.clone();
            token.set_sensitive(true);
            self.request(
                STAGE,
                credential,
                Method::POST,
                &self.ticket_url,
                Some(&body),
                Some(token),
            )?
        } else {
            first
        };
        let response = accepted(STAGE, response)?;
        response
            .headers()
            .get("rbx-authentication-ticket")
            .and_then(|value| value.to_str().ok())
            .filter(|ticket| !ticket.is_empty() && ticket.len() <= 8192)
            .map(|ticket| Zeroizing::new(ticket.to_owned()))
            .ok_or(Failure::InvalidResponse(STAGE))
    }

    fn request(
        &self,
        stage: &'static str,
        credential: &mut Credential,
        method: Method,
        url: &str,
        body: Option<&Value>,
        csrf: Option<HeaderValue>,
    ) -> Result<Response, Failure> {
        let user_id = credential
            .verified_user
            .ok_or(Failure::SignInRequired(stage))?;
        let response = self.send(stage, credential, method, url, body, csrf)?;
        if response.status() == StatusCode::UNAUTHORIZED {
            credential.verified_user = None;
        }
        let can_rotate = response.status().is_success()
            || (stage == "launch ticket"
                && response.status() == StatusCode::FORBIDDEN
                && response
                    .headers()
                    .get(CSRF)
                    .is_some_and(|value| !value.is_empty()));
        if can_rotate && !response.headers().contains_key("rblx-challenge-id") {
            let update = replacement(&response, credential, stage)
                .and_then(|rotation| self.verify_replacement(credential, user_id, rotation));
            if update.is_err() {
                credential.verified_user = None;
            }
            update?;
        }
        Ok(response)
    }

    fn send(
        &self,
        stage: &'static str,
        credential: &Credential,
        method: Method,
        url: &str,
        body: Option<&Value>,
        csrf: Option<HeaderValue>,
    ) -> Result<Response, Failure> {
        if let Some(expires) = credential.expires
            && expires <= unix_seconds(stage)?
        {
            return Err(Failure::SignInRequired(stage));
        }
        let mut request = self
            .http
            .request(method, url)
            .header(ACCEPT, "application/json, text/plain, */*")
            .header(COOKIE, credential.header())
            .header(ORIGIN, "https://www.roblox.com")
            .header(REFERER, "https://www.roblox.com/");
        if let Some(body) = body {
            request = request.json(body);
        }
        if let Some(csrf) = csrf {
            request = request.header(CSRF, csrf);
        }
        self.check_launch()?;
        let response = request.send();
        self.check_launch()?;
        response.map_err(|error| {
            if error.is_timeout() {
                Failure::Timeout(stage)
            } else {
                Failure::Network(stage)
            }
        })
    }
}

fn unix_seconds(stage: &'static str) -> Result<u64, Failure> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .map_err(|_| Failure::InvalidResponse(stage))
}

fn replacement(
    response: &Response,
    credential: &Credential,
    stage: &'static str,
) -> Result<Option<Credential>, Failure> {
    let mut headers = response
        .headers()
        .get_all(SET_COOKIE)
        .iter()
        .filter_map(|header| header.to_str().ok())
        .filter(|header| {
            header
                .split_once('=')
                .is_some_and(|(name, _)| name.trim().eq_ignore_ascii_case(COOKIE_NAME))
        });
    let Some(header) = headers.next() else {
        return Ok(None);
    };
    if headers.next().is_some() {
        return Err(Failure::InvalidResponse(stage));
    }
    let cookie = Cookie::parse(header).map_err(|_| Failure::InvalidResponse(stage))?;
    if cookie.domain().is_some_and(|domain| {
        !domain.eq_ignore_ascii_case("roblox.com")
            && !response
                .url()
                .host_str()
                .is_some_and(|host| host.eq_ignore_ascii_case(domain))
    }) {
        return Err(Failure::InvalidResponse(stage));
    }
    let now = unix_seconds(stage)?;
    let expiry = match cookie.max_age() {
        Some(age) => Some(now.saturating_add(u64::try_from(age.whole_seconds()).unwrap_or(0))),
        None => cookie
            .expires_datetime()
            .map(|date| u64::try_from(date.unix_timestamp()).unwrap_or(0)),
    };
    if expiry.is_some_and(|expires| expires <= now) {
        return Err(Failure::SignInRequired(stage));
    }
    let mut candidate = Credential::new(Zeroizing::new(cookie.value_trimmed().to_owned()))
        .map_err(|_| Failure::SignInRequired(stage))?;
    candidate.expires = credential.expires;
    if let Some(expiry) = expiry {
        candidate.limit_expiry(expiry);
    }
    Ok(
        (candidate.value != credential.value || candidate.expires != credential.expires)
            .then_some(candidate),
    )
}

fn accepted(stage: &'static str, response: Response) -> Result<Response, Failure> {
    if response.headers().contains_key("rblx-challenge-id") {
        return Err(Failure::Challenge(stage));
    }
    match response.status() {
        status if status.is_success() => Ok(response),
        StatusCode::UNAUTHORIZED => Err(Failure::SignInRequired(stage)),
        StatusCode::TOO_MANY_REQUESTS => Err(Failure::RateLimited(
            stage,
            response
                .headers()
                .get(RETRY_AFTER)
                .and_then(|value| value.to_str().ok())
                .and_then(|value| value.parse().ok()),
        )),
        status => Err(Failure::Refused(stage, status.as_u16())),
    }
}

fn read_json<T: serde::de::DeserializeOwned>(
    response: Response,
    stage: &'static str,
) -> Result<T, Failure> {
    let mut bytes = Zeroizing::new(Vec::new());
    response
        .take(MAX_JSON + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| Failure::Network(stage))?;
    if bytes.len() as u64 > MAX_JSON {
        return Err(Failure::InvalidResponse(stage));
    }
    serde_json::from_slice(&bytes).map_err(|_| Failure::InvalidResponse(stage))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        io::{BufRead, BufReader, Write},
        net::TcpListener,
        thread,
        time::Instant,
    };

    struct Exchange {
        request: &'static str,
        cookie: &'static str,
        csrf: Option<&'static str>,
        status: &'static str,
        headers: &'static str,
        body: &'static str,
    }

    fn server(exchanges: Vec<Exchange>) -> (RobloxAuth, thread::JoinHandle<()>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        listener.set_nonblocking(true).unwrap();
        let worker = thread::spawn(move || {
            for expected in exchanges {
                let deadline = Instant::now() + Duration::from_secs(5);
                let (mut stream, _) = loop {
                    match listener.accept() {
                        Ok(connection) => break connection,
                        Err(error)
                            if error.kind() == std::io::ErrorKind::WouldBlock
                                && Instant::now() < deadline =>
                        {
                            thread::sleep(Duration::from_millis(5));
                        }
                        Err(error) => panic!("fixture accept failed: {error}"),
                    }
                };
                stream.set_nonblocking(false).unwrap();
                stream
                    .set_read_timeout(Some(Duration::from_secs(4)))
                    .unwrap();
                let mut reader = BufReader::new(&mut stream);
                let mut first = String::new();
                reader.read_line(&mut first).unwrap();
                assert_eq!(first.trim_end(), expected.request);
                let mut headers = String::new();
                loop {
                    let mut line = String::new();
                    reader.read_line(&mut line).unwrap();
                    if line.trim_end().is_empty() {
                        break;
                    }
                    headers.push_str(&line.to_ascii_lowercase());
                }
                assert!(
                    headers.contains(&format!("cookie: .roblosecurity={}\r\n", expected.cookie))
                );
                assert!(headers.contains("origin: https://www.roblox.com\r\n"));
                assert!(headers.contains("referer: https://www.roblox.com/\r\n"));
                if let Some(csrf) = expected.csrf {
                    assert!(headers.contains(&format!("x-csrf-token: {csrf}\r\n")));
                } else {
                    assert!(!headers.contains("x-csrf-token:"));
                }
                if let Some(size) = headers
                    .lines()
                    .find_map(|line| line.strip_prefix("content-length: "))
                {
                    let mut body = vec![0; size.parse().unwrap()];
                    reader.read_exact(&mut body).unwrap();
                    assert_eq!(
                        serde_json::from_slice::<Value>(&body).unwrap(),
                        serde_json::json!({"clientAssertion": "fixture"})
                    );
                }
                write!(stream, "HTTP/1.1 {}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n{}\r\n{}",
                    expected.status, expected.body.len(), expected.headers, expected.body).unwrap();
            }
        });
        let auth = RobloxAuth {
            http: Client::builder()
                .no_proxy()
                .timeout(Duration::from_secs(5))
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
            identity_url: format!("http://{address}/identity"),
            assertion_url: format!("http://{address}/assertion"),
            ticket_url: format!("http://{address}/ticket"),
            launch_permit: None,
        };
        (auth, worker)
    }

    #[test]
    fn a_stop_at_the_ticket_response_discards_it_without_sending_a_csrf_retry() {
        use std::sync::atomic::{AtomicUsize, Ordering};
        let (mut auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /assertion HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"clientAssertion":"fixture"}"#,
            },
            Exchange {
                request: "POST /ticket HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "403 Forbidden",
                headers: "x-csrf-token: must-not-retry\r\n",
                body: "{}",
            },
        ]);
        let checkpoints = AtomicUsize::new(0);
        auth.launch_permit = Some(Box::new(move || {
            checkpoints.fetch_add(1, Ordering::Relaxed) < 5
        }));
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        auth.verify(&mut credential, 42).unwrap();
        assert_eq!(auth.ticket(&mut credential), Err(Failure::Cancelled));
        worker.join().unwrap();
    }

    #[test]
    fn identity_response_cannot_install_a_replacement_for_another_account() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=foreign; Secure; HttpOnly\r\n",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "foreign",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":43}"#,
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        assert_eq!(
            auth.verify(&mut credential, 42),
            Err(Failure::IdentityChanged)
        );
        assert!(credential.value() == "original");
        assert!(!credential.has_verified_update(42));
        worker.join().unwrap();
    }

    #[test]
    fn a_second_distinct_replacement_during_confirmation_is_rejected() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=first\r\n",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "first",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=second\r\n",
                body: r#"{"id":42}"#,
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        assert_eq!(
            auth.verify(&mut credential, 42),
            Err(Failure::UnstableSignIn)
        );
        assert!(credential.value() == "original" && !credential.has_verified_update(42));
        worker.join().unwrap();
    }

    #[test]
    fn expiry_only_update_can_shorten_a_verified_sign_in_and_revocation_cannot_replace_it() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=original; Max-Age=3600; Expires=Wed, 01 Jan 2042 00:00:00 GMT\r\n",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /balance HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=revoked; Max-Age=-1\r\n",
                body: r#"{"robux":100}"#,
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        let now = unix_seconds("fixture").unwrap();
        credential.limit_expiry(now + 7200);
        auth.verify(&mut credential, 42).unwrap();
        assert!(credential.has_verified_update(42));
        assert!((now + 3600..=now + 3610).contains(&credential.expires_at().unwrap()));
        let url = auth.identity_url.replace("/identity", "/balance");
        assert_eq!(
            auth.read_profile::<Value>("Robux balance", &mut credential, &url),
            Err(Failure::SignInRequired("Robux balance"))
        );
        assert!(credential.value() == "original");
        assert!(!credential.has_verified_update(42));
        worker.join().unwrap();
    }

    #[test]
    fn authenticated_profile_read_uses_the_cookie_verified_for_this_account() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=verified; Secure; HttpOnly\r\n",
                body: r#"{"id":42,"name":"ZenithFixture"}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "verified",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42,"name":"ZenithFixture"}"#,
            },
            Exchange {
                request: "GET /balance HTTP/1.1",
                cookie: "verified",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"robux":0}"#,
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        auth.verify(&mut credential, 42).unwrap();
        let url = auth.identity_url.replace("/identity", "/balance");
        let balance: Value = auth
            .read_profile("Robux balance", &mut credential, &url)
            .unwrap();
        assert_eq!(balance["robux"], 0);
        worker.join().unwrap();
    }

    #[test]
    fn uses_cookie_rotations_across_identity_assertion_and_csrf_retry() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "original",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=identity-rotation; Path=/\r\n",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "identity-rotation",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /assertion HTTP/1.1",
                cookie: "identity-rotation",
                csrf: None,
                status: "200 OK",
                headers: "Set-Cookie: .ROBLOSECURITY=assertion-rotation; Path=/\r\n",
                body: r#"{"clientAssertion":"fixture"}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "assertion-rotation",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "POST /ticket HTTP/1.1",
                cookie: "assertion-rotation",
                csrf: None,
                status: "403 Forbidden",
                headers: "x-csrf-token: csrf-retry\r\nSet-Cookie: .ROBLOSECURITY=csrf-rotation; Path=/\r\n",
                body: "{}",
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "csrf-rotation",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "POST /ticket HTTP/1.1",
                cookie: "csrf-rotation",
                csrf: Some("csrf-retry"),
                status: "200 OK",
                headers: "rbx-authentication-ticket: short-lived-fixture\r\n",
                body: "{}",
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("original".into())).unwrap();
        auth.verify(&mut credential, 42).unwrap();
        assert_eq!(
            auth.ticket(&mut credential).unwrap().as_str(),
            "short-lived-fixture"
        );
        assert!(credential.has_verified_update(42));
        worker.join().unwrap();

        #[cfg(windows)]
        {
            use crate::windows::{SavedIdentity, open_saved_bundle, seal_saved_bundle};
            let identity = SavedIdentity {
                profile_id: uuid::Uuid::new_v4(),
                user_id: 42,
                username: "ZenithFixture".into(),
            };
            let expires = unix_seconds("fixture").unwrap() + 60 * 60;
            credential.limit_expiry(expires);
            let mut protected =
                seal_saved_bundle(&identity, &credential, expires + 60 * 60).unwrap();
            let (reopened_identity, reopened) = open_saved_bundle(&mut protected).unwrap();
            assert!(reopened_identity == identity);
            assert!(reopened.value() == "csrf-rotation");
            assert_eq!(reopened.expires_at(), Some(expires));
            assert!(!reopened.has_verified_update(42));
        }
    }

    #[test]
    fn mismatched_identity_and_account_challenge_are_distinct_failures() {
        let (auth, worker) = server(vec![
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "fixture",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":43}"#,
            },
            Exchange {
                request: "GET /identity HTTP/1.1",
                cookie: "fixture",
                csrf: None,
                status: "200 OK",
                headers: "",
                body: r#"{"id":42}"#,
            },
            Exchange {
                request: "GET /assertion HTTP/1.1",
                cookie: "fixture",
                csrf: None,
                status: "403 Forbidden",
                headers: "rblx-challenge-id: needs-owner\r\nx-csrf-token: must-not-retry\r\n",
                body: "{}",
            },
        ]);
        let mut credential = Credential::new(Zeroizing::new("fixture".into())).unwrap();
        assert_eq!(
            auth.verify(&mut credential, 42),
            Err(Failure::IdentityChanged)
        );
        assert_eq!(
            auth.ticket(&mut credential),
            Err(Failure::SignInRequired("client assertion"))
        );
        auth.verify(&mut credential, 42).unwrap();
        assert!(matches!(
            auth.ticket(&mut credential),
            Err(Failure::Challenge("client assertion"))
        ));
        worker.join().unwrap();
    }
}
