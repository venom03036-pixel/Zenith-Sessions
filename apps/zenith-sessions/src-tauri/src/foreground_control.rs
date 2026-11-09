use std::{future::Future, time::Duration};

use hmac::{Hmac, Mac};
use sha2::Sha256;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::{ClientOptions, NamedPipeClient},
    time::{Instant, timeout_at},
};
use uuid::Uuid;
use windows::Win32::Storage::FileSystem::SECURITY_ANONYMOUS;
use zeroize::Zeroizing;

use crate::pipe_peer::owned_by_current_user;

const PIPE: &str = r"\\.\pipe\Zenith.Macros.Personal.Foreground.v1";
const TIMEOUT: Duration = Duration::from_secs(3);
const FRAME_LIMIT: usize = 96;
const MAGIC: &[u8; 4] = b"ZMC1";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Error {
    PairingCode,
    Unavailable,
    TimedOut,
    Protocol,
    Closed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Rejected,
    Preparing,
    Running,
    Paused,
    Stopping,
    Finished,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Outcome {
    None,
    InvalidRequest,
    Busy,
    Stopped,
    PreparationRejected,
    TargetUnavailable,
    InputHeld,
    LicenseRequired,
    Completed,
    Faulted,
    CleanupFailed,
}

#[derive(Debug, PartialEq, Eq)]
pub struct Status {
    pub state: State,
    pub outcome: Outcome,
    pub accepted: bool,
}

pub struct Start {
    pub host_session: Uuid,
    pub macro_id: Uuid,
    pub revision: Uuid,
    pub window: i64,
    pub process: i32,
    pub creation_file_time: i64,
}

impl Start {
    fn frame(&self) -> Result<[u8; 69], Error> {
        if self.host_session.is_nil()
            || self.macro_id.is_nil()
            || self.revision.is_nil()
            || self.window <= 0
            || self.window == 0xffff
            || self.process <= 0
            || !(1..=2_650_467_743_999_999_999).contains(&self.creation_file_time)
        {
            return Err(Error::Protocol);
        }
        let mut frame = [0; 69];
        frame[0] = 1;
        frame[1..17].copy_from_slice(self.host_session.as_bytes());
        frame[17..33].copy_from_slice(self.macro_id.as_bytes());
        frame[33..49].copy_from_slice(self.revision.as_bytes());
        frame[49..57].copy_from_slice(&self.window.to_le_bytes());
        frame[57..61].copy_from_slice(&self.process.to_le_bytes());
        frame[61..69].copy_from_slice(&self.creation_file_time.to_le_bytes());
        Ok(frame)
    }
}

/// One authorized connection. Dropping it or abandoning an exchange revokes the run in Personal.
/// A session owner must poll status within Personal's heartbeat deadline while a run is active.
pub struct ForegroundControl {
    wire: Option<Wire>,
    operation: Option<Uuid>,
}

impl ForegroundControl {
    pub async fn connect(code: Zeroizing<String>) -> Result<Self, Error> {
        Self::connect_at(PIPE, code).await
    }

    async fn connect_at(path: &str, code: Zeroizing<String>) -> Result<Self, Error> {
        let grant = pairing_code(&code)?;
        drop(code);
        let pipe = ClientOptions::new()
            .security_qos_flags(SECURITY_ANONYMOUS.0)
            .open(path)
            .map_err(|_| Error::Unavailable)?;
        if !owned_by_current_user(&pipe) {
            return Err(Error::Unavailable);
        }
        let wire = bounded(Wire::connect(pipe, grant)).await?;
        Ok(Self {
            wire: Some(wire),
            operation: None,
        })
    }

    pub async fn start(&mut self, target: Start) -> Result<Status, Error> {
        if self.operation.is_some() {
            return Err(Error::Protocol);
        }
        let (status, id) = self.exchange(&target.frame()?, None).await?;
        if status.accepted {
            self.operation = Some(id);
        } else {
            self.wire = None;
        }
        Ok(status)
    }

    pub async fn status(&mut self) -> Result<Status, Error> {
        self.control(2).await
    }
    pub async fn pause(&mut self) -> Result<Status, Error> {
        self.control(3).await
    }
    pub async fn resume(&mut self) -> Result<Status, Error> {
        self.control(4).await
    }
    pub async fn stop(&mut self) -> Result<Status, Error> {
        self.control(5).await
    }

    async fn control(&mut self, command: u8) -> Result<Status, Error> {
        let id = self.operation.ok_or(Error::Closed)?;
        let mut frame = [0; 17];
        frame[0] = command;
        frame[1..].copy_from_slice(id.as_bytes());
        self.exchange(&frame, Some(id))
            .await
            .map(|(status, _)| status)
    }

    async fn exchange(&mut self, frame: &[u8], id: Option<Uuid>) -> Result<(Status, Uuid), Error> {
        // If this future is cancelled, the local wire drops and cannot carry a later request.
        let mut wire = self.wire.take().ok_or(Error::Closed)?;
        let reply = bounded(wire.exchange(frame)).await?;
        let decoded = decode(&reply, frame[0], id)?;
        self.wire = Some(wire);
        Ok(decoded)
    }
}

async fn bounded<T>(operation: impl Future<Output = Result<T, Error>>) -> Result<T, Error> {
    let deadline = Instant::now() + TIMEOUT;
    let result = timeout_at(deadline, operation)
        .await
        .map_err(|_| Error::TimedOut)?;
    if Instant::now() >= deadline {
        Err(Error::TimedOut)
    } else {
        result
    }
}

fn pairing_code(code: &str) -> Result<Zeroizing<[u8; 16]>, Error> {
    let bytes = code.trim().as_bytes();
    if bytes.len() != 32 {
        return Err(Error::PairingCode);
    }
    let mut grant = Zeroizing::new([0; 16]);
    for (byte, pair) in grant.iter_mut().zip(bytes.as_chunks::<2>().0) {
        let digit = |value| match value {
            b'0'..=b'9' => Ok(value - b'0'),
            b'a'..=b'f' => Ok(value - b'a' + 10),
            b'A'..=b'F' => Ok(value - b'A' + 10),
            _ => Err(Error::PairingCode),
        };
        *byte = digit(pair[0])? << 4 | digit(pair[1])?;
    }
    Ok(grant)
}

struct Wire {
    pipe: NamedPipeClient,
    key: Zeroizing<[u8; 32]>,
    sequence: u64,
}

fn mac(key: &[u8], parts: &[&[u8]]) -> Hmac<Sha256> {
    let mut tag = Hmac::<Sha256>::new_from_slice(key).expect("HMAC accepts any key length");
    for part in parts {
        tag.update(part);
    }
    tag
}

impl Wire {
    async fn connect(mut pipe: NamedPipeClient, grant: Zeroizing<[u8; 16]>) -> Result<Self, Error> {
        let mut hello = [0; 36];
        hello[..4].copy_from_slice(MAGIC);
        getrandom::fill(&mut hello[4..]).map_err(|_| Error::Unavailable)?;
        pipe.write_all(&hello).await.map_err(|_| Error::Closed)?;
        let mut challenge = [0; 64];
        pipe.read_exact(&mut challenge)
            .await
            .map_err(|_| Error::Closed)?;
        let (nonce, supplied) = challenge.split_at(32);
        mac(&*grant, &[&[1], &hello, nonce])
            .verify_slice(supplied)
            .map_err(|_| Error::PairingCode)?;
        let proof = mac(&*grant, &[&[2], &hello, nonce]).finalize().into_bytes();
        pipe.write_all(&proof).await.map_err(|_| Error::Closed)?;
        let key = Zeroizing::new(
            mac(&*grant, &[&[3], &hello, nonce])
                .finalize()
                .into_bytes()
                .into(),
        );
        Ok(Self {
            pipe,
            key,
            sequence: 0,
        })
    }

    async fn exchange(&mut self, body: &[u8]) -> Result<Vec<u8>, Error> {
        self.sequence = self.sequence.checked_add(1).ok_or(Error::Protocol)?;
        let sequence = self.sequence.to_le_bytes();
        let mut packet = Vec::with_capacity(2 + body.len() + 32);
        packet.extend_from_slice(&(body.len() as u16).to_le_bytes());
        packet.extend_from_slice(body);
        packet.extend_from_slice(
            &mac(&*self.key, &[b"C", &sequence, body])
                .finalize()
                .into_bytes(),
        );
        self.pipe
            .write_all(&packet)
            .await
            .map_err(|_| Error::Closed)?;
        let length = self.pipe.read_u16_le().await.map_err(|_| Error::Closed)? as usize;
        if !(1..=FRAME_LIMIT).contains(&length) {
            return Err(Error::Protocol);
        }
        let mut result = vec![0; length];
        let mut supplied = [0; 32];
        self.pipe
            .read_exact(&mut result)
            .await
            .map_err(|_| Error::Closed)?;
        self.pipe
            .read_exact(&mut supplied)
            .await
            .map_err(|_| Error::Closed)?;
        mac(&*self.key, &[b"S", &sequence, &result])
            .verify_slice(&supplied)
            .map_err(|_| Error::Protocol)?;
        Ok(result)
    }
}

fn decode(reply: &[u8], command: u8, expected: Option<Uuid>) -> Result<(Status, Uuid), Error> {
    use Outcome::*;
    use State::*;
    if reply.len() != 20 || reply[0] != command || reply[3] > 1 {
        return Err(Error::Protocol);
    }
    let state = [Rejected, Preparing, Running, Paused, Stopping, Finished]
        .get(reply[1] as usize)
        .copied()
        .ok_or(Error::Protocol)?;
    let outcome = [
        None,
        InvalidRequest,
        Busy,
        Stopped,
        PreparationRejected,
        TargetUnavailable,
        InputHeld,
        LicenseRequired,
        Completed,
        Faulted,
        CleanupFailed,
    ]
    .get(reply[2] as usize)
    .copied()
    .ok_or(Error::Protocol)?;
    let accepted = reply[3] == 1;
    let id = Uuid::from_slice(&reply[4..]).map_err(|_| Error::Protocol)?;
    if state == Rejected {
        if expected.is_some() || accepted || !id.is_nil() || outcome == None || outcome == Completed
        {
            return Err(Error::Protocol);
        }
    } else if id.is_nil()
        || expected.is_some_and(|expected| id != expected)
        || (state == Finished) != (outcome != None)
        || command == 1 && !accepted
    {
        return Err(Error::Protocol);
    }
    Ok((
        Status {
            state,
            outcome,
            accepted,
        },
        id,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn response_requires_the_current_operation_and_a_consistent_state() {
        let id = Uuid::from_u128(42);
        let mut reply = [0; 20];
        reply[..4].copy_from_slice(&[2, 2, 0, 1]);
        reply[4..].copy_from_slice(id.as_bytes());
        assert_eq!(decode(&reply, 2, Some(id)).unwrap().0.state, State::Running);
        assert_eq!(
            decode(&reply, 2, Some(Uuid::new_v4())),
            Err(Error::Protocol)
        );
        assert_eq!(decode(&reply, 3, Some(id)), Err(Error::Protocol));
        reply[1] = 5;
        assert_eq!(decode(&reply, 2, Some(id)), Err(Error::Protocol));
        reply[2] = 8;
        assert_eq!(
            decode(&reply, 2, Some(id)).unwrap().0.outcome,
            Outcome::Completed
        );
    }

    #[test]
    #[ignore = "Run with the staged .NET control fixture"]
    fn rust_controls_the_dotnet_foreground_fixture_and_disconnect_stops_it() {
        use std::{
            io::{BufRead, BufReader},
            os::windows::process::CommandExt,
            process::{Child, Command, Stdio},
        };
        struct Fixture(Child);
        impl Drop for Fixture {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
        let worker = std::env::var_os("ZENITH_MACRO_FIXTURE").unwrap();
        let name = format!(
            "Zenith.Macros.Personal.Foreground.v1.fixture-{}",
            Uuid::new_v4()
        );
        let path = format!(r"\\.\pipe\{name}");
        let mut fixture = Fixture(
            Command::new("dotnet")
                .arg(worker)
                .args(["--foreground-pipe", &name])
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .creation_flags(0x0800_0000)
                .spawn()
                .unwrap(),
        );
        let mut line = Zeroizing::new(String::new());
        BufReader::new(fixture.0.stdout.take().unwrap())
            .read_line(&mut line)
            .unwrap();
        let code = Zeroizing::new(line.strip_prefix("READY ").unwrap().trim().to_owned());
        drop(line);
        tauri::async_runtime::block_on(async {
            let mut control = ForegroundControl::connect_at(&path, code).await.unwrap();
            let start = control
                .start(Start {
                    host_session: "6ab58055-c06d-4c29-9f6d-b0b2af79ad27".parse().unwrap(),
                    macro_id: "708d8d3b-36c7-4f12-a3db-c7712b091504".parse().unwrap(),
                    revision: "e4b2bb83-7d49-4447-bfdc-aeac8c4c829e".parse().unwrap(),
                    window: 0x123456,
                    process: 4242,
                    creation_file_time: 99,
                })
                .await
                .unwrap();
            assert_eq!(
                start,
                Status {
                    state: State::Running,
                    outcome: Outcome::None,
                    accepted: true
                }
            );
            assert_eq!(control.pause().await.unwrap().state, State::Paused);
            assert_eq!(control.status().await.unwrap().state, State::Paused);
            assert_eq!(control.resume().await.unwrap().state, State::Running);
            drop(control); // The worker exits successfully only when the host joined Stop.
        });
        assert!(fixture.0.wait().unwrap().success());
    }
}
