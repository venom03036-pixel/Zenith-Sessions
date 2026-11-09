use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use windows::Win32::{
    Foundation::{HWND, LPARAM, POINT, RECT, WPARAM},
    Graphics::Gdi::{
        GetMonitorInfoW, HMONITOR, MONITOR_DEFAULTTONULL, MONITORINFO, MonitorFromWindow,
    },
    UI::{
        HiDpi::{
            DPI_AWARENESS_CONTEXT, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForWindow,
            GetSystemMetricsForDpi, SetThreadDpiAwarenessContext,
        },
        WindowsAndMessaging::{
            GA_ROOT, GW_OWNER, GWL_STYLE, GetAncestor, GetWindow, GetWindowLongW, GetWindowRect,
            GetWindowThreadProcessId, IsIconic, IsWindowVisible, IsZoomed, MINMAXINFO,
            PostMessageW, SC_MINIMIZE, SC_RESTORE, SM_CXMINTRACK, SM_CYMINTRACK, SMTO_ABORTIFHUNG,
            SMTO_BLOCK, SMTO_ERRORONEXIT, SWP_ASYNCWINDOWPOS, SWP_NOACTIVATE, SWP_NOOWNERZORDER,
            SWP_NOZORDER, SendMessageTimeoutW, SetWindowPos, WM_CLOSE, WM_GETMINMAXINFO,
            WM_SYSCOMMAND, WS_POPUP, WS_THICKFRAME,
        },
    },
};

use super::{OwnedPlayer, ProcessIdentity, current_process_identity};
use crate::layout::{Bounds, MAX_WINDOWS, plan_tiles};

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Placement {
    Placed,
    Changed,
    Rejected,
    Unconfirmed,
    Cancelled,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WindowVisibility {
    Minimize,
    Restore,
}

impl OwnedPlayer {
    fn pinned_visible_window(&self) -> Result<PinnedWindow, &'static str> {
        let handle = self
            .find_matching_window(false, true)
            .ok_or("This client needs one visible Roblox window.")?;
        let process = self
            .identity()
            .ok_or("The owned Roblox process is unavailable.")?;
        PinnedWindow::new(handle, process).ok_or("The owned Roblox window changed.")
    }

    pub fn request_window_close(&self, permitted: impl Fn() -> bool) -> Result<(), &'static str> {
        const CANCELLED: &str = "Zenith stopped this close request.";
        if !permitted() {
            return Err(CANCELLED);
        }
        let target = self.pinned_visible_window()?;
        if !target.visible() {
            return Err("The owned Roblox window changed.");
        }
        if !permitted() {
            return Err(CANCELLED);
        }
        // SAFETY: the HWND was revalidated against the owned process and window thread.
        // WM_CLOSE carries no pointers. Posting it leaves confirmation to the client.
        unsafe { PostMessageW(Some(target.handle), WM_CLOSE, WPARAM(0), LPARAM(0)) }
            .map_err(|_| "Windows declined the Roblox close request.")
    }

    pub fn set_window_visibility(
        &self,
        visibility: WindowVisibility,
        permitted: impl Fn() -> bool,
    ) -> Result<(), &'static str> {
        const CHANGED: &str = "The owned Roblox window changed.";
        const CANCELLED: &str = "Zenith stopped this window action.";
        if !permitted() {
            return Err(CANCELLED);
        }
        let target = self.pinned_visible_window()?;
        let minimize = matches!(visibility, WindowVisibility::Minimize);
        if target.minimized().ok_or(CHANGED)? == minimize {
            return permitted().then_some(()).ok_or(CANCELLED);
        }
        if !permitted() {
            return Err(CANCELLED);
        }
        let command = if minimize { SC_MINIMIZE } else { SC_RESTORE };
        // SAFETY: the target is a revalidated child-process window. This is a system
        // message with scalar arguments, sent from a worker with a bounded wait.
        let sent = unsafe {
            SendMessageTimeoutW(
                target.handle,
                WM_SYSCOMMAND,
                WPARAM(command as usize),
                LPARAM(0),
                SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT,
                300,
                None,
            )
        };
        if !permitted() {
            return Err(CANCELLED);
        }
        if sent.0 == 0 || target.minimized().ok_or(CHANGED)? != minimize {
            return Err("Windows did not confirm the window change.");
        }
        Ok(())
    }
}

