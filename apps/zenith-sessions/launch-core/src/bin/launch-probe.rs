#[cfg(not(windows))]
fn main() {
    eprintln!("The launch probe requires Windows.");
    std::process::exit(1);
}

#[cfg(windows)]
fn main() -> std::process::ExitCode {
    match probe::run() {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("Probe stopped: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}

#[cfg(windows)]
mod probe {
    use std::{
        env,
        path::PathBuf,
        thread,
        time::{Duration, Instant, SystemTime, UNIX_EPOCH},
    };

    use uuid::Uuid;
    use zenith_session_launch::{
        auth::RobloxAuth,
        evidence::Observed,
        target::{Target, new_tracker, player_uri},
        windows::{OwnedPlayer, VerifiedPlayer, memory_sample, read_probe_credential},
    };

    pub fn run() -> Result<(), String> {
        let args: Vec<_> = env::args_os().skip(1).collect();
        if args.as_slice() == ["--inspect"] {
            read_admission()?;
            return Ok(());
        }
        if let [flag, path] = args.as_slice()
            && flag == "--player-check"
        {
            let player = select_player(path)?;
            println!(
                "Roblox player publisher verified: {}",
                player.path().display()
            );
            return Ok(());
        }
        if let [flag, path, username] = args.as_slice()
            && flag == "--bundle-check"
        {
            let username = username.to_str().ok_or("invalid username")?;
            let (user_id, _) = read_probe_credential(&PathBuf::from(path), username)?;
            println!(
                "Protected probe bundle matches the requested profile (Roblox user {user_id})."
            );
            return Ok(());
        }
        let [bundle, username, place, executable, logs, flags @ ..] = args.as_slice() else {
            return Err("Usage: launch-probe <protected-bundle> <username> <place-id> <--registered-player or RobloxPlayerBeta.exe> <Roblox logs directory> [--try-one]".into());
        };
        let try_one = match flags {
            [] => false,
            [flag] if flag == "--try-one" => true,
            _ => return Err("unknown probe argument".into()),
        };
        let username = username
            .to_str()
            .filter(|name| {
                !name.is_empty()
                    && name.len() <= 20
                    && name
                        .bytes()
                        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
            })
            .ok_or("invalid Roblox username")?;
        let place = place
            .to_str()
            .and_then(|id| id.parse().ok())
            .filter(|id| *id > 0)
            .ok_or("invalid place ID")?;
        let started = Instant::now();
        let attempt = Uuid::new_v4();
        println!("Attempt {attempt} for {username}, place {place}");
        check_memory(try_one)?;
        let player = select_player(executable)?;
        println!("Selected player {}", player.path().display());
        println!(
            "Roblox publisher confirmed at {}ms",
            started.elapsed().as_millis()
        );
        let (expected_user, mut credential) =
            read_probe_credential(&PathBuf::from(bundle), username)?;
        let auth = RobloxAuth::new().map_err(|error| error.to_string())?;
        auth.verify(&mut credential, expected_user)
            .map_err(|error| error.to_string())?;
        println!(
            "Saved account confirmed at {}ms",
            started.elapsed().as_millis()
        );
        let ticket = auth
            .ticket(&mut credential)
            .map_err(|error| error.to_string())?;
        drop(credential);
        println!(
            "Launch ticket received at {}ms",
            started.elapsed().as_millis()
        );
        check_memory(try_one)?;
        let tracker = new_tracker();
        println!("Launch tracker {tracker}");
        let launch_time = SystemTime::now();
        let timestamp = launch_time
            .duration_since(UNIX_EPOCH)
            .map_err(|_| "system clock is invalid")?;
        let uri = player_uri(
            &ticket,
            &Target { place, job: None },
            tracker,
            attempt,
            timestamp.as_millis(),
        );
        drop(ticket);
        let mut child = player.spawn(&uri)?;
        drop(uri);
        drop(player);
        println!(
            "Process started at {}ms (PID {})",
            started.elapsed().as_millis(),
            child.pid()
        );
        let result = observe(
            &mut child,
            PathBuf::from(logs),
            expected_user,
            place,
            tracker,
            launch_time,
            started,
        );
        child
            .stop()
            .map_err(|_| "could not stop the exact owned probe process")?;
        println!(
            "Owned probe process stopped at {}ms",
            started.elapsed().as_millis()
        );
        result
    }

    fn select_player(executable: &std::ffi::OsStr) -> Result<VerifiedPlayer, String> {
        if executable == "--registered-player" {
            VerifiedPlayer::registered()
        } else {
            VerifiedPlayer::open(&PathBuf::from(executable))
        }
    }

    fn check_memory(try_one: bool) -> Result<(), String> {
        let decision = read_admission()?;
        if decision.limiting_resource.is_none() || (try_one && decision.can_try_one) {
            return Ok(());
        }
        Err("resource admission held the launch".into())
    }

    fn read_admission() -> Result<zenith_session_launch::admission::Admission, String> {
        let decision = memory_sample()?.admission(Instant::now());
        println!(
            "Admission {}",
            serde_json::to_string(&decision).expect("fixed admission shape")
        );
        Ok(decision)
    }

    fn observe(
        child: &mut OwnedPlayer,
        logs: PathBuf,
        user: u64,
        place: u64,
        tracker: u64,
        launch_time: SystemTime,
        started: Instant,
    ) -> Result<(), String> {
        let identity = child
            .identity()
            .ok_or("could not identify the newly started process")?;
        println!(
            "Process receipt {}",
            serde_json::to_string(&identity).expect("fixed receipt shape")
        );
        let deadline = Instant::now() + Duration::from_secs(65);
        let mut window_seen = false;
        let mut latest = None;
        while Instant::now() < deadline {
            if child
                .exited()
                .map_err(|_| "could not query the owned process")?
            {
                return Err("the started process exited before the requested account and destination could be confirmed".into());
            }
            let has_window = child.has_window();
            if has_window && !window_seen {
                window_seen = true;
                println!(
                    "Owned client window visible at {}ms",
                    started.elapsed().as_millis()
                );
            }
            if let Some(facts) = Observed::for_attempt(&logs, tracker, launch_time) {
                let mismatch = facts.user_id.is_some_and(|observed| observed != user)
                    || facts.place_id.is_some_and(|observed| observed != place);
                if mismatch {
                    println!(
                        "Observed {}",
                        serde_json::to_string(&facts).expect("fixed evidence shape")
                    );
                    return Err(
                        "the client reported an account or place different from the request".into(),
                    );
                }
                if has_window
                    && facts.user_id == Some(user)
                    && facts.place_id == Some(place)
                    && facts.job_id.is_some()
                    && facts.connected
                {
                    println!(
                        "Confirmed arrival at {}ms: {}",
                        started.elapsed().as_millis(),
                        serde_json::to_string(&facts).expect("fixed evidence shape")
                    );
                    return Ok(());
                }
                latest = Some(facts);
            }
            thread::sleep(Duration::from_millis(500));
        }
        println!(
            "Last correlated evidence {}",
            serde_json::to_string(&latest).expect("fixed evidence shape")
        );
        Err("timed out waiting for an owned window and corroborated account and destination evidence".into())
    }
}
