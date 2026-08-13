use std::{ffi::c_void, mem::size_of, ops::Range, ptr::read_unaligned, sync::mpsc, time::Duration};

use c_api::PacketMonitorPacketType;
use log::{debug, error, info, trace, warn};
use windows::w;

use crate::{CaptureBackend, Packet, PacketPayload, filter::PktMonFilter};

mod c_api;
mod c_filter;

#[derive(Debug)]
struct MonitorContext {
    sender: mpsc::SyncSender<Packet>,

    #[cfg(feature = "tokio")]
    notify: Option<std::sync::Weak<tokio::sync::Notify>>,
}

type UserContext = *mut MonitorContext;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum StreamEventKind {
    Started,
    Stopped,
    FatalError,
    ProcessInfo,
}

fn checked_event_kind(raw: u32) -> Option<StreamEventKind> {
    match raw {
        0 => Some(StreamEventKind::Started),
        1 => Some(StreamEventKind::Stopped),
        2 => Some(StreamEventKind::FatalError),
        3 => Some(StreamEventKind::ProcessInfo),
        _ => None,
    }
}

#[derive(Debug)]
pub struct RealTimeBackend {
    api: c_api::PacketMonitorApi,

    handle: c_api::PacketMonitorHandle,
    session: c_api::PacketMonitorSession,
    stream: c_api::PacketMonitorRealTimeStream,

    receiver: mpsc::Receiver<Packet>,

    #[allow(dead_code)]
    context: Box<MonitorContext>,

    #[cfg(feature = "tokio")]
    notify: Option<std::sync::Arc<tokio::sync::Notify>>,

    loaded: bool,
}

extern "stdcall" fn event_callback(
    _context: *mut c_void,
    event_info: *const c_api::PacketMonitorStreamEventInfo,
    event_kind: u32,
) {
    if event_info.is_null() {
        return;
    }
    let Some(event_kind) = checked_event_kind(event_kind) else {
        return;
    };
    match event_kind {
        StreamEventKind::Started => {
            let info = unsafe { &(*event_info).stream_start_info };
            debug!(
                "Packet monitor stream started with buffer size {} and truncation size {}",
                info.packet_buffer_size_in_bytes, info.truncation_size
            );
        }
        StreamEventKind::Stopped => {
            let info = unsafe { &(*event_info).stream_stop_info };
            if info.is_fatal_error {
                error!(
                    "Fatal packet monitor stream error with reason: {}",
                    info.reason
                );
            } else {
                debug!("Packet monitor stream stopped with reason: {}", info.reason);
            }
        }
        StreamEventKind::FatalError => {
            error!("Fatal packet monitor stream event");
        }
        StreamEventKind::ProcessInfo => {
            let info = unsafe { &(*event_info).stream_process_info };
            if info.is_warning {
                warn!(
                    "Packet monitor stream warning reason: {}, packet length: {}",
                    info.reason, info.packet_length
                );
            } else {
                error!(
                    "Packet monitor stream error reason: {}, packet length: {}",
                    info.reason, info.packet_length
                );
            }
        }
    }
}

trait ConstructionCleanup {
    fn close_stream(&self, stream: c_api::PacketMonitorRealTimeStream);
    fn close_session(&self, session: c_api::PacketMonitorSession);
    fn uninitialize(&self, handle: c_api::PacketMonitorHandle);
}

impl ConstructionCleanup for c_api::PacketMonitorApi {
    fn close_stream(&self, stream: c_api::PacketMonitorRealTimeStream) {
        (self.close_realtime_stream)(stream);
    }

    fn close_session(&self, session: c_api::PacketMonitorSession) {
        (self.close_session_handle)(session);
    }

    fn uninitialize(&self, handle: c_api::PacketMonitorHandle) {
        (self.uninitialize)(handle);
    }
}

struct BackendConstructionGuard<'a, C: ConstructionCleanup + ?Sized> {
    cleanup: &'a C,
    handle: Option<c_api::PacketMonitorHandle>,
    session: Option<c_api::PacketMonitorSession>,
    stream: Option<c_api::PacketMonitorRealTimeStream>,
    armed: bool,
}

