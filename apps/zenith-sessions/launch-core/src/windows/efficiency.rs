use std::mem::size_of;

use serde::{Deserialize, Serialize};
use windows::Win32::{
    Foundation::HANDLE,
    System::Threading::{
        GetProcessInformation, PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        PROCESS_POWER_THROTTLING_EXECUTION_SPEED, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        PROCESS_POWER_THROTTLING_STATE, ProcessPowerThrottling, SetProcessInformation,
    },
};

use super::OwnedPlayer;

const EXECUTION: u32 = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
const TIMER: u32 = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
const KNOWN_FLAGS: u32 = EXECUTION | TIMER;
const UNAVAILABLE: &str = "Windows did not report a supported efficiency policy for this client.";
const CANCELLED: &str = "Zenith stopped this efficiency action.";
const CONFIRMATION: &str = "Windows did not confirm the requested efficiency policy.";

#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EfficiencyAction {
    Request,
    Restore,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum EfficiencyPolicy {
    SystemManaged,
    EfficiencyRequested,
    PerformanceRequested,
}

#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EfficiencyState {
    pub policy: EfficiencyPolicy,
    pub can_request: bool,
    pub can_restore: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct PowerSettings {
    control: u32,
    state: u32,
}

impl PowerSettings {
    fn policy(self) -> EfficiencyPolicy {
        if self.control & EXECUTION == 0 {
            EfficiencyPolicy::SystemManaged
        } else if self.state & EXECUTION != 0 {
            EfficiencyPolicy::EfficiencyRequested
        } else {
            EfficiencyPolicy::PerformanceRequested
        }
    }

    fn with_efficiency(self, enable: bool) -> Self {
        let value = if enable { EXECUTION } else { 0 };
        Self {
            control: (self.control & !EXECUTION) | value,
            state: (self.state & !EXECUTION) | value,
        }
    }
}

impl OwnedPlayer {
    pub(super) fn sample_efficiency(&mut self, handle: HANDLE) -> Option<EfficiencyState> {
        read_power(handle).map(|settings| self.efficiency_state(settings))
    }

    fn efficiency_state(&mut self, settings: PowerSettings) -> EfficiencyState {
        let policy = settings.policy();
        self.managed_efficiency &= policy == EfficiencyPolicy::EfficiencyRequested;
        EfficiencyState {
            policy,
            can_request: policy == EfficiencyPolicy::SystemManaged,
            can_restore: self.managed_efficiency,
        }
    }

    pub fn change_efficiency(
        &mut self,
        action: EfficiencyAction,
        permitted: impl Fn() -> bool,
    ) -> Result<EfficiencyState, &'static str> {
        let handle = self
            .performance_handle()
            .ok_or("The owned Roblox process changed or exited.")?;
        let before = read_power(handle).ok_or(UNAVAILABLE)?;
        let state = self.efficiency_state(before);
        let enable = match action {
            EfficiencyAction::Request if state.can_request => true,
            EfficiencyAction::Restore if state.can_restore => false,
            EfficiencyAction::Request => {
                return Err("This client already has an explicit Windows efficiency policy.");
            }
            EfficiencyAction::Restore => {
                return Err("Zenith has no current efficiency change to restore for this client.");
            }
        };
        let requested = before.with_efficiency(enable);
        if self.performance_handle().is_none() || !permitted() {
            return Err(CANCELLED);
        }
        write_power(handle, requested)?;
        if enable {
            self.managed_efficiency = true;
        }
        let after = read_power(handle).ok_or(CONFIRMATION)?;
        let state = self.efficiency_state(after);
        if self.performance_handle().is_none() || after != requested {
            return Err(CONFIRMATION);
        }
        if !permitted() {
            if enable
                && read_power(handle) == Some(after)
                && write_power(handle, before).is_ok()
                && read_power(handle) == Some(before)
            {
                self.managed_efficiency = false;
            }
            return Err(CANCELLED);
        }
        Ok(state)
    }
}

fn read_power(handle: HANDLE) -> Option<PowerSettings> {
    let mut raw = PROCESS_POWER_THROTTLING_STATE {
        Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        ControlMask: 0,
        StateMask: 0,
    };
    // SAFETY: the caller retains this exact process handle. The writable struct matches the queried class.
    unsafe {
        GetProcessInformation(
            handle,
            ProcessPowerThrottling,
            (&mut raw as *mut PROCESS_POWER_THROTTLING_STATE).cast(),
            size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32,
        )
    }
    .ok()?;
    (raw.Version == PROCESS_POWER_THROTTLING_CURRENT_VERSION
        && raw.ControlMask & !KNOWN_FLAGS == 0
        && raw.StateMask & !raw.ControlMask == 0)
        .then_some(PowerSettings {
            control: raw.ControlMask,
            state: raw.StateMask,
        })
}

