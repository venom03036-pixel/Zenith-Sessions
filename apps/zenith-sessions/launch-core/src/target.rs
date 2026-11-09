// Ports the public place and job envelope from Zenith's NativeRobloxHandoffRequest.
use std::fmt::Write;

use uuid::Uuid;
use zeroize::Zeroizing;

use crate::{
    auth::{Credential, RobloxAuth},
    directory::Directory,
    private_invite::PrivateInvite,
};

pub struct Target {
    pub place: u64,
    server: Server,
}

enum Server {
    Public(Option<Uuid>),
    Private(PrivateInvite),
}

impl Target {
    pub fn public(place: u64, job: Option<Uuid>) -> Self {
        Self {
            place,
            server: Server::Public(job),
        }
    }

    pub fn private(place: u64, invite: PrivateInvite) -> Self {
        Self {
            place,
            server: Server::Private(invite),
        }
    }

    pub fn job(&self) -> Option<Uuid> {
        match self.server {
            Server::Public(job) => job,
            Server::Private(_) => None,
        }
    }

    pub fn is_private(&self) -> bool {
        matches!(self.server, Server::Private(_))
    }

    pub fn resolve(
        &mut self,
        auth: &RobloxAuth,
        credential: &mut Credential,
        directory: &Directory,
    ) -> Result<(), String> {
        if let Server::Private(invite) = &self.server {
            let (place, code) = invite.resolve(auth, credential, directory)?;
            if self.place != place {
                return Err("The private invite points to a different place. Save the current invite again.".into());
            }
            self.server = Server::Private(PrivateInvite::Direct { place, code });
        }
        Ok(())
    }
}

pub fn new_tracker() -> u64 {
    const MASK: u64 = (1 << 53) - 1;
    (Uuid::new_v4().as_u64_pair().1 & MASK).max(1)
}

pub fn player_uri(
    ticket: &str,
    target: &Target,
    tracker: u64,
    attempt: Uuid,
    timestamp_ms: u128,
) -> Result<Zeroizing<String>, &'static str> {
    let (request, origin) = match target.server {
        Server::Public(Some(_)) => ("RequestGameJob", "JoinInstance"),
        Server::Public(None) => ("RequestGame", "PlayButton"),
        Server::Private(_) => ("RequestPrivateGame", "PlayButton"),
    };
    let place = target.place;
    let mut destination = Zeroizing::new(format!(
        "https://www.roblox.com/Game/PlaceLauncher.ashx?request={request}&browserTrackerId={tracker}&placeId={place}"
    ));
    match &target.server {
        Server::Public(job) => {
            if let Some(job) = job {
                write!(destination, "&gameId={job}").unwrap();
            }
            destination.push_str("&isPlayTogetherGame=false");
        }
        Server::Private(invite @ PrivateInvite::Direct { code, .. }) if invite.valid_for(place) => {
            destination.push_str("&linkCode=");
            destination.push_str(code.as_str());
        }
        Server::Private(_) => {
            return Err("The private invite has not resolved to the requested place.");
        }
    }
    write!(
        destination,
        "&joinAttemptId={attempt}&joinAttemptOrigin={origin}"
    )
    .unwrap();
    let mut uri = Zeroizing::new(String::with_capacity(
        3 * (ticket.len() + destination.len()) + 256,
    ));
    uri.push_str("roblox-player:1+launchmode:play+gameinfo:");
    encode(&mut uri, ticket);
    write!(uri, "+launchtime:{timestamp_ms}+placelauncherurl:").unwrap();
    encode(&mut uri, &destination);
    write!(
        uri,
        "+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+LaunchExp:InApp+channel:"
    )
    .unwrap();
    Ok(uri)
}

fn encode(output: &mut String, input: &str) {
    for byte in input.bytes() {
        if byte.is_ascii_alphanumeric() || b"-_.~".contains(&byte) {
            output.push(char::from(byte));
        } else {
            write!(output, "%{byte:02X}").unwrap();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_job_and_reserved_characters_survive_native_uri_encoding() {
        let job = Uuid::from_u128(1);
        let target = Target::public(110829983956014, Some(job));
        let uri = player_uri("fixture+A/B=", &target, 12345678, Uuid::from_u128(2), 100).unwrap();
        assert!(uri.starts_with("roblox-player:1+launchmode:play+gameinfo:fixture%2BA%2FB%3D+"));
        assert!(uri.contains(&format!("request%3DRequestGameJob%26browserTrackerId%3D12345678%26placeId%3D110829983956014%26gameId%3D{job}%26")));
        assert!(uri.contains("joinAttemptOrigin%3DJoinInstance+browsertrackerid:12345678+"));
    }

    #[test]
    fn private_request_has_no_public_job_or_fallback_and_refuses_an_unresolved_invite() {
        let attempt = Uuid::from_u128(2);
        let invite =
            PrivateInvite::parse("https://roblox.com/games/12?privateServerLinkCode=0045").unwrap();
        let target = Target::private(12, invite.clone());
        let uri = player_uri("fixture", &target, 1001, attempt, 100).unwrap();
        assert!(uri.contains("request%3DRequestPrivateGame%26browserTrackerId%3D1001%26placeId%3D12%26linkCode%3D0045%26"));
        for field in [
            "gameId",
            "accessCode",
            "isPlayTogetherGame",
            "RequestGameJob",
        ] {
            assert!(!uri.contains(field));
        }
        assert!(player_uri("fixture", &Target::private(13, invite), 1001, attempt, 100).is_err());
        let share =
            PrivateInvite::parse("https://roblox.com/share?code=fixture&type=Server").unwrap();
        assert!(player_uri("fixture", &Target::private(12, share), 1001, attempt, 100).is_err());
    }
}
