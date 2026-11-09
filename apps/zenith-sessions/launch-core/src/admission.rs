use std::time::{Duration, Instant};

use serde::Serialize;

pub const GIB: u64 = 1024 * 1024 * 1024;

pub struct MemorySample {
    pub sampled_at: Instant,
    pub physical_total: u64,
    pub physical_available: u64,
    pub commit_total: u64,
    pub commit_available: u64,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Limit {
    Stale,
    Invalid,
    Commit,
    PhysicalMemory,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Admission {
    pub limiting_resource: Option<Limit>,
    pub sample_age_ms: u64,
    pub physical_available: u64,
    pub physical_required: u64,
    pub commit_available: u64,
    pub commit_required: u64,
    pub can_try_one: bool,
}

impl MemorySample {
    pub fn admission(&self, now: Instant) -> Admission {
        let age = now.saturating_duration_since(self.sampled_at);
        let physical_required = (self.physical_total / 5).max(3 * GIB) + 5 * GIB / 2;
        let commit_required = (self.commit_total / 10).max(2 * GIB) + 3 * GIB;
        let limiting_resource = if self.physical_total == 0
            || self.commit_total == 0
            || self.physical_available > self.physical_total
            || self.commit_available > self.commit_total
            || self.sampled_at > now
        {
            Some(Limit::Invalid)
        } else if age > Duration::from_secs(3) {
            Some(Limit::Stale)
        } else if self.commit_available < commit_required {
            Some(Limit::Commit)
        } else if self.physical_available < physical_required {
            Some(Limit::PhysicalMemory)
        } else {
            None
        };
        let can_try_one =
            limiting_resource == Some(Limit::PhysicalMemory) && self.physical_available >= GIB;
        Admission {
            limiting_resource,
            sample_age_ms: age.as_millis().min(u64::MAX as u128) as u64,
            physical_available: self.physical_available,
            physical_required,
            commit_available: self.commit_available,
            commit_required,
            can_try_one,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn override_is_available_only_for_fresh_physical_headroom_with_commit() {
        let now = Instant::now();
        let mut sample = MemorySample {
            sampled_at: now,
            physical_total: 32 * GIB,
            physical_available: 2 * GIB,
            commit_total: 60 * GIB,
            commit_available: 10 * GIB,
        };
        let decision = sample.admission(now);
        assert_eq!(decision.limiting_resource, Some(Limit::PhysicalMemory));
        assert!(decision.can_try_one);
        assert!(
            !sample
                .admission(now + Duration::from_millis(3001))
                .can_try_one
        );
        sample.physical_available = GIB - 1;
        assert!(!sample.admission(now).can_try_one);
        sample.physical_available = 2 * GIB;
        sample.commit_available = 9 * GIB - 1;
        assert_eq!(sample.admission(now).limiting_resource, Some(Limit::Commit));
        assert!(!sample.admission(now).can_try_one);
    }
}