fn write_power(handle: HANDLE, settings: PowerSettings) -> Result<(), &'static str> {
    let raw = PROCESS_POWER_THROTTLING_STATE {
        Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        ControlMask: settings.control,
        StateMask: settings.state,
    };
    // SAFETY: the caller retains the process handle. The read-only struct matches the selected class.
    unsafe {
        SetProcessInformation(
            handle,
            ProcessPowerThrottling,
            (&raw as *const PROCESS_POWER_THROTTLING_STATE).cast(),
            size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32,
        )
    }
    .map_err(|_| "Windows rejected the requested efficiency policy.")
}

#[cfg(test)]
mod tests {
    use std::cell::Cell;

    use super::*;
    use crate::windows::tests::fixture_player;

    const DEFAULT: PowerSettings = PowerSettings {
        control: 0,
        state: 0,
    };

    #[test]
    fn owned_request_and_restore_preserve_timer_policy_and_leave_another_child_alone() {
        let mut owned = fixture_player();
        let mut other = fixture_player();
        let handle = owned.performance_handle().unwrap();
        let other_handle = other.performance_handle().unwrap();
        let timer_disabled = PowerSettings {
            control: TIMER,
            state: 0,
        };
        let timer_enabled = PowerSettings {
            control: TIMER,
            state: TIMER,
        };
        write_power(handle, timer_disabled).unwrap();
        let priority = owned
            .performance_sample()
            .unwrap()
            .metrics(None)
            .priority
            .unwrap()
            .class;
        let unrelated = read_power(other_handle).unwrap();
        let requested = owned
            .change_efficiency(EfficiencyAction::Request, || true)
            .unwrap();
        assert_eq!(requested.policy, EfficiencyPolicy::EfficiencyRequested);
        assert!(requested.can_restore);
        assert_eq!(
            read_power(handle),
            Some(timer_disabled.with_efficiency(true))
        );
        let metrics = owned.performance_sample().unwrap().metrics(None);
        assert_eq!(metrics.priority.unwrap().class, priority);
        assert!(metrics.efficiency.unwrap().can_restore);
        write_power(handle, timer_enabled.with_efficiency(true)).unwrap();
        let restored = owned
            .change_efficiency(EfficiencyAction::Restore, || true)
            .unwrap();
        assert_eq!(restored.policy, EfficiencyPolicy::SystemManaged);
        assert!(!restored.can_restore);
        assert_eq!(read_power(handle), Some(timer_enabled));
        assert_eq!(read_power(other_handle), Some(unrelated));
        owned.stop().unwrap();
        other.stop().unwrap();
    }

    #[test]
    fn revocation_and_external_execution_policy_do_not_leave_zenith_in_control() {
        let mut owned = fixture_player();
        let handle = owned.performance_handle().unwrap();
        write_power(handle, DEFAULT).unwrap();
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Request, || false)
                .is_err()
        );
        assert_eq!(read_power(handle), Some(DEFAULT));
        let checks = Cell::new(0);
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Request, || {
                    checks.set(checks.get() + 1);
                    checks.get() == 1
                })
                .is_err()
        );
        assert_eq!(read_power(handle), Some(DEFAULT));
        owned
            .change_efficiency(EfficiencyAction::Request, || true)
            .unwrap();
        let external = PowerSettings {
            control: EXECUTION,
            state: 0,
        };
        write_power(handle, external).unwrap();
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Restore, || true)
                .is_err()
        );
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Request, || true)
                .is_err()
        );
        assert_eq!(read_power(handle), Some(external));
        let identity = owned.identity.unwrap();
        owned.identity = Some(super::super::ProcessIdentity {
            started_filetime: identity.started_filetime - 1,
            ..identity
        });
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Request, || true)
                .is_err()
        );
        assert_eq!(read_power(handle), Some(external));
        owned.identity = Some(identity);
        owned.stop().unwrap();
        assert!(
            owned
                .change_efficiency(EfficiencyAction::Request, || true)
                .is_err()
        );
    }
}
