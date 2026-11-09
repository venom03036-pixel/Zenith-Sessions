use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use tokio::net::windows::named_pipe::NamedPipeClient;
use windows::Win32::{
    Foundation::{ERROR_SUCCESS, HANDLE, HLOCAL, LocalFree},
    Security::{
        Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT},
        EqualSid, GetTokenInformation, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID,
        TOKEN_QUERY, TOKEN_USER, TokenUser,
    },
    System::Threading::{GetCurrentProcess, OpenProcessToken},
};

struct Descriptor(PSECURITY_DESCRIPTOR);

impl Drop for Descriptor {
    fn drop(&mut self) {
        // SAFETY: GetSecurityInfo allocated this descriptor for LocalFree.
        if !self.0.0.is_null() {
            unsafe {
                let _ = LocalFree(Some(HLOCAL(self.0.0)));
            }
        }
    }
}

pub fn owned_by_current_user(pipe: &NamedPipeClient) -> bool {
    let mut token = HANDLE::default();
    // SAFETY: obtain a new query-only handle for the current process token.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) }.is_err() {
        return false;
    }
    let token = unsafe { OwnedHandle::from_raw_handle(token.0) };
    let handle = HANDLE(token.as_raw_handle());
    let mut needed = 0;
    // SAFETY: the first call asks only for the size of the TOKEN_USER structure and SID.
    let _ = unsafe { GetTokenInformation(handle, TokenUser, None, 0, &mut needed) };
    if (needed as usize) < size_of::<TOKEN_USER>() || needed > 4096 {
        return false;
    }
    let mut buffer = vec![0usize; (needed as usize).div_ceil(size_of::<usize>())];
    let data = buffer.as_mut_ptr().cast();
    if unsafe { GetTokenInformation(handle, TokenUser, Some(data), needed, &mut needed) }.is_err() {
        return false;
    }
    // SAFETY: GetTokenInformation filled the aligned buffer. The SID is borrowed from it.
    let user = unsafe { (*data.cast::<TOKEN_USER>()).User.Sid };

    let mut owner = PSID::default();
    let mut descriptor = Descriptor(PSECURITY_DESCRIPTOR::default());
    // SAFETY: the client owns this pipe handle and both output pointers are writable.
    let result = unsafe {
        GetSecurityInfo(
            HANDLE(pipe.as_raw_handle()),
            SE_KERNEL_OBJECT,
            OWNER_SECURITY_INFORMATION,
            Some(&mut owner),
            None,
            None,
            None,
            Some(&mut descriptor.0),
        )
    };
    // Both SIDs remain alive through the comparison. Shared group owners are refused.
    result == ERROR_SUCCESS
        && !user.0.is_null()
        && !owner.0.is_null()
        && unsafe { EqualSid(user, owner) }.is_ok()
}
