use crate::gen::protos::Unk;
use protobuf::Message;
use protobuf::UnknownValueRef::*;
use std::collections::{BTreeSet, HashMap};
use zeroize::Zeroizing;

const MIN_ACHIEVEMENT_PACKET_BYTES: usize = 1000;
const MAX_QUEST_ROWS: usize = 10_000;
const MAX_QUEST_ROW_FIELDS: usize = 16;
const EARLIEST_FINISH_TIMESTAMP: u64 = 1_420_066_800;
const STARDB_ACHIEVEMENT_SENTINEL: u64 = 4_040_201;

pub fn matches_player_get_token_sc_rsp(data: &[u8]) -> Option<Zeroizing<u64>> {
    let d_msg = Unk::parse_from_bytes(data);
    match d_msg {
        Ok(d_msg) => {
            let mut possible_seed = None;
            let unknown_fields = d_msg.unknown_fields();
            for (_, field_data) in unknown_fields.iter() {
                if let Varint(seed) = field_data {
                    if seed <= 1 << 32 {
                        continue;
                    }
                    if possible_seed.replace(Zeroizing::new(seed)).is_some() {
                        return None;
                    }
                };
            }
            possible_seed
        }
        _ => None,
    }
}

#[derive(Default)]
pub struct Achievement {
    pub id: u32,
    pub status: u32,
    pub finish_timestamp: Option<u32>,
}

pub fn matches_get_quest_data_sc_rsp(data: &[u8]) -> Option<Vec<Achievement>> {
    if data.len() < MIN_ACHIEVEMENT_PACKET_BYTES {
        return None;
    }
    decode_achievement_rows(data)
}

fn decode_achievement_rows(data: &[u8]) -> Option<Vec<Achievement>> {
    let message = Unk::parse_from_bytes(data).ok()?;
    let mut rows = Vec::<HashMap<u32, u64>>::new();
    let mut list_tag = None;
    for (field_number, field_data) in message.unknown_fields().iter() {
        let LengthDelimited(bytes) = field_data else {
            continue;
        };
        let embedded = match Unk::parse_from_bytes(bytes) {
            Ok(embedded) => embedded,
            Err(_) => continue,
        };
        if embedded.unknown_fields().iter().count() > MAX_QUEST_ROW_FIELDS {
            continue;
        }
        let row = embedded
            .unknown_fields()
            .iter()
            .filter_map(|(tag, value)| match value {
                Varint(value) => Some((tag, value)),
                _ => None,
            })
            .collect::<HashMap<_, _>>();
        if row.is_empty() {
            continue;
        }
        if rows.len() >= MAX_QUEST_ROWS {
            return None;
        }
        match list_tag {
            Some(existing) if existing != field_number => return None,
            None => list_tag = Some(field_number),
            _ => {}
        }
        rows.push(row);
    }
    let first = rows.first()?;
    let mut finish_tag = None;
    let mut id_tag = None;
    let mut possible_status_tags = first.keys().copied().collect::<BTreeSet<_>>();
    for row in &rows {
        for (tag, value) in row {
            if *value > EARLIEST_FINISH_TIMESTAMP && *value <= u32::MAX as u64 {
                match finish_tag {
                    Some(existing) if existing != *tag => return None,
                    None => finish_tag = Some(*tag),
                    _ => {}
                }
            }
            if *value == STARDB_ACHIEVEMENT_SENTINEL {
                match id_tag {
                    Some(existing) if existing != *tag => return None,
                    None => id_tag = Some(*tag),
                    _ => {}
                }
            }
            if *value > 4 {
                possible_status_tags.remove(tag);
            }
        }
    }
    let id_tag = id_tag?;
    let finish_tag = finish_tag?;
    possible_status_tags.remove(&id_tag);
    possible_status_tags.remove(&finish_tag);
    let mut status_tags = possible_status_tags.into_iter();
    let status_tag = status_tags.next()?;
    if status_tags.next().is_some() {
        return None;
    }

    rows.into_iter()
        .map(|row| {
            let id = *row.get(&id_tag)?;
            let status = row.get(&status_tag).copied().unwrap_or_default();
            if id > u32::MAX as u64 || status > 4 {
                return None;
            }
            Some(Achievement {
                id: id as u32,
                status: status as u32,
                finish_timestamp: row.get(&finish_tag).copied().map(|value| value as u32),
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn push_varint(bytes: &mut Vec<u8>, mut value: u64) {
        while value >= 0x80 {
            bytes.push((value as u8 & 0x7f) | 0x80);
            value >>= 7;
        }
        bytes.push(value as u8);
    }

    fn push_varint_field(bytes: &mut Vec<u8>, tag: u32, value: u64) {
        push_varint(bytes, u64::from(tag) << 3);
        push_varint(bytes, value);
    }

    fn push_message_field(bytes: &mut Vec<u8>, tag: u32, message: &[u8]) {
        push_varint(bytes, (u64::from(tag) << 3) | 2);
        push_varint(bytes, message.len() as u64);
        bytes.extend_from_slice(message);
    }

    fn quest(id: u32, status: u32, finish: Option<u32>) -> Vec<u8> {
        let mut row = Vec::new();
        push_varint_field(&mut row, 13, u64::from(id));
        push_varint_field(&mut row, 2, u64::from(id % 17 + 5));
        if status != 0 {
            push_varint_field(&mut row, 8, u64::from(status));
        }
        if let Some(finish) = finish {
            push_varint_field(&mut row, 3, u64::from(finish));
        }
        push_message_field(&mut row, 15, &[0; 256]);
        row
    }

    #[test]
    fn stardb_shape_match_accepts_the_bounded_quest_list() {
        let mut payload = Vec::new();
        for (id, status, finish) in [
            (4_040_201, 2, Some(1_800_000_001)),
            (4_010_101, 0, None),
            (4_010_102, 1, None),
            (4_010_103, 3, Some(1_800_000_002)),
        ] {
            push_message_field(&mut payload, 5, &quest(id, status, finish));
        }

        let decoded = decode_achievement_rows(&payload).unwrap();
        assert_eq!(decoded.len(), 4);
        assert_eq!(decoded[0].id, 4_040_201);
        assert_eq!(decoded[0].status, 2);
        assert_eq!(decoded[1].status, 0);
        assert_eq!(decoded[3].finish_timestamp, Some(1_800_000_002));
    }

    #[test]
    fn stardb_shape_match_rejects_missing_sentinel_mixed_lists_and_ambiguous_status() {
        let mut missing_sentinel = Vec::new();
        push_message_field(
            &mut missing_sentinel,
            5,
            &quest(4_010_101, 2, Some(1_800_000_001)),
        );
        push_message_field(&mut missing_sentinel, 5, &quest(4_010_102, 0, None));
        assert!(decode_achievement_rows(&missing_sentinel).is_none());

        let mut mixed_lists = Vec::new();
        push_message_field(
            &mut mixed_lists,
            5,
            &quest(4_040_201, 2, Some(1_800_000_001)),
        );
        push_message_field(&mut mixed_lists, 6, &quest(4_010_102, 0, None));
        assert!(decode_achievement_rows(&mixed_lists).is_none());

        let mut ambiguous_status = Vec::new();
        for (id, status, finish) in [
            (4_040_201, 2, Some(1_800_000_001)),
            (4_010_101, 3, Some(1_800_000_002)),
        ] {
            let mut row = quest(id, status, finish);
            push_varint_field(&mut row, 9, u64::from(status));
            push_message_field(&mut ambiguous_status, 5, &row);
        }
        assert!(decode_achievement_rows(&ambiguous_status).is_none());
    }
}
