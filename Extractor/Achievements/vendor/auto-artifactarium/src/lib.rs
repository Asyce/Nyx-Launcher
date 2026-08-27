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
//! use auto_artifactarium::{GamePacket, GameSniffer, ConnectionPacket};
//!
//! let packets: Vec<Vec<u8>> = vec![/**/];
//!
//! let mut sniffer = GameSniffer::new();
//! for packet in packets {
//!     match sniffer.receive_packet(packet) {
//!         Some(GamePacket::Connection(ConnectionPacket::Disconnected(..))) => {
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

use rsa::{pkcs1::DecodeRsaPrivateKey, RsaPrivateKey};
use std::collections::HashMap;
use std::fmt;
use std::net::{IpAddr, SocketAddr};
use tracing::{error, info, info_span, instrument, warn};
use zeroize::{Zeroize, Zeroizing};

use crate::connection::parse_connection_packet;
use crate::crypto::{bruteforce, decrypt_command, lookup_initial_key};
// use crate::gen::protos::GetPlayerTokenRsp;
use crate::gen::protos::PacketHead;
use crate::kcp::{validate_kcp_segment, KcpSniffer};
use crate::unk_util::{
    matches_achievement_all_data_notify, matches_get_player_token_rsp, Achievement,
};
use crate::Key::Dispatch;

// pub mod command_id;
pub mod gen;

mod connection;
mod crypto;
mod cs_rand;
mod kcp;
mod unk_util;

const PORTS: [u16; 2] = [22101, 22102];

/// Top-level packet sent by the game
pub enum GamePacket {
    Connection(ConnectionPacket),
    Commands(Vec<GameCommand>),
}

/// Packet for connection management
pub enum ConnectionPacket {
    HandshakeRequested(FlowId, PacketDirection),
    Disconnected(FlowId, PacketDirection),
    HandshakeEstablished(FlowId, PacketDirection),
    SegmentData(FlowId, PacketDirection, Vec<u8>),
}

impl ConnectionPacket {
    fn flow(&self) -> FlowId {
        match self {
            Self::HandshakeRequested(flow, _)
            | Self::Disconnected(flow, _)
            | Self::HandshakeEstablished(flow, _)
            | Self::SegmentData(flow, ..) => *flow,
        }
    }
}

/// Game command header.
///
/// Contains the type of the command in `command_id`
/// and the data encoded in protobuf in `proto_data`
///
/// ## Bit Layout
/// | Bit indices     |  Type |  Name |
/// | - | - | - |
/// |   0..2      |  `u16`  |  Header (magic constant) |
/// |   2..4      |  `u16`  |  command_id |
/// |   4..6      |  `u16`  |  header_len (unsure) |
/// |   6..10     |  `u32`  |  data_len |
/// |  10..10+data_len |  variable  |  proto_data |
/// | data_len..data_len+2  |  `u16`  |  Tail (magic constant) |
#[derive(Clone)]
pub struct GameCommand {
    pub command_id: u16,
    #[allow(unused)]
    pub header_len: u16,
    #[allow(unused)]
    pub data_len: u32,
    #[allow(unused)]
    pub proto_header: Zeroizing<Vec<u8>>,
    pub proto_data: Zeroizing<Vec<u8>>,
}

impl GameCommand {
    const HEADER_LEN: usize = 10;
    const TAIL_LEN: usize = 2;

