use std::{
    collections::HashSet,
    io::Read,
    sync::Mutex,
    time::{Duration, Instant},
};

use reqwest::{
    StatusCode, Url,
    blocking::{Client, Response},
    header::{ACCEPT, CONTENT_TYPE, RETRY_AFTER},
};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use uuid::Uuid;

mod game_details;
pub use game_details::{GameArt, GameDetails};

const SEARCH_URL: &str = "https://apis.roblox.com/search-api/omni-search";
const PAGE_SIZE: usize = 25;
const MAX_RESPONSE: u64 = 512 * 1024;
const INVALID_RESPONSE: &str = "Roblox returned an unreadable public directory response.";

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    pub place_id: String,
    pub name: String,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Server {
    id: Uuid,
    playing: u32,
    max_players: u32,
    ping: Option<u32>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerPage {
    servers: Vec<Server>,
    next_cursor: Option<String>,
}

pub struct Directory {
    pub(crate) http: Client,
    search_cooldown: Mutex<Option<Instant>>,
    server_cooldown: Mutex<Option<Instant>>,
    game_details_cooldown: Mutex<Option<Instant>>,
    game_art_cooldown: Mutex<Option<Instant>>,
}

impl Directory {
    pub fn new() -> Result<Self, String> {
        let http = Client::builder()
            .user_agent("ZenithSessions/0.1")
            .connect_timeout(Duration::from_secs(8))
            .timeout(Duration::from_secs(12))
            .redirect(reqwest::redirect::Policy::none())
            .retry(reqwest::retry::never())
            .build()
            .map_err(|_| "Could not initialize the Roblox public directory")?;
        Ok(Self {
            http,
            search_cooldown: Mutex::new(None),
            server_cooldown: Mutex::new(None),
            game_details_cooldown: Mutex::new(None),
            game_art_cooldown: Mutex::new(None),
        })
    }

    pub fn search(&self, query: &str) -> Result<Vec<Game>, String> {
        let query = query.trim();
        if query.is_empty()
            || query.chars().count() > 100
            || query.chars().any(char::is_control)
            || query.contains(['/', '?', '#', '=', '\\'])
        {
            return Err(
                "Enter a game name, or paste a public game link in the destination field.".into(),
            );
        }
        let mut url = Url::parse(SEARCH_URL).expect("fixed search endpoint");
        url.query_pairs_mut()
            .append_pair("searchQuery", query)
            .append_pair("pageType", "all")
            .append_pair("sessionId", &Uuid::new_v4().to_string());
        let result = self.read(url, &self.search_cooldown)?;
        parse_games(result).map_err(str::to_owned)
    }

    pub fn servers(&self, place: u64, cursor: Option<&str>) -> Result<ServerPage, String> {
        if cursor.is_some_and(|value| !valid_cursor(value)) {
            return Err("The server page has expired. Browse again from the first page.".into());
        }
        let mut url = Url::parse(&format!(
            "https://games.roblox.com/v1/games/{place}/servers/Public"
        ))
        .expect("fixed server endpoint with numeric place");
        url.query_pairs_mut()
            .append_pair("sortOrder", "Asc")
            .append_pair("excludeFullGames", "true")
            .append_pair("limit", &PAGE_SIZE.to_string());
        if let Some(cursor) = cursor {
            url.query_pairs_mut().append_pair("cursor", cursor);
        }
        let result = self.read(url, &self.server_cooldown)?;
        parse_servers(result, cursor).map_err(str::to_owned)
    }

    fn read<T: DeserializeOwned>(
        &self,
        url: Url,
        cooldown: &Mutex<Option<Instant>>,
    ) -> Result<T, String> {
        let mut cooldown = cooldown
            .try_lock()
            .map_err(|_| "A Roblox directory request is already running.")?;
        let remaining = cooldown.and_then(|until| until.checked_duration_since(Instant::now()));
        if let Some(remaining) = remaining {
            return Err(format!(
                "Roblox rate-limited this directory. Retry in {} seconds.",
                remaining.as_secs().saturating_add(1)
            ));
        }
        let response = self
            .http
            .get(url)
            .header(ACCEPT, "application/json")
            .send()
            .map_err(|_| "Could not reach the Roblox public directory.")?;
        if response.status() == StatusCode::TOO_MANY_REQUESTS {
            let seconds = response
                .headers()
                .get(RETRY_AFTER)
                .and_then(|value| value.to_str().ok())
                .and_then(|value| value.parse::<u32>().ok())
                .unwrap_or(30)
                .max(1);
            *cooldown = Instant::now().checked_add(Duration::from_secs(u64::from(seconds)));
            return Err(format!(
                "Roblox rate-limited this directory. Retry in {seconds} seconds."
            ));
        }
        public_json(response)
    }
}

pub(crate) fn public_json<T: DeserializeOwned>(response: Response) -> Result<T, String> {
    if response.status() != StatusCode::OK {
        return Err(format!(
            "Roblox public directory is unavailable (HTTP {}).",
            response.status().as_u16()
        ));
    }
    if response
        .content_length()
        .is_some_and(|size| size > MAX_RESPONSE)
        || response
            .headers()
            .get(CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_none_or(|value| value.split(';').next() != Some("application/json"))
    {
        return Err(INVALID_RESPONSE.into());
    }
    let mut bytes = Vec::new();
    response
        .take(MAX_RESPONSE + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "Could not read the Roblox public directory response.")?;
    if bytes.len() as u64 > MAX_RESPONSE {
        return Err(INVALID_RESPONSE.into());
    }
    serde_json::from_slice(&bytes).map_err(|_| INVALID_RESPONSE.into())
}

pub(crate) fn public_image_url(value: &str) -> Option<String> {
    if value.len() > 2048 {
        return None;
    }
    let url = Url::parse(value).ok()?;
    (url.scheme() == "https"
        && url
            .host_str()
            .is_some_and(|host| host.ends_with(".rbxcdn.com"))
        && url.port().is_none()
        && url.username().is_empty()
        && url.password().is_none()
        && url.query().is_none()
        && url.fragment().is_none())
    .then(|| url.to_string())
}

pub fn public_place(input: &str) -> Result<u64, &'static str> {
    const INVALID_PLACE: &str =
        "Enter a place ID or a public Roblox game link without query parameters.";
    let input = input.trim();
    if input.len() > 512 || input.chars().any(char::is_control) {
        return Err(INVALID_PLACE);
    }
    if let Some(place) = numeric_place(input) {
        return Ok(place);
    }
    let url = Url::parse(input).map_err(|_| INVALID_PLACE)?;
    if url.scheme() != "https"
        || !matches!(url.host_str(), Some("www.roblox.com" | "roblox.com"))
        || url.port().is_some()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(INVALID_PLACE);
    }
    let mut segments = url.path_segments().ok_or(INVALID_PLACE)?;
    if segments.next() != Some("games") {
        return Err(INVALID_PLACE);
    }
    let place = segments
        .next()
        .and_then(numeric_place)
        .ok_or(INVALID_PLACE)?;
    if segments.count() > 1 {
        return Err(INVALID_PLACE);
    }
    Ok(place)
}

