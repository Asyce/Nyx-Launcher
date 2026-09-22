//! Test-only Genshin candidate item-body qualification, not a live decoder.
//!
//! Field shapes: konkers/auto-artifactarium 4ba25fac64b88970143af6bc2a2ef51338e620d0
//! (MIT; source hashes and attribution are in PROVENANCE.md).
//! No account, complete-bag boundary, equipped location, or mapping is inferred.
//! Unknown item/equip/reliquary fields and ambiguous/missing details fail closed.
//! Known non-artifact details stay opaque; only their wire type/length is checked.

use super::MAX_GEAR_ROWS;
use super::wire::{WireError, length, require_wire, scalar, skip, tag, uint32};
use crate::capture::MAX_FRAME_BYTES;
use protobuf::{CodedInputStream, rt::WireType};

const PLAYER_STORE_NOTIFY: u16 = 8132;

#[derive(Debug, Default, Eq, PartialEq)]
struct ItemObservation {
    artifacts: Vec<Artifact>,
    other_items: usize,
}

#[derive(Debug, Eq, PartialEq)]
struct Artifact {
    item_id: u32,
    guid: u64,
    is_locked: bool,
    reliquary: Reliquary,
}

#[derive(Debug, Default, Eq, PartialEq)]
struct Reliquary {
    level: u32,
    exp: u32,
    promote_level: u32,
    main_prop_id: u32,
    append_prop_id_list: Vec<u32>,
    starred: bool,
    // Preserve the upstream name and raw values; no meaning is assumed.
    elixer_choices: Vec<u32>,
    unactivated_prop_id_list: Vec<u32>,
}

fn read_body(command: u16, body: &[u8]) -> Result<ItemObservation, WireError> {
    if command != PLAYER_STORE_NOTIFY {
        return Err(WireError::UnsupportedCommand);
    }
    if body.len() > MAX_FRAME_BYTES {
        return Err(WireError::BodyTooLarge);
    }
    let mut input = CodedInputStream::from_bytes(body);
    input.push_limit(body.len() as u64)?;
    let mut observation = ItemObservation::default();
    while !input.eof()? {
        let (field, wire) = tag(&mut input)?;
        if field == 5 {
            if let Some(artifact) = read_item(&mut input, wire)? {
                if observation.artifacts.len() == MAX_GEAR_ROWS {
                    return Err(WireError::TooManyRows);
                }
                observation.artifacts.push(artifact);
            } else {
                observation.other_items += 1;
            }
        } else {
            // PacketWithItems describes only field 5. Extra root fields are
            // length-checked and skipped, never treated as completion evidence.
            skip(&mut input, wire)?;
        }
    }
    Ok(observation)
}

fn read_item(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
) -> Result<Option<Artifact>, WireError> {
    let len = length(input, wire)?;
    let previous_limit = input.push_limit(u64::from(len))?;
    let mut seen = 0;
    let mut item_id = 0;
    let mut guid = 0;
    let mut detail_seen = false;
    let mut artifact = None;
    while !input.eof()? {
        let (field, wire) = tag(input)?;
        match field {
            1 => item_id = uint32(input, wire, &mut seen, field)?,
            2 => guid = scalar(input, wire, &mut seen, field)?,
            5..=7 => {
                one_detail(&mut detail_seen)?;
                if field == 6 {
                    artifact = read_equip(input, wire)?;
                } else {
                    require_wire(wire, WireType::LengthDelimited)?;
                    skip(input, wire)?;
                }
            }
            _ => return Err(WireError::Malformed),
        }
    }
    if !detail_seen {
        return Err(WireError::Malformed);
    }
    input.pop_limit(previous_limit);
    Ok(artifact.map(|(is_locked, reliquary)| Artifact {
        item_id,
        guid,
        is_locked,
        reliquary,
    }))
}

fn read_equip(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
) -> Result<Option<(bool, Reliquary)>, WireError> {
    let len = length(input, wire)?;
    let previous_limit = input.push_limit(u64::from(len))?;
    let mut seen = 0;
    let mut is_locked = false;
    let mut detail_seen = false;
    let mut reliquary = None;
    while !input.eof()? {
        let (field, wire) = tag(input)?;
        match field {
            1 | 2 => {
                one_detail(&mut detail_seen)?;
                if field == 1 {
                    reliquary = Some(read_reliquary(input, wire)?);
                } else {
                    require_wire(wire, WireType::LengthDelimited)?;
                    skip(input, wire)?;
                }
            }
            3 => is_locked = scalar(input, wire, &mut seen, field)? != 0,
            _ => return Err(WireError::Malformed),
        }
    }
    if !detail_seen {
        return Err(WireError::Malformed);
    }
    input.pop_limit(previous_limit);
    Ok(reliquary.map(|row| (is_locked, row)))
}

