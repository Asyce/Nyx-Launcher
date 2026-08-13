use crate::{
    Game,
    capture::{CancelSignal, CaptureError},
    output::OutputError,
};
use serde_json::{Map, Value};
use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Write},
};
use windows::{
    Win32::{
        Foundation::{CloseHandle, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0},
        System::Threading::{
            OpenEventW, OpenMutexW, SYNCHRONIZATION_SYNCHRONIZE, WaitForSingleObject,
        },
    },
    core::PCWSTR,
};

pub const SCHEMA_VERSION: u64 = 1;
const EVENT_PREFIX: &str = r"Local\Pengo.Nyx.ExportCancel.v1.";
const PARENT_MUTEX_PREFIX: &str = r"Local\Pengo.Nyx.ExportParent.v1.";
const OWNERSHIP_TRANSFER_EVENT_PREFIX: &str = r"Local\Pengo.Nyx.ExportOwnership.v1.";
const PIPE_PREFIX: &str = r"\\.\pipe\Pengo.Nyx.AchievementIpc.v1.";
const PROOF_SIZE: usize = 32;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CancelEventError;

pub struct CancelEvent {
    event: HANDLE,
    parent_mutex: HANDLE,
    ownership_transferred: HANDLE,
}

impl CancelEvent {
    pub fn open(job_id: &str) -> Result<Self, CancelEventError> {
        let name = format!("{EVENT_PREFIX}{job_id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let event =
            unsafe { OpenEventW(SYNCHRONIZATION_SYNCHRONIZE, false, PCWSTR(name.as_ptr())) }
                .map_err(|_| CancelEventError)?;
        let parent_name = format!("{PARENT_MUTEX_PREFIX}{job_id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let parent_mutex = match unsafe {
            OpenMutexW(
                SYNCHRONIZATION_SYNCHRONIZE,
                false,
                PCWSTR(parent_name.as_ptr()),
            )
        } {
            Ok(handle) => handle,
            Err(_) => {
                unsafe {
                    let _ = CloseHandle(event);
                }
                return Err(CancelEventError);
            }
        };
        let ownership_name = format!("{OWNERSHIP_TRANSFER_EVENT_PREFIX}{job_id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let ownership_transferred = match unsafe {
            OpenEventW(
                SYNCHRONIZATION_SYNCHRONIZE,
                false,
                PCWSTR(ownership_name.as_ptr()),
            )
        } {
            Ok(handle) => handle,
            Err(_) => {
                unsafe {
                    let _ = CloseHandle(event);
                    let _ = CloseHandle(parent_mutex);
                }
                return Err(CancelEventError);
            }
        };
        Ok(Self {
            event,
            parent_mutex,
            ownership_transferred,
        })
    }
}

impl CancelSignal for CancelEvent {
    fn is_cancelled(&self) -> bool {
        unsafe {
            WaitForSingleObject(self.event, 0) == WAIT_OBJECT_0
                || (WaitForSingleObject(self.ownership_transferred, 0) != WAIT_OBJECT_0
                    && WaitForSingleObject(self.parent_mutex, 0) == WAIT_ABANDONED)
        }
    }
}

impl Drop for CancelEvent {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.event);
            let _ = CloseHandle(self.parent_mutex);
            let _ = CloseHandle(self.ownership_transferred);
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum State {
    Preparing,
    Ready,
    WaitingForGame,
    Exported,
    Failed,
    Cancelled,
}

impl State {
    const fn key(self) -> &'static str {
        match self {
            Self::Preparing => "preparing",
            Self::Ready => "ready",
            Self::WaitingForGame => "waiting_for_game",
            Self::Exported => "exported",
            Self::Failed => "failed",
            Self::Cancelled => "cancelled",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ErrorCode {
    AdministratorRequired,
    NormalUserRequired,
    CancelUnavailable,
    DecoderUnavailable,
    CaptureStartFailed,
    CaptureReadFailed,
    CaptureTimeoutNoFrames,
    CaptureTimeoutUnrecognizedFrames,
    CaptureTimeoutNoCommands,
    CaptureTimeout,
    CaptureSafetyLimit,
    CaptureParserFailed,
    CaptureInvalidSnapshot,
    CaptureCleanupFailed,
    CaptureClosed,
    OutputUnsafe,
    OutputExists,
    OutputWriteFailed,
    InternalError,
}

impl ErrorCode {
    const fn key(self) -> &'static str {
        match self {
            Self::AdministratorRequired => "administrator_required",
            Self::NormalUserRequired => "normal_user_required",
            Self::CancelUnavailable => "cancel_unavailable",
            Self::DecoderUnavailable => "decoder_unavailable",
            Self::CaptureStartFailed => "capture_start_failed",
            Self::CaptureReadFailed => "capture_read_failed",
            Self::CaptureTimeoutNoFrames => "capture_timeout_no_frames",
            Self::CaptureTimeoutUnrecognizedFrames => "capture_timeout_unrecognized_frames",
            Self::CaptureTimeoutNoCommands => "capture_timeout_no_commands",
            Self::CaptureTimeout => "capture_timeout",
            Self::CaptureSafetyLimit => "capture_safety_limit",
            Self::CaptureParserFailed => "capture_parser_failed",
            Self::CaptureInvalidSnapshot => "capture_invalid_snapshot",
            Self::CaptureCleanupFailed => "capture_cleanup_failed",
            Self::CaptureClosed => "capture_closed",
            Self::OutputUnsafe => "output_unsafe",
            Self::OutputExists => "output_exists",
            Self::OutputWriteFailed => "output_write_failed",
            Self::InternalError => "internal_error",
        }
    }
}

pub struct StatusWriter<W: Write> {
    writer: W,
    job_id: String,
    game: Game,
    proof: Option<String>,
    receiver_optional: bool,
}

impl StatusWriter<File> {
    pub fn connect(job_id: String, game: Game) -> io::Result<Self> {
        let path = format!("{PIPE_PREFIX}{job_id}");
        let mut pipe = OpenOptions::new().read(true).write(true).open(path)?;
        let mut proof = [0_u8; PROOF_SIZE];
        pipe.read_exact(&mut proof)?;
        Ok(Self::authenticated(pipe, job_id, game, proof))
    }
}

impl<W: Write> StatusWriter<W> {
    pub fn new(writer: W, job_id: String, game: Game) -> Self {
        Self {
            writer,
            job_id,
            game,
            proof: None,
            receiver_optional: false,
        }
    }

