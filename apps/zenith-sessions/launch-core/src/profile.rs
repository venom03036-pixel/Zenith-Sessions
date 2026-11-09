use std::collections::{HashMap, HashSet};

use reqwest::{Url, blocking::RequestBuilder, header::ACCEPT};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use uuid::Uuid;

use crate::{
    auth::{Credential, RobloxAuth},
    directory::{Directory, Game, public_json, valid_cursor},
};

const PAGE_SIZE: usize = 25;
const BAD_PROFILE: &str = "Roblox returned an invalid profile response.";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Listing<T> {
    data: Vec<T>,
    next_page_cursor: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Page<T> {
    items: Vec<T>,
    next_cursor: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Person {
    id: String,
    name: String,
    display_name: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    user_id: String,
    display_name: String,
    description: String,
    created: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProfileResponse {
    id: u64,
    display_name: String,
    description: String,
    created: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Headshot {
    target_id: u64,
    state: String,
    image_url: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Favorite {
    name: String,
    root_place: NumericId,
}

#[derive(Deserialize)]
struct NumericId {
    id: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct UserName {
    id: u64,
    name: String,
    display_name: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct FriendsResponse {
    page_items: Vec<NumericId>,
    next_cursor: Option<String>,
}

#[derive(Deserialize)]
struct Balance {
    robux: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PrivateProfile {
    balance: Result<String, String>,
    friends: Result<Page<Person>, String>,
}

impl Directory {
    pub fn headshots(&self, saved_ids: &[(Uuid, u64)]) -> Result<HashMap<Uuid, String>, String> {
        let mut images = HashMap::new();
        for accounts in saved_ids.chunks(50) {
            let ids = accounts
                .iter()
                .map(|(_, id)| id.to_string())
                .collect::<Vec<_>>()
                .join(",");
            let mut url = Url::parse("https://thumbnails.roblox.com/v1/users/avatar-headshot")
                .expect("fixed headshot endpoint");
            url.query_pairs_mut().extend_pairs([
                ("userIds", ids.as_str()),
                ("size", "150x150"),
                ("format", "Png"),
                ("isCircular", "false"),
            ]);
            let response: Listing<Headshot> = request(self.http.get(url))?;
            images.extend(verified_headshots(accounts, response.data));
        }
        Ok(images)
    }

    pub fn public_profile(&self, id: u64) -> Result<Profile, String> {
        let response: ProfileResponse = request(
            self.http
                .get(format!("https://users.roblox.com/v1/users/{id}")),
        )?;
        if response.id != id
            || response.display_name.trim().is_empty()
            || response.display_name.chars().count() > 100
        {
            return Err(BAD_PROFILE.into());
        }
        Ok(Profile {
            user_id: id.to_string(),
            display_name: response.display_name,
            description: response.description.chars().take(1_000).collect(),
            created: response.created.chars().take(40).collect(),
        })
    }

    pub fn favorites(&self, id: u64, cursor: Option<&str>) -> Result<Page<Game>, String> {
        let url = page_url(
            &format!("https://games.roblox.com/v2/users/{id}/favorite/games"),
            cursor,
            &[("sortOrder", "Desc")],
        )?;
        let response: Listing<Favorite> = request(self.http.get(url))?;
        if response.data.len() > PAGE_SIZE {
            return Err(BAD_PROFILE.into());
        }
        let mut items = Vec::new();
        let mut places = HashSet::new();
        for favorite in response.data {
            if favorite.root_place.id <= 0
                || favorite.name.trim().is_empty()
                || favorite.name.chars().count() > 256
            {
                return Err(BAD_PROFILE.into());
            }
            if places.insert(favorite.root_place.id) {
                items.push(Game {
                    place_id: favorite.root_place.id.to_string(),
                    name: favorite.name,
                });
            }
        }
        Ok(Page {
            items,
            next_cursor: next_cursor(response.next_page_cursor, cursor)?,
        })
    }

    pub fn private_profile(
        &self,
        id: u64,
        credential: &mut Credential,
    ) -> Result<PrivateProfile, String> {
        let auth = verified_auth(id, credential)?;
        let balance = auth
            .read_profile::<Balance>(
                "Robux balance",
                credential,
                "https://economy.roblox.com/v1/user/currency",
            )
            .map(|value| value.robux.to_string())
            .map_err(|error| error.to_string());
        let friends = self.read_friends(&auth, id, credential, None);
        Ok(PrivateProfile { balance, friends })
    }

    pub fn friends(
        &self,
        id: u64,
        credential: &mut Credential,
        cursor: &str,
    ) -> Result<Page<Person>, String> {
        let auth = verified_auth(id, credential)?;
        self.read_friends(&auth, id, credential, Some(cursor))
    }

    fn read_friends(
        &self,
        auth: &RobloxAuth,
        id: u64,
        credential: &mut Credential,
        cursor: Option<&str>,
    ) -> Result<Page<Person>, String> {
        let url = friends_url(id, cursor)?;
        let response: FriendsResponse = auth
            .read_profile("Friends list", credential, url.as_str())
            .map_err(|error| error.to_string())?;
        let next_cursor = next_cursor(response.next_cursor, cursor)?;
        if response.page_items.len() > PAGE_SIZE {
            return Err(BAD_PROFILE.into());
        }
        let mut seen = HashSet::new();
        let ids: Vec<u64> = response
            .page_items
            .into_iter()
            .filter_map(|item| u64::try_from(item.id).ok())
            .filter(|id| *id > 0 && seen.insert(*id))
            .collect();
        let mut names = HashMap::new();
        if !ids.is_empty()
            && let Ok(response) = request::<Listing<UserName>>(
                self.http
                    .post("https://users.roblox.com/v1/users")
                    .json(&serde_json::json!({ "userIds": ids, "excludeBannedUsers": false })),
            )
        {
            names.extend(response.data.into_iter().map(|user| (user.id, user)));
        }
        let items = ids
            .into_iter()
            .map(|id| {
                let user = names.remove(&id);
                Person {
                    id: id.to_string(),
                    name: user
                        .as_ref()
                        .map(|user| user.name.chars().take(40).collect())
                        .unwrap_or_default(),
                    display_name: user
                        .map(|user| user.display_name.chars().take(100).collect())
                        .unwrap_or_default(),
                }
            })
            .collect();
        Ok(Page { items, next_cursor })
    }
}

fn friends_url(id: u64, cursor: Option<&str>) -> Result<Url, String> {
    page_url(
        &format!("https://friends.roblox.com/v1/users/{id}/friends/find"),
        cursor,
        &[],
    )
}

fn verified_auth(id: u64, credential: &mut Credential) -> Result<RobloxAuth, String> {
    let auth = RobloxAuth::new().map_err(|error| error.to_string())?;
    auth.verify(credential, id)
        .map_err(|error| error.to_string())?;
    Ok(auth)
}

fn request<T: DeserializeOwned>(request: RequestBuilder) -> Result<T, String> {
    let response = request
        .header(ACCEPT, "application/json")
        .send()
        .map_err(|_| "Could not reach Roblox.")?;
    public_json(response)
}

fn verified_headshots(accounts: &[(Uuid, u64)], rows: Vec<Headshot>) -> HashMap<Uuid, String> {
    rows.into_iter()
        .filter_map(|row| {
            if row.state != "Completed" {
                return None;
            }
            let (profile_id, _) = accounts.iter().find(|(_, id)| *id == row.target_id)?;
            let url = Url::parse(row.image_url.as_deref()?).ok()?;
            (url.scheme() == "https"
                && url
                    .host_str()
                    .is_some_and(|host| host.ends_with(".rbxcdn.com"))
                && url.port().is_none()
                && url.username().is_empty()
                && url.password().is_none()
                && url.query().is_none()
                && url.fragment().is_none())
            .then(|| (*profile_id, url.to_string()))
        })
        .collect()
}

fn page_url(endpoint: &str, cursor: Option<&str>, options: &[(&str, &str)]) -> Result<Url, String> {
    let mut url = Url::parse(endpoint).expect("fixed profile endpoint");
    let mut query = url.query_pairs_mut();
    query
        .append_pair("limit", &PAGE_SIZE.to_string())
        .extend_pairs(options);
    if let Some(cursor) = cursor {
        if !valid_cursor(cursor) {
            return Err("This page has expired. Refresh the account details.".into());
        }
        query.append_pair("cursor", cursor);
    }
    drop(query);
    Ok(url)
}

fn next_cursor(value: Option<String>, previous: Option<&str>) -> Result<Option<String>, String> {
    if value
        .as_deref()
        .is_some_and(|value| !valid_cursor(value) || Some(value) == previous)
    {
        return Err(BAD_PROFILE.into());
    }
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    #[ignore = "Explicit anonymous smoke check of Roblox's public profile"]
    fn public_roblox_profile_headshot_and_favorites_contract() {
        let directory = Directory::new().unwrap();
        let profile_id = Uuid::from_u128(1);
        let profile = directory.public_profile(1).unwrap();
        assert_eq!(profile.user_id, "1");
        assert!(
            directory
                .headshots(&[(profile_id, 1)])
                .unwrap()
                .contains_key(&profile_id)
        );
        assert!(directory.favorites(1, None).is_ok());
    }

    #[test]
    fn headshots_are_bound_to_saved_profiles_and_the_roblox_cdn() {
        let first = Uuid::from_u128(1);
        let second = Uuid::from_u128(2);
        let response: Listing<Headshot> = serde_json::from_str(
            r#"{"data":[
          {"targetId":12,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/verified"},
          {"targetId":13,"state":"Completed","imageUrl":"https://tr.rbxcdn.com.evil.test/foreign"},
          {"targetId":14,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/unselected"}]}"#,
        )
        .unwrap();
        let images = verified_headshots(&[(first, 12), (second, 13)], response.data);
        assert_eq!(
            images.get(&first).map(String::as_str),
            Some("https://tr.rbxcdn.com/verified")
        );
        assert_eq!(images.len(), 1);
    }

    #[test]
    fn friend_pages_use_roblox_cursors_and_encode_them_as_one_parameter() {
        let page: FriendsResponse =
            serde_json::from_str(r#"{"PageItems":[{"id":42}], "NextCursor":"a&b=c"}"#).unwrap();
        assert_eq!(page.page_items[0].id, 42);
        let cursor = page.next_cursor.as_deref();
        let url = friends_url(11, cursor).unwrap();
        assert_eq!(url.query(), Some("limit=25&cursor=a%26b%3Dc"));
        assert!(next_cursor(page.next_cursor.clone(), cursor).is_err());
        assert!(serde_json::from_str::<Balance>(r#"{"robux":-1}"#).is_err());
    }
}
