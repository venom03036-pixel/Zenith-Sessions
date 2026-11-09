use std::{
    sync::{Arc, Weak, atomic::Ordering},
    time::{Duration, Instant},
};

use futures_util::future::{Either, select};
use tauri::async_runtime::{JoinHandle, spawn, spawn_blocking};
use tokio::{
    sync::oneshot,
    time::{MissedTickBehavior, interval},
};

use crate::{
    LaunchOptions, LaunchPreferences, LaunchSource, Workspace, run_launch, session::RetryRequest,
};

pub struct SessionMonitor {
    stop: oneshot::Sender<()>,
    worker: JoinHandle<()>,
}

impl SessionMonitor {
    pub fn start(state: &Arc<Workspace>) -> Self {
        let (stop, stopped) = oneshot::channel();
        let worker = spawn(run(Arc::downgrade(state), stopped));
        Self { stop, worker }
    }

    pub async fn shutdown(self) {
        let _ = self.stop.send(());
        let _ = self.worker.await;
    }
}

async fn run(state: Weak<Workspace>, mut stopped: oneshot::Receiver<()>) {
    let mut ticks = interval(Duration::from_secs(1));
    ticks.set_missed_tick_behavior(MissedTickBehavior::Skip);
    let mut restart: Option<(RetryRequest, JoinHandle<()>)> = None;
    while let Either::Left(_) = select(Box::pin(ticks.tick()), &mut stopped).await {
        let Some(state) = state.upgrade() else { break };
        if state.exiting.load(Ordering::Acquire) {
            continue;
        }
        if restart
            .as_ref()
            .is_some_and(|(_, task)| task.inner().is_finished())
            && let Some((request, task)) = restart.take()
            && task.await.is_err()
            && let Ok(mut owner) = state.owner.lock()
        {
            owner.fail_retry(request, "The restart worker ended unexpectedly.");
        }
        let can_restart = restart.is_none();
        let observer = Arc::clone(&state);
        if let Ok(Some(request)) = spawn_blocking(move || tick(&observer, can_restart)).await {
            restart = Some((
                request,
                spawn_blocking(move || {
                    let result = run_launch(
                        &state,
                        request.account,
                        LaunchSource::Recovery(request),
                        LaunchOptions {
                            epoch: request.epoch(),
                            try_one: false,
                            preferences: LaunchPreferences {
                                allow_pair: false,
                                recover: false,
                            },
                        },
                    );
                    if let Err(error) = result
                        && let Ok(mut owner) = state.owner.lock()
                    {
                        owner.fail_retry(request, &error);
                    }
                }),
            ));
        }
    }
    // Close revokes launch permission before requesting shutdown. An HTTP request already in a
    // blocking worker may finish unwinding after this, but cannot create a native process.
    if let Some((_, restart)) = restart {
        restart.abort();
    }
}

fn tick(state: &Workspace, can_restart: bool) -> Option<RetryRequest> {
    let observation = state.owner.lock().ok()?.observation();
    let evidence = observation.map(|sample| (sample, sample.read(&state.logs)));
    let mut owner = state.owner.lock().ok()?;
    if state.exiting.load(Ordering::Acquire) {
        return None;
    }
    owner.reconcile(evidence);
    if can_restart {
        owner.retry_candidate(Instant::now())
    } else {
        None
    }
}
