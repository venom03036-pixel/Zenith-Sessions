use ::windows::{
    Win32::{
        Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE},
        System::Threading::CreateMutexW,
    },
    core::{PCWSTR, w},
};

/// Holds the compatibility name for the duration of a clean, explicitly requested pair trial.
/// The lease never opens another process or displaces an existing object.
pub struct PairLease(HANDLE);

impl PairLease {
    pub fn acquire() -> Result<Self, &'static str> {
        Self::create(w!("ROBLOX_singletonEvent"))
    }

    fn create(name: PCWSTR) -> Result<Self, &'static str> {
        // SAFETY: name is NUL-terminated and remains alive throughout the call. The handle,
        // including a handle for an existing mutex, is closed by PairLease.
        let (lease, existed) = unsafe {
            let handle = CreateMutexW(None, false, name).map_err(|_| {
                "Could not reserve the Roblox compatibility name. Close existing Roblox clients before the pair trial."
            })?;
            let existed = GetLastError() == ERROR_ALREADY_EXISTS;
            (Self(handle), existed)
        };
        if existed {
            return Err(
                "Another program already owns the compatibility name. Close it before retrying.",
            );
        }
        Ok(lease)
    }
}

impl Drop for PairLease {
    fn drop(&mut self) {
        // SAFETY: this instance owns exactly one Windows handle.
        let _ = unsafe { CloseHandle(self.0) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ::windows::{Win32::System::Threading::CreateEventW, core::HSTRING};

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
