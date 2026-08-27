use std::collections::{BTreeMap, BTreeSet};

use crate::gen::protos::Unk;
use base64::prelude::BASE64_STANDARD;
use base64::Engine;
use protobuf::Message;
use protobuf::UnknownValueRef::*;
use rsa::{Pkcs1v15Encrypt, RsaPrivateKey};
use tracing::info;
use zeroize::Zeroizing;

const MIN_ACHIEVEMENT_PACKET_BYTES: usize = 1000;
const MAX_ACHIEVEMENT_ROWS: usize = 10_000;
const MAX_ACHIEVEMENT_ROW_FIELDS: usize = 16;
const EARLIEST_FINISH_TIMESTAMP: u64 = 1_420_066_800;
const GENSHIN_ACHIEVEMENT_SENTINEL: u64 = 80_014;

pub fn matches_get_player_token_rsp(
    data: &[u8],
    rsa_keys: &[RsaPrivateKey],
) -> Option<Zeroizing<Vec<u64>>> {
    // Cut at last "==": token 256 bytes -> 1 modulo 3, so always == at end in base64.
    let end = data
        .windows(2)
        .rposition(|w| w == b"==")
        .map_or(data.len(), |pos| pos + 2);
    let data = &data[..end];

    let d_msg = Unk::parse_from_bytes(data);
    match d_msg {
        Ok(d_msg) => {
            let unknown_fields = d_msg.unknown_fields();
            let capacity = unknown_fields.iter().count().checked_mul(rsa_keys.len())?;
            let mut to_ret = Zeroizing::new(Vec::with_capacity(capacity));
            for (_field_number, field_data) in unknown_fields.iter() {
                let possible_encrypted = match field_data {
                    LengthDelimited(encrypted_bytes) => {
                        BASE64_STANDARD.decode(encrypted_bytes).ok()
                    }
                    _ => None,
                };
                if let Some(possible_encrypted) = possible_encrypted {
                    to_ret.extend(rsa_keys.iter().filter_map(|key| {
                        let seed =
                            Zeroizing::new(key.decrypt(Pkcs1v15Encrypt, &possible_encrypted).ok()?);
                        (seed.len() == 8)
                            .then(|| u64::from_be_bytes(seed.as_slice().try_into().unwrap()))
                    }));
                }
            }
            if !to_ret.is_empty() {
                Some(to_ret)
            } else {
                None
            }
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

pub fn matches_achievement_all_data_notify(data: Vec<u8>) -> Option<Vec<Achievement>> {
    if data.len() < MIN_ACHIEVEMENT_PACKET_BYTES {
        return None;
    }
    decode_achievement_rows(&data)
}

fn decode_achievement_rows(data: &[u8]) -> Option<Vec<Achievement>> {
    let message = Unk::parse_from_bytes(data).ok()?;
    let mut groups = BTreeMap::<u32, Vec<(bool, Option<BTreeMap<u32, u64>>)>>::new();
    for (observed_rows, (field_number, field_data)) in message.unknown_fields().iter().enumerate() {
        if observed_rows >= MAX_ACHIEVEMENT_ROWS {
            return None;
        }
        let LengthDelimited(bytes) = field_data else {
            groups.entry(field_number).or_default().push((false, None));
            continue;
        };
        let embedded = Unk::parse_from_bytes(bytes).ok();
        let has_sentinel = embedded.as_ref().is_some_and(|embedded| {
            embedded.unknown_fields().iter().any(|(_, value)| {
                matches!(value, Varint(value) if value == GENSHIN_ACHIEVEMENT_SENTINEL)
            })
        });
        let row = embedded.and_then(|embedded| {
            let fields = embedded.unknown_fields();
            let field_count = fields.iter().count();
            if !(2..=MAX_ACHIEVEMENT_ROW_FIELDS).contains(&field_count) {
                return None;
            }
            let mut row = BTreeMap::new();
            for (tag, value) in fields.iter() {
                let Varint(value) = value else {
                    return None;
                };
                if row.insert(tag, value).is_some() {
                    return None;
                }
            }
            Some(row)
        });
        groups
            .entry(field_number)
            .or_default()
            .push((has_sentinel, row));
    }

    let mut candidate_groups = groups
        .into_iter()
        .filter(|(_, members)| members.iter().any(|(has_sentinel, _)| *has_sentinel));
    let (_, members) = candidate_groups.next()?;
    if candidate_groups.next().is_some() {
        return None;
    }
    let rows = members
        .into_iter()
        .map(|(_, row)| row)
        .collect::<Option<Vec<_>>>()?;
    let first = rows.first()?;
    info!("Collected some possible achievements, trying to find field tags...");

    let mut finish_tags = BTreeSet::new();
    let mut id_tags = BTreeSet::new();
    let mut possible_status_tags = first.keys().copied().collect::<BTreeSet<_>>();
    for row in &rows {
        for (tag, value) in row {
            if *value > EARLIEST_FINISH_TIMESTAMP && *value <= u32::MAX as u64 {
                finish_tags.insert(*tag);
            }
            if *value == GENSHIN_ACHIEVEMENT_SENTINEL {
                id_tags.insert(*tag);
            }
            if *value > 3 {
                possible_status_tags.remove(tag);
            }
        }
    }

    let mut finish_tags = finish_tags.into_iter();
    let finish_tag = finish_tags.next()?;
    if finish_tags.next().is_some() {
        return None;
    }
    let mut id_tags = id_tags.into_iter();
    let id_tag = id_tags.next()?;
    if id_tags.next().is_some() {
        return None;
    }
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
            if id > u32::MAX as u64 || status > 3 {
                return None;
            }
            let finish_timestamp = match row.get(&finish_tag).copied() {
                Some(value) if value <= u32::MAX as u64 => Some(value as u32),
                Some(_) => return None,
                None => None,
            };
            Some(Achievement {
                id: id as u32,
                status: status as u32,
                finish_timestamp,
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

    fn achievement(fields: &[(u32, u64)]) -> Vec<u8> {
        let mut row = Vec::new();
        for &(tag, value) in fields {
            push_varint_field(&mut row, tag, value);
        }
        row
    }

    fn valid_payload(reverse_fields: bool) -> Vec<u8> {
        let mut payload = Vec::new();
        let mut first = vec![
            (13, GENSHIN_ACHIEVEMENT_SENTINEL),
            (2, 10),
            (8, 3),
            (3, 1_800_000_001),
        ];
        let mut second = vec![(13, 80_015), (2, 11)];
        if reverse_fields {
            first.reverse();
            second.reverse();
        }
        push_message_field(&mut payload, 5, &achievement(&first));
        push_message_field(&mut payload, 5, &achievement(&second));
        payload
    }

    fn decoded_shape(payload: &[u8]) -> Vec<(u32, u32, Option<u32>)> {
        decode_achievement_rows(payload)
            .unwrap()
            .into_iter()
            .map(|item| (item.id, item.status, item.finish_timestamp))
            .collect()
    }

    #[test]
    fn genshin_shape_match_is_field_order_independent() {
        let normal = valid_payload(false);
        let reversed = valid_payload(true);
        assert_eq!(decoded_shape(&normal), decoded_shape(&reversed));
        assert_eq!(
            decoded_shape(&normal),
            vec![
                (GENSHIN_ACHIEVEMENT_SENTINEL as u32, 3, Some(1_800_000_001)),
                (80_015, 0, None),
            ]
        );

        assert!(matches_achievement_all_data_notify(normal.clone()).is_none());
        let mut padded = normal;
        while padded.len() < MIN_ACHIEVEMENT_PACKET_BYTES {
            push_varint_field(&mut padded, 100, 1);
        }
        assert!(matches_achievement_all_data_notify(padded).is_some());
    }

    #[test]
    fn genshin_shape_match_ignores_unrelated_groups() {
        let mut payload = valid_payload(false);
        let expected = decoded_shape(&payload);
        push_message_field(&mut payload, 6, &achievement(&[(20, 7), (21, 9)]));
        push_message_field(&mut payload, 7, &[0x80]);
        push_varint_field(&mut payload, 6, 1);
        assert_eq!(decoded_shape(&payload), expected);

        let mut wrong_wire_candidate = valid_payload(false);
        push_varint_field(&mut wrong_wire_candidate, 5, 1);
        assert!(decode_achievement_rows(&wrong_wire_candidate).is_none());

        let mut malformed_sentinel = achievement(&[(13, GENSHIN_ACHIEVEMENT_SENTINEL)]);
        push_message_field(&mut malformed_sentinel, 9, &[]);
        let mut two_candidates = valid_payload(false);
        push_message_field(&mut two_candidates, 6, &malformed_sentinel);
        assert!(decode_achievement_rows(&two_candidates).is_none());

        push_message_field(&mut payload, 5, &[0x80]);
        assert!(decode_achievement_rows(&payload).is_none());
    }

    #[test]
    fn genshin_shape_match_rejects_zero_multiple_and_ambiguous_candidates() {
        let mut missing_sentinel = Vec::new();
        push_message_field(
            &mut missing_sentinel,
            5,
            &achievement(&[(13, 80_015), (8, 2), (3, 1_800_000_001)]),
        );
        assert!(decode_achievement_rows(&missing_sentinel).is_none());

        let mut two_candidates = valid_payload(false);
        push_message_field(
            &mut two_candidates,
            6,
            &achievement(&[
                (13, GENSHIN_ACHIEVEMENT_SENTINEL),
                (2, 10),
                (8, 2),
                (3, 1_800_000_003),
            ]),
        );
        assert!(decode_achievement_rows(&two_candidates).is_none());

        for fields in [
            vec![
                (13, GENSHIN_ACHIEVEMENT_SENTINEL),
                (14, GENSHIN_ACHIEVEMENT_SENTINEL),
                (2, 10),
                (8, 2),
                (3, 1_800_000_001),
            ],
            vec![
                (13, GENSHIN_ACHIEVEMENT_SENTINEL),
                (2, 10),
                (8, 2),
                (3, 1_800_000_001),
                (4, 1_800_000_002),
            ],
            vec![
                (13, GENSHIN_ACHIEVEMENT_SENTINEL),
                (2, 10),
                (8, 2),
                (9, 3),
                (3, 1_800_000_001),
            ],
        ] {
            let mut payload = Vec::new();
            push_message_field(&mut payload, 5, &achievement(&fields));
            assert!(decode_achievement_rows(&payload).is_none());
        }
    }

    #[test]
    fn genshin_shape_match_rejects_invalid_candidate_rows_and_values() {
        let mut duplicate_tag = Vec::new();
        push_message_field(
            &mut duplicate_tag,
            5,
            &achievement(&[
                (13, GENSHIN_ACHIEVEMENT_SENTINEL),
                (13, 80_015),
                (2, 10),
                (8, 2),
                (3, 1_800_000_001),
            ]),
        );
        assert!(decode_achievement_rows(&duplicate_tag).is_none());

        let mut one_field = valid_payload(false);
        push_message_field(&mut one_field, 5, &achievement(&[(20, 1)]));
        assert!(decode_achievement_rows(&one_field).is_none());

        let mut oversized_fields = vec![
            (13, GENSHIN_ACHIEVEMENT_SENTINEL),
            (8, 2),
            (3, 1_800_000_001),
        ];
        oversized_fields.extend((20..34).map(|tag| (tag, 100)));
        let mut oversized_row = Vec::new();
        push_message_field(&mut oversized_row, 5, &achievement(&oversized_fields));
        assert!(decode_achievement_rows(&oversized_row).is_none());

        let mut non_varint_row = achievement(&[(13, 80_016), (8, 2)]);
        push_message_field(&mut non_varint_row, 9, &[]);
        let mut non_varint = valid_payload(false);
        push_message_field(&mut non_varint, 5, &non_varint_row);
        assert!(decode_achievement_rows(&non_varint).is_none());

        let mut too_many_rows = Vec::new();
        for index in 0..=MAX_ACHIEVEMENT_ROWS {
            let id = if index == 0 {
                GENSHIN_ACHIEVEMENT_SENTINEL
            } else {
                90_000 + index as u64
            };
            let mut fields = vec![(13, id), (2, 10), (8, 2)];
            if index == 0 {
                fields.push((3, 1_800_000_001));
            }
            push_message_field(&mut too_many_rows, 5, &achievement(&fields));
        }
        assert!(decode_achievement_rows(&too_many_rows).is_none());

        let wide_value = u64::from(u32::MAX) + 1;
        let mut wide_id = valid_payload(false);
        push_message_field(
            &mut wide_id,
            5,
            &achievement(&[(13, wide_value), (2, 10), (8, 2)]),
        );
        assert!(decode_achievement_rows(&wide_id).is_none());

        let mut wide_finish = valid_payload(false);
        push_message_field(
            &mut wide_finish,
            5,
            &achievement(&[(13, 80_016), (2, 10), (8, 2), (3, wide_value)]),
        );
        assert!(decode_achievement_rows(&wide_finish).is_none());

        let mut invalid_status = valid_payload(false);
        push_message_field(
            &mut invalid_status,
            5,
            &achievement(&[(13, 80_016), (2, 10), (8, 5)]),
        );
        assert!(decode_achievement_rows(&invalid_status).is_none());
    }
}
