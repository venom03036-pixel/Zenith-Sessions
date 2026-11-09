#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::time::Instant;

use zenith_session_launch::{admission::Admission, windows::memory_sample};

#[tauri::command]
fn read_admission() -> Result<Admission, String> {
    Ok(memory_sample()?.admission(Instant::now()))
}

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![read_admission])
        .run(tauri::generate_context!())
        .expect("could not start Zenith Sessions diagnostics");
}
