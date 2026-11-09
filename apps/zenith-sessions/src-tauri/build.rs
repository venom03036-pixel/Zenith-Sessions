fn main() {
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "workspace_snapshot",
            "import_account",
            "remove_account",
            "launch_account",
            "stop_session",
            "focus_session",
        ]),
    ))
    .expect("could not build Zenith Tauri configuration");
}