    #[instrument(skip(bytes), fields(len = bytes.len()))]
    pub fn try_new(bytes: Vec<u8>) -> Option<Self> {
        let bytes = Zeroizing::new(bytes);
        let header_overhead = Self::HEADER_LEN + Self::TAIL_LEN;
        if bytes.len() < header_overhead {
            warn!(len = bytes.len(), "game command header incomplete");
            return None;
        }

        if bytes[0] != 0x45
            || bytes[1] != 0x67
            || bytes[bytes.len() - 2] != 0x89
            || bytes[bytes.len() - 1] != 0xAB
        {
            error!("Didn't get magic in try_new!");
            return None;
        }

        // skip header magic const
        let command_id = u16::from_be_bytes(bytes[2..4].try_into().unwrap());
        let header_len = u16::from_be_bytes(bytes[4..6].try_into().unwrap());
        let data_len = u32::from_be_bytes(bytes[6..10].try_into().unwrap());

        let data_start = Self::HEADER_LEN.checked_add(header_len as usize)?;
        let data_end = data_start.checked_add(data_len as usize)?;

        if data_end.checked_add(Self::TAIL_LEN) != Some(bytes.len()) {
            warn!(len = bytes.len(), "game command frame length invalid");
            return None;
        }

        let proto_header = Zeroizing::new(bytes[10..data_start].to_vec());
        let proto_data = Zeroizing::new(bytes[data_start..data_end].to_vec());
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

    pub fn parse_header<T: protobuf::Message>(&self) -> protobuf::Result<T> {
        T::parse_from_bytes(&self.proto_header)
    }
}

#[cfg(test)]
mod tests {
    use super::{connection::parse_connection_packet, GameCommand};
    use super::{
        ConnectionPacket, FlowState, GamePacket, GameSniffer, Key, PacketDirection, PORTS,
    };
    use etherparse::PacketBuilder;

    #[test]
    fn game_command_frame_boundaries() {
        let valid = vec![0x45, 0x67, 0, 1, 0, 0, 0, 0, 0, 0, 0x89, 0xAB];

        assert!(GameCommand::try_new(valid.clone()).is_some());
        for end in 0..valid.len() {
            assert!(GameCommand::try_new(valid[..end].to_vec()).is_none());
        }

        let mut oversized_header = valid.clone();
        oversized_header[4..6].copy_from_slice(&u16::MAX.to_be_bytes());
        assert!(GameCommand::try_new(oversized_header).is_none());

        let mut oversized_data = valid.clone();
        oversized_data[6..10].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(GameCommand::try_new(oversized_data).is_none());

        let mut trailing = valid;
        trailing.extend_from_slice(&[0x89, 0xAB]);
        assert!(GameCommand::try_new(trailing).is_none());
    }

    #[test]
    fn game_command_buffers_are_zeroizing() {
        fn assert_zeroizing(_: &zeroize::Zeroizing<Vec<u8>>) {}

        let command =
            GameCommand::try_new(vec![0x45, 0x67, 0, 1, 0, 0, 0, 0, 0, 0, 0x89, 0xAB]).unwrap();
        assert_zeroizing(&command.proto_header);
        assert_zeroizing(&command.proto_data);
    }

    fn ipv4_packet(
        source: [u8; 4],
        source_port: u16,
        destination: [u8; 4],
        destination_port: u16,
        payload: &[u8],
    ) -> Vec<u8> {
        let builder = PacketBuilder::ethernet2([0; 6], [1; 6])
            .ipv4(source, destination, 64)
            .udp(source_port, destination_port);
        let mut packet = Vec::with_capacity(builder.size(payload.len()));
        builder.write(&mut packet, payload).unwrap();
        packet
    }

    fn ipv6_packet(
        source: [u8; 16],
        source_port: u16,
        destination: [u8; 16],
        destination_port: u16,
        payload: &[u8],
    ) -> Vec<u8> {
        let builder = PacketBuilder::ethernet2([0; 6], [1; 6])
            .ipv6(source, destination, 64)
            .udp(source_port, destination_port);
        let mut packet = Vec::with_capacity(builder.size(payload.len()));
        builder.write(&mut packet, payload).unwrap();
        packet
    }

    fn kcp_segment(conv: u32) -> Vec<u8> {
        let mut segment = vec![0; 32];
        segment[..4].copy_from_slice(&conv.to_le_bytes());
        segment[8] = 82;
        segment
    }