pub fn tile_owned_players(
    anchor: isize,
    players: &[&OwnedPlayer],
    cancelled: impl Fn() -> bool,
) -> Result<Vec<Placement>, &'static str> {
    if players.is_empty() || players.len() > MAX_WINDOWS {
        return Err("Select at least one confirmed Roblox window.");
    }
    let _dpi = PhysicalDpi::enter()?;
    let mut targets = Vec::with_capacity(players.len());
    for player in players {
        if cancelled() {
            return Err("Window arrangement was cancelled.");
        }
        let window = player.find_window(true)
            .ok_or("Each selected client needs one resizable, restored Roblox window. Leave fullscreen and restore any minimized or maximized windows.")?;
        let target = PinnedWindow::new(
            window,
            player
                .identity()
                .ok_or("A Roblox process identity is unavailable.")?,
        )
        .filter(|target| normal_window(target.handle))
        .ok_or("A Roblox window changed before arrangement. Refresh and try again.")?;
        targets.push(target);
    }
    arrange(HWND(anchor as *mut _), &targets, cancelled)
}

pub(super) fn normal_window(window: HWND) -> bool {
    // SAFETY: these read window state and handle invalid or closed windows.
    unsafe {
        let style = GetWindowLongW(window, GWL_STYLE) as u32;
        style & WS_THICKFRAME.0 != 0
            && style & WS_POPUP.0 == 0
            && !IsIconic(window).as_bool()
            && !IsZoomed(window).as_bool()
            && visible_top_level(window)
    }
}

fn visible_top_level(window: HWND) -> bool {
    // SAFETY: these calls only query the supplied window.
    unsafe {
        IsWindowVisible(window).as_bool()
            && GetAncestor(window, GA_ROOT) == window
            && GetWindow(window, GW_OWNER).is_err()
    }
}

#[derive(Clone, Copy)]
struct PinnedWindow {
    handle: HWND,
    process: ProcessIdentity,
    thread: u32,
}

impl PinnedWindow {
    fn new(handle: HWND, process: ProcessIdentity) -> Option<Self> {
        // SAFETY: the API reads the thread identity of the supplied window.
        let thread = unsafe { GetWindowThreadProcessId(handle, None) };
        let window = Self {
            handle,
            process,
            thread,
        };
        window.matches_process().then_some(window)
    }

    fn current(self) -> bool {
        self.matches_process() && normal_window(self.handle)
    }

    fn matches_process(self) -> bool {
        let mut pid = 0;
        // SAFETY: the process output is writable for the lifetime of this call.
        let thread = unsafe { GetWindowThreadProcessId(self.handle, Some(&mut pid)) };
        thread != 0
            && thread == self.thread
            && pid == self.process.pid
            && current_process_identity(pid) == Some(self.process)
    }

    fn minimized(self) -> Option<bool> {
        self.visible().then(|| {
            // SAFETY: the HWND belongs to the expected top-level window.
            unsafe { IsIconic(self.handle) }.as_bool()
        })
    }

    fn visible(self) -> bool {
        self.matches_process() && visible_top_level(self.handle)
    }

    fn minimum(self, destination_dpi: u32) -> Option<(i32, i32)> {
        if !self.current() {
            return None;
        }
        // SAFETY: system metrics read only. Windows marshals this system-defined message
        // and uses the writable MINMAXINFO during this bounded call.
        let (min, window_dpi) = unsafe {
            let dpi = GetDpiForWindow(self.handle);
            if dpi == 0 {
                return None;
            }
            let mut info = MINMAXINFO {
                ptMinTrackSize: POINT {
                    x: GetSystemMetricsForDpi(SM_CXMINTRACK, dpi),
                    y: GetSystemMetricsForDpi(SM_CYMINTRACK, dpi),
                },
                ..Default::default()
            };
            let sent = SendMessageTimeoutW(
                self.handle,
                WM_GETMINMAXINFO,
                WPARAM(0),
                LPARAM((&raw mut info) as isize),
                SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT,
                100,
                None,
            );
            if sent.0 == 0 || info.ptMinTrackSize.x <= 0 || info.ptMinTrackSize.y <= 0 {
                return None;
            }
            (info.ptMinTrackSize, dpi)
        };
        let dpi = destination_dpi.max(window_dpi);
        Some((
            scale_up(min.x, dpi, window_dpi)?.max(scale_up(320, dpi, 96)?),
            scale_up(min.y, dpi, window_dpi)?.max(scale_up(240, dpi, 96)?),
        ))
    }
}

fn scale_up(value: i32, dpi: u32, baseline: u32) -> Option<i32> {
    let scaled = i64::from(value).checked_mul(i64::from(dpi))?;
    i32::try_from(scaled.checked_add(i64::from(baseline - 1))? / i64::from(baseline)).ok()
}

