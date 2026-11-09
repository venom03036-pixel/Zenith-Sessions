fn main() {
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "workspace_snapshot",
            "import_account",
            "remove_account",
            "account_headshots",
            "account_profile",
            "account_favorites",
            "account_private_profile",
            "account_friends",
            "search_public_games",
            "browse_public_servers",
            "launch_account",
            "stop_session",
            "stop_all_sessions",
            "focus_session",
            "begin_sign_in",
            "finish_sign_in",
            "cancel_sign_in",
        ]),
    ))
    .expect("could not build Zenith Tauri configuration");
}
