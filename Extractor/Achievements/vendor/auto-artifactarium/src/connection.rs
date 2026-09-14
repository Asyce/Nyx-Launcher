use etherparse::{NetSlice, SlicedPacket, TransportSlice, UdpHeader};
use std::net::IpAddr;
use tracing::{debug, info, instrument, trace, warn};

use crate::{ConnectionPacket, PacketDirection};

#[instrument(skip_all)]
pub fn parse_connection_packet(port_filter: &[u16], bytes: Vec<u8>) -> Option<ConnectionPacket> {
    let (source_ip, destination_ip, udp, payload) = parse_udp(bytes)?;
    let (direction, flow) = validate_flow(port_filter, source_ip, destination_ip, udp)?;

    if payload.len() < 4 {
        None
    } else if payload.len() <= 20 {
        let code = u32::from_be_bytes(payload.get(..4)?.try_into().ok()?);
        match code {
            0xFF if direction == PacketDirection::Sent => {
                info!("handshake requested");
                Some(ConnectionPacket::HandshakeRequested(flow, direction))
            }
            0xFF => None,
            404 => {
                warn!("disconnected packet");
                Some(ConnectionPacket::Disconnected(flow, direction))
            }
            _ => {
                trace!("handshake established");
                Some(ConnectionPacket::HandshakeEstablished(flow, direction))
            }
        }
    } else {
        Some(ConnectionPacket::SegmentData(flow, direction, payload))
    }
}

#[instrument(skip_all, fields(len = data.len()))]
pub fn parse_udp(data: Vec<u8>) -> Option<(IpAddr, IpAddr, UdpHeader, Vec<u8>)> {
    let packet = match SlicedPacket::from_ethernet(&data) {
        Ok(p) => p,
        Err(e) => {
            debug!("failed: {e}");
            return None;
        }
    };

    let (source_ip, destination_ip) = match packet.net? {
        NetSlice::Ipv4(ip) => (
            ip.header().source_addr().into(),
            ip.header().destination_addr().into(),
        ),
        NetSlice::Ipv6(ip) => (
            ip.header().source_addr().into(),
            ip.header().destination_addr().into(),
        ),
    };

    // sanity checking the pcap filters
    let Some(transport) = packet.transport else {
        debug!("transport was not present");
        return None;
    };

    let TransportSlice::Udp(udp) = transport else {
        debug!("packet was not udp");
        return None;
    };

    trace!("complete");

    Some((
        source_ip,
        destination_ip,
        udp.to_header(),
        udp.payload().to_vec(),
    ))
}

fn validate_flow(
    port_filter: &[u16],
    source_ip: IpAddr,
    destination_ip: IpAddr,
    udp: UdpHeader,
) -> Option<(PacketDirection, crate::FlowId)> {
    let (src, dest) = (udp.source_port, udp.destination_port);
    match (port_filter.contains(&src), port_filter.contains(&dest)) {
        (true, false) => Some((
            PacketDirection::Received,
            crate::FlowId::new(destination_ip, dest, source_ip, src),
        )),
        (false, true) => Some((
            PacketDirection::Sent,
            crate::FlowId::new(source_ip, src, destination_ip, dest),
        )),
        _ => {
            trace!("incorrect ports");
            None
        }
    }
}
