use crate::{AchievementRecord, Game, SnapshotError, validate_complete_snapshot};
use pengo_pktmon_realtime::{
    Capture,
    filter::{PktMonFilter, TransportProtocol},
};
use std::{
    fmt,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

pub const MAX_PACKETS: u64 = 200_000;
pub const MAX_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_FRAME_BYTES: usize = 2 * 1024 * 1024;
pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(180);
pub const MAX_TIMEOUT: Duration = Duration::from_secs(300);

#[derive(Clone, Copy, Debug)]
pub struct CaptureLimits {
    pub timeout: Duration,
    pub max_packets: u64,
    pub max_bytes: u64,
    pub max_frame_bytes: usize,
}

impl Default for CaptureLimits {
    fn default() -> Self {
        Self {
            timeout: DEFAULT_TIMEOUT,
            max_packets: MAX_PACKETS,
            max_bytes: MAX_BYTES,
            max_frame_bytes: MAX_FRAME_BYTES,
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum FrameEvent {
    Frame(Vec<u8>),
    Idle,
    Closed,
}

#[derive(Clone, Copy, Debug, Default, Eq, Ord, PartialEq, PartialOrd)]
pub enum DecoderProgress {
    #[default]
    None,
    Transport,
    Commands,
}

pub trait FrameSource {
    fn start(&mut self) -> Result<(), String>;
    fn next(&mut self, wait: Duration) -> Result<FrameEvent, String>;
    fn stop(&mut self) -> Result<(), String>;
    fn unload(&mut self) -> Result<(), String>;
}

pub trait SnapshotDecoder {
    fn decode(&mut self, frame: &[u8]) -> Option<Vec<AchievementRecord>>;

    fn progress(&self) -> DecoderProgress {
        DecoderProgress::None
    }
}

pub trait CancelSignal {
    fn is_cancelled(&self) -> bool;
}

impl CancelSignal for AtomicBool {
    fn is_cancelled(&self) -> bool {
        self.load(Ordering::SeqCst)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackendChoice {
    PktMon,
    Npcap,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackendSelectionError {
    AdministratorRequired,
    NpcapRejectsAdministrator,
}

pub fn choose_backend(
    game: Game,
    administrator: bool,
) -> Result<BackendChoice, BackendSelectionError> {
    match (game, administrator) {
        (Game::Gi, false) => Ok(BackendChoice::Npcap),
        (Game::Gi, true) => Err(BackendSelectionError::NpcapRejectsAdministrator),
        (Game::Hsr, true) => Err(BackendSelectionError::NpcapRejectsAdministrator),
        (Game::Hsr, false) => Ok(BackendChoice::Npcap),
    }
}

#[derive(Debug)]
pub enum CaptureError {
    Start,
    Read,
    TimeoutNoFrames,
    TimeoutUnrecognizedFrames,
    TimeoutNoCommands,
    Timeout,
    Cancelled,
    PacketCap,
    ByteCap,
    FrameCap,
    ParserPanic,
    Snapshot(SnapshotError),
    Cleanup,
    Closed,
}

impl fmt::Display for CaptureError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Start => write!(
                f,
                "Windows realtime Packet Monitor could not start; Windows 11 and administrator rights are required"
            ),
            Self::Read => write!(f, "the bounded capture stopped unexpectedly"),
            Self::TimeoutNoFrames => write!(
                f,
                "capture timed out without seeing traffic on the reviewed game ports"
            ),
            Self::TimeoutUnrecognizedFrames => write!(
                f,
                "capture saw reviewed-port traffic but did not recognize the game transport"
            ),
            Self::TimeoutNoCommands => write!(
                f,
                "capture recognized the game transport but did not decode game commands"
            ),
            Self::Timeout => write!(
                f,
                "capture timed out; close the game and retry from a fresh start"
            ),
            Self::Cancelled => write!(f, "capture cancelled"),
            Self::PacketCap | Self::ByteCap | Self::FrameCap => {
                write!(f, "capture stopped at a safety limit")
            }
            Self::ParserPanic => write!(f, "the packet parser stopped safely"),
            Self::Snapshot(error) => error.fmt(f),
            Self::Cleanup => write!(
                f,
                "capture cleanup could not be proved; no JSON was written"
            ),
            Self::Closed => write!(f, "capture closed before a complete snapshot arrived"),
        }
    }
}

impl std::error::Error for CaptureError {}

pub fn capture_complete_snapshot<S: FrameSource + ?Sized, D: SnapshotDecoder>(
    source: &mut S,
    decoder: &mut D,
    game: Game,
    selected_catalog: &[u32],
    other_catalog: &[u32],
    limits: CaptureLimits,
    cancelled: &dyn CancelSignal,
) -> Result<Vec<u32>, CaptureError> {
    if limits.timeout > MAX_TIMEOUT
        || limits.max_packets > MAX_PACKETS
        || limits.max_bytes > MAX_BYTES
        || limits.max_frame_bytes > MAX_FRAME_BYTES
    {
        return Err(CaptureError::Start);
    }
    if source.start().is_err() {
        let _ = source.stop();
        let _ = source.unload();
        return Err(CaptureError::Start);
    }
    let started = Instant::now();
    let mut packets = 0_u64;
    let mut bytes = 0_u64;
    let result = loop {
        if cancelled.is_cancelled() {
            break Err(CaptureError::Cancelled);
        }
        if started.elapsed() >= limits.timeout {
            break Err(match (packets, decoder.progress()) {
                (0, _) => CaptureError::TimeoutNoFrames,
                (_, DecoderProgress::None) => CaptureError::TimeoutUnrecognizedFrames,
                (_, DecoderProgress::Transport) => CaptureError::TimeoutNoCommands,
                (_, DecoderProgress::Commands) => CaptureError::Timeout,
            });
        }
        let event = match source.next(Duration::from_millis(250)) {
            Ok(event) => event,
            Err(_) => break Err(CaptureError::Read),
        };
        let frame = match event {
            FrameEvent::Idle => continue,
            FrameEvent::Closed => break Err(CaptureError::Closed),
            FrameEvent::Frame(frame) => frame,
        };
        packets = packets.saturating_add(1);
        if packets > limits.max_packets {
            break Err(CaptureError::PacketCap);
        }
        if frame.len() > limits.max_frame_bytes {
            break Err(CaptureError::FrameCap);
        }
        bytes = bytes.saturating_add(frame.len() as u64);
        if bytes > limits.max_bytes {
            break Err(CaptureError::ByteCap);
        }
        let decoded = match catch_unwind(AssertUnwindSafe(|| decoder.decode(&frame))) {
            Ok(value) => value,
            Err(_) => break Err(CaptureError::ParserPanic),
        };
        if let Some(records) = decoded {
            break validate_complete_snapshot(game, &records, selected_catalog, other_catalog)
                .map_err(CaptureError::Snapshot);
        }
    };
    let stop_ok = source.stop().is_ok();
    let unload_ok = source.unload().is_ok();
    if !stop_ok || !unload_ok {
        return Err(CaptureError::Cleanup);
    }
    result
}

pub struct RealTimeFrameSource {
    capture: Option<Capture>,
}

impl RealTimeFrameSource {
    pub fn new(ports: [u16; 2]) -> Result<Self, CaptureError> {
        let mut capture = Capture::new().map_err(|_| CaptureError::Start)?;
        for port in ports {
            capture
                .add_filter(PktMonFilter {
                    name: format!("Pengo achievement UDP {port}"),
                    transport_protocol: Some(TransportProtocol::UDP),
                    port: port.into(),
                    ..PktMonFilter::default()
                })
                .map_err(|_| CaptureError::Start)?;
        }
        Ok(Self {
            capture: Some(capture),
        })
    }
}

impl FrameSource for RealTimeFrameSource {
    fn start(&mut self) -> Result<(), String> {
        self.capture
            .as_mut()
            .ok_or("missing capture")?
            .start()
            .map_err(|_| "start failed".into())
    }
    fn next(&mut self, wait: Duration) -> Result<FrameEvent, String> {
        use std::sync::mpsc::RecvTimeoutError;
        match self
            .capture
            .as_ref()
            .ok_or("missing capture")?
            .next_packet_timeout(wait)
        {
            Ok(packet) => Ok(FrameEvent::Frame(packet.payload.to_vec().clone())),
            Err(RecvTimeoutError::Timeout) => Ok(FrameEvent::Idle),
            Err(RecvTimeoutError::Disconnected) => Ok(FrameEvent::Closed),
        }
    }
    fn stop(&mut self) -> Result<(), String> {
        self.capture.as_mut().map_or(Ok(()), |capture| {
            capture.stop().map_err(|_| "stop failed".into())
        })
    }
    fn unload(&mut self) -> Result<(), String> {
        self.capture.take().map_or(Ok(()), |capture| {
            capture.unload().map_err(|_| "unload failed".into())
        })
    }
}

impl Drop for RealTimeFrameSource {
    fn drop(&mut self) {
        if let Some(mut capture) = self.capture.take() {
            let _ = capture.stop();
            let _ = capture.unload();
        }
    }
}

pub fn cancellation_flag() -> Result<Arc<AtomicBool>, ctrlc::Error> {
    let flag = Arc::new(AtomicBool::new(false));
    let handler_flag = Arc::clone(&flag);
    ctrlc::set_handler(move || handler_flag.store(true, Ordering::SeqCst))?;
    Ok(flag)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{collections::VecDeque, sync::Mutex};

    struct FakeSource {
        events: VecDeque<FrameEvent>,
        lifecycle: Arc<Mutex<Vec<&'static str>>>,
        stop_ok: bool,
        unload_ok: bool,
    }
    impl FrameSource for FakeSource {
        fn start(&mut self) -> Result<(), String> {
            self.lifecycle.lock().unwrap().push("start");
            Ok(())
        }
        fn next(&mut self, _: Duration) -> Result<FrameEvent, String> {
            Ok(self.events.pop_front().unwrap_or(FrameEvent::Idle))
        }
        fn stop(&mut self) -> Result<(), String> {
            self.lifecycle.lock().unwrap().push("stop");
            self.stop_ok.then_some(()).ok_or("stop".into())
        }
        fn unload(&mut self) -> Result<(), String> {
            self.lifecycle.lock().unwrap().push("unload");
            self.unload_ok.then_some(()).ok_or("unload".into())
        }
    }
    struct FakeDecoder {
        result: Option<Vec<AchievementRecord>>,
        panic: bool,
        progress: DecoderProgress,
    }
    impl SnapshotDecoder for FakeDecoder {
        fn decode(&mut self, _: &[u8]) -> Option<Vec<AchievementRecord>> {
            if self.panic {
                panic!("parser")
            }
            self.result.take()
        }

        fn progress(&self) -> DecoderProgress {
            self.progress
        }
    }
    fn source(events: Vec<FrameEvent>) -> (FakeSource, Arc<Mutex<Vec<&'static str>>>) {
        let lifecycle = Arc::new(Mutex::new(Vec::new()));
        (
            FakeSource {
                events: events.into(),
                lifecycle: Arc::clone(&lifecycle),
                stop_ok: true,
                unload_ok: true,
            },
            lifecycle,
        )
    }
    fn limits() -> CaptureLimits {
        CaptureLimits {
            timeout: Duration::from_secs(1),
            max_packets: 2,
            max_bytes: 8,
            max_frame_bytes: 4,
        }
    }

    #[test]
    fn backend_selection_uses_npcap_for_both_normal_user_games_and_rejects_elevation() {
        assert_eq!(
            choose_backend(Game::Hsr, true),
            Err(BackendSelectionError::NpcapRejectsAdministrator)
        );
        assert_eq!(choose_backend(Game::Hsr, false), Ok(BackendChoice::Npcap));
        assert_eq!(
            choose_backend(Game::Gi, true),
            Err(BackendSelectionError::NpcapRejectsAdministrator)
        );
        assert_eq!(choose_backend(Game::Gi, false), Ok(BackendChoice::Npcap));
    }

    #[test]
    fn success_requires_stop_and_unload() {
        let (mut source, lifecycle) = source(vec![FrameEvent::Frame(vec![1])]);
        let mut decoder = FakeDecoder {
            result: Some(vec![AchievementRecord { id: 1, status: 2 }]),
            panic: false,
            progress: DecoderProgress::Commands,
        };
        let result = capture_complete_snapshot(
            &mut source,
            &mut decoder,
            Game::Gi,
            &[1],
            &[2],
            limits(),
            &AtomicBool::new(false),
        )
        .unwrap();
        assert_eq!(result, vec![1]);
        assert_eq!(*lifecycle.lock().unwrap(), vec!["start", "stop", "unload"]);
    }

    #[test]
    fn cleanup_failure_blocks_success() {
        let (mut failing_source, _) = source(vec![FrameEvent::Frame(vec![1])]);
        failing_source.stop_ok = false;
        let mut decoder = FakeDecoder {
            result: Some(vec![AchievementRecord { id: 1, status: 2 }]),
            panic: false,
            progress: DecoderProgress::Commands,
        };
        assert!(matches!(
            capture_complete_snapshot(
                &mut failing_source,
                &mut decoder,
                Game::Gi,
                &[1],
                &[2],
                limits(),
                &AtomicBool::new(false)
            ),
            Err(CaptureError::Cleanup)
        ));
    }

    #[test]
    fn cancel_timeout_close_and_caps_cleanup() {
        let cases = [
            (
                vec![FrameEvent::Idle],
                CaptureLimits {
                    timeout: Duration::ZERO,
                    ..limits()
                },
                false,
                CaptureError::TimeoutNoFrames,
            ),
            (
                vec![FrameEvent::Idle],
                limits(),
                true,
                CaptureError::Cancelled,
            ),
            (
                vec![FrameEvent::Closed],
                limits(),
                false,
                CaptureError::Closed,
            ),
            (
                vec![FrameEvent::Frame(vec![0; 5])],
                limits(),
                false,
                CaptureError::FrameCap,
            ),
            (
                vec![
                    FrameEvent::Frame(vec![0; 4]),
                    FrameEvent::Frame(vec![0; 4]),
                    FrameEvent::Frame(vec![0; 1]),
                ],
                limits(),
                false,
                CaptureError::PacketCap,
            ),
            (
                vec![FrameEvent::Frame(vec![0; 4]), FrameEvent::Frame(vec![0; 4])],
                CaptureLimits {
                    max_packets: 2,
                    max_bytes: 7,
                    ..limits()
                },
                false,
                CaptureError::ByteCap,
            ),
        ];
        for (events, test_limits, cancelled, expected) in cases {
            let (mut source, lifecycle) = source(events);
            let mut decoder = FakeDecoder {
                result: None,
                panic: false,
                progress: DecoderProgress::None,
            };
            let result = capture_complete_snapshot(
                &mut source,
                &mut decoder,
                Game::Gi,
                &[1],
                &[2],
                test_limits,
                &AtomicBool::new(cancelled),
            );
            assert_eq!(result.unwrap_err().to_string(), expected.to_string());
            let lifecycle = lifecycle.lock().unwrap();
            assert_eq!(
                &lifecycle[lifecycle.len().saturating_sub(2)..],
                ["stop", "unload"]
            );
        }
    }

    #[test]
    fn timeout_distinguishes_matching_frames_from_no_matching_frames() {
        let (mut source, _) = source(vec![FrameEvent::Frame(vec![1])]);
        let mut decoder = FakeDecoder {
            result: None,
            panic: false,
            progress: DecoderProgress::Commands,
        };
        let result = capture_complete_snapshot(
            &mut source,
            &mut decoder,
            Game::Gi,
            &[1],
            &[2],
            CaptureLimits {
                timeout: Duration::from_millis(1),
                max_packets: 2,
                max_bytes: 8,
                max_frame_bytes: 4,
            },
            &AtomicBool::new(false),
        );
        assert!(matches!(result, Err(CaptureError::Timeout)));
    }

    #[test]
    fn parser_panic_and_empty_recognized_packet_are_fatal() {
        let (mut panic_source, _) = source(vec![FrameEvent::Frame(vec![1])]);
        let mut decoder = FakeDecoder {
            result: None,
            panic: true,
            progress: DecoderProgress::Commands,
        };
        assert!(matches!(
            capture_complete_snapshot(
                &mut panic_source,
                &mut decoder,
                Game::Gi,
                &[1],
                &[2],
                limits(),
                &AtomicBool::new(false)
            ),
            Err(CaptureError::ParserPanic)
        ));
        let (mut empty_source, _) = source(vec![FrameEvent::Frame(vec![1])]);
        let mut decoder = FakeDecoder {
            result: Some(vec![]),
            panic: false,
            progress: DecoderProgress::Commands,
        };
        assert!(matches!(
            capture_complete_snapshot(
                &mut empty_source,
                &mut decoder,
                Game::Gi,
                &[1],
                &[2],
                limits(),
                &AtomicBool::new(false)
            ),
            Err(CaptureError::Snapshot(SnapshotError::Empty))
        ));
    }
}
