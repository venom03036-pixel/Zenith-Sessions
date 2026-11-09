// Ports the public place and job envelope from Zenith's NativeRobloxHandoffRequest.
use std::fmt::Write;

use uuid::Uuid;
use zeroize::Zeroizing;

pub struct Target {
    pub place: u64,
    pub job: Option<Uuid>,
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
) -> Zeroizing<String> {
    let (request, job, origin) = match target.job {
        Some(job) => ("RequestGameJob", format!("&gameId={job}"), "JoinInstance"),
        None => ("RequestGame", String::new(), "PlayButton"),
    };
    let place = target.place;
    let destination = format!(
        "https://www.roblox.com/Game/PlaceLauncher.ashx?request={request}&browserTrackerId={tracker}&placeId={place}{job}&isPlayTogetherGame=false&joinAttemptId={attempt}&joinAttemptOrigin={origin}"
    );
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
    uri
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
        let target = Target {
            place: 110829983956014,
            job: Some(job),
        };
        let uri = player_uri("fixture+A/B=", &target, 12345678, Uuid::from_u128(2), 100);
        assert!(uri.starts_with("roblox-player:1+launchmode:play+gameinfo:fixture%2BA%2FB%3D+"));
        assert!(uri.contains(&format!("request%3DRequestGameJob%26browserTrackerId%3D12345678%26placeId%3D110829983956014%26gameId%3D{job}%26")));
        assert!(uri.contains("joinAttemptOrigin%3DJoinInstance+browsertrackerid:12345678+"));
    }
}
