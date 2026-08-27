//! Parse network packets transmitted between the game and the server
//!
//! Packets are built up in following layers depending on the purpose of the packet:
//!
//! - Packets for connection management ([`GamePacket::Connection`])
//!     - **Ethernet/IP/UDP**, handled using [`etherparse`]
//!     - **[`ConnectionPacket`]**, containing events for connection establishment/disconnection
//! - Packets for game commands ([`GamePacket::Commands`])
//!     - **Ethernet/IP/UDP**, handled using [`etherparse`]
//!     - **KCP**, handled using [`kcp`]
//!         - The KCP header contains an extra field that needs to be removed
//!           to be compatible with the regular KCP protocol
//!     - **[`GameCommand`]**, encrypted using XOR
//!     - **Protobuf**, payload, needs to be parsed into using the types generated in [`gen::proto`]
//!
//! [`GameCommand`]s are encrypted using an XOR-key.
//! One of the first packets sent is a request for a new key from a seed.
//! That key is used for the rest of the packets.
//! This means the recording for packets needs to start before the game starts (train hyperdrive).
//!
//! ## Example
//! ```
//! use auto_reliquary::{GamePacket, GameSniffer, ConnectionPacket};
//!
//! let packets: Vec<Vec<u8>> = vec![/**/];
//!
//! let mut sniffer = GameSniffer::new();
//! for packet in packets {
//!     match sniffer.receive_packet(packet) {
//!         Some(GamePacket::Connection(ConnectionPacket::Disconnected)) => {
//!             println!("Disconnected!");
//!             break;
//!         }
//!         Some(GamePacket::Commands(commands)) => {
//!             for command in commands {
//!                 println!("{:?}", command);
//!             }
//!         }
//!         _ => {}
//!     }
//! }
//! ```
//!

use std::collections::HashMap;
use std::fmt;
use tracing::{info, info_span, instrument, warn};

use crate::connection::parse_connection_packet;
use crate::crypto::{decrypt_command, lookup_initial_key, new_key_from_seed};
// use crate::gen::protos::PlayerGetTokenScRsp;
use crate::kcp::KcpSniffer;
use crate::unk_utils::{
    matches_get_quest_data_sc_rsp, matches_player_get_token_sc_rsp, Achievement,
};

pub mod gen;

mod connection;
mod crypto;
mod kcp;
mod unk_utils;

const PORTS: [u16; 2] = [23301, 23302];

/// Top-level packet sent by the game
pub enum GamePacket {
    Connection(ConnectionPacket),
    Commands(Vec<GameCommand>),
}

/// Packet for connection management
pub enum ConnectionPacket {
    HandshakeRequested,
    Disconnected,
    HandshakeEstablished,
    SegmentData(PacketDirection, Vec<u8>),
}

/// Game command header.
///
/// Contains the type of the command in `command_id`
/// and the data encoded in protobuf in `proto_data`
///
/// ## Bit Layout
/// | Bit indices     |  Type |  Name |
/// | - | - | - |
/// |   0..4      |  `u32`  |  Header (magic constant) |
/// |   0..6      |  `u16`  |  command_id |
/// |   6..8      |  `u16`  |  header_len (unsure) |
/// |   8..12     |  `u32`  |  data_len |
/// |  12..12+data_len |  variable  |  proto_data |
/// | data_len..data_len+4  |  `u32`  |  Tail (magic constant) |
#[derive(Clone)]
pub struct GameCommand {
    pub command_id: u16,
    #[allow(unused)]
    pub header_len: u16,
    #[allow(unused)]
    pub data_len: u32,
    #[allow(unused)]
    pub proto_header: Vec<u8>,
    pub proto_data: Vec<u8>,
}

impl GameCommand {
    const HEADER_LEN: usize = 12;
    const TAIL_LEN: usize = 4;

    #[instrument(skip(bytes), fields(len = bytes.len()))]
    pub fn try_new(bytes: Vec<u8>) -> Option<Self> {
        let header_overhead = Self::HEADER_LEN + Self::TAIL_LEN;
        if bytes.len() < header_overhead {
            warn!(len = bytes.len(), "game command header incomplete");
            return None;
        }

        // skip header magic const
        let command_id = u16::from_be_bytes(bytes[4..6].try_into().unwrap());
        let header_len = u16::from_be_bytes(bytes[6..8].try_into().unwrap());
        let data_len = u32::from_be_bytes(bytes[8..12].try_into().unwrap());

        let data_start = Self::HEADER_LEN.checked_add(header_len as usize)?;
        let data_end = data_start.checked_add(data_len as usize)?;

        if data_end.checked_add(Self::TAIL_LEN) != Some(bytes.len()) {
            warn!(len = bytes.len(), "game command frame length invalid");
            return None;
        }

        let proto_header = bytes[12..data_start].to_vec();
        let proto_data = bytes[data_start..data_end].to_vec();
        Some(GameCommand {
            command_id,
            header_len,
            data_len,
            proto_header,
            proto_data,
        })
    }

