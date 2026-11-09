use std::collections::HashSet;

use reqwest::Url;
use serde::{Deserialize, Serialize};

use super::{Directory, INVALID_RESPONSE, numeric_place, public_image_url};

const MAX_THUMBNAILS: usize = 6;
const UNAVAILABLE: &str = "Roblox did not return public details for this place.";

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameDetails {
    requested_place_id: String,
    root_place_id: String,
    universe_id: String,
    name: String,
    description: String,
    creator: String,
    playing: String,
    visits: Option<String>,
    max_players: u32,
}

#[derive(Serialize)]
pub struct GameArt {
    icon: Result<Option<String>, String>,
    thumbnails: Result<Vec<String>, String>,
}

#[derive(Deserialize)]
struct Listing<T> {
    data: Vec<T>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PlaceMapping {
    universe_id: Option<u64>,
    place_id: Option<u64>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct GameRecord {
    id: u64,
    root_place_id: u64,
    name: String,
    description: String,
    creator: Creator,
    playing: u64,
    visits: Option<u64>,
    max_players: u32,
    #[serde(default)]
    is_content_restricted: bool,
}

#[derive(Deserialize)]
struct Creator {
    name: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Thumbnail {
    target_id: u64,
    state: String,
    image_url: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Gallery {
    universe_id: u64,
    thumbnails: Vec<Thumbnail>,
}

impl Directory {
    pub fn game_details(&self, place: u64) -> Result<GameDetails, String> {
        let mapping: PlaceMapping = self.read(
            endpoint(&format!(
                "https://apis.roblox.com/universes/v1/places/{place}/universe"
            )),
            &self.game_details_cooldown,
        )?;
        if mapping.place_id.is_some_and(|id| id != place) {
            return Err(INVALID_RESPONSE.into());
        }
        let universe = mapping
            .universe_id
            .filter(|&id| valid_id(id))
            .ok_or(UNAVAILABLE)?;
        let listing = self.read(
            endpoint(&format!(
                "https://games.roblox.com/v1/games?universeIds={universe}"
            )),
            &self.game_details_cooldown,
        )?;
        parse_details(place, universe, listing).map_err(str::to_owned)
    }

    pub fn game_art(&self, universe: &str) -> Result<GameArt, String> {
        let universe = numeric_place(universe).ok_or("Select a valid public experience.")?;
        let icon = self
            .read(
                endpoint(&format!("https://thumbnails.roblox.com/v1/games/icons?universeIds={universe}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false")),
                &self.game_art_cooldown,
            )
            .and_then(|listing| parse_icon(universe, listing).map_err(str::to_owned));
        let thumbnails = self
            .read(
                endpoint(&format!("https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={universe}&countPerUniverse={MAX_THUMBNAILS}&defaults=false&size=576x324&format=Png&isCircular=false")),
                &self.game_art_cooldown,
            )
            .and_then(|listing| parse_gallery(universe, listing).map_err(str::to_owned));
        Ok(GameArt { icon, thumbnails })
    }
}

fn endpoint(value: &str) -> Url {
    Url::parse(value).expect("fixed public game endpoint with validated numeric ID")
}

fn valid_id(id: u64) -> bool {
    (1..=i64::MAX as u64).contains(&id)
}

fn valid_name(name: &str, max: usize) -> bool {
    !name.trim().is_empty() && name.chars().count() <= max && !name.chars().any(char::is_control)
}

fn parse_details(
    place: u64,
    universe: u64,
    listing: Listing<GameRecord>,
) -> Result<GameDetails, &'static str> {
    let game = match listing.data.as_slice() {
        [] => return Err(UNAVAILABLE),
        [game] if game.is_content_restricted && (game.id == universe || game.id == 0) => {
            return Err(UNAVAILABLE);
        }
        [game]
            if game.id == universe
                && valid_id(game.root_place_id)
                && game.max_players != 0
                && valid_name(&game.name, 256)
                && valid_name(&game.creator.name, 100) =>
        {
            game
        }
        _ => return Err(INVALID_RESPONSE),
    };
    let description = game
        .description
        .chars()
        .filter(|character| !character.is_control() || *character == '\n')
        .take(3_000)
        .collect();
    Ok(GameDetails {
        requested_place_id: place.to_string(),
        root_place_id: game.root_place_id.to_string(),
        universe_id: universe.to_string(),
        name: game.name.clone(),
        description,
        creator: game.creator.name.clone(),
        playing: game.playing.to_string(),
        visits: game.visits.map(|visits| visits.to_string()),
        max_players: game.max_players,
    })
}

fn parse_icon(universe: u64, listing: Listing<Thumbnail>) -> Result<Option<String>, &'static str> {
    match listing.data.as_slice() {
        [] => Ok(None),
        [row] if row.target_id == universe => Ok(completed_image(row)),
        _ => Err(INVALID_RESPONSE),
    }
}

fn parse_gallery(universe: u64, listing: Listing<Gallery>) -> Result<Vec<String>, &'static str> {
    match listing.data.as_slice() {
        [] => Ok(Vec::new()),
        [gallery]
            if gallery.universe_id == universe && gallery.thumbnails.len() <= MAX_THUMBNAILS =>
        {
            let mut seen = HashSet::new();
            Ok(gallery
                .thumbnails
                .iter()
                .filter(|row| valid_id(row.target_id))
                .filter_map(completed_image)
                .filter(|image| seen.insert(image.clone()))
                .collect())
        }
        _ => Err(INVALID_RESPONSE),
    }
}

fn completed_image(row: &Thumbnail) -> Option<String> {
    if row.state != "Completed" {
        return None;
    }
    public_image_url(row.image_url.as_deref()?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requested_subplace_stays_separate_from_the_root_and_restricted_data_is_refused() {
        let game = r#"{"data":[{"id":12,"rootPlaceId":13,"name":"Fixture","description":"Fixture",
            "creator":{"name":"Studio"},"playing":1,"maxPlayers":12,"visits":9007199254740999}]}"#;
        let details =
            parse_details(9007199254740997, 12, serde_json::from_str(game).unwrap()).unwrap();
        assert_eq!(details.requested_place_id, "9007199254740997");
        assert_eq!(details.root_place_id, "13");
        assert_eq!(details.visits.as_deref(), Some("9007199254740999"));
        assert!(parse_details(1, 99, serde_json::from_str(game).unwrap()).is_err());
        let restricted = game.replace("\"id\":12", "\"id\":0,\"isContentRestricted\":true");
        assert_eq!(
            parse_details(1, 12, serde_json::from_str(&restricted).unwrap()).err(),
            Some(UNAVAILABLE)
        );
    }

    #[test]
    fn artwork_requires_the_requested_universe_and_completed_roblox_cdn_images() {
        let icon = r#"{"data":[{"targetId":12,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/icon"}]}"#;
        assert!(parse_icon(11, serde_json::from_str(icon).unwrap()).is_err());
        assert_eq!(
            parse_icon(12, serde_json::from_str(icon).unwrap())
                .unwrap()
                .as_deref(),
            Some("https://tr.rbxcdn.com/icon")
        );
        let gallery = r#"{"data":[{"universeId":12,"thumbnails":[
            {"targetId":50,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/approved"},
            {"targetId":51,"state":"Pending","imageUrl":"https://tr.rbxcdn.com/pending"},
            {"targetId":52,"state":"Completed","imageUrl":"https://tr.rbxcdn.com.evil.test/foreign"},
            {"targetId":53,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/approved"}]}]}"#;
        assert!(parse_gallery(11, serde_json::from_str(gallery).unwrap()).is_err());
        assert_eq!(
            parse_gallery(12, serde_json::from_str(gallery).unwrap()).unwrap(),
            ["https://tr.rbxcdn.com/approved"]
        );
    }

    #[test]
    #[ignore = "Explicit anonymous Roblox game details and artwork smoke check"]
    fn anonymous_public_game_details_and_artwork() {
        let directory = Directory::new().unwrap();
        let details = directory.game_details(110829983956014).unwrap();
        assert_eq!(details.requested_place_id, "110829983956014");
        assert_eq!(details.universe_id, "6682487255");
        let art = directory.game_art(&details.universe_id).unwrap();
        assert!(art.icon.unwrap().is_some());
        assert!(!art.thumbnails.unwrap().is_empty());
    }
}