impl<'a, C: ConstructionCleanup + ?Sized> BackendConstructionGuard<'a, C> {
    fn new(cleanup: &'a C) -> Self {
        Self {
            cleanup,
            handle: None,
            session: None,
            stream: None,
            armed: true,
        }
    }

    fn disarm(mut self) {
        self.armed = false;
    }
}

impl<C: ConstructionCleanup + ?Sized> Drop for BackendConstructionGuard<'_, C> {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        if let Some(stream) = self.stream.take() {
            self.cleanup.close_stream(stream);
        }
        if let Some(session) = self.session.take() {
            self.cleanup.close_session(session);
        }
        if let Some(handle) = self.handle.take() {
            self.cleanup.uninitialize(handle);
        }
    }
}

extern "stdcall" fn data_callback(
    context: *mut c_void,
    data: *const c_api::PacketMonitorStreamDataDescriptor,
) {
    if context.is_null() || data.is_null() {
        return;
    }
    let context = unsafe { &*(context as UserContext) };
    let descriptor = unsafe { &*data };
    let Some((metadata_offset, packet_range)) = checked_descriptor_ranges(descriptor) else {
        return;
    };
    let bytes = unsafe {
        std::slice::from_raw_parts(descriptor.data.cast::<u8>(), descriptor.data_size as usize)
    };
    let metadata = unsafe {
        read_unaligned(
            bytes
                .as_ptr()
                .add(metadata_offset)
                .cast::<c_api::PacketMonitorStreamMetadata>(),
        )
    };

    trace!("Packet type: {:?}", metadata.packet_type);

    if descriptor.missed_packet_write_count > 0 {
        warn!(
            "missed writing packets!!: {}",
            descriptor.missed_packet_write_count
        );
    }

    if descriptor.missed_packet_read_count > 0 {
        warn!(
            "missed reading packets!!: {}",
            descriptor.missed_packet_read_count
        );
    }

    let packet_payload_vector = bytes[packet_range].to_vec();

    // trace!("Cloned time: {:?}", start.elapsed().as_nanos());

    let packet = Packet {
        component_id: metadata.component_id,
        payload: match PacketMonitorPacketType::try_from(metadata.packet_type) {
            Ok(packet_type) => match packet_type {
                PacketMonitorPacketType::PktMonPayload_Unknown => {
                    PacketPayload::Unknown(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_Ethernet => {
                    PacketPayload::Ethernet(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_WiFi => {
                    PacketPayload::WiFi(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_IP => {
                    PacketPayload::IP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_HTTP => {
                    PacketPayload::HTTP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_TCP => {
                    PacketPayload::TCP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_UDP => {
                    PacketPayload::UDP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_ARP => {
                    PacketPayload::ARP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_ICMP => {
                    PacketPayload::ICMP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_ESP => {
                    PacketPayload::ESP(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_AH => {
                    PacketPayload::AH(packet_payload_vector)
                }
                PacketMonitorPacketType::PktMonPayload_L4Payload => {
                    PacketPayload::L4Payload(packet_payload_vector)
                }
            },
            Err(()) => PacketPayload::Unknown(packet_payload_vector),
        },
    };

    // let sender = context.sender.clone();
    // trace!("lock time: {:?}", start.elapsed().as_nanos());
    // Never let captured traffic grow an unbounded in-memory queue. Dropping an
    // overflow frame is safer; Pengo will then reject the incomplete snapshot.
    let _ = context.sender.try_send(packet);
    // trace!("send time: {:?}", start.elapsed().as_nanos());

    #[cfg(feature = "tokio")]
    if let Some(ref notify) = context.notify {
        if let Some(notify) = notify.upgrade() {
            notify.notify_one();
        }
    }
}

const MAX_DESCRIPTOR_BYTES: u32 = 64 * 1024;
const MAX_PACKET_BYTES: u32 = 9_000;

fn checked_descriptor_ranges(
    descriptor: &c_api::PacketMonitorStreamDataDescriptor,
) -> Option<(usize, Range<usize>)> {
    if descriptor.data.is_null()
        || descriptor.data_size == 0
        || descriptor.data_size > MAX_DESCRIPTOR_BYTES
        || descriptor.packet_length > MAX_PACKET_BYTES
    {
        return None;
    }
    let metadata_end = descriptor
        .metadata_offset
        .checked_add(size_of::<c_api::PacketMonitorStreamMetadata>() as u32)?;
    let packet_end = descriptor
        .packet_offset
        .checked_add(descriptor.packet_length)?;
    if metadata_end > descriptor.data_size || packet_end > descriptor.data_size {
        return None;
    }
    Some((
        descriptor.metadata_offset as usize,
        descriptor.packet_offset as usize..packet_end as usize,
    ))
}

impl RealTimeBackend {
    pub fn new() -> std::io::Result<Self> {
        let api = c_api::PacketMonitorApi::new()?;
        let mut construction = BackendConstructionGuard::new(&api);

        #[cfg(feature = "tokio")]
        let notify = Some(std::sync::Arc::new(tokio::sync::Notify::new()));

        let mut handle = c_api::PacketMonitorHandle::default();
        (api.initialize)(
            c_api::PACKETMONITOR_API_VERSION,
            std::ptr::null_mut(),
            &mut handle,
        )
        .ok()?;
        construction.handle = Some(handle);
        trace!("Initialized handle {:?}", handle);

        let mut session = c_api::PacketMonitorSession::default();
        (api.create_live_session)(handle, w!("PktMon Rust"), &mut session).ok()?;
        construction.session = Some(session);

        trace!("Created session {:?}", session);

        let (sender, receiver) = mpsc::sync_channel(1024);

        let context_box = Box::new(MonitorContext {
            sender,

            #[cfg(feature = "tokio")]
            notify: notify.clone().map(|n| std::sync::Arc::downgrade(&n)),
        });
        let context_ptr = &*context_box as *const _;

        let stream_config = c_api::PacketMonitorRealTimeStreamConfiguration {
            user_context: context_ptr as *mut c_void,
            event_callback: event_callback as *mut _,
            data_callback: data_callback as *mut _,
            buffer_size_multiplier: 10, // Idk if this is a good default
            truncation_size: 9000,      // Max value
        };

        let mut stream = c_api::PacketMonitorRealTimeStream::default();
        (api.create_realtime_stream)(handle, &stream_config, &mut stream).ok()?;
        construction.stream = Some(stream);

        trace!("Created stream {:?}", stream);

        (api.attach_output_to_session)(session, stream).ok()?;
        construction.disarm();

        Ok(RealTimeBackend {
            api,

            handle,
            session,
            stream,

            context: context_box,

            receiver,

            #[cfg(feature = "tokio")]
            notify,

            loaded: true,
        })
    }
}

pub(crate) fn api_available() -> bool {
    c_api::PacketMonitorApi::new().is_ok()
}

impl CaptureBackend for RealTimeBackend {
    fn start(&mut self) -> std::io::Result<()> {
        info!("Setting session active");
        (self.api.set_session_active)(self.session, true).ok()?;

        Ok(())
    }

    fn stop(&mut self) -> std::io::Result<()> {
        info!("Setting session inactive");
        (self.api.set_session_active)(self.session, false).ok()?;

        Ok(())
    }

    fn unload(&mut self) -> std::io::Result<()> {
        if self.loaded {
            debug!("Closing stream");
            (self.api.close_realtime_stream)(self.stream);

            debug!("Closing session");
            (self.api.close_session_handle)(self.session);

            debug!("Uninitializing");
            (self.api.uninitialize)(self.handle);

            self.loaded = false;
        }

        Ok(())
    }

    fn add_filter(&mut self, filter: PktMonFilter) -> std::io::Result<()> {
        info!("Adding filter");
        (self.api.add_capture_constraint)(self.session, &filter.into()).ok()?;

        Ok(())
    }

    fn next_packet(&self) -> Result<Packet, mpsc::RecvError> {
        debug!("Receiving packet");
        self.receiver.recv()
    }

    fn next_packet_timeout(&self, timeout: Duration) -> Result<Packet, mpsc::RecvTimeoutError> {
        debug!("Receiving packet with timeout");
        self.receiver.recv_timeout(timeout)
    }

    fn try_next_packet(&self) -> Result<Packet, mpsc::TryRecvError> {
        self.receiver.try_recv()
    }

    #[cfg(feature = "tokio")]
    fn notify(&self) -> Option<std::sync::Arc<tokio::sync::Notify>> {
        self.notify.clone()
    }
}

impl Drop for RealTimeBackend {
    fn drop(&mut self) {
        self.unload().unwrap();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::RefCell, ptr::NonNull};

    #[derive(Default)]
    struct FakeCleanup {
        calls: RefCell<Vec<&'static str>>,
    }

    impl ConstructionCleanup for FakeCleanup {
        fn close_stream(&self, _stream: c_api::PacketMonitorRealTimeStream) {
            self.calls.borrow_mut().push("stream");
        }

        fn close_session(&self, _session: c_api::PacketMonitorSession) {
            self.calls.borrow_mut().push("session");
        }

        fn uninitialize(&self, _handle: c_api::PacketMonitorHandle) {
            self.calls.borrow_mut().push("handle");
        }
    }

    fn descriptor() -> c_api::PacketMonitorStreamDataDescriptor {
        c_api::PacketMonitorStreamDataDescriptor {
            data: NonNull::<u8>::dangling().as_ptr().cast(),
            data_size: 256,
            metadata_offset: 8,
            packet_offset: 64,
            packet_length: 128,
            missed_packet_write_count: 0,
            missed_packet_read_count: 0,
        }
    }

    #[test]
    fn callback_ranges_accept_only_bounded_in_buffer_offsets() {
        assert_eq!(checked_descriptor_ranges(&descriptor()), Some((8, 64..192)));
        for invalid in [
            c_api::PacketMonitorStreamDataDescriptor {
                data: std::ptr::null(),
                ..descriptor()
            },
            c_api::PacketMonitorStreamDataDescriptor {
                data_size: MAX_DESCRIPTOR_BYTES + 1,
                ..descriptor()
            },
            c_api::PacketMonitorStreamDataDescriptor {
                metadata_offset: 250,
                ..descriptor()
            },
            c_api::PacketMonitorStreamDataDescriptor {
                packet_offset: u32::MAX,
                ..descriptor()
            },
            c_api::PacketMonitorStreamDataDescriptor {
                packet_length: MAX_PACKET_BYTES + 1,
                ..descriptor()
            },
        ] {
            assert_eq!(checked_descriptor_ranges(&invalid), None);
        }
    }

    #[test]
    fn event_kind_ffi_accepts_only_documented_integer_values() {
        assert_eq!(checked_event_kind(0), Some(StreamEventKind::Started));
        assert_eq!(checked_event_kind(1), Some(StreamEventKind::Stopped));
        assert_eq!(checked_event_kind(2), Some(StreamEventKind::FatalError));
        assert_eq!(checked_event_kind(3), Some(StreamEventKind::ProcessInfo));
        assert_eq!(checked_event_kind(4), None);
        assert_eq!(checked_event_kind(u32::MAX), None);

        event_callback(std::ptr::null_mut(), std::ptr::null(), 0);
        event_callback(std::ptr::null_mut(), std::ptr::null(), u32::MAX);
    }

    #[test]
    fn partial_construction_cleans_up_in_reverse_order() {
        for (has_session, has_stream, expected) in [
            (false, false, vec!["handle"]),
            (true, false, vec!["session", "handle"]),
            (true, true, vec!["stream", "session", "handle"]),
        ] {
            let cleanup = FakeCleanup::default();
            {
                let mut guard = BackendConstructionGuard::new(&cleanup);
                guard.handle = Some(c_api::PacketMonitorHandle::default());
                if has_session {
                    guard.session = Some(c_api::PacketMonitorSession::default());
                }
                if has_stream {
                    guard.stream = Some(c_api::PacketMonitorRealTimeStream::default());
                }
            }
            assert_eq!(*cleanup.calls.borrow(), expected);
        }
    }

    #[test]
    fn successful_construction_disarms_cleanup() {
        let cleanup = FakeCleanup::default();
        let mut guard = BackendConstructionGuard::new(&cleanup);
        guard.handle = Some(c_api::PacketMonitorHandle::default());
        guard.session = Some(c_api::PacketMonitorSession::default());
        guard.stream = Some(c_api::PacketMonitorRealTimeStream::default());
        guard.disarm();
        assert!(cleanup.calls.borrow().is_empty());
    }
}