#[derive(PartialEq, Eq)]
struct WorkArea {
    monitor: HMONITOR,
    bounds: Bounds,
    dpi: u32,
}

fn work_area(anchor: HWND) -> Option<WorkArea> {
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    // SAFETY: the monitor information structure is writable and its ABI size is set.
    unsafe {
        let monitor = MonitorFromWindow(anchor, MONITOR_DEFAULTTONULL);
        let dpi = GetDpiForWindow(anchor);
        if monitor.0.is_null() || dpi == 0 || !GetMonitorInfoW(monitor, &mut info).as_bool() {
            return None;
        }
        Some(WorkArea {
            monitor,
            bounds: rect_bounds(info.rcWork)?,
            dpi,
        })
    }
}

fn rect_bounds(rect: RECT) -> Option<Bounds> {
    let width = rect.right.checked_sub(rect.left)?;
    let height = rect.bottom.checked_sub(rect.top)?;
    (width > 0 && height > 0).then_some(Bounds {
        x: rect.left,
        y: rect.top,
        width,
        height,
    })
}

fn observed_bounds(window: HWND) -> Option<Bounds> {
    let mut rect = RECT::default();
    // SAFETY: the output RECT stays live during the call.
    unsafe { GetWindowRect(window, &mut rect) }.ok()?;
    rect_bounds(rect)
}

fn arrange(
    anchor: HWND,
    targets: &[PinnedWindow],
    cancelled: impl Fn() -> bool,
) -> Result<Vec<Placement>, &'static str> {
    let work = work_area(anchor).ok_or("The monitor containing Zenith is unavailable.")?;
    let mut minimums = Vec::with_capacity(targets.len());
    for target in targets {
        if cancelled() {
            return Err("Window arrangement was cancelled.");
        }
        minimums.push(
            target
                .minimum(work.dpi)
                .ok_or("Windows could not confirm a selected window's minimum size.")?,
        );
    }
    let gap = scale_up(8, work.dpi, 96).ok_or("The display scale is unavailable.")?;
    let cells = plan_tiles(work.bounds, gap, &minimums)
        .ok_or("The monitor work area is too small for the selected window minimums.")?;

    // Preflight the full selection before posting the first move.
    if work_area(anchor).as_ref() != Some(&work) || targets.iter().any(|target| !target.current()) {
        return Err("The selected windows or monitor changed. Refresh and try again.");
    }
    let mut outcomes = Vec::with_capacity(targets.len());
    for (target, cell) in targets.iter().zip(&cells) {
        let outcome = if cancelled() {
            Placement::Cancelled
        } else if work_area(anchor).as_ref() != Some(&work) || !target.current() {
            Placement::Changed
        } else {
            // SAFETY: this is a revalidated owned top-level window. The async flag avoids
            // blocking on the target's queue; no flags change activation or stacking.
            let posted = unsafe {
                SetWindowPos(
                    target.handle,
                    None,
                    cell.x,
                    cell.y,
                    cell.width,
                    cell.height,
                    SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOZORDER,
                )
            };
            if posted.is_ok() {
                Placement::Unconfirmed
            } else {
                Placement::Rejected
            }
        };
        outcomes.push(outcome);
    }
    let deadline = Instant::now() + Duration::from_millis(600);
    while !cancelled() {
        for ((target, cell), outcome) in targets.iter().zip(&cells).zip(&mut outcomes) {
            if *outcome == Placement::Unconfirmed {
                if !target.current() || work_area(anchor).as_ref() != Some(&work) {
                    *outcome = Placement::Changed;
                } else if observed_bounds(target.handle) == Some(*cell) {
                    *outcome = Placement::Placed;
                }
            }
        }
        if !outcomes.contains(&Placement::Unconfirmed) || Instant::now() >= deadline {
            break;
        }
        std::thread::sleep(Duration::from_millis(25));
    }
    Ok(outcomes)
}

struct PhysicalDpi(DPI_AWARENESS_CONTEXT);

impl PhysicalDpi {
    fn enter() -> Result<Self, &'static str> {
        // SAFETY: changes only this worker thread, restored when the scope ends.
        let previous =
            unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        if previous.0.is_null() {
            Err("Windows could not enable physical display coordinates.")
        } else {
            Ok(Self(previous))
        }
    }
}

impl Drop for PhysicalDpi {
    fn drop(&mut self) {
        // SAFETY: this is the context that was returned for the same thread.
        let _ = unsafe { SetThreadDpiAwarenessContext(self.0) };
    }
}

