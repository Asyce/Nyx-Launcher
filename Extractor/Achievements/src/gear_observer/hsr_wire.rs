//! Test-only HSR 4.5 command-body qualification, not a packet or session decoder.
//!
//! Field shapes: IceDynamix/reliquary d5cf3b7e7e66470d2d8efff6676aa18762b21d3b
//! (MIT; exact source hashes and attribution are in PROVENANCE.md).
//! Deliberately stricter than general protobuf: duplicate known scalar fields
//! and groups are rejected. Omitted scalars retain protobuf zero/false defaults.
//! Skipped messages remain opaque; only their wire type and length are checked.
//! No result contains a session seed, auth bytes, flow, or completion claim.

use super::MAX_GEAR_ROWS;
use crate::capture::MAX_FRAME_BYTES;
use protobuf::{CodedInputStream, rt::WireType};

const TOKEN: u16 = 19;
const LOGIN_FINISH: u16 = 36;
const BAG: u16 = 513;

#[derive(Debug, Eq, PartialEq)]
enum BodyObservation {
    Token { uid: u32, retcode: u32 },
    LoginFinish { retcode: u32 },
    Bag { retcode: u32, relics: Vec<Relic> },
}

#[derive(Debug, Default, Eq, PartialEq)]
struct Relic {
    tid: u32,
    is_discarded: bool,
    equip_avatar_id: u32,
    is_protected: bool,
    preview_sub_affix_list: Vec<RelicAffix>,
    unique_id: u32,
    exp: u32,
    level: u32,
    reforge_block_sub_affix_id: u32,
    reforge_sub_affix_list: Vec<RelicAffix>,
    main_affix_id: u32,
    sub_affix_list: Vec<RelicAffix>,
}

#[derive(Debug, Default, Eq, PartialEq)]
struct RelicAffix {
    affix_id: u32,
    count: u32,
    step: u32,
}

#[derive(Debug, Eq, PartialEq)]
enum WireError {
    UnsupportedCommand,
    BodyTooLarge,
    TooManyRows,
    Malformed,
}

impl From<protobuf::Error> for WireError {
    fn from(_: protobuf::Error) -> Self {
        // Do not retain parser errors that might contain a decoded input value.
        Self::Malformed
    }
}

fn tag(input: &mut CodedInputStream<'_>) -> Result<(u32, WireType), WireError> {
    let raw = u32::try_from(input.read_raw_varint64()?).map_err(|_| WireError::Malformed)?;
    let field = raw >> 3;
    if field == 0 {
        return Err(WireError::Malformed);
    }
    let wire = WireType::new(raw & 7).ok_or(WireError::Malformed)?;
    Ok((field, wire))
}

fn require_wire(actual: WireType, expected: WireType) -> Result<(), WireError> {
    if actual != expected {
        return Err(WireError::Malformed);
    }
    Ok(())
}

fn once(seen: &mut u32, field: u32) -> Result<(), WireError> {
    // Only the explicitly matched scalar fields 1..=14 call this helper.
    let bit = 1 << field;
    if *seen & bit != 0 {
        return Err(WireError::Malformed);
    }
    *seen |= bit;
    Ok(())
}

fn scalar(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
    seen: &mut u32,
    field: u32,
) -> Result<u64, WireError> {
    require_wire(wire, WireType::Varint)?;
    once(seen, field)?;
    Ok(input.read_uint64()?)
}

fn uint32(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
    seen: &mut u32,
    field: u32,
) -> Result<u32, WireError> {
    u32::try_from(scalar(input, wire, seen, field)?).map_err(|_| WireError::Malformed)
}

