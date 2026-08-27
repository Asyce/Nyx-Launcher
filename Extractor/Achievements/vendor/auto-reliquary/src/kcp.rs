use std::time::SystemTime;

use kcp::{get_conv, Kcp};
use tracing::{error, info, instrument, span, trace, warn, Level};

const GAME_KCP_HEADER_LEN: usize = 28;

pub(crate) struct KcpSniffer {
    conv_id: u32,
    kcp: Kcp<Vec<u8>>,
    time_start: SystemTime,
}

pub(crate) struct ValidatedKcpSegment {
    pub(crate) conv_id: u32,
    bytes: Vec<u8>,
}

impl KcpSniffer {
    #[instrument(skip(segment))]
    pub fn try_new(segment: &ValidatedKcpSegment) -> Option<(Self, Vec<Vec<u8>>)> {
        let mut sniffer = Self::new(segment.conv_id);
        let received = sniffer.receive_segments(segment).or_else(|| {
            error!("could not create new kcp instance");
            None
        })?;
        Some((sniffer, received))
    }

    #[instrument]
    fn new(conv_id: u32) -> Self {
        info!("new connection, created new kcp instance");

        KcpSniffer {
            conv_id,
            kcp: new_kcp(conv_id),
            time_start: SystemTime::now(),
        }
    }

    #[instrument(skip_all, fields(conv_id = self.conv_id, len = segment.bytes.len()))]
    pub fn receive_segments(&mut self, segment: &ValidatedKcpSegment) -> Option<Vec<Vec<u8>>> {
        if segment.conv_id != self.conv_id {
            warn!(
                expected = self.conv_id,
                "packet did not belong to conversation"
            );
            return None;
        }

        match self.kcp.input(&segment.bytes) {
            Ok(size) => trace!(size, "input successful"),
            Err(e) => {
                warn!("could not input to kcp: {e}");
                return None;
            }
        }

        let mut recv = Vec::new();
        while let Ok(size) = self.kcp.peeksize() {
            let span = span!(Level::TRACE, "receiving", size);
            let _enter = span.enter();

            let mut bytes = vec![0; size];

            match self.kcp.recv(&mut bytes) {
                Ok(_size) => {
                    recv.push(bytes);
                }
                Err(e) => {
                    warn!(%e, "could not receive kcp bytes");
                }
            }
        }

        if let Err(e) = self.kcp.update(self.clock()) {
            warn!(%e, "could not update kcp state");
        }

        Some(recv)
    }

    #[inline]
    fn clock(&self) -> u32 {
        SystemTime::now()
            .duration_since(self.time_start)
            .expect("time went backwards")
            .as_millis() as u32
    }
}

#[inline]
fn new_kcp(conv_id: u32) -> Kcp<Vec<u8>> {
    let mut kcp = Kcp::new(conv_id, Vec::new());
    kcp.set_wndsize(1024, 1024);
    kcp
}

pub(crate) fn validate_kcp_segment(payload: &[u8]) -> Option<ValidatedKcpSegment> {
    if payload.len() < GAME_KCP_HEADER_LEN {
        warn!(len = payload.len(), "kcp header was too short");
        return None;
    }
    let conv_id = get_conv(payload);
    let bytes = reformat_kcp_segments(payload)?;
    new_kcp(conv_id).input(&bytes).ok()?;
    Some(ValidatedKcpSegment { conv_id, bytes })
}

// reformat to skip bytes 4..8
fn reformat_kcp_segments(data: &[u8]) -> Option<Vec<u8>> {
    let span = span!(Level::TRACE, "split");
    let _enter = span.enter();

    let mut reformatted_bytes = Vec::new();

    let mut i = 0;
    while i < data.len() {
        let header_end = i.checked_add(GAME_KCP_HEADER_LEN)?;
        let header = data.get(i..header_end)?;
        let conv_id = &header[..4];
        let remaining_header = &header[8..28];
        let content_len = u32::from_le_bytes(header[24..28].try_into().unwrap()) as usize;
        let segment_end = header_end.checked_add(content_len)?;
        let content = data.get(header_end..segment_end)?;

        for b in conv_id.iter().chain(remaining_header).chain(content) {
            reformatted_bytes.push(*b);
        }

        i = segment_end;
    }

    Some(reformatted_bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_truncated_and_other_conversation() {
        let mut conversation_a = vec![0; GAME_KCP_HEADER_LEN];
        conversation_a[..4].copy_from_slice(&1_u32.to_le_bytes());
        conversation_a[8] = 82;
        let validated_a =
            validate_kcp_segment(&conversation_a).expect("minimum game KCP ACK should be accepted");
        let (mut sniffer, _) = KcpSniffer::try_new(&validated_a).unwrap();

        let mut conversation_b = vec![0; GAME_KCP_HEADER_LEN];
        conversation_b[..4].copy_from_slice(&2_u32.to_le_bytes());
        conversation_b[8] = 82;
        let validated_b = validate_kcp_segment(&conversation_b).unwrap();
        assert!(sniffer.receive_segments(&validated_b).is_none());

        let mut invalid = conversation_a;
        invalid[8] = 0;
        assert!(validate_kcp_segment(&invalid).is_none());

        for len in 0..GAME_KCP_HEADER_LEN {
            assert!(validate_kcp_segment(&vec![0; len]).is_none());
        }
    }
}