#[cfg(test)]
mod tests {
    use std::{
        io::{BufRead, BufReader},
        os::windows::process::CommandExt,
        process::{Command, Stdio},
        sync::mpsc,
    };

    use windows::{
        Win32::{
            Foundation::LRESULT,
            UI::WindowsAndMessaging::{
                CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GWLP_WNDPROC,
                IsWindow, MSG, PM_REMOVE, PeekMessageW, SW_SHOWNOACTIVATE, SetWindowLongPtrW,
                ShowWindow, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_OVERLAPPEDWINDOW,
            },
        },
        core::w,
    };

    use super::*;
    use crate::windows::process_identity;

    const FIXTURE_ENV: &str = "ZENITH_LAYOUT_FIXTURE_CHILD";
    const TEST_NAME: &str = "windows::arrange::tests::native_layout_moves_owned_windows_only";

    unsafe extern "system" fn fixture_window_proc(
        window: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        if message == WM_GETMINMAXINFO {
            // SAFETY: this system message supplies a writable MINMAXINFO in the fixture process.
            unsafe { (*(lparam.0 as *mut MINMAXINFO)).ptMinTrackSize = POINT { x: 512, y: 300 } };
            return LRESULT(0);
        }
        // SAFETY: use Windows' standard processing for other messages.
        unsafe { DefWindowProcW(window, message, wparam, lparam) }
    }

    struct FixtureWindow(HWND);

    impl FixtureWindow {
        fn open() -> Self {
            // SAFETY: STATIC is a system class. The window is a fixture on this thread,
            // without a parent, and shown without taking foreground activation.
            let window = unsafe {
                let window = CreateWindowExW(
                    WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    w!("STATIC"),
                    w!("Zenith layout fixture"),
                    WS_OVERLAPPEDWINDOW,
                    50,
                    50,
                    640,
                    400,
                    None,
                    None,
                    None,
                    None,
                )
                .expect("create fixture window");
                let old = SetWindowLongPtrW(
                    window,
                    GWLP_WNDPROC,
                    fixture_window_proc as *const () as isize,
                );
                assert_ne!(old, 0);
                let _ = ShowWindow(window, SW_SHOWNOACTIVATE);
                window
            };
            Self(window)
        }
    }

    impl Drop for FixtureWindow {
        fn drop(&mut self) {
            // SAFETY: this fixture owns the window and stays on its creating thread.
            let _ = unsafe { DestroyWindow(self.0) };
        }
    }

    struct FixturePlayer(OwnedPlayer);

    impl FixturePlayer {
        fn start(windows: u8) -> Self {
            Self::with_mode(&windows.to_string())
        }

