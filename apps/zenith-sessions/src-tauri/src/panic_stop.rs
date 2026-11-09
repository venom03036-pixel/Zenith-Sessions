use std::sync::{
    Mutex,
    atomic::{AtomicU64, Ordering},
};

use serde::Serialize;
use tokio::sync::Notify;

use crate::session::SessionOwner;

#[derive(Default)]
pub struct PanicStop {
    epoch: AtomicU64,
    outcome: Mutex<Option<(u64, Result<(), String>)>>,
    revoked: Notify,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PanicView {
    epoch: String,
    held: bool,
    can_resume: bool,
    shortcut_available: bool,
    error: Option<String>,
}

impl PanicStop {
    pub fn permit(&self) -> Result<u64, &'static str> {
        let epoch = self.epoch.load(Ordering::Acquire);
        if epoch & 1 == 0 {
            Ok(epoch)
        } else {
            Err("Panic stop is holding new launches. Resume launches first.")
        }
    }

    pub fn permits(&self, epoch: u64) -> bool {
        epoch & 1 == 0 && self.epoch.load(Ordering::Acquire) == epoch
    }

    pub fn trip(&self) -> u64 {
        // Every activation invalidates a stale Resume button, even if Panic was already held.
        let next = |epoch: u64| (epoch | 1).saturating_add(2);
        let previous = self
            .epoch
            .try_update(Ordering::AcqRel, Ordering::Acquire, |epoch| {
                Some(next(epoch))
            })
            .expect("the panic epoch update always supplies a value");
        self.revoked.notify_waiters();
        next(previous)
    }

    pub async fn wait_revoked(&self, epoch: u64) {
        let notice = self.revoked.notified();
        tokio::pin!(notice);
        notice.as_mut().enable();
        if self.permits(epoch) {
            notice.await;
        }
    }

    pub fn drain(&self, epoch: u64, owner: &Mutex<SessionOwner>) -> Result<(), String> {
        let result = match owner.lock() {
            Ok(mut owner) if self.epoch.load(Ordering::Acquire) == epoch => owner.stop_all(false),
            Ok(_) => return Ok(()), // a newer activation owns cleanup
            Err(_) => Err("Panic stop could not access the session owner.".into()),
        };
        let mut outcome = self
            .outcome
            .lock()
            .map_err(|_| "Panic stop could not record its result.")?;
        if self.epoch.load(Ordering::Acquire) == epoch {
            *outcome = Some((epoch, result.clone()));
        }
        result
    }

    pub fn view(&self, owner: &SessionOwner, shortcut_available: bool) -> PanicView {
        let outcome = self
            .outcome
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let epoch = self.epoch.load(Ordering::Acquire);
        let held = epoch & 1 != 0;
        let result = outcome
            .as_ref()
            .filter(|(request, _)| *request == epoch)
            .map(|(_, result)| result);
        PanicView {
            epoch: epoch.to_string(),
            held,
            shortcut_available,
            can_resume: held
                && epoch != u64::MAX
                && result.is_some_and(Result::is_ok)
                && !owner.has_active_attempts(),
            error: result.and_then(|result| result.as_ref().err().cloned()),
        }
    }

    pub fn resume(&self, epoch: u64, owner: &SessionOwner) -> Result<(), String> {
        let view = self.view(owner, false);
        if !view.can_resume {
            return Err("Panic stop is still waiting for owned work to finish. Retry Panic if stopping failed.".into());
        }
        let next = epoch
            .checked_add(1)
            .ok_or("Restart Zenith before resuming launches.")?;
        if epoch & 1 == 0
            || self
                .epoch
                .compare_exchange(epoch, next, Ordering::AcqRel, Ordering::Acquire)
                .is_err()
        {
            return Err("The Panic state changed. Review the current hold before resuming.".into());
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn trip_fences_old_permits_before_cleanup_and_resume_cannot_reuse_them() {
        let control = PanicStop::default();
        let owner = Mutex::new(SessionOwner::default());
        let first = control.permit().unwrap();
        let owner_busy = owner.lock().unwrap();
        let stop = control.trip();
        assert!(!control.permits(first));
        assert!(control.permit().is_err());
        assert!(control.resume(stop, &owner_busy).is_err());
        drop(owner_busy);
        control.drain(stop, &owner).unwrap();
        control.resume(stop, &owner.lock().unwrap()).unwrap();
        assert!(!control.permits(first));
        assert!(control.permits(control.permit().unwrap()));
    }

    #[test]
    fn repeated_activation_invalidates_a_stale_resume() {
        let control = PanicStop::default();
        let owner = Mutex::new(SessionOwner::default());
        let first = control.trip();
        control.drain(first, &owner).unwrap();
        let second = control.trip();
        control.drain(second, &owner).unwrap();
        assert!(control.resume(first, &owner.lock().unwrap()).is_err());
        assert!(control.permit().is_err());
        control.resume(second, &owner.lock().unwrap()).unwrap();
    }
}