fn numeric_place(value: &str) -> Option<u64> {
    value
        .bytes()
        .all(|byte| byte.is_ascii_digit())
        .then(|| value.parse::<u64>().ok())?
        .filter(|place| (1..=i64::MAX as u64).contains(place))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SearchResponse {
    search_results: Vec<SearchGroup>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SearchGroup {
    content_group_type: String,
    contents: Vec<serde_json::Value>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SearchGame {
    root_place_id: u64,
    name: String,
}

fn parse_games(response: SearchResponse) -> Result<Vec<Game>, &'static str> {
    let mut seen = HashSet::new();
    let mut games = Vec::new();
    for group in response.search_results {
        if group.content_group_type != "Game" {
            continue;
        }
        for item in group.contents {
            let game: SearchGame = serde_json::from_value(item).map_err(|_| INVALID_RESPONSE)?;
            if !(1..=i64::MAX as u64).contains(&game.root_place_id)
                || game.name.trim().is_empty()
                || game.name.chars().count() > 256
                || game.name.chars().any(char::is_control)
            {
                return Err(INVALID_RESPONSE);
            }
            if seen.insert(game.root_place_id) && games.len() < 50 {
                games.push(Game {
                    place_id: game.root_place_id.to_string(),
                    name: game.name,
                });
            }
        }
    }
    Ok(games)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ServerResponse {
    data: Vec<Server>,
    next_page_cursor: Option<String>,
}

pub(crate) fn valid_cursor(cursor: &str) -> bool {
    !cursor.is_empty() && cursor.len() <= 4096 && cursor.bytes().all(|byte| byte.is_ascii_graphic())
}

fn parse_servers(
    response: ServerResponse,
    previous: Option<&str>,
) -> Result<ServerPage, &'static str> {
    let next = response.next_page_cursor.as_deref();
    if next.is_some_and(|cursor| !valid_cursor(cursor) || Some(cursor) == previous)
        || response.data.len() > PAGE_SIZE
    {
        return Err(INVALID_RESPONSE);
    }
    let mut seen = HashSet::new();
    let mut servers = Vec::new();
    for server in response.data {
        if server.id.is_nil() || server.max_players == 0 || server.playing > server.max_players {
            return Err(INVALID_RESPONSE);
        }
        if seen.insert(server.id) {
            servers.push(server);
        }
    }
    Ok(ServerPage {
        servers,
        next_cursor: response.next_page_cursor,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn public_destinations_preserve_exact_ids_and_reject_private_or_foreign_links() {
        assert_eq!(public_place("9007199254740999"), Ok(9007199254740999));
        assert_eq!(
            public_place("https://www.roblox.com/games/1818/Fixture"),
            Ok(1818)
        );
        for invalid in [
            "0",
            "+1818",
            "https://www.roblox.com.evil.example/games/1818",
            "https://evil.example@www.roblox.com/games/1818",
            "https://www.roblox.com/games/1818?privateServerLinkCode=fixture",
            "https://www.roblox.com/share?code=fixture",
            "https://www.roblox.com/games/1818/game/extra",
        ] {
            assert!(public_place(invalid).is_err());
        }
    }

    #[test]
    fn search_returns_only_unique_game_places_without_rounding_ids() {
        let response = serde_json::from_str(r#"{"searchResults":[
            {"contentGroupType":"User","contents":[{"name":"not a game"}]},
            {"contentGroupType":"Game","contents":[{"name":"Fixture","rootPlaceId":9007199254740999},
            {"name":"Fixture","rootPlaceId":9007199254740999}]}
        ]}"#).unwrap();
        let games = parse_games(response).unwrap();
        assert_eq!(games.len(), 1);
        assert_eq!(games[0].place_id, "9007199254740999");
    }

    #[test]
    fn server_pages_discard_unrequested_fields_and_reject_repeated_cursors() {
        let fixture = r#"{"data":[{"id":"00000000-0000-0000-0000-000000000001",
            "playing":3,"maxPlayers":5,"ping":14,"playerTokens":["fixture-token"]}],
            "nextPageCursor":"next+page/="}"#;
        let page = parse_servers(serde_json::from_str(fixture).unwrap(), None).unwrap();
        assert_eq!(page.servers[0].playing, 3);
        assert!(
            !serde_json::to_string(&page)
                .unwrap()
                .contains("fixture-token")
        );
        assert!(
            parse_servers(serde_json::from_str(fixture).unwrap(), Some("next+page/=")).is_err()
        );
        assert!(
            parse_servers(
                serde_json::from_str(&fixture.replace("\"playing\":3", "\"playing\":6")).unwrap(),
                None
            )
            .is_err()
        );
    }

    #[test]
    #[ignore = "Explicit anonymous Roblox endpoint smoke check"]
    fn live_public_anime_card_clash_directory() {
        let directory = Directory::new().unwrap();
        let games = directory.search("Anime Card Clash").unwrap();
        assert!(games.iter().any(|game| game.place_id == "110829983956014"));
        let page = directory.servers(110829983956014, None).unwrap();
        assert!(!page.servers.is_empty());
    }
}
