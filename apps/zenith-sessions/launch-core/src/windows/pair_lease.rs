use std::os::windows::io::{FromRawHandle, OwnedHandle};

use ::windows::{
    Win32::{
        Foundation::{ERROR_ALREADY_EXISTS, GetLastError},
        System::Threading::CreateMutexW,
    },
    core::{PCWSTR, w},
};

/// Holds the compatibility name for a clean, explicitly requested pair.
/// The lease never opens another process or displaces an existing object.
pub struct PairLease {
    _handle: OwnedHandle,
}

impl PairLease {
    pub fn acquire() -> Result<Self, &'static str> {
        Self::create(w!("ROBLOX_singletonEvent"))
    }

    fn create(name: PCWSTR) -> Result<Self, &'static str> {
        // SAFETY: name is NUL-terminated and remains alive throughout the call. The handle,
        // including a handle for an existing mutex, is closed by PairLease.
        let (lease, existed) = unsafe {
            let handle = CreateMutexW(None, false, name).map_err(|_| {
                "Could not reserve the Roblox compatibility name. Close other Roblox clients and launchers first."
            })?;
            let existed = GetLastError() == ERROR_ALREADY_EXISTS;
            (
                Self {
                    _handle: OwnedHandle::from_raw_handle(handle.0),
                },
                existed,
            )
        };
        if existed {
            return Err(
                "Another program already owns the compatibility name. Close it before retrying.",
            );
        }
        Ok(lease)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ::windows::{
        Win32::{Foundation::CloseHandle, System::Threading::CreateEventW},
        core::HSTRING,
    };

    #[test]
    fn an_existing_named_object_is_never_adopted_and_the_lease_is_reusable() {
        let name = HSTRING::from(format!("Local\\ZenithProbeTest-{}", uuid::Uuid::new_v4()));
        let name = PCWSTR(name.as_ptr());
        let lease = PairLease::create(name).unwrap();
        assert!(PairLease::create(name).is_err());
        drop(lease);
        // SAFETY: the name is backed by a live HSTRING. This test holds its own synthetic event.
        let event = unsafe { CreateEventW(None, false, false, name) }.unwrap();
        let blocked = PairLease::create(name).is_err();
        // SAFETY: event is the handle created by this test.
        unsafe { CloseHandle(event) }.unwrap();
        assert!(blocked);
        assert!(PairLease::create(name).is_ok());
    }
}