    #[test]
    fn isolates_udp_flows() {
        let client = [10, 0, 0, 1];
        let server = [10, 0, 0, 2];
        let data = [0u8; 21];
        let sent =
            parse_connection_packet(&PORTS, ipv4_packet(client, 40000, server, PORTS[0], &data));
        let received =
            parse_connection_packet(&PORTS, ipv4_packet(server, PORTS[0], client, 40000, &data));
        let sent_flow = match sent {
            Some(ConnectionPacket::SegmentData(flow, PacketDirection::Sent, _)) => flow,
            _ => panic!("sent flow was not parsed"),
        };
        let received_flow = match received {
            Some(ConnectionPacket::SegmentData(flow, PacketDirection::Received, _)) => flow,
            _ => panic!("received flow was not parsed"),
        };
        assert!(sent_flow == received_flow);
        assert!(parse_connection_packet(
            &PORTS,
            ipv4_packet(client, PORTS[1], server, PORTS[0], &data),
        )
        .is_none());

        let handshake = 0xFFu32.to_be_bytes();
        let disconnect = 404u32.to_be_bytes();
        let flow_a = ipv4_packet(client, 40000, server, PORTS[0], &handshake);
        let wrong_direction = ipv4_packet(server, PORTS[0], client, 40000, &handshake);
        let flow_a_disconnect = ipv4_packet(server, PORTS[0], client, 40000, &disconnect);
        let flow_b = ipv6_packet(
            [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1],
            40001,
            [0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 2],
            PORTS[1],
            &handshake,
        );
        let mut sniffer = GameSniffer::new();

        let mut malformed = kcp_segment(1);
        malformed[8] = 0;
        assert!(sniffer
            .receive_packet(ipv4_packet(client, 40000, server, PORTS[0], &malformed,))
            .is_none());
        assert!(sniffer.flow == FlowState::Initial);
        assert!(sniffer.kcp_conv.is_none());
        assert!(sniffer.sent_kcp.is_none());
        assert!(sniffer.recv_kcp.is_none());
        assert!(sniffer.receive_packet(wrong_direction).is_none());
        assert!(sniffer.flow == FlowState::Initial);
        assert!(matches!(
            sniffer.receive_packet(flow_a.clone()),
            Some(GamePacket::Connection(
                ConnectionPacket::HandshakeRequested(..)
            ))
        ));
        let bound = sniffer.flow;
        sniffer.key = Some(Key::Dispatch(zeroize::Zeroizing::new(vec![1])));
        sniffer.sent_time = Some(2);
        sniffer.possible_seeds.push(3);
        assert!(sniffer.receive_packet(flow_b.clone()).is_none());
        assert!(sniffer.flow == bound);
        assert!(sniffer.key.is_some());
        assert!(sniffer.sent_time == Some(2));
        assert!(sniffer.possible_seeds.as_slice() == [3]);

        let observational = ipv4_packet(client, 40000, server, PORTS[0], &1u32.to_be_bytes());
        assert!(sniffer.receive_packet(observational).is_some());
        assert!(sniffer.key.is_some());

        assert!(sniffer.receive_packet(flow_a).is_some());
        assert!(sniffer.flow == bound);
        assert!(sniffer.key.is_none());
        assert!(sniffer.sent_time.is_none());
        assert!(sniffer.possible_seeds.is_empty());

        let sent_conv = ipv4_packet(client, 40000, server, PORTS[0], &kcp_segment(1));
        let malformed_conv = ipv4_packet(server, PORTS[0], client, 40000, &malformed);
        let wrong_conv = ipv4_packet(server, PORTS[0], client, 40000, &kcp_segment(2));
        let matching_conv = ipv4_packet(server, PORTS[0], client, 40000, &kcp_segment(1));
        assert!(sniffer.receive_packet(sent_conv.clone()).is_some());
        assert!(sniffer.sent_kcp.is_some());
        assert!(sniffer.recv_kcp.is_none());
        assert!(sniffer.receive_packet(malformed_conv).is_none());
        assert!(sniffer.sent_kcp.is_some());
        assert!(sniffer.recv_kcp.is_none());
        assert!(sniffer.kcp_conv == Some(1));
        assert!(sniffer.receive_packet(wrong_conv).is_none());
        assert!(sniffer.sent_kcp.is_some());
        assert!(sniffer.recv_kcp.is_none());
        assert!(sniffer.kcp_conv == Some(1));
        assert!(sniffer.receive_packet(matching_conv).is_some());
        assert!(sniffer.recv_kcp.is_some());

        assert!(sniffer.receive_packet(flow_a_disconnect).is_some());
        assert!(sniffer.flow == FlowState::Closed);
        assert!(sniffer.sent_kcp.is_none());
        assert!(sniffer.recv_kcp.is_none());
        assert!(sniffer.kcp_conv.is_none());
        assert!(sniffer.receive_packet(sent_conv).is_none());
        assert!(sniffer.flow == FlowState::Closed);
        assert!(sniffer.receive_packet(flow_b).is_some());
        assert!(matches!(sniffer.flow, FlowState::Bound(_)));
        assert!(parse_connection_packet(&PORTS, vec![0; 13]).is_none());
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

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct FlowId {
    client: SocketAddr,
    server: SocketAddr,
}

impl FlowId {
    fn new(client_ip: IpAddr, client_port: u16, server_ip: IpAddr, server_port: u16) -> Self {
        Self {
            client: SocketAddr::new(client_ip, client_port),
            server: SocketAddr::new(server_ip, server_port),
        }
    }
}

pub enum Key {
    Dispatch(Zeroizing<Vec<u8>>),
    Session(Zeroizing<Vec<u8>>),
}

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum FlowState {
    #[default]
    Initial,
    Bound(FlowId),
    Closed,
}

#[derive(Default)]
pub struct GameSniffer {
    flow: FlowState,
    kcp_conv: Option<u32>,
    sent_kcp: Option<KcpSniffer>,
    recv_kcp: Option<KcpSniffer>,
    key: Option<Key>,
    initial_keys: HashMap<u16, Zeroizing<Vec<u8>>>,
    rsa_keys: Vec<RsaPrivateKey>,
    sent_time: Option<u64>,
    possible_seeds: Zeroizing<Vec<u64>>,
}

impl GameSniffer {
    pub fn new() -> Self {
        let pem_data_4 = include_str!("../keys/private_key_4.pem");
        let pem_data_5 = include_str!("../keys/private_key_5.pem");

        let rsa_4 = RsaPrivateKey::from_pkcs1_pem(pem_data_4);
        let rsa_5 = RsaPrivateKey::from_pkcs1_pem(pem_data_5);

        GameSniffer {
            rsa_keys: [rsa_4, rsa_5].into_iter().filter_map(Result::ok).collect(),
            ..Default::default()
        }
    }

    pub fn set_initial_keys(mut self, initial_keys: HashMap<u16, Vec<u8>>) -> Self {
        self.initial_keys = initial_keys
            .into_iter()
            .map(|(version, key)| (version, Zeroizing::new(key)))
            .collect();
        self
    }

    fn reset_session(&mut self) {
        self.recv_kcp = None;
        self.sent_kcp = None;
        self.key = None;
        self.kcp_conv = None;
        self.sent_time = None;
        self.possible_seeds.zeroize();
    }

    #[instrument(skip_all, fields(len = bytes.len()))]
    pub fn receive_packet(&mut self, bytes: Vec<u8>) -> Option<GamePacket> {
        let packet = parse_connection_packet(&PORTS, bytes)?;
        let flow = packet.flow();
        match self.flow {
            FlowState::Initial => match packet {
                ConnectionPacket::HandshakeRequested(_, PacketDirection::Sent) => {
                    self.reset_session();
                    self.flow = FlowState::Bound(flow);
                    Some(GamePacket::Connection(packet))
                }
                ConnectionPacket::SegmentData(_, direction, kcp_seg) => {
                    let commands = self.receive_kcp_segment(direction, &kcp_seg)?;
                    self.flow = FlowState::Bound(flow);
                    Some(GamePacket::Commands(commands))
                }
                _ => None,
            },
            FlowState::Bound(bound) if bound != flow => None,
            FlowState::Bound(_) => match packet {
                ConnectionPacket::HandshakeRequested(_, PacketDirection::Sent) => {
                    info!("handshake requested, resetting state");
                    self.reset_session();
                    Some(GamePacket::Connection(packet))
                }
                ConnectionPacket::Disconnected(..) => {
                    self.reset_session();
                    self.flow = FlowState::Closed;
                    Some(GamePacket::Connection(packet))
                }
                ConnectionPacket::HandshakeEstablished(..) => Some(GamePacket::Connection(packet)),
                ConnectionPacket::SegmentData(_, direction, kcp_seg) => self
                    .receive_kcp_segment(direction, &kcp_seg)
                    .map(GamePacket::Commands),
                ConnectionPacket::HandshakeRequested(..) => None,
            },
            FlowState::Closed => match packet {
                ConnectionPacket::HandshakeRequested(_, PacketDirection::Sent) => {
                    self.reset_session();
                    self.flow = FlowState::Bound(flow);
                    Some(GamePacket::Connection(packet))
                }
                _ => None,
            },
        }
    }

    fn receive_kcp_segment(
        &mut self,
        direction: PacketDirection,
        kcp_seg: &[u8],
    ) -> Option<Vec<GameCommand>> {
        let segment = validate_kcp_segment(kcp_seg)?;
        let conv = segment.conv_id;
        match self.kcp_conv {
            Some(expected) if expected != conv => return None,
            _ => {}
        }

        let packets = {
            let kcp = match direction {
                PacketDirection::Sent => &mut self.sent_kcp,
                PacketDirection::Received => &mut self.recv_kcp,
            };
            match kcp {
                Some(kcp) => kcp.receive_segments(&segment)?,
                None => {
                    let (candidate, packets) = KcpSniffer::try_new(&segment)?;
                    *kcp = Some(candidate);
                    packets
                }
            }
        };
        self.kcp_conv = Some(conv);
        Some(
            packets
                .into_iter()
                .filter_map(|data| self.receive_command(data))
                .collect(),
        )
    }

    #[instrument(skip_all, fields(len = data.len()))]
    fn receive_command(&mut self, mut data: Vec<u8>) -> Option<GameCommand> {
        let key_r = match &self.key {
            None => {
                let key = lookup_initial_key(&self.initial_keys, &data);
                match key {
                    Some(key) => {
                        self.key = Some(Dispatch(key));
                        self.key.as_ref().unwrap()
                    }
                    None => {
                        error!("No dispatch key found");
                        return None;
                    }
                }
            }
            Some(Dispatch(k)) => {
                let mut test = Zeroizing::new(data.clone());
                decrypt_command(k, &mut test);

                if test.len() >= 4
                    && test[0] == 0x45
                    && test[1] == 0x67
                    && test[test.len() - 2] == 0x89
                    && test[test.len() - 1] == 0xAB
                {
                    self.key.as_ref().unwrap()
                } else {
                    let sent_time = self.sent_time?;
                    let discovered_key = self
                        .possible_seeds
                        .iter()
                        .find_map(|&seed| bruteforce(sent_time, seed, &data));
                    match discovered_key {
                        Some(key) => {
                            self.possible_seeds.zeroize();
                            self.key = Some(Key::Session(key));
                            self.key.as_ref().unwrap()
                        }
                        None => {
                            error!("Couldn't bruteforce from deduced keys");
                            return None;
                        }
                    }
                }
            }
            Some(Key::Session(k)) => {
                let mut test = Zeroizing::new(data.clone());
                decrypt_command(k, &mut test);

                if test.len() >= 2 && test[0] == 0x45 && test[1] == 0x67 {
                    //|| test[test.len() - 2] == 0x89 && test[test.len() - 1] == 0xAB
                    self.key.as_ref().unwrap()
                } else {
                    warn!("Invalidated session key");
                    self.key = None;
                    error!("Session key dead, relaunch game");
                    return None;
                }
            }
        };

        let key = match key_r {
            Dispatch(k) | Key::Session(k) => k,
        };

        decrypt_command(key, &mut data);

        let command = GameCommand::try_new(data)?;

        let span = info_span!("command", ?command);
        let _enter = span.enter();

        info!("received");

        // if !matches!(
        //     command.command_id,
        //     command_id::GET_PLAYER_TOKEN_RSP | command_id::ACHIEVEMENT_ALL_DATA_NOTIFY
        // ) {
        //     return None;
        // }

        if let Some(Dispatch(_)) = self.key {
            if let Some(possible_seeds) =
                matches_get_player_token_rsp(&command.proto_data, &self.rsa_keys)
            {
                self.possible_seeds = possible_seeds;
                let header_command = command.parse_header::<PacketHead>().ok()?;
                self.sent_time = Some(header_command.sent_ms);
            }
        }

        Some(command)
    }
}

pub fn matches_achievement_packet(game_command: &GameCommand) -> Option<Vec<Achievement>> {
    matches_achievement_all_data_notify(&game_command.proto_data)
}