        fn with_mode(mode: &str) -> Self {
            let mut child = Command::new(std::env::current_exe().unwrap())
                .args(["--exact", TEST_NAME, "--nocapture"])
                .env(FIXTURE_ENV, mode)
                .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
                .stdout(Stdio::piped())
                .spawn()
                .unwrap();
            let stdout = child.stdout.take().unwrap();
            let fixture = Self(OwnedPlayer {
                identity: process_identity(&child),
                child,
                managed_priority: false,
                managed_efficiency: false,
            });
            let (ready, wait) = mpsc::sync_channel(1);
            std::thread::spawn(move || {
                for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                    if line == "zenith-layout-ready" {
                        let _ = ready.try_send(());
                    }
                }
            });
            wait.recv_timeout(Duration::from_secs(5))
                .expect("fixture opened its test window");
            fixture
        }
    }

    impl Drop for FixturePlayer {
        fn drop(&mut self) {
            let _ = self.0.stop();
        }
    }

    #[test]
    fn native_layout_moves_owned_windows_only() {
        let _dpi = PhysicalDpi::enter().unwrap();
        let anchor = FixtureWindow::open();
        if let Ok(count) = std::env::var(FIXTURE_ENV) {
            let _second = (count == "2").then(FixtureWindow::open);
            println!("zenith-layout-ready");
            if count == "stalled" {
                std::thread::sleep(Duration::from_secs(10));
                return;
            }
            let deadline = Instant::now() + Duration::from_secs(10);
            let mut message = MSG::default();
            while Instant::now() < deadline {
                // SAFETY: pump only this test thread's fixture window.
                while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
                    unsafe { DispatchMessageW(&message) };
                }
                if count == "close" && !unsafe { IsWindow(Some(anchor.0)) }.as_bool() {
                    return;
                }
                std::thread::sleep(Duration::from_millis(5));
            }
            return;
        }

        let first = FixturePlayer::start(1);
        let second = FixturePlayer::start(1);
        let ambiguous = FixturePlayer::start(2);
        assert!(first.0.has_unique_visible_window());
        assert!(second.0.has_unique_visible_window());
        assert!(ambiguous.0.has_window());
        assert!(!ambiguous.0.has_unique_visible_window());
        let players = [&first.0, &second.0];
        let anchor_before = observed_bounds(anchor.0).unwrap();
        let first_window = first.0.find_window(true).unwrap();
        let target = PinnedWindow::new(first_window, first.0.identity().unwrap()).unwrap();
        let dpi = work_area(anchor.0).unwrap().dpi;
        let minimum = target.minimum(dpi).unwrap();
        assert!(
            minimum.0 >= 512 && minimum.1 >= 300,
            "cross-process custom window minimum: {minimum:?}"
        );
        let layout = tile_owned_players(anchor.0.0 as isize, &players, || false).unwrap();
        assert!(layout.iter().all(|outcome| *outcome == Placement::Placed));
        let second_window = second.0.find_window(true).unwrap();
        let a = observed_bounds(first_window).unwrap();
        let b = observed_bounds(second_window).unwrap();
        assert_ne!(a, b);
        assert!(a.x + a.width <= b.x || a.y + a.height <= b.y);
        assert_eq!(observed_bounds(anchor.0), Some(anchor_before));

        let wrong_process = second.0.identity().unwrap();
        assert!(PinnedWindow::new(first_window, wrong_process).is_none());
        assert!(tile_owned_players(anchor.0.0 as isize, &players, || true).is_err());
        assert_eq!(observed_bounds(first_window), Some(a));
    }

    #[test]
    fn native_window_visibility_confirms_only_the_owned_unique_window() {
        use std::cell::Cell;

        let _dpi = PhysicalDpi::enter().unwrap();
        let mut target = FixturePlayer::start(1);
        let other = FixturePlayer::start(1);
        let ambiguous = FixturePlayer::start(2);
        let stalled = FixturePlayer::with_mode("stalled");
        let window = target.0.find_matching_window(false, true).unwrap();
        let other_window = other.0.find_matching_window(false, true).unwrap();
        let original = observed_bounds(window).unwrap();
        let checks = Cell::new(0);
        assert!(
            target
                .0
                .set_window_visibility(WindowVisibility::Minimize, || {
                    let previous = checks.replace(checks.get() + 1);
                    previous == 0
                })
                .is_err()
        );
        assert!(!unsafe { IsIconic(window) }.as_bool());
        assert!(
            ambiguous
                .0
                .set_window_visibility(WindowVisibility::Minimize, || true)
                .is_err()
        );
        target
            .0
            .set_window_visibility(WindowVisibility::Minimize, || true)
            .unwrap();
        assert!(unsafe { IsIconic(window) }.as_bool());
        assert!(!unsafe { IsIconic(other_window) }.as_bool());
        target
            .0
            .set_window_visibility(WindowVisibility::Restore, || true)
            .unwrap();
        assert!(!unsafe { IsIconic(window) }.as_bool());
        assert_eq!(observed_bounds(window), Some(original));

        let started = Instant::now();
        assert_eq!(
            stalled
                .0
                .set_window_visibility(WindowVisibility::Minimize, || true),
            Err("Windows did not confirm the window change.")
        );
        assert!(started.elapsed() < Duration::from_secs(2));
        target.0.stop().unwrap();
        assert!(
            target
                .0
                .set_window_visibility(WindowVisibility::Minimize, || true)
                .is_err()
        );
    }

    #[test]
    fn native_close_requests_only_the_exact_window_and_leaves_unresponsive_clients_alive() {
        let mut target = FixturePlayer::with_mode("close");
        let mut other = FixturePlayer::with_mode("close");
        let mut stalled = FixturePlayer::with_mode("stalled");
        let ambiguous = FixturePlayer::start(2);
        assert!(ambiguous.0.request_window_close(|| true).is_err());
        assert!(target.0.request_window_close(|| false).is_err());
        assert!(!target.0.exited().unwrap());
        stalled.0.request_window_close(|| true).unwrap();
        target.0.request_window_close(|| true).unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while !target.0.exited().unwrap() {
            assert!(
                Instant::now() < deadline,
                "the fixture did not handle WM_CLOSE"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
        assert!(target.0.exit_status().unwrap().unwrap().success());
        assert!(!other.0.exited().unwrap());
        assert!(!stalled.0.exited().unwrap());
        assert!(target.0.request_window_close(|| true).is_err());
    }
}
