use std::{
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    sync::{Arc, mpsc},
    thread::{self, JoinHandle},
};

use windows::{
    Win32::{
        Foundation::{HANDLE, WAIT_OBJECT_0},
        System::Threading::{CreateEventW, GetCurrentThreadId, INFINITE, SetEvent},
        UI::{
            Input::KeyboardAndMouse::{
                MOD_CONTROL, MOD_NOREPEAT, MOD_SHIFT, RegisterHotKey, UnregisterHotKey,
            },
            WindowsAndMessaging::{
                MSG, MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_NOREMOVE, PM_REMOVE,
                PeekMessageW, QS_HOTKEY, QS_POSTMESSAGE, WM_HOTKEY,
            },
        },
    },
    core::PCWSTR,
};

const ID: i32 = 1;

pub struct PanicHotkey {
    stop: Arc<OwnedHandle>,
    worker: Option<JoinHandle<()>>,
    #[cfg(test)]
    thread_id: u32,
}

impl PanicHotkey {
    pub fn start(key: u32, on_press: impl Fn() + Send + 'static) -> Option<Self> {
        // SAFETY: create a private, initially unsignaled event and transfer its handle once.
        let handle = unsafe { CreateEventW(None, true, false, PCWSTR::null()).ok()? };
        let stop = Arc::new(unsafe { OwnedHandle::from_raw_handle(handle.0) });
        let worker_stop = Arc::clone(&stop);
        let (ready, registered) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("zenith-panic-hotkey".into())
            .spawn(move || {
                let mut message = MSG::default();
                // SAFETY: only this thread registers and reads messages on its own queue.
                let _ = unsafe { PeekMessageW(&mut message, None, 0, 0, PM_NOREMOVE) };
                if unsafe { RegisterHotKey(None, ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, key) }
                    .is_err()
                {
                    return;
                }
                if ready.send(unsafe { GetCurrentThreadId() }).is_ok() {
                    let handles = [HANDLE(worker_stop.as_raw_handle())];
                    loop {
                        // SAFETY: the event is retained throughout this wait, and MSG is writable.
                        let wake = unsafe {
                            MsgWaitForMultipleObjectsEx(
                                Some(&handles),
                                INFINITE,
                                QS_HOTKEY | QS_POSTMESSAGE,
                                MWMO_INPUTAVAILABLE,
                            )
                        };
                        if wake.0 != WAIT_OBJECT_0.0 + 1 {
                            break;
                        }
                        if unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool()
                            && message.message == WM_HOTKEY
                            && message.wParam.0 == ID as usize
                        {
                            on_press();
                        }
                    }
                }
                // SAFETY: the hotkey is owned by this thread.
                if let Err(error) = unsafe { UnregisterHotKey(None, ID) } {
                    eprintln!("Could not release the Zenith panic shortcut: {error}");
                }
            })
            .ok()?;
        let Ok(_thread_id) = registered.recv() else {
            let _ = worker.join();
            return None;
        };
        Some(Self {
            stop,
            worker: Some(worker),
            #[cfg(test)]
            thread_id: _thread_id,
        })
    }

    pub fn is_running(&self) -> bool {
        self.worker
            .as_ref()
            .is_some_and(|worker| !worker.is_finished())
    }
}

impl Drop for PanicHotkey {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            // SAFETY: both owners retain the shutdown event until the worker exits.
            if let Err(error) = unsafe { SetEvent(HANDLE(self.stop.as_raw_handle())) } {
                eprintln!("Could not signal the Zenith panic shortcut to stop: {error}");
            }
            if worker.join().is_err() {
                eprintln!("The Zenith panic shortcut worker stopped unexpectedly.");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{panic_stop::PanicStop, session::SessionOwner};
    use std::{sync::Mutex, time::Duration};
    use windows::Win32::{
        Foundation::{LPARAM, WPARAM},
        UI::{Input::KeyboardAndMouse::VK_F24, WindowsAndMessaging::PostThreadMessageW},
    };

    #[test]
    fn registered_native_queue_routes_the_shortcut_and_releases_it_on_drop() {
        let (sent, received) = mpsc::channel();
        let key = u32::from(VK_F24.0);
        let control = Arc::new(PanicStop::default());
        let owner = Arc::new(Mutex::new(SessionOwner::default()));
        let old_permit = control.permit().unwrap();
        let worker_control = Arc::clone(&control);
        let worker_owner = Arc::clone(&owner);
        let worker = PanicHotkey::start(key, move || {
            let epoch = worker_control.trip();
            worker_control.drain(epoch, &worker_owner).unwrap();
            sent.send(epoch).unwrap();
        })
        .unwrap();
        assert!(worker.is_running());
        assert!(PanicHotkey::start(key, || {}).is_none());
        // SAFETY: post a fixture message to this test's own native queue, with no input injection.
        unsafe { PostThreadMessageW(worker.thread_id, WM_HOTKEY, WPARAM(ID as usize), LPARAM(0)) }
            .unwrap();
        let epoch = received.recv_timeout(Duration::from_secs(3)).unwrap();
        assert!(!control.permits(old_permit));
        assert!(control.permit().is_err());
        control.resume(epoch, &owner.lock().unwrap()).unwrap();
        drop(worker);
        assert!(PanicHotkey::start(key, || {}).is_some());
    }
}