    fn authenticated(writer: W, job_id: String, game: Game, proof: [u8; PROOF_SIZE]) -> Self {
        const HEX: &[u8; 16] = b"0123456789abcdef";
        let mut encoded = String::with_capacity(PROOF_SIZE * 2);
        for byte in proof {
            encoded.push(char::from(HEX[usize::from(byte >> 4)]));
            encoded.push(char::from(HEX[usize::from(byte & 0x0f)]));
        }
        Self {
            writer,
            job_id,
            game,
            proof: Some(encoded),
            receiver_optional: false,
        }
    }

    pub fn state(&mut self, state: State) -> io::Result<()> {
        let result = self.write(state, None, None, None);
        if result.is_ok() && state == State::Ready {
            self.receiver_optional = true;
        }
        if self.receiver_optional {
            Ok(())
        } else {
            result
        }
    }

    pub fn failed(&mut self, code: ErrorCode) -> io::Result<()> {
        let result = self.write(State::Failed, None, Some(code), None);
        if self.receiver_optional {
            Ok(())
        } else {
            result
        }
    }

    pub fn exported(&mut self, item_count: usize, output_file: &str) -> io::Result<()> {
        let result = self.write(State::Exported, Some(item_count), None, Some(output_file));
        if self.receiver_optional {
            Ok(())
        } else {
            result
        }
    }

