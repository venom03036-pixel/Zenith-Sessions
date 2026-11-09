use std::path::Path;

use ::windows::{
    Win32::{
        Foundation::{HANDLE, HWND, INVALID_HANDLE_VALUE},
        Security::{
            Cryptography::{CERT_NAME_ATTR_TYPE, CertGetNameStringW, szOID_ORGANIZATION_NAME},
            WinTrust::{
                WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_DATA_0,
                WINTRUST_FILE_INFO, WINTRUST_SIGNATURE_SETTINGS, WSS_VERIFY_SPECIFIC,
                WTD_CHOICE_FILE, WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT, WTD_REVOKE_WHOLECHAIN,
                WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY, WTD_UI_NONE,
                WTHelperGetProvCertFromChain, WTHelperGetProvSignerFromChain,
                WTHelperProvDataFromStateData, WinVerifyTrust,
            },
        },
    },
    core::{HSTRING, PCWSTR},
};

pub fn verify(path: &Path, handle: HANDLE) -> Result<(), String> {
    // Use the primary signature and the verified certificate's organization, as in
    // Zenith's WindowsClientExecutableTrust. Keep the verification bound to the held file.
    let wide = HSTRING::from(path.as_os_str());
    let mut file = WINTRUST_FILE_INFO {
        cbStruct: size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: PCWSTR(wide.as_ptr()),
        hFile: handle,
        ..Default::default()
    };
    let mut signature = WINTRUST_SIGNATURE_SETTINGS {
        cbStruct: size_of::<WINTRUST_SIGNATURE_SETTINGS>() as u32,
        dwFlags: WSS_VERIFY_SPECIFIC,
        ..Default::default()
    };
    let mut data = WINTRUST_DATA {
        cbStruct: size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_WHOLECHAIN,
        dwUnionChoice: WTD_CHOICE_FILE,
        Anonymous: WINTRUST_DATA_0 { pFile: &mut file },
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
        pSignatureSettings: &mut signature,
        ..Default::default()
    };
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    let hwnd = HWND(INVALID_HANDLE_VALUE.0);
    // SAFETY: all structures, the pinned file handle and its path outlive both WinVerifyTrust calls.
    let (status, publisher_is_roblox, close_status) = unsafe {
        let status = WinVerifyTrust(hwnd, &mut action, (&raw mut data).cast());
        let publisher_is_roblox = status == 0 && primary_signer_is_roblox(data.hWVTStateData);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        let close_status = WinVerifyTrust(hwnd, &mut action, (&raw mut data).cast());
        (status, publisher_is_roblox, close_status)
    };
    if status != 0 {
        return Err(format!(
            "Roblox player signature verification failed (0x{:08X})",
            status as u32
        ));
    }
    if close_status != 0 {
        return Err("Windows could not release the player verification state".into());
    }
    if !publisher_is_roblox {
        return Err("the selected executable is not signed by Roblox Corporation".into());
    }
    Ok(())
}

/// The caller owns a live WinTrust provider state from successful primary-signature verification.
unsafe fn primary_signer_is_roblox(state: HANDLE) -> bool {
    const EXPECTED: &str = "Roblox Corporation";
    unsafe {
        let provider = WTHelperProvDataFromStateData(state);
        if provider.is_null() {
            return false;
        }
        let signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if signer.is_null() || (*signer).dwError != 0 || (*signer).csCertChain < 2 {
            return false;
        }
        let leaf = WTHelperGetProvCertFromChain(signer, 0);
        if leaf.is_null() || (*leaf).dwError != 0 || (*leaf).pCert.is_null() {
            return false;
        }
        let mut organization = [0; 128];
        let length = CertGetNameStringW(
            (*leaf).pCert,
            CERT_NAME_ATTR_TYPE,
            0,
            Some(szOID_ORGANIZATION_NAME.as_ptr().cast()),
            Some(&mut organization),
        ) as usize;
        length == EXPECTED.len() + 1
            && organization[..length - 1]
                .iter()
                .copied()
                .eq(EXPECTED.encode_utf16())
    }
}
