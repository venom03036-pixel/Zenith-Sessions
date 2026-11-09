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
        ffi::OsStr,
        path::{Path, PathBuf},
        thread,
        time::{Duration, Instant, SystemTime, UNIX_EPOCH},
    };

    use uuid::Uuid;
    use zenith_session_launch::{
        auth::{Credential, RobloxAuth},
        evidence::Observed,
        target::{Target, new_tracker, player_uri},
        windows::{OwnedPlayer, PairLease, VerifiedPlayer, memory_sample, read_probe_credential},
    };

    struct Account<'a> {
        username: &'a str,
        user_id: u64,
        credential: Credential,
    }

    impl<'a> Account<'a> {
        fn load(bundle: &OsStr, username: &'a OsStr) -> Result<Self, String> {
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
            let (user_id, credential) = read_probe_credential(Path::new(bundle), username)?;
            Ok(Self {
                username,
                user_id,
                credential,
            })
        }
    }

    struct Trial {
        player: OwnedPlayer,
        started: Instant,
    }

    impl Trial {
        fn stop(&mut self) -> Result<(), String> {
            self.player
                .stop()
                .map_err(|_| format!("could not stop owned process {}", self.player.pid()))?;
            println!(
                "Owned probe process stopped at {}ms (PID {})",
                self.started.elapsed().as_millis(),
                self.player.pid()
            );
            Ok(())
        }
    }

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
        if args.first().is_some_and(|arg| arg == "--pair") {
            let [
                _,
                first,
                first_name,
                second,
                second_name,
                place,
                executable,
                logs,
            ] = args.as_slice()
            else {
                return Err("Usage: launch-probe --pair <bundle-a> <username-a> <bundle-b> <username-b> <place-id> <--registered-player or RobloxPlayerBeta.exe> <Roblox logs directory>".into());
            };
            let first = Account::load(first, first_name)?;
            let second = Account::load(second, second_name)?;
            if first.user_id == second.user_id {
                return Err(
                    "A pair trial requires two different verified Roblox identities.".into(),
                );
            }
            return run_pair(
                [first, second],
                parse_place(place)?,
                executable,
                Path::new(logs),
            );
        }
        let [bundle, username, place, executable, logs, flags @ ..] = args.as_slice() else {
            return Err("Usage: launch-probe <protected-bundle> <username> <place-id> <--registered-player or RobloxPlayerBeta.exe> <Roblox logs directory> [--try-one]".into());
        };
        let try_one = match flags {
            [] => false,
            [flag] if flag == "--try-one" => true,
            _ => return Err("unknown probe argument".into()),
        };
        let place = parse_place(place)?;
        check_memory(try_one)?;
        let player = select_player(executable)?;
        let account = Account::load(bundle, username)?;
        launch(account, place, &player, Path::new(logs), try_one)?.stop()
    }

    fn parse_place(place: &OsStr) -> Result<u64, String> {
        place
            .to_str()
            .and_then(|id| id.parse().ok())
            .filter(|id| *id > 0)
            .ok_or_else(|| "invalid place ID".into())
    }

    fn run_pair(
        accounts: [Account<'_>; 2],
        place: u64,
        executable: &OsStr,
        logs: &Path,
    ) -> Result<(), String> {
        check_memory(false)?;
        let player = select_player(executable)?;
        let _lease = PairLease::acquire()?;
        let mut trials = Vec::with_capacity(accounts.len());
        let result = (|| {
            for account in accounts {
                // One member reaches confirmed arrival before the next member is admitted.
                trials.push(launch(account, place, &player, logs, false)?);
            }
            let overlap_until = Instant::now() + Duration::from_secs(3);
            while Instant::now() < overlap_until {
                for trial in &mut trials {
                    if trial
                        .player
                        .exited()
                        .map_err(|_| "could not query an owned pair process")?
                        || !trial.player.has_window()
                    {
                        return Err(
                            "An owned client exited or lost its window during the overlap check."
                                .into(),
                        );
                    }
                }
                thread::sleep(Duration::from_millis(250));
            }
            println!(
                "Both distinct accounts arrived and their owned windows coexisted for three seconds."
            );
            Ok(())
        })();
        let mut failures = Vec::new();
        if let Err(error) = result {
            failures.push(error);
        }
        for trial in trials.iter_mut().rev() {
            if let Err(error) = trial.stop() {
                failures.push(error);
            }
        }
        if failures.is_empty() {
            Ok(())
        } else {
            Err(failures.join(". "))
        }
    }

    fn launch(
        mut account: Account<'_>,
        place: u64,
        player: &VerifiedPlayer,
        logs: &Path,
        try_one: bool,
    ) -> Result<Trial, String> {
        let started = Instant::now();
        let attempt = Uuid::new_v4();
        println!("Attempt {attempt} for {}, place {place}", account.username);
        check_memory(try_one)?;
        println!("Selected player {}", player.path().display());
        let auth = RobloxAuth::new().map_err(|error| error.to_string())?;
        auth.verify(&mut account.credential, account.user_id)
            .map_err(|error| error.to_string())?;
        println!(
            "Saved account confirmed at {}ms",
            started.elapsed().as_millis()
        );
        let ticket = auth
            .ticket(&mut account.credential)
            .map_err(|error| error.to_string())?;
        let expected_user = account.user_id;
        drop(account);
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
        let mut trial = Trial {
            player: player.spawn(&uri)?,
            started,
        };
        drop(uri);
        println!(
            "Process started at {}ms (PID {})",
            started.elapsed().as_millis(),
            trial.player.pid()
        );
        let result = observe(
            &mut trial.player,
            logs,
            expected_user,
            place,
            tracker,
            launch_time,
            started,
        );
        if let Err(error) = result {
            return match trial.stop() {
                Ok(()) => Err(error),
                Err(cleanup) => Err(format!("{error}. {cleanup}")),
            };
        }
        Ok(trial)
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
        logs: &Path,
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
            if let Some(facts) = Observed::for_attempt(logs, tracker, launch_time) {
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