fn length(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<u32, WireError> {
    require_wire(wire, WireType::LengthDelimited)?;
    u32::try_from(input.read_raw_varint64()?).map_err(|_| WireError::Malformed)
}

fn skip(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<(), WireError> {
    match wire {
        WireType::StartGroup | WireType::EndGroup => return Err(WireError::Malformed),
        WireType::LengthDelimited => {
            let len = length(input, wire)?;
            input.skip_raw_bytes(len)?;
        }
        _ => input.skip_field(wire)?,
    }
    Ok(())
}

fn read_body(command: u16, body: &[u8]) -> Result<BodyObservation, WireError> {
    if !matches!(command, TOKEN | LOGIN_FINISH | BAG) {
        return Err(WireError::UnsupportedCommand);
    }
    if body.len() > MAX_FRAME_BYTES {
        return Err(WireError::BodyTooLarge);
    }
    let mut input = CodedInputStream::from_bytes(body);
    // The library's slice reader starts without a protobuf limit. Bound nested
    // lengths and skipped bytes to the actual borrowed body before reading.
    input.push_limit(body.len() as u64)?;
    let mut seen = 0;
    let mut uid = 0;
    let mut retcode = 0;
    let mut relics = Vec::new();
    while !input.eof()? {
        let (field, wire) = tag(&mut input)?;
        match (command, field) {
            (TOKEN, 1) => {
                // Validate the uint64 field but never keep the session seed.
                let _ = scalar(&mut input, wire, &mut seen, field)?;
            }
            (TOKEN, 11) => uid = uint32(&mut input, wire, &mut seen, field)?,
            (TOKEN | LOGIN_FINISH, 13) | (BAG, 1) => {
                retcode = uint32(&mut input, wire, &mut seen, field)?;
            }
            (TOKEN, 2 | 12 | 14) => {
                require_wire(wire, WireType::LengthDelimited)?;
                if field != 12 {
                    once(&mut seen, field)?;
                }
                // Description, BlackInfo and authkey are never copied or decoded.
                skip(&mut input, wire)?;
            }
            (BAG, 9) => {
                if relics.len() == MAX_GEAR_ROWS {
                    return Err(WireError::TooManyRows);
                }
                relics.push(read_relic(&mut input, wire)?);
            }
            (BAG, 2 | 4 | 6 | 10 | 12 | 14 | 15 | 1996) => {
                require_wire(wire, WireType::LengthDelimited)?;
                skip(&mut input, wire)?;
            }
            (BAG, 3 | 7 | 11 | 13) => {
                // Unrelated repeated scalars may be packed or unpacked.
                if !matches!(wire, WireType::Varint | WireType::LengthDelimited) {
                    return Err(WireError::Malformed);
                }
                skip(&mut input, wire)?;
            }
            _ => skip(&mut input, wire)?,
        }
    }
    Ok(match command {
        TOKEN => BodyObservation::Token { uid, retcode },
        LOGIN_FINISH => BodyObservation::LoginFinish { retcode },
        BAG => BodyObservation::Bag { retcode, relics },
        _ => unreachable!(),
    })
}

fn read_relic(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<Relic, WireError> {
    let len = length(input, wire)?;
    let previous_limit = input.push_limit(u64::from(len))?;
    let mut row = Relic::default();
    let mut seen = 0;
    while !input.eof()? {
        let (field, wire) = tag(input)?;
        match field {
            1 => row.tid = uint32(input, wire, &mut seen, field)?,
            2 => row.is_discarded = scalar(input, wire, &mut seen, field)? != 0,
            3 => row.equip_avatar_id = uint32(input, wire, &mut seen, field)?,
            4 => row.is_protected = scalar(input, wire, &mut seen, field)? != 0,
            5 => row.preview_sub_affix_list.push(read_affix(input, wire)?),
            7 => row.unique_id = uint32(input, wire, &mut seen, field)?,
            8 => row.exp = uint32(input, wire, &mut seen, field)?,
            9 => row.level = uint32(input, wire, &mut seen, field)?,
            10 => row.reforge_block_sub_affix_id = uint32(input, wire, &mut seen, field)?,
            12 => row.reforge_sub_affix_list.push(read_affix(input, wire)?),
            13 => row.main_affix_id = uint32(input, wire, &mut seen, field)?,
            14 => row.sub_affix_list.push(read_affix(input, wire)?),
            _ => skip(input, wire)?,
        }
    }
    input.pop_limit(previous_limit);
    Ok(row)
}

fn read_affix(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<RelicAffix, WireError> {
    let len = length(input, wire)?;
    let previous_limit = input.push_limit(u64::from(len))?;
    let mut affix = RelicAffix::default();
    let mut seen = 0;
    while !input.eof()? {
        let (field, wire) = tag(input)?;
        match field {
            1 => affix.affix_id = uint32(input, wire, &mut seen, field)?,
            2 => affix.count = uint32(input, wire, &mut seen, field)?,
            3 => affix.step = uint32(input, wire, &mut seen, field)?,
            _ => skip(input, wire)?,
        }
    }
    input.pop_limit(previous_limit);
    Ok(affix)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Game;
    use crate::gear_observer::{
        GearObserver, MappingClass, Observation, ObservationContext, ObserverError, SnapshotCounts,
        SyntheticGearRow,
    };
    use protobuf::CodedOutputStream;

    fn encoded(write: impl FnOnce(&mut CodedOutputStream<'_>)) -> Vec<u8> {
        let mut body = Vec::new();
        let mut output = CodedOutputStream::vec(&mut body);
        write(&mut output);
        output.flush().unwrap();
        drop(output);
        body
    }

    fn scalars(fields: &[(u32, u64)]) -> Vec<u8> {
        encoded(|output| {
            for &(field, value) in fields {
                output.write_uint64(field, value).unwrap();
            }
        })
    }

    fn nested(field: u32, body: &[u8]) -> Vec<u8> {
        encoded(|output| output.write_bytes(field, body).unwrap())
    }

    fn bag(relics: &[Vec<u8>]) -> Vec<u8> {
        encoded(|output| {
            for row in relics {
                output.write_bytes(9, row).unwrap();
            }
        })
    }

    fn rows(body: &[u8]) -> Vec<Relic> {
        let BodyObservation::Bag { retcode, relics } = read_body(BAG, body).unwrap() else {
            panic!("expected synthetic bag body");
        };
        assert_eq!(retcode, 0);
        relics
    }

    #[test]
    fn exact_commands_preserve_omitted_and_explicit_defaults() {
        assert_eq!(
            read_body(TOKEN, &[]),
            Ok(BodyObservation::Token { uid: 0, retcode: 0 })
        );
        assert_eq!(
            read_body(LOGIN_FINISH, &[]),
            Ok(BodyObservation::LoginFinish { retcode: 0 })
        );
        assert!(rows(&[]).is_empty());
        for command in [0, 18, 20, 35, 37, 512, 514, u16::MAX] {
            assert_eq!(read_body(command, &[]), Err(WireError::UnsupportedCommand));
        }
        let zero = scalars(&[
            (1, 0),
            (2, 0),
            (3, 0),
            (4, 0),
            (7, 0),
            (8, 0),
            (9, 0),
            (10, 0),
            (13, 0),
        ]);
        assert_eq!(
            rows(&bag(&[Vec::new(), zero])),
            vec![Relic::default(), Relic::default()]
        );
        for field in [5, 12, 14] {
            let row = rows(&nested(9, &nested(field, &[]))).remove(0);
            let lists = [
                row.preview_sub_affix_list,
                row.reforge_sub_affix_list,
                row.sub_affix_list,
            ];
            assert_eq!(lists.iter().map(Vec::len).sum::<usize>(), 1);
            assert_eq!(
                lists.into_iter().flatten().collect::<Vec<_>>(),
                vec![RelicAffix::default()]
            );
        }
    }

    #[test]
    fn all_relic_fields_and_three_affix_lists_survive_reordering() {
        let affix = scalars(&[(3, 3), (1, 101), (2, 2)]);
        let mut fields = [
            scalars(&[(1, 123), (2, 1), (3, 456), (4, 2)]),
            nested(5, &affix),
            nested(5, &[]),
            scalars(&[(7, u64::from(u32::MAX)), (8, 999), (9, 0), (10, 789)]),
            nested(12, &affix),
            scalars(&[(13, 321)]),
            nested(14, &affix),
            nested(14, &affix),
        ];
        let first = rows(&[scalars(&[(1, 0)]), nested(9, &fields.concat())].concat());
        fields.reverse();
        let mut reordered = rows(&[nested(9, &fields.concat()), scalars(&[(1, 0)])].concat());
        // Repeated-field order is meaningful; restore only the deliberately reversed lists.
        reordered[0].preview_sub_affix_list.reverse();
        reordered[0].sub_affix_list.reverse();
        assert_eq!(first, reordered);
        let row = &first[0];
        assert_eq!(
            (
                row.tid,
                row.is_discarded,
                row.equip_avatar_id,
                row.is_protected
            ),
            (123, true, 456, true)
        );
        assert_eq!(
            (
                row.unique_id,
                row.exp,
                row.level,
                row.reforge_block_sub_affix_id,
                row.main_affix_id
            ),
            (u32::MAX, 999, 0, 789, 321)
        );
        assert_eq!(
            row.preview_sub_affix_list,
            vec![
                RelicAffix {
                    affix_id: 101,
                    count: 2,
                    step: 3
                },
                RelicAffix::default()
            ]
        );
        assert_eq!(
            row.reforge_sub_affix_list,
            vec![RelicAffix {
                affix_id: 101,
                count: 2,
                step: 3
            }]
        );
        assert_eq!(
            row.sub_affix_list,
            vec![
                RelicAffix {
                    affix_id: 101,
                    count: 2,
                    step: 3
                },
                RelicAffix {
                    affix_id: 101,
                    count: 2,
                    step: 3
                }
            ]
        );
    }

    #[test]
    fn sensitive_and_unrelated_fields_are_skipped_without_retention() {
        let token = encoded(|output| {
            output.write_bytes(14, b"synthetic-auth-only").unwrap();
            output.write_uint32(13, 7).unwrap();
            output.write_bytes(12, &[0xff]).unwrap();
            output.write_uint64(1, u64::MAX).unwrap();
            output.write_uint32(11, 42).unwrap();
            output.write_bytes(2, &[0xff, 0xfe]).unwrap();
        });
        let before = token.clone();
        let observed = read_body(TOKEN, &token).unwrap();
        assert_eq!(token, before);
        drop(token);
        assert_eq!(
            observed,
            BodyObservation::Token {
                uid: 42,
                retcode: 7
            }
        );
        assert!(!format!("{observed:?}").contains("synthetic-auth-only"));
        assert_eq!(
            read_body(TOKEN, &scalars(&[(11, 42), (13, 7), (1, 0)])).unwrap(),
            observed
        );
        assert_eq!(
            read_body(LOGIN_FINISH, &scalars(&[(13, 7)])),
            Ok(BodyObservation::LoginFinish { retcode: 7 })
        );
        let unrelated = encoded(|output| {
            for field in [2, 4, 6, 10, 12, 14, 15, 1996] {
                output.write_bytes(field, &[0xff]).unwrap();
            }
            for field in [3, 7, 11, 13] {
                output.write_uint32(field, 1).unwrap();
                output.write_bytes(field, &[1, 2]).unwrap();
            }
            output.write_fixed32(100, 1).unwrap();
            output.write_fixed64(101, 2).unwrap();
            output.write_uint64(102, 3).unwrap();
            output.write_bytes(103, b"synthetic-unrelated").unwrap();
        });
        assert!(rows(&unrelated).is_empty());
        assert_eq!(
            read_body(BAG, &scalars(&[(1, 17)])),
            Ok(BodyObservation::Bag {
                retcode: 17,
                relics: Vec::new()
            })
        );
    }

    #[test]
    fn malformed_tags_lengths_and_nested_messages_fail_closed() {
        for malformed in [
            vec![0],
            vec![6],
            vec![7],
            vec![14],
            vec![15],
            vec![0x0b, 0x0c],
            vec![0x0c],
            vec![0x80],
            vec![104],
            vec![0x09, 0],
            vec![0xff; 11],
        ] {
            for command in [TOKEN, LOGIN_FINISH, BAG] {
                assert_eq!(read_body(command, &malformed), Err(WireError::Malformed));
            }
        }
        assert_eq!(
            read_body(TOKEN, &[vec![8], vec![0xff; 10]].concat()),
            Err(WireError::Malformed)
        );
        for malformed in [
            vec![74],
            vec![74, 1],
            vec![74, 2, 8],
            vec![74, 1, 0],
            vec![74, 1, 8, 8, 0],
        ] {
            assert_eq!(read_body(BAG, &malformed), Err(WireError::Malformed));
        }
        for field in [5, 12, 14] {
            for malformed in [vec![8], vec![0], vec![0x80], vec![10, 2, 1]] {
                assert_eq!(
                    read_body(BAG, &nested(9, &nested(field, &malformed))),
                    Err(WireError::Malformed)
                );
            }
        }
        let bad_length = encoded(|output| {
            output.write_tag(9, WireType::LengthDelimited).unwrap();
            output.write_raw_varint64(u64::from(u32::MAX) + 1).unwrap();
        });
        assert_eq!(read_body(BAG, &bad_length), Err(WireError::Malformed));
        let overflow_tag = |field: u32| {
            encoded(|output| {
                output
                    .write_raw_varint64((1u64 << 32) | (u64::from(field) << 3))
                    .unwrap();
                output.write_raw_varint64(0).unwrap();
            })
        };
        for command in [TOKEN, LOGIN_FINISH, BAG] {
            for field in [1, 11, 13] {
                assert_eq!(
                    read_body(command, &overflow_tag(field)),
                    Err(WireError::Malformed)
                );
            }
            assert_eq!(
                read_body(command, &scalars(&[(u32::MAX >> 3, 0)])),
                read_body(command, &[])
            );
        }
        for malformed in [overflow_tag(7), nested(14, &overflow_tag(1))] {
            assert_eq!(
                read_body(BAG, &nested(9, &malformed)),
                Err(WireError::Malformed)
            );
        }
        // A skipped field must still fit entirely inside its enclosing message.
        assert_eq!(read_body(TOKEN, &[114, 2, 1]), Err(WireError::Malformed));
        assert_eq!(
            read_body(BAG, &nested(9, &[0xa2, 6, 2, 1])),
            Err(WireError::Malformed)
        );
    }

    #[test]
    fn known_fields_reject_wrong_wire_types() {
        for (command, fields) in [
            (TOKEN, &[1, 11, 13][..]),
            (LOGIN_FINISH, &[13][..]),
            (BAG, &[1][..]),
        ] {
            for &field in fields {
                assert_eq!(
                    read_body(command, &nested(field, &[])),
                    Err(WireError::Malformed)
                );
            }
        }
        for field in [2, 12, 14] {
            assert_eq!(
                read_body(TOKEN, &scalars(&[(field, 0)])),
                Err(WireError::Malformed)
            );
        }
        for field in [2, 4, 6, 9, 10, 12, 14, 15, 1996] {
            assert_eq!(
                read_body(BAG, &scalars(&[(field, 0)])),
                Err(WireError::Malformed)
            );
        }
        for field in [1, 2, 3, 4, 7, 8, 9, 10, 13] {
            assert_eq!(
                read_body(BAG, &nested(9, &nested(field, &[]))),
                Err(WireError::Malformed)
            );
        }
        for list in [5, 12, 14] {
            assert_eq!(
                read_body(BAG, &nested(9, &scalars(&[(list, 0)]))),
                Err(WireError::Malformed)
            );
            for field in [1, 2, 3] {
                assert_eq!(
                    read_body(BAG, &nested(9, &nested(list, &nested(field, &[])))),
                    Err(WireError::Malformed)
                );
            }
        }
    }

    #[test]
    fn known_scalars_reject_duplicates_and_uint32_overflow() {
        let overflow = u64::from(u32::MAX) + 1;
        for (command, fields) in [
            (TOKEN, &[11, 13][..]),
            (LOGIN_FINISH, &[13][..]),
            (BAG, &[1][..]),
        ] {
            for &field in fields {
                assert_eq!(
                    read_body(command, &scalars(&[(field, overflow)])),
                    Err(WireError::Malformed)
                );
                assert_eq!(
                    read_body(command, &scalars(&[(field, 0), (field, 0)])),
                    Err(WireError::Malformed)
                );
            }
        }
        assert_eq!(
            read_body(TOKEN, &scalars(&[(1, 0), (1, 0)])),
            Err(WireError::Malformed)
        );
        for field in [2, 14] {
            assert_eq!(
                read_body(TOKEN, &[nested(field, &[]), nested(field, &[])].concat()),
                Err(WireError::Malformed)
            );
        }
        for field in [1, 2, 3, 4, 7, 8, 9, 10, 13] {
            assert_eq!(
                read_body(BAG, &nested(9, &scalars(&[(field, 0), (field, 0)]))),
                Err(WireError::Malformed)
            );
            if !matches!(field, 2 | 4) {
                assert_eq!(
                    read_body(BAG, &nested(9, &scalars(&[(field, overflow)]))),
                    Err(WireError::Malformed)
                );
            }
        }
        for list in [5, 12, 14] {
            for field in [1, 2, 3] {
                for malformed in [
                    scalars(&[(field, overflow)]),
                    scalars(&[(field, 0), (field, 0)]),
                ] {
                    assert_eq!(
                        read_body(BAG, &nested(9, &nested(list, &malformed))),
                        Err(WireError::Malformed)
                    );
                }
            }
        }
    }

    #[test]
    fn body_and_relic_row_limits_are_exact() {
        let at_limit = nested(100, &vec![0; MAX_FRAME_BYTES - 5]);
        assert_eq!(at_limit.len(), MAX_FRAME_BYTES);
        assert!(rows(&at_limit).is_empty());
        for command in [TOKEN, LOGIN_FINISH, BAG] {
            assert_eq!(
                read_body(command, &vec![0; MAX_FRAME_BYTES + 1]),
                Err(WireError::BodyTooLarge)
            );
        }
        assert_eq!(
            rows(&bag(&vec![Vec::new(); MAX_GEAR_ROWS])).len(),
            MAX_GEAR_ROWS
        );
        assert_eq!(
            read_body(BAG, &bag(&vec![Vec::new(); MAX_GEAR_ROWS + 1])),
            Err(WireError::TooManyRows)
        );
    }

    #[test]
    fn decoded_rows_only_enter_observer_with_explicit_synthetic_proof() {
        let context = ObservationContext {
            generation: 1,
            flow: 2,
            account: 3,
        };
        let one = SnapshotCounts {
            observed: 1,
            supported: 1,
            unsupported: 0,
            serialized: 1,
            imported: 1,
        };
        let mut observer = GearObserver::new(Game::Hsr);
        observer.begin(context.generation, context.flow);
        let parsed = rows(&nested(9, &scalars(&[(7, 71)])));
        // Synthetic fixture assertion only: no real mapping, writer, or importer is invoked.
        let mapped = [SyntheticGearRow {
            instance_id: u64::from(parsed[0].unique_id),
            mapping: MappingClass::Supported,
        }];
        let full = Observation::FullSnapshot {
            rows: &mapped,
            counts: one,
        };
        let stale = ObservationContext {
            generation: 0,
            ..context
        };
        assert_eq!(
            observer.observe(stale, full),
            Err(ObserverError::StaleContext)
        );
        let wrong_flow = ObservationContext { flow: 0, ..context };
        assert_eq!(
            observer.observe(wrong_flow, full),
            Err(ObserverError::StaleContext)
        );
        // Neither successful token/login parsing nor row count creates an accepted snapshot.
        read_body(TOKEN, &scalars(&[(11, 3)])).unwrap();
        read_body(LOGIN_FINISH, &[]).unwrap();
        assert_eq!(observer.snapshot(), None);
        let missing_import_proof = Observation::FullSnapshot {
            rows: &mapped,
            counts: SnapshotCounts { imported: 0, ..one },
        };
        assert_eq!(
            observer.observe(context, missing_import_proof),
            Err(ObserverError::CountMismatch)
        );
        observer.begin(context.generation, context.flow);
        assert_eq!(observer.observe(context, full), Ok(one));
        let mixed = ObservationContext {
            account: 4,
            ..context
        };
        assert_eq!(
            observer.observe(mixed, full),
            Err(ObserverError::MixedAccount)
        );
        observer.cancel();
        assert!(!observer.has_context());
        assert!(!observer.is_poisoned());
        assert_eq!(observer.snapshot(), None);
        assert_eq!(
            observer.observe(context, full),
            Err(ObserverError::StaleContext)
        );
        observer.begin(context.generation, context.flow);
        assert_eq!(observer.observe(context, full), Ok(one));
        observer.disconnect();
        assert!(!observer.has_context());
        assert_eq!(observer.snapshot(), None);
        for (ids, error) in [
            (vec![0], ObserverError::InvalidInstanceId),
            (vec![71, 71], ObserverError::DuplicateInstanceId),
        ] {
            let decoded = rows(&bag(&ids
                .iter()
                .map(|id| scalars(&[(7, *id)]))
                .collect::<Vec<_>>()));
            let mapped = decoded
                .iter()
                .map(|row| SyntheticGearRow {
                    instance_id: u64::from(row.unique_id),
                    mapping: MappingClass::Supported,
                })
                .collect::<Vec<_>>();
            let counts = if ids.len() == 1 {
                one
            } else {
                SnapshotCounts {
                    observed: 2,
                    supported: 2,
                    unsupported: 0,
                    serialized: 2,
                    imported: 2,
                }
            };
            observer.begin(context.generation, context.flow);
            assert_eq!(
                observer.observe(
                    context,
                    Observation::FullSnapshot {
                        rows: &mapped,
                        counts
                    }
                ),
                Err(error)
            );
            assert!(observer.is_poisoned());
        }
    }
}