    fn write(
        &mut self,
        state: State,
        item_count: Option<usize>,
        error: Option<ErrorCode>,
        output_file: Option<&str>,
    ) -> io::Result<()> {
        let mut event = Map::new();
        event.insert("schemaVersion".into(), Value::from(SCHEMA_VERSION));
        event.insert("jobId".into(), Value::from(self.job_id.clone()));
        event.insert("game".into(), Value::from(self.game.key()));
        event.insert("kind".into(), Value::from("achievements"));
        event.insert("state".into(), Value::from(state.key()));
        if let Some(proof) = &self.proof {
            event.insert("proof".into(), Value::from(proof.clone()));
        }
        if let Some(item_count) = item_count {
            event.insert("itemCount".into(), Value::from(item_count));
        }
        if let Some(error) = error {
            event.insert("errorCode".into(), Value::from(error.key()));
        }
        if let Some(output_file) = output_file {
            event.insert("outputFile".into(), Value::from(output_file));
        }
        serde_json::to_writer(&mut self.writer, &Value::Object(event))?;
        self.writer.write_all(b"\n")?;
        self.writer.flush()
    }
}

pub fn capture_error_code(error: &CaptureError) -> Option<ErrorCode> {
    match error {
        CaptureError::Cancelled => None,
        CaptureError::Start => Some(ErrorCode::CaptureStartFailed),
        CaptureError::Read => Some(ErrorCode::CaptureReadFailed),
        CaptureError::TimeoutNoFrames => Some(ErrorCode::CaptureTimeoutNoFrames),
        CaptureError::TimeoutUnrecognizedFrames => {
            Some(ErrorCode::CaptureTimeoutUnrecognizedFrames)
        }
        CaptureError::TimeoutNoCommands => Some(ErrorCode::CaptureTimeoutNoCommands),
        CaptureError::Timeout => Some(ErrorCode::CaptureTimeout),
        CaptureError::PacketCap | CaptureError::ByteCap | CaptureError::FrameCap => {
            Some(ErrorCode::CaptureSafetyLimit)
        }
        CaptureError::ParserPanic => Some(ErrorCode::CaptureParserFailed),
        CaptureError::Snapshot(_) => Some(ErrorCode::CaptureInvalidSnapshot),
        CaptureError::Cleanup => Some(ErrorCode::CaptureCleanupFailed),
        CaptureError::Closed => Some(ErrorCode::CaptureClosed),
    }
}

pub fn output_error_code(error: &OutputError) -> ErrorCode {
    match error {
        OutputError::UnsafeLocation => ErrorCode::OutputUnsafe,
        OutputError::Exists => ErrorCode::OutputExists,
        OutputError::Cancelled => unreachable!("cancel is a state, not an error code"),
        OutputError::Io(_) => ErrorCode::OutputWriteFailed,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::SnapshotError;
    use windows::Win32::System::Threading::{CreateEventW, CreateMutexW, SetEvent};

    fn create_manual_event(name: &str) -> HANDLE {
        let wide_name = name.encode_utf16().chain([0]).collect::<Vec<_>>();
        unsafe { CreateEventW(None, true, false, PCWSTR(wide_name.as_ptr())) }.unwrap()
    }

    #[test]
    fn job_derived_event_cancels_without_console_input() {
        let owner = create_manual_event(&format!("{EVENT_PREFIX}cancel-test_01"));
        let ownership =
            create_manual_event(&format!("{OWNERSHIP_TRANSFER_EVENT_PREFIX}cancel-test_01"));
        let parent_name = format!("{PARENT_MUTEX_PREFIX}cancel-test_01")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let parent = unsafe { CreateMutexW(None, true, PCWSTR(parent_name.as_ptr())) }.unwrap();
        let event = CancelEvent::open("cancel-test_01").unwrap();
        assert!(!event.is_cancelled());
        assert!(unsafe { SetEvent(owner) }.as_bool());
        assert!(event.is_cancelled());
        unsafe {
            let _ = CloseHandle(owner);
            let _ = CloseHandle(ownership);
            let _ = CloseHandle(parent);
        }
    }

    #[test]
    fn abandoned_parent_mutex_cancels_after_launcher_crash() {
        let id = "parent-crash-test";
        let event_name = format!("{EVENT_PREFIX}{id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let parent_name = format!("{PARENT_MUTEX_PREFIX}{id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let owner =
            unsafe { CreateEventW(None, true, false, PCWSTR(event_name.as_ptr())) }.unwrap();
        let ownership = create_manual_event(&format!("{OWNERSHIP_TRANSFER_EVENT_PREFIX}{id}"));
        let (ready_tx, ready_rx) = std::sync::mpsc::channel();
        let (exit_tx, exit_rx) = std::sync::mpsc::channel();
        let parent_thread = std::thread::spawn(move || {
            let parent = unsafe { CreateMutexW(None, true, PCWSTR(parent_name.as_ptr())) }.unwrap();
            ready_tx.send(parent.0).unwrap();
            exit_rx.recv().unwrap();
        });
        let parent = HANDLE(ready_rx.recv().unwrap());
        let event = CancelEvent::open(id).unwrap();
        assert!(!event.is_cancelled());
        exit_tx.send(()).unwrap();
        parent_thread.join().unwrap();
        assert!(event.is_cancelled());
        unsafe {
            let _ = CloseHandle(parent);
            let _ = CloseHandle(owner);
            let _ = CloseHandle(ownership);
        }
    }

    #[test]
    fn transferred_ready_job_survives_parent_exit_but_explicit_cancel_still_stops() {
        let id = "ready-parent-close-test";
        let owner = create_manual_event(&format!("{EVENT_PREFIX}{id}"));
        let ownership = create_manual_event(&format!("{OWNERSHIP_TRANSFER_EVENT_PREFIX}{id}"));
        let parent_name = format!("{PARENT_MUTEX_PREFIX}{id}")
            .encode_utf16()
            .chain([0])
            .collect::<Vec<_>>();
        let (ready_tx, ready_rx) = std::sync::mpsc::channel();
        let (exit_tx, exit_rx) = std::sync::mpsc::channel();
        let parent_thread = std::thread::spawn(move || {
            let parent = unsafe { CreateMutexW(None, true, PCWSTR(parent_name.as_ptr())) }.unwrap();
            ready_tx.send(parent.0).unwrap();
            exit_rx.recv().unwrap();
        });
        let parent = HANDLE(ready_rx.recv().unwrap());
        let event = CancelEvent::open(id).unwrap();

        assert!(unsafe { SetEvent(ownership) }.as_bool());
        exit_tx.send(()).unwrap();
        parent_thread.join().unwrap();
        assert!(!event.is_cancelled());
        assert!(unsafe { SetEvent(owner) }.as_bool());
        assert!(event.is_cancelled());

        unsafe {
            let _ = CloseHandle(parent);
            let _ = CloseHandle(owner);
            let _ = CloseHandle(ownership);
        }
    }

    struct DisconnectAfterReady {
        bytes: Vec<u8>,
        lines: usize,
    }

    impl Write for DisconnectAfterReady {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            if self.lines >= 2 {
                return Err(io::Error::new(io::ErrorKind::BrokenPipe, "launcher closed"));
            }
            self.lines += bytes.iter().filter(|byte| **byte == b'\n').count();
            self.bytes.extend_from_slice(bytes);
            Ok(bytes.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn authenticated_ready_makes_later_status_delivery_best_effort() {
        let sink = DisconnectAfterReady {
            bytes: Vec::new(),
            lines: 0,
        };
        let mut writer = StatusWriter::authenticated(
            sink,
            "0123456789abcdef0123456789abcdef".into(),
            Game::Gi,
            [0xab; PROOF_SIZE],
        );

        writer.state(State::Preparing).unwrap();
        writer.state(State::Ready).unwrap();
        writer.state(State::WaitingForGame).unwrap();
        writer
            .exported(1, "Genshin Impact/20260717T120000Z-abc123.json")
            .unwrap();

        assert_eq!(writer.writer.lines, 2);
    }

    #[test]
    fn status_is_one_redacted_ndjson_object() {
        let mut bytes = Vec::new();
        let mut writer = StatusWriter::new(bytes, "safe-job_1".into(), Game::Gi);
        writer
            .exported(2, "gi/20260717T120000Z-abc123.json")
            .unwrap();
        bytes = writer.writer;
        assert_eq!(bytes.iter().filter(|byte| **byte == b'\n').count(), 1);
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(value["schemaVersion"], 1);
        assert_eq!(value["jobId"], "safe-job_1");
        assert_eq!(value["game"], "gi");
        assert_eq!(value["kind"], "achievements");
        assert_eq!(value["state"], "exported");
        assert_eq!(value["itemCount"], 2);
        assert_eq!(value["outputFile"], "gi/20260717T120000Z-abc123.json");
        let text = String::from_utf8(bytes).unwrap();
        for forbidden in ["token", "cookie", "https://", r"C:\\Users\\"] {
            assert!(!text.contains(forbidden));
        }
    }

    #[test]
    fn authenticated_status_carries_the_exact_one_use_pipe_proof() {
        let mut writer = StatusWriter::authenticated(
            Vec::new(),
            "0123456789abcdef0123456789abcdef".into(),
            Game::Hsr,
            [0xab; PROOF_SIZE],
        );
        writer.state(State::Preparing).unwrap();
        let value: Value = serde_json::from_slice(&writer.writer).unwrap();
        assert_eq!(value["proof"], "ab".repeat(PROOF_SIZE));
        assert_eq!(value["jobId"], "0123456789abcdef0123456789abcdef");
        assert_eq!(value["game"], "hsr");
    }

    #[test]
    fn errors_collapse_to_allowlisted_codes() {
        assert_eq!(
            capture_error_code(&CaptureError::Snapshot(SnapshotError::Unknown(123456))),
            Some(ErrorCode::CaptureInvalidSnapshot)
        );
        assert_eq!(capture_error_code(&CaptureError::Cancelled), None);
        assert_eq!(
            output_error_code(&OutputError::Io(io::Error::other(
                "C:\\Users\\account\\secret"
            ))),
            ErrorCode::OutputWriteFailed
        );
    }

    #[test]
    fn state_and_error_tokens_are_an_exact_ascii_allowlist() {
        let states = [
            State::Preparing,
            State::Ready,
            State::WaitingForGame,
            State::Exported,
            State::Failed,
            State::Cancelled,
        ];
        assert_eq!(
            states.map(State::key),
            [
                "preparing",
                "ready",
                "waiting_for_game",
                "exported",
                "failed",
                "cancelled",
            ]
        );
        let errors = [
            ErrorCode::AdministratorRequired,
            ErrorCode::NormalUserRequired,
            ErrorCode::CancelUnavailable,
            ErrorCode::DecoderUnavailable,
            ErrorCode::CaptureStartFailed,
            ErrorCode::CaptureReadFailed,
            ErrorCode::CaptureTimeoutNoFrames,
            ErrorCode::CaptureTimeoutUnrecognizedFrames,
            ErrorCode::CaptureTimeoutNoCommands,
            ErrorCode::CaptureTimeout,
            ErrorCode::CaptureSafetyLimit,
            ErrorCode::CaptureParserFailed,
            ErrorCode::CaptureInvalidSnapshot,
            ErrorCode::CaptureCleanupFailed,
            ErrorCode::CaptureClosed,
            ErrorCode::OutputUnsafe,
            ErrorCode::OutputExists,
            ErrorCode::OutputWriteFailed,
            ErrorCode::InternalError,
        ];
        assert_eq!(
            errors.map(ErrorCode::key),
            [
                "administrator_required",
                "normal_user_required",
                "cancel_unavailable",
                "decoder_unavailable",
                "capture_start_failed",
                "capture_read_failed",
                "capture_timeout_no_frames",
                "capture_timeout_unrecognized_frames",
                "capture_timeout_no_commands",
                "capture_timeout",
                "capture_safety_limit",
                "capture_parser_failed",
                "capture_invalid_snapshot",
                "capture_cleanup_failed",
                "capture_closed",
                "output_unsafe",
                "output_exists",
                "output_write_failed",
                "internal_error",
            ]
        );
        assert!(errors.iter().all(|code| {
            code.key()
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte == b'_')
        }));
    }
}
