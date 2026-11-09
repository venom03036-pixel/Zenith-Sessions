use std::{
    os::windows::io::AsRawHandle,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use windows::Win32::{
    Foundation::{FILETIME, HANDLE, WAIT_TIMEOUT},
    System::{
        ProcessStatus::{
            K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX, PROCESS_MEMORY_COUNTERS_EX2,
        },
        Threading::{
            ABOVE_NORMAL_PRIORITY_CLASS, ALL_PROCESSOR_GROUPS, BELOW_NORMAL_PRIORITY_CLASS,
            GetActiveProcessorCount, GetPriorityClass, GetProcessIoCounters, GetProcessTimes,
            HIGH_PRIORITY_CLASS, IDLE_PRIORITY_CLASS, IO_COUNTERS, NORMAL_PRIORITY_CLASS,
            REALTIME_PRIORITY_CLASS, SetPriorityClass, WaitForSingleObject,
        },
    },
};

use super::{EfficiencyState, OwnedPlayer, ProcessIdentity, process_identity_from_handle};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum PriorityClass {
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    Realtime,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PriorityAction {
    Lower,
    Restore,
}

#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PriorityState {
    pub class: PriorityClass,
    pub can_lower: bool,
    pub can_restore: bool,
}

#[derive(Clone, Copy)]
struct ProcessMemory {
    working_set: u64,
    private_working_set: Option<u64>,
    private_commit: u64,
}

pub struct ProcessSample {
    at: Instant,
    identity: ProcessIdentity,
    logical_processors: u32,
    cpu_ticks: Option<u64>,
    read_bytes: Option<u64>,
    write_bytes: Option<u64>,
    memory: Option<ProcessMemory>,
    uptime_seconds: Option<u64>,
    priority: Option<PriorityState>,
    efficiency: Option<EfficiencyState>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessMetrics {
    pub cpu_percent: Option<f64>,
    pub read_bytes_per_second: Option<f64>,
    pub write_bytes_per_second: Option<f64>,
    pub working_set: Option<u64>,
    pub private_working_set: Option<u64>,
    pub private_commit: Option<u64>,
    pub uptime_seconds: Option<u64>,
    pub priority: Option<PriorityState>,
    pub efficiency: Option<EfficiencyState>,
}

impl ProcessSample {
    pub fn metrics(&self, previous: Option<&Self>) -> ProcessMetrics {
        let interval = previous.and_then(|old| {
            let elapsed = self.at.checked_duration_since(old.at)?;
            (old.identity == self.identity
                && (Duration::from_millis(250)..=Duration::from_secs(5)).contains(&elapsed))
            .then_some((old, elapsed.as_secs_f64()))
        });
        let cpu_percent = interval.and_then(|(old, seconds)| {
            if self.logical_processors == 0 || self.logical_processors != old.logical_processors {
                return None;
            }
            let ticks = self.cpu_ticks?.checked_sub(old.cpu_ticks?)?;
            Some(
                (ticks as f64 / 10_000_000.0 / seconds / f64::from(self.logical_processors)
                    * 100.0)
                    .min(100.0),
            )
        });
        let rate = |latest: Option<u64>, select: fn(&Self) -> Option<u64>| {
            let (old, seconds) = interval?;
            Some(latest?.checked_sub(select(old)?)? as f64 / seconds)
        };
        ProcessMetrics {
            cpu_percent,
            read_bytes_per_second: rate(self.read_bytes, |old| old.read_bytes),
            write_bytes_per_second: rate(self.write_bytes, |old| old.write_bytes),
            working_set: self.memory.as_ref().map(|memory| memory.working_set),
            private_working_set: self
                .memory
                .as_ref()
                .and_then(|memory| memory.private_working_set),
            private_commit: self.memory.as_ref().map(|memory| memory.private_commit),
            uptime_seconds: self.uptime_seconds,
            priority: self.priority,
            efficiency: self.efficiency,
        }
    }
}

impl OwnedPlayer {
    pub(super) fn performance_handle(&self) -> Option<HANDLE> {
        let identity = self.identity?;
        let handle = HANDLE(self.child.as_raw_handle());
        // SAFETY: Child owns this exact handle for the lifetime of the borrow.
        (unsafe { WaitForSingleObject(handle, 0) } == WAIT_TIMEOUT
            && process_identity_from_handle(handle, identity.pid) == Some(identity))
        .then_some(handle)
    }

    pub fn performance_sample(&mut self) -> Option<ProcessSample> {
        let handle = self.performance_handle()?;
        let identity = self.identity?;
        let (cpu_ticks, io, logical_processors) = unsafe {
            // SAFETY: this handle is held by Child and the output structures are writable.
            let (mut creation, mut exit, mut kernel, mut user) = Default::default();
            let ticks = GetProcessTimes(handle, &mut creation, &mut exit, &mut kernel, &mut user)
                .ok()
                .and_then(|_| filetime(kernel).checked_add(filetime(user)));
            let mut io = IO_COUNTERS::default();
            let io = GetProcessIoCounters(handle, &mut io).ok().map(|_| io);
            (ticks, io, GetActiveProcessorCount(ALL_PROCESSOR_GROUPS))
        };
        let memory = memory(handle);
        let priority = read_priority(handle).map(|class| self.priority_state(class));
        let efficiency = self.sample_efficiency(handle);
        // If the client exited mid-sample, leave a gap instead of publishing its final counters.
        self.performance_handle()?;
        let uptime_seconds = (identity.started_filetime / 10_000_000)
            .checked_sub(11_644_473_600)
            .and_then(|started| {
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .ok()?
                    .as_secs()
                    .checked_sub(started)
            });
        Some(ProcessSample {
            at: Instant::now(),
            identity,
            logical_processors,
            cpu_ticks,
            read_bytes: io.map(|io| io.ReadTransferCount),
            write_bytes: io.map(|io| io.WriteTransferCount),
            memory,
            uptime_seconds,
            priority,
            efficiency,
        })
    }

    fn priority_state(&mut self, class: PriorityClass) -> PriorityState {
        self.managed_priority &= class == PriorityClass::BelowNormal;
        PriorityState {
            class,
            can_lower: class == PriorityClass::Normal,
            can_restore: self.managed_priority,
        }
    }

    pub fn change_priority(
        &mut self,
        action: PriorityAction,
    ) -> Result<PriorityState, &'static str> {
        let handle = self
            .performance_handle()
            .ok_or("The owned Roblox process changed or exited.")?;
        let before = read_priority(handle).ok_or("Windows could not read the current priority.")?;
        let state = self.priority_state(before);
        let (value, expected) = match action {
            PriorityAction::Lower if state.can_lower => {
                (BELOW_NORMAL_PRIORITY_CLASS, PriorityClass::BelowNormal)
            }
            PriorityAction::Restore if state.can_restore => {
                (NORMAL_PRIORITY_CLASS, PriorityClass::Normal)
            }
            PriorityAction::Lower => {
                return Err("Below normal can only be applied from Normal priority.");
            }
            PriorityAction::Restore => {
                return Err("Zenith has no current priority change to restore for this client.");
            }
        };
        // SAFETY: this exact child handle was created and retained by Zenith.
        unsafe { SetPriorityClass(handle, value) }
            .map_err(|_| "Windows rejected the priority change.")?;
        if matches!(action, PriorityAction::Lower) {
            self.managed_priority = true;
        }
        let after = read_priority(handle).ok_or("Windows did not confirm the priority change.")?;
        let state = self.priority_state(after);
        if after != expected || self.performance_handle().is_none() {
            return Err("Windows did not confirm the priority change.");
        }
        Ok(state)
    }
}

fn read_priority(handle: HANDLE) -> Option<PriorityClass> {
    // SAFETY: all callers keep an owned process handle alive.
    let raw = unsafe { GetPriorityClass(handle) };
    Some(match raw {
        value if value == IDLE_PRIORITY_CLASS.0 => PriorityClass::Idle,
        value if value == BELOW_NORMAL_PRIORITY_CLASS.0 => PriorityClass::BelowNormal,
        value if value == NORMAL_PRIORITY_CLASS.0 => PriorityClass::Normal,
        value if value == ABOVE_NORMAL_PRIORITY_CLASS.0 => PriorityClass::AboveNormal,
        value if value == HIGH_PRIORITY_CLASS.0 => PriorityClass::High,
        value if value == REALTIME_PRIORITY_CLASS.0 => PriorityClass::Realtime,
        _ => return None,
    })
}

fn memory(handle: HANDLE) -> Option<ProcessMemory> {
    let extended_size = size_of::<PROCESS_MEMORY_COUNTERS_EX2>() as u32;
    let mut extended = PROCESS_MEMORY_COUNTERS_EX2 {
        cb: extended_size,
        PrivateWorkingSetSize: usize::MAX,
        ..Default::default()
    };
    // SAFETY: Windows accepts the documented extended structures through the base pointer.
    if unsafe { K32GetProcessMemoryInfo(handle, (&raw mut extended).cast(), extended_size) }
        .as_bool()
    {
        return Some(ProcessMemory {
            working_set: extended.WorkingSetSize as u64,
            private_working_set: (extended.PrivateWorkingSetSize <= extended.WorkingSetSize)
                .then_some(extended.PrivateWorkingSetSize as u64),
            private_commit: extended.PrivateUsage as u64,
        });
    }
    let fallback_size = size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
    let mut fallback = PROCESS_MEMORY_COUNTERS_EX {
        cb: fallback_size,
        ..Default::default()
    };
    unsafe { K32GetProcessMemoryInfo(handle, (&raw mut fallback).cast(), fallback_size) }
        .as_bool()
        .then_some(ProcessMemory {
            working_set: fallback.WorkingSetSize as u64,
            private_working_set: None,
            private_commit: fallback.PrivateUsage as u64,
        })
}

fn filetime(value: FILETIME) -> u64 {
    (u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rates_use_two_fresh_samples_from_the_same_process() {
        let at = Instant::now();
        let identity = ProcessIdentity {
            pid: 42,
            started_filetime: 42,
        };
        let old = ProcessSample {
            at,
            identity,
            logical_processors: 4,
            cpu_ticks: Some(10_000_000),
            read_bytes: Some(500),
            write_bytes: Some(1000),
            memory: None,
            uptime_seconds: None,
            priority: None,
            efficiency: None,
        };
        let next = ProcessSample {
            at: at + Duration::from_secs(1),
            cpu_ticks: Some(15_000_000),
            read_bytes: Some(1000),
            write_bytes: Some(999),
            ..old
        };
        assert!(next.metrics(None).cpu_percent.is_none());
        let current = next.metrics(Some(&old));
        assert_eq!(current.cpu_percent, Some(12.5));
        assert_eq!(current.read_bytes_per_second, Some(500.0));
        assert!(current.write_bytes_per_second.is_none());
        let foreign = ProcessSample {
            identity: ProcessIdentity {
                started_filetime: 43,
                ..identity
            },
            ..next
        };
        assert!(foreign.metrics(Some(&old)).read_bytes_per_second.is_none());
        let stale = ProcessSample {
            at: at + Duration::from_secs(6),
            ..foreign
        };
        assert!(stale.metrics(Some(&old)).cpu_percent.is_none());
    }
}
