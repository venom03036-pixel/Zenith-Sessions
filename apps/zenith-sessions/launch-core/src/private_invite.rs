use reqwest::Url;
use serde::{Deserialize, Serialize};
use zeroize::Zeroizing;

use crate::{
    auth::{Credential, RobloxAuth},
    directory::{Directory, public_place},
};

const INVALID_INVITE: &str =
    "Paste a Roblox private-server invite or a Roblox share link with type=Server.";

#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct PrivateCode(Zeroizing<String>);

impl PrivateCode {
    pub(crate) fn parse(value: &str) -> Option<Self> {
        valid_private_code(value).then(|| Self(Zeroizing::new(value.to_owned())))
    }

    pub(crate) fn as_str(&self) -> &str {
        &self.0
    }
}

fn valid_private_code(value: &str) -> bool {
    !value.is_empty() && value.len() <= 64 && value.bytes().all(|byte| byte.is_ascii_digit())
}

fn valid_share_code(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
}

#[derive(Clone, Deserialize, PartialEq, Eq, Serialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum PrivateInvite {
    Direct { place: u64, code: PrivateCode },
    Share { code: Zeroizing<String> },
}

impl PrivateInvite {
    pub fn parse(input: &str) -> Result<Self, &'static str> {
        let input = input.trim();
        if input.len() > 768 || input.chars().any(char::is_control) {
            return Err(INVALID_INVITE);
        }
        let mut url = Url::parse(input).map_err(|_| INVALID_INVITE)?;
        if url.scheme() != "https"
            || !matches!(url.host_str(), Some("www.roblox.com" | "roblox.com"))
            || url.port().is_some()
            || !url.username().is_empty()
            || url.password().is_some()
            || url.fragment().is_some()
        {
            return Err(INVALID_INVITE);
        }
        let mut code = None;
        let mut server_type = false;
        let share = matches!(url.path(), "/share" | "/share/");
        for (name, value) in url.query_pairs() {
            match (share, name.as_ref()) {
                (true, "code") | (false, "privateServerLinkCode" | "linkCode")
                    if code.is_none() =>
                {
                    code = Some(Zeroizing::new(value.into_owned()));
                }
                (true, "type") if !server_type && value.eq_ignore_ascii_case("Server") => {
                    server_type = true;
                }
                _ => return Err(INVALID_INVITE),
            }
        }
        let code = code.ok_or(INVALID_INVITE)?;
        if share {
            return if server_type && valid_share_code(&code) {
                Ok(Self::Share { code })
            } else {
                Err(INVALID_INVITE)
            };
        }
        url.set_query(None);
        let place = public_place(url.as_str()).map_err(|_| INVALID_INVITE)?;
        let code = PrivateCode::parse(&code).ok_or(INVALID_INVITE)?;
        Ok(Self::Direct { place, code })
    }

    pub fn direct_place(&self) -> Option<u64> {
        match self {
            Self::Direct { place, .. } => Some(*place),
            Self::Share { .. } => None,
        }
    }

    pub fn code_in_label(&self, label: &str) -> bool {
        let code = match self {
            Self::Direct { code, .. } => code.as_str(),
            Self::Share { code } => code.as_str(),
        };
        label == code || (code.len() >= 8 && label.contains(code))
    }

    pub fn valid_for(&self, place: u64) -> bool {
        (1..=i64::MAX as u64).contains(&place)
            && match self {
                Self::Direct {
                    place: direct,
                    code,
                } => *direct == place && valid_private_code(code.as_str()),
                Self::Share { code } => valid_share_code(code),
            }
    }

    pub fn resolve(
        &self,
        auth: &RobloxAuth,
        credential: &mut Credential,
        directory: &Directory,
    ) -> Result<(u64, PrivateCode), String> {
        match self {
            Self::Direct { place, code } if self.valid_for(*place) => Ok((*place, code.clone())),
            Self::Share { code } if valid_share_code(code) => {
                let (universe, place, code) = auth.resolve_private_share(credential, code)?;
                let place = match place {
                    Some(place) => {
                        if directory.universe_for_place(place)? != universe {
                            return Err(
                                "Roblox returned inconsistent place information for this invite."
                                    .into(),
                            );
                        }
                        place
                    }
                    None => directory.root_place_for_universe(universe)?,
                };
                Ok((place, code))
            }
            _ => Err(INVALID_INVITE.into()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_explicit_roblox_private_invites_are_accepted() {
        assert_eq!(
            PrivateInvite::parse(
                "https://www.roblox.com/games/9007199254740999/Fixture?privateServerLinkCode=01234"
            )
            .unwrap()
            .direct_place(),
            Some(9007199254740999)
        );
        assert!(matches!(
            PrivateInvite::parse("https://roblox.com/share?type=Server&code=fixture-Ab12"),
            Ok(PrivateInvite::Share { .. })
        ));
        for link in [
            "https://roblox.com/share?type=ExperienceDetails&code=fixture",
            "https://roblox.com/share?type=Server&code=fixture&code=extra",
            "https://roblox.com/share?type=Server&code=fixture&v=2",
            "https://roblox.com/share?code=fixture",
            "https://roblox.com.evil.test/share?type=Server&code=fixture",
            "https://roblox.com/games/12?privateServerLinkCode=123&gameInstanceId=fixture",
            "https://roblox.com/games/12?privateServerLinkCode=123%26gameId%3D456",
            "https://roblox.com/games/12?linkCode=",
        ] {
            assert!(PrivateInvite::parse(link).is_err());
        }
    }
}
