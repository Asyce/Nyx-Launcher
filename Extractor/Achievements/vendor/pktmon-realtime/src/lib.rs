//! Pengo's pinned, realtime-only subset of pktmon 0.6.2.
//!
//! The Windows 10 ETL fallback is deliberately not compiled because it changes
//! machine-wide Packet Monitor filters. This crate supports the Windows 11 live
//! API only.

#![allow(
    clippy::clone_on_copy,
    clippy::derivable_impls,
    clippy::enum_variant_names,
    clippy::explicit_counter_loop,
    clippy::missing_transmute_annotations
)]

use filter::PktMonFilter;
use log::{debug, info};
use realtime::RealTimeBackend;
use std::{
    fmt::Debug,
    io,
    sync::mpsc::{RecvError, RecvTimeoutError, TryRecvError},
    time::Duration,
};

mod ctypes;
pub mod filter;
mod realtime;

/// Checks only whether the independent realtime API and all required exports
/// exist. It does not initialize Packet Monitor or begin a capture.
pub fn realtime_api_available() -> bool {
    realtime::api_available()
}

#[derive(Debug, Clone, Hash, PartialEq, Eq)]
pub enum PacketPayload {
    Unknown(Vec<u8>),
    Ethernet(Vec<u8>),
    WiFi(Vec<u8>),
    IP(Vec<u8>),
    HTTP(Vec<u8>),
    TCP(Vec<u8>),
    UDP(Vec<u8>),
    ARP(Vec<u8>),
    ICMP(Vec<u8>),
    ESP(Vec<u8>),
    AH(Vec<u8>),
    L4Payload(Vec<u8>),
}

impl PacketPayload {
    pub fn to_vec(&self) -> &Vec<u8> {
        match self {
            Self::Unknown(value)
            | Self::Ethernet(value)
            | Self::WiFi(value)
            | Self::IP(value)
            | Self::HTTP(value)
            | Self::TCP(value)
            | Self::UDP(value)
            | Self::ARP(value)
            | Self::ICMP(value)
            | Self::ESP(value)
            | Self::AH(value)
            | Self::L4Payload(value) => value,
        }
    }
}

#[derive(Debug, Clone, Hash, PartialEq, Eq)]
pub struct Packet {
    pub component_id: u16,
    pub payload: PacketPayload,
}

pub(crate) trait CaptureBackend: Debug + Send {
    fn start(&mut self) -> io::Result<()>;
    fn stop(&mut self) -> io::Result<()>;
    fn unload(&mut self) -> io::Result<()>;
    fn add_filter(&mut self, filter: PktMonFilter) -> io::Result<()>;
    fn next_packet(&self) -> Result<Packet, RecvError>;
    fn next_packet_timeout(&self, timeout: Duration) -> Result<Packet, RecvTimeoutError>;
    fn try_next_packet(&self) -> Result<Packet, TryRecvError>;

    #[cfg(feature = "tokio")]
    fn notify(&self) -> Option<std::sync::Arc<tokio::sync::Notify>>;
}

pub struct Capture {
    backend: RealTimeBackend,
    running: bool,
}

impl Debug for Capture {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.debug_struct("Capture").finish_non_exhaustive()
    }
}

impl Capture {
    pub fn new() -> io::Result<Self> {
        Ok(Self {
            backend: RealTimeBackend::new()?,
            running: false,
        })
    }

    pub fn start(&mut self) -> io::Result<()> {
        if !self.running {
            self.backend.start()?;
            self.running = true;
            info!("realtime capture started");
        }
        Ok(())
    }

    pub fn stop(&mut self) -> io::Result<()> {
        if self.running {
            self.running = false;
            self.backend.stop()?;
            info!("realtime capture stopped");
        }
        Ok(())
    }

    pub fn unload(mut self) -> io::Result<()> {
        self.stop()?;
        self.backend.unload()
    }

    pub fn add_filter(&mut self, filter: PktMonFilter) -> io::Result<()> {
        self.backend.add_filter(filter)
    }

    pub fn next_packet(&self) -> Result<Packet, RecvError> {
        self.backend.next_packet()
    }

    pub fn next_packet_timeout(&self, timeout: Duration) -> Result<Packet, RecvTimeoutError> {
        self.backend.next_packet_timeout(timeout)
    }

    pub fn try_next_packet(&self) -> Result<Packet, TryRecvError> {
        self.backend.try_next_packet()
    }
}

impl Drop for Capture {
    fn drop(&mut self) {
        if self.running {
            if let Err(error) = self.stop() {
                debug!("realtime capture stop failed: {error:?}");
            }
        }
    }
}