    pub fn parse_proto<T: protobuf::Message>(&self) -> protobuf::Result<T> {
        T::parse_from_bytes(&self.proto_data)
    }
}

#[cfg(test)]
mod tests {
    use super::GameCommand;

    #[test]
    fn game_command_frame_boundaries() {
        let valid = vec![0; GameCommand::HEADER_LEN + GameCommand::TAIL_LEN];

        assert!(GameCommand::try_new(valid.clone()).is_some());
        for end in 0..valid.len() {
            assert!(GameCommand::try_new(valid[..end].to_vec()).is_none());
        }

        let mut oversized_header = valid.clone();
        oversized_header[6..8].copy_from_slice(&u16::MAX.to_be_bytes());
        assert!(GameCommand::try_new(oversized_header).is_none());

        let mut oversized_data = valid.clone();
        oversized_data[8..12].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(GameCommand::try_new(oversized_data).is_none());

        let mut trailing = valid;
        trailing.push(0);
        assert!(GameCommand::try_new(trailing).is_none());
    }
}

impl fmt::Debug for GameCommand {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("GameCommand")
            .field("command_id", &self.command_id)
            .field("header_len", &self.header_len)
            .field("data_len", &self.data_len)
            .finish()
    }
}

#[derive(Debug, Clone, Copy, Hash, PartialEq, Eq)]
pub enum PacketDirection {
    Sent,
    Received,
}

pub enum Key {
    Dispatch(Vec<u8>),
    Session(Vec<u8>),
}

#[derive(Default)]
pub struct GameSniffer {
    sent_kcp: Option<KcpSniffer>,
    recv_kcp: Option<KcpSniffer>,
    key: Option<Key>,
    initial_keys: HashMap<u32, Vec<u8>>,
}

impl GameSniffer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn set_initial_keys(mut self, initial_keys: HashMap<u32, Vec<u8>>) -> Self {
        self.initial_keys = initial_keys;
        self
    }

    #[instrument(skip_all, fields(len = bytes.len()))]
    pub fn receive_packet(&mut self, bytes: Vec<u8>) -> Option<GamePacket> {
        let packet = parse_connection_packet(&PORTS, bytes)?;
        match packet {
            ConnectionPacket::HandshakeRequested => {
                info!("handshake requested, resetting state");
                self.recv_kcp = None;
                self.sent_kcp = None;
                self.key = None;
                Some(GamePacket::Connection(packet))
            }
            ConnectionPacket::HandshakeEstablished | ConnectionPacket::Disconnected => {
                self.key = None;
                Some(GamePacket::Connection(packet))
            }

            ConnectionPacket::SegmentData(direction, kcp_seg) => {
                let commands = self.receive_kcp_segment(direction, &kcp_seg);
                match commands {
                    Some(commands) => Some(GamePacket::Commands(commands)),
                    None => Some(GamePacket::Connection(ConnectionPacket::SegmentData(
                        direction, kcp_seg,
                    ))),
                }
            }
        }
    }

    fn receive_kcp_segment(
        &mut self,
        direction: PacketDirection,
        kcp_seg: &[u8],
    ) -> Option<Vec<GameCommand>> {
        let kcp = match direction {
            PacketDirection::Sent => &mut self.sent_kcp,
            PacketDirection::Received => &mut self.recv_kcp,
        };

        if kcp.is_none() {
            let new_kcp = KcpSniffer::try_new(kcp_seg)?;
            *kcp = Some(new_kcp);
        }

        if let Some(kcp) = kcp {
            let commands = kcp
                .receive_segments(kcp_seg)
                .into_iter()
                .filter_map(|data| self.receive_command(data))
                .collect();

            return Some(commands);
        }

        None
    }

    #[instrument(skip_all, fields(len = data.len()))]
    fn receive_command(&mut self, mut data: Vec<u8>) -> Option<GameCommand> {
        let key = match &self.key {
            Some(k) => k,
            None => {
                let initial_key = lookup_initial_key(&self.initial_keys, &data)?;
                self.key = Some(Key::Dispatch(initial_key));
                self.key.as_ref()?
            }
        };
        let key_bytes = match key {
            Key::Dispatch(k) | Key::Session(k) => k,
        };

        decrypt_command(key_bytes, &mut data);

        let command = GameCommand::try_new(data)?;

        let span = info_span!("command", ?command);
        let _enter = span.enter();

        info!("received");

        // if !matches!(
        //     command.command_id,
        //     command_id::PLAYER_GET_TOKEN_SC_RSP | command_id::GET_QUEST_DATA_SC_RSP
        // ) {
        //     return None;
        // }

        if let Some(Key::Dispatch(_)) = self.key {
            if let Some(seed) = matches_player_get_token_sc_rsp(command.proto_data.clone()) {
                self.key = Some(Key::Session(new_key_from_seed(seed)));
            }
        }

        Some(command)
    }
}

pub fn matches_achievement_packet(game_command: &GameCommand) -> Option<Vec<Achievement>> {
    matches_get_quest_data_sc_rsp(&game_command.proto_data)
}