fn one_detail(seen: &mut bool) -> Result<(), WireError> {
    if *seen {
        return Err(WireError::Malformed);
    }
    *seen = true;
    Ok(())
}

fn read_reliquary(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
) -> Result<Reliquary, WireError> {
    let len = length(input, wire)?;
    let previous_limit = input.push_limit(u64::from(len))?;
    let mut row = Reliquary::default();
    let mut seen = 0;
    while !input.eof()? {
        let (field, wire) = tag(input)?;
        match field {
            1 => row.level = uint32(input, wire, &mut seen, field)?,
            2 => row.exp = uint32(input, wire, &mut seen, field)?,
            3 => row.promote_level = uint32(input, wire, &mut seen, field)?,
            4 => row.main_prop_id = uint32(input, wire, &mut seen, field)?,
            5 => repeated_uint32(input, wire, &mut row.append_prop_id_list)?,
            6 => row.starred = scalar(input, wire, &mut seen, field)? != 0,
            7 => repeated_uint32(input, wire, &mut row.elixer_choices)?,
            8 => repeated_uint32(input, wire, &mut row.unactivated_prop_id_list)?,
            _ => return Err(WireError::Malformed),
        }
    }
    input.pop_limit(previous_limit);
    Ok(row)
}

fn repeated_uint32(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
    values: &mut Vec<u32>,
) -> Result<(), WireError> {
    match wire {
        WireType::Varint => {
            values.push(u32::try_from(input.read_uint64()?).map_err(|_| WireError::Malformed)?);
        }
        WireType::LengthDelimited => {
            let len = length(input, wire)?;
            let previous_limit = input.push_limit(u64::from(len))?;
            while !input.eof()? {
                values.push(u32::try_from(input.read_uint64()?).map_err(|_| WireError::Malformed)?);
            }
            input.pop_limit(previous_limit);
        }
        _ => return Err(WireError::Malformed),
    }
    Ok(())
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

    fn packed(field: u32, values: &[u32]) -> Vec<u8> {
        nested(
            field,
            &encoded(|output| {
                for &value in values {
                    output.write_uint32_no_tag(value).unwrap();
                }
            }),
        )
    }

    fn item(guid: u64, detail: &[u8]) -> Vec<u8> {
        [scalars(&[(1, 31533), (2, guid)]), detail.to_vec()].concat()
    }

    fn artifact(guid: u64, reliquary: &[u8]) -> Vec<u8> {
        item(guid, &nested(6, &nested(1, reliquary)))
    }

    fn store(items: &[Vec<u8>]) -> Vec<u8> {
        items.iter().flat_map(|row| nested(5, row)).collect()
    }

    fn read(body: &[u8]) -> Result<ItemObservation, WireError> {
        read_body(PLAYER_STORE_NOTIFY, body)
    }

    #[test]
    fn candidate_fields_preserve_raw_values_without_stat_or_level_conversion() {
        let reliquary = [
            scalars(&[(1, 21), (2, 42), (3, 7), (4, 13007), (6, 1)]),
            packed(5, &[501022, 501201, 501241, 501221]),
            packed(7, &[22, 24]),
            packed(8, &[501021]),
        ]
        .concat();
        // Lock may follow the oneof and item identity may follow equip.
        let equip = [nested(1, &reliquary), scalars(&[(3, 1)])].concat();
        let row = [nested(6, &equip), scalars(&[(2, u64::MAX), (1, 31533)])].concat();
        assert_eq!(
            read(&store(&[row])).unwrap(),
            ItemObservation {
                artifacts: vec![Artifact {
                    item_id: 31533,
                    guid: u64::MAX,
                    is_locked: true,
                    reliquary: Reliquary {
                        level: 21,
                        exp: 42,
                        promote_level: 7,
                        main_prop_id: 13007,
                        append_prop_id_list: vec![501022, 501201, 501241, 501221],
                        starred: true,
                        elixer_choices: vec![22, 24],
                        unactivated_prop_id_list: vec![501021],
                    },
                }],
                other_items: 0,
            }
        );
    }

    #[test]
    fn omitted_scalars_keep_proto_defaults_without_claiming_export_validity() {
        assert_eq!(read(&[]).unwrap(), ItemObservation::default());
        let row = read(&store(&[nested(6, &nested(1, &[]))]))
            .unwrap()
            .artifacts
            .remove(0);
        assert_eq!(row.guid, 0);
        assert_eq!(row.item_id, 0);
        assert!(!row.is_locked);
        assert_eq!(row.reliquary, Reliquary::default());
    }

    #[test]
    fn repeated_fields_accept_mixed_packing_and_preserve_order_and_duplicates() {
        for field in [5, 7, 8] {
            let reliquary = [
                scalars(&[(field, 9)]),
                packed(field, &[9, u32::MAX]),
                packed(field, &[]),
                scalars(&[(field, 2)]),
            ]
            .concat();
            let mut observation = read(&store(&[artifact(1, &reliquary)])).unwrap();
            let row = observation.artifacts.remove(0).reliquary;
            let values = match field {
                5 => row.append_prop_id_list,
                7 => row.elixer_choices,
                _ => row.unactivated_prop_id_list,
            };
            assert_eq!(values, [9, 9, u32::MAX, 2]);
        }
    }

    #[test]
    fn known_other_items_are_counted_without_retaining_their_payloads() {
        // An opaque payload is deliberately not a qualified nested schema.
        let opaque = [0xff, 0x00, 0xab];
        let body = store(&[
            item(90, &nested(5, &opaque)),
            artifact(1, &[]),
            item(91, &nested(7, &opaque)),
            item(92, &nested(6, &nested(2, &opaque))),
        ]);
        let observed = read(&body).unwrap();
        assert_eq!(observed.other_items, 3);
        assert_eq!(observed.artifacts.len(), 1);
        assert_eq!(observed.artifacts[0].guid, 1);
    }

    #[test]
    fn missing_or_unknown_item_details_cannot_disappear_from_the_count() {
        for row in [
            vec![],
            scalars(&[(1, 31533), (2, 1)]),
            nested(6, &[]),
            nested(8, &[]),
            nested(6, &nested(4, &[])),
            artifact(1, &nested(9, &[])),
            [artifact(1, &[]), scalars(&[(9, 0)])].concat(),
        ] {
            assert_eq!(read(&store(&[row])), Err(WireError::Malformed));
        }
    }

    #[test]
    fn duplicate_or_conflicting_oneof_details_are_rejected_in_either_order() {
        for first in [5, 6, 7] {
            for second in [5, 6, 7] {
                let details = [first, second]
                    .iter()
                    .flat_map(|&field| {
                        nested(field, &if field == 6 { nested(1, &[]) } else { vec![] })
                    })
                    .collect::<Vec<_>>();
                assert_eq!(
                    read(&store(&[item(1, &details)])),
                    Err(WireError::Malformed)
                );
            }
        }
        for first in [1, 2] {
            for second in [1, 2] {
                let equip = [nested(first, &[]), nested(second, &[])].concat();
                assert_eq!(
                    read(&store(&[item(1, &nested(6, &equip))])),
                    Err(WireError::Malformed)
                );
            }
        }
    }

    #[test]
    fn duplicate_known_scalars_are_rejected_at_each_nesting_level() {
        for field in [1, 2] {
            let row = [artifact(1, &[]), scalars(&[(field, 0)])].concat();
            assert_eq!(read(&store(&[row])), Err(WireError::Malformed));
        }
        let equip = [nested(1, &[]), scalars(&[(3, 1), (3, 0)])].concat();
        assert_eq!(
            read(&store(&[item(1, &nested(6, &equip))])),
            Err(WireError::Malformed)
        );
        for field in [1, 2, 3, 4, 6] {
            let row = artifact(1, &scalars(&[(field, 0), (field, 1)]));
            assert_eq!(read(&store(&[row])), Err(WireError::Malformed));
        }
    }

    #[test]
    fn wrong_wire_types_zero_tags_groups_and_overflow_fail_closed() {
        for body in [
            vec![0],
            vec![0x0e],
            vec![0x0b, 0x0c],
            vec![0x0c],
            scalars(&[(5, 0)]),
            store(&[scalars(&[(6, 0)])]),
            store(&[item(1, &nested(6, &scalars(&[(1, 0)])))]),
            store(&[artifact(1, &nested(1, &[]))]),
            store(&[artifact(1, &[0x2d, 0, 0, 0, 0])]),
            store(&[artifact(1, &scalars(&[(4, u64::from(u32::MAX) + 1)]))]),
            store(&[[artifact(1, &[]), nested(1, &[])].concat()]),
            store(&[[nested(6, &nested(1, &[])), scalars(&[(1, u64::MAX)])].concat()]),
        ] {
            assert_eq!(read(&body), Err(WireError::Malformed));
        }
    }

    #[test]
    fn nested_lengths_and_every_truncated_prefix_stay_inside_the_body() {
        let body = store(&[artifact(1, &packed(5, &[501022, 501201]))]);
        for end in 1..body.len() {
            assert_eq!(read(&body[..end]), Err(WireError::Malformed));
        }
        for len in [u64::from(u32::MAX), u64::from(u32::MAX) + 1, u64::MAX] {
            let bad_length = encoded(|output| {
                output.write_tag(5, WireType::LengthDelimited).unwrap();
                output.write_raw_varint64(len).unwrap();
            });
            assert_eq!(read(&bad_length), Err(WireError::Malformed));
        }
        assert_eq!(
            read(&store(&[nested(5, &[0; 2])[..3].to_vec()])),
            Err(WireError::Malformed)
        );
    }

    #[test]
    fn packed_scalars_reject_overflow_and_incomplete_varints() {
        for field in [5, 7, 8] {
            for payload in [
                vec![0x80],
                encoded(|output| output.write_raw_varint64(u64::from(u32::MAX) + 1).unwrap()),
                vec![0xff; 11],
            ] {
                assert_eq!(
                    read(&store(&[artifact(1, &nested(field, &payload))])),
                    Err(WireError::Malformed)
                );
            }
            assert_eq!(
                read(&store(&[artifact(1, &scalars(&[(field, u64::MAX)]))])),
                Err(WireError::Malformed)
            );
        }
    }

    #[test]
    fn only_the_pinned_command_is_qualified_and_root_metadata_is_not_retained() {
        for command in [0, 19, 36, 513, 6586, u16::MAX] {
            assert_eq!(read_body(command, &[]), Err(WireError::UnsupportedCommand));
        }
        let body = [
            scalars(&[(1, 7)]),
            store(&[artifact(1, &[])]),
            nested(100, &[0xff, 0x80]),
        ]
        .concat();
        assert_eq!(read(&body), read(&store(&[artifact(1, &[])])));
    }

    #[test]
    fn body_limit_is_exact_and_skipped_data_cannot_escape_it() {
        // Field 100's two-byte tag and three-byte length prefix use five bytes.
        let body = nested(100, &vec![0; MAX_FRAME_BYTES - 5]);
        assert_eq!(body.len(), MAX_FRAME_BYTES);
        assert_eq!(read(&body).unwrap(), ItemObservation::default());
        assert_eq!(read(&body[..body.len() - 1]), Err(WireError::Malformed));
        assert_eq!(
            read(&vec![0; MAX_FRAME_BYTES + 1]),
            Err(WireError::BodyTooLarge)
        );
    }

    #[test]
    fn artifact_limit_is_exact_without_a_minimum_or_a_limit_on_other_items() {
        let row = nested(5, &artifact(1, &[]));
        let mut body = row.repeat(MAX_GEAR_ROWS);
        // Repeated synthetic GUIDs must remain visible to the observer.
        assert_eq!(read(&body).unwrap().artifacts.len(), MAX_GEAR_ROWS);
        body.extend(nested(5, &item(2, &nested(5, &[]))).repeat(MAX_GEAR_ROWS + 1));
        assert_eq!(read(&body).unwrap().other_items, MAX_GEAR_ROWS + 1);
        body.extend(&row);
        assert_eq!(read(&body), Err(WireError::TooManyRows));
        assert_eq!(read(&row).unwrap().artifacts.len(), 1);
    }

    #[test]
    fn candidate_rows_still_require_separate_context_mapping_and_count_evidence() {
        let observation = read(&store(&[artifact(1, &[]), artifact(1, &[])])).unwrap();
        let rows = observation
            .artifacts
            .iter()
            .map(|row| SyntheticGearRow {
                instance_id: row.guid,
                // This is supplied by this synthetic test, never the body reader.
                mapping: MappingClass::Supported,
            })
            .collect::<Vec<_>>();
        let context = ObservationContext {
            generation: 1,
            flow: 2,
            account: 3,
        };
        let mut observer = GearObserver::new(Game::Gi);
        observer.begin(context.generation, context.flow);
        let counts = SnapshotCounts {
            observed: 2,
            supported: 2,
            unsupported: 0,
            serialized: 2,
            imported: 2,
        };
        assert_eq!(
            observer.observe(
                context,
                Observation::FullSnapshot {
                    rows: &rows,
                    counts
                }
            ),
            Err(ObserverError::DuplicateInstanceId)
        );
        assert!(observer.snapshot().is_none());
    }
}
