use serde::Deserialize;
use zeroize::Zeroizing;

use super::{Credential, RobloxAuth, read_json};
use crate::private_invite::PrivateCode;

const SHARE_URL: &str = "https://apis.roblox.com/sharelinks/v1/resolve-link";
const STAGE: &str = "private invite resolution";
const INVALID: &str = "Roblox did not return a valid private-server destination for this invite.";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ShareResponse {
    private_server_invite_data: Option<PrivateShare>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PrivateShare {
    status: String,
    universe_id: Option<u64>,
    place_id: Option<u64>,
    link_code: Option<Zeroizing<String>>,
}

impl RobloxAuth {
    pub(crate) fn resolve_private_share(
        &self,
        credential: &mut Credential,
        share_code: &str,
    ) -> Result<(u64, Option<u64>, PrivateCode), String> {
        self.resolve_private_share_at(credential, share_code, SHARE_URL)
    }

    pub(super) fn resolve_private_share_at(
        &self,
        credential: &mut Credential,
        share_code: &str,
        url: &str,
    ) -> Result<(u64, Option<u64>, PrivateCode), String> {
        let body = serde_json::json!({"linkId": share_code, "linkType": "Server"});
        let response = self
            .post_with_csrf(STAGE, credential, url, &body)
            .map_err(|error| error.to_string())?;
        let result = read_json(response, STAGE).map_err(|error| error.to_string())?;
        private_destination(result)
    }
}

fn private_destination(response: ShareResponse) -> Result<(u64, Option<u64>, PrivateCode), String> {
    let data = response.private_server_invite_data.ok_or(INVALID)?;
    match data.status.as_str() {
        "Expired" => return Err("This Roblox private invite has expired.".into()),
        "Invalid" => {
            return Err("Roblox refused this private invite for the selected account.".into());
        }
        "Valid" => (),
        _ => return Err(INVALID.into()),
    }
    let valid_id = |id| (1..=i64::MAX as u64).contains(&id);
    let universe = data.universe_id.filter(|&id| valid_id(id)).ok_or(INVALID)?;
    if data.place_id.is_some_and(|id| !valid_id(id)) {
        return Err(INVALID.into());
    }
    let code = data
        .link_code
        .as_deref()
        .and_then(|code| PrivateCode::parse(code))
        .ok_or(INVALID)?;
    Ok((universe, data.place_id, code))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn private_resolution_never_uses_public_or_expired_results() {
        let valid = r#"{"privateServerInviteData":{"status":"Valid","universeId":32,"placeId":33,"linkCode":"0045"}}"#;
        let (universe, place, code) =
            private_destination(serde_json::from_str(valid).unwrap()).unwrap();
        assert_eq!((universe, place, code.as_str()), (32, Some(33), "0045"));
        for invalid in [
            valid.replace("\"Valid\"", "\"Expired\""),
            valid.replace("\"Valid\"", "\"Invalid\""),
            valid.replace("\"Valid\"", "\"Unknown\""),
            valid.replace("privateServerInviteData", "experienceDetailsInviteData"),
            valid.replace("\"0045\"", "\"0045&gameId=13\""),
            valid.replace("\"placeId\":33", "\"placeId\":0"),
        ] {
            assert!(private_destination(serde_json::from_str(&invalid).unwrap()).is_err());
        }
    }
}
