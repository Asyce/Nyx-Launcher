//! Protocol-independent, synthetic-only gear snapshot state machine.
//!
//! This module intentionally has no capture, decoder, launcher, or output wiring.

use crate::Game;
use zeroize::Zeroizing;

pub const MAX_GEAR_ROWS: usize = 10_000;

#[derive(Clone, Copy)]
pub struct ObservationContext {
    pub generation: u64,
    pub flow: u64,
    pub account: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MappingClass {
    Supported,
    GenshinAllowedLowRarity,
    Unknown,
}

#[derive(Clone, Copy)]
pub struct SyntheticGearRow {
    pub instance_id: u64,
    pub mapping: MappingClass,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct SnapshotCounts {
    pub observed: usize,
    pub supported: usize,
    pub unsupported: usize,
    pub serialized: usize,
    pub imported: usize,
}

#[derive(Clone, Copy)]
pub enum Observation<'a> {
    FullSnapshot {
        rows: &'a [SyntheticGearRow],
        counts: SnapshotCounts,
    },
    Malformed,
    Update,
    Delete,
    Delta,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObserverError {
    StaleContext,
    Poisoned,
    InvalidAccount,
    MixedAccount,
    Malformed,
    SnapshotAlreadyAccepted,
    TooManyRows,
    InvalidInstanceId,
    DuplicateInstanceId,
    UnknownMapping,
    UnsupportedLowRarity,
    CountMismatch,
    UnsupportedEvent,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct FlowContext {
    generation: u64,
    flow: u64,
}

pub struct GearObserver {
    game: Game,
    context: Option<FlowContext>,
    account: Option<Zeroizing<u64>>,
    snapshot: Option<SnapshotCounts>,
    poisoned: bool,
}

impl GearObserver {
    pub const fn new(game: Game) -> Self {
        Self {
            game,
            context: None,
            account: None,
            snapshot: None,
            poisoned: false,
        }
    }

    pub fn begin(&mut self, generation: u64, flow: u64) {
        self.clear();
        self.context = Some(FlowContext { generation, flow });
    }

    pub fn observe(
        &mut self,
        context: ObservationContext,
        observation: Observation<'_>,
    ) -> Result<SnapshotCounts, ObserverError> {
        if self.context
            != Some(FlowContext {
                generation: context.generation,
                flow: context.flow,
            })
        {
            return Err(ObserverError::StaleContext);
        }
        if self.poisoned {
            return Err(ObserverError::Poisoned);
        }
        if context.account == 0 {
            return self.poison(ObserverError::InvalidAccount);
        }
        match self.account.as_deref() {
            Some(account) if *account != context.account => {
                return self.poison(ObserverError::MixedAccount);
            }
            None => self.account = Some(Zeroizing::new(context.account)),
            _ => {}
        }

        let (rows, counts) = match observation {
            Observation::FullSnapshot { rows, counts } => (rows, counts),
            Observation::Malformed => return self.poison(ObserverError::Malformed),
            Observation::Update | Observation::Delete | Observation::Delta => {
                return self.poison(ObserverError::UnsupportedEvent);
            }
        };
        if self.snapshot.is_some() {
            return self.poison(ObserverError::SnapshotAlreadyAccepted);
        }
        if rows.len() > MAX_GEAR_ROWS {
            return self.poison(ObserverError::TooManyRows);
        }
        if let Err(error) = checked_instance_ids(rows) {
            return self.poison(error);
        }

        let mut supported = 0usize;
        let mut unsupported = 0usize;
        for row in rows {
            match row.mapping {
                MappingClass::Supported => supported += 1,
                MappingClass::GenshinAllowedLowRarity if self.game == Game::Gi => {
                    unsupported += 1;
                }
                MappingClass::GenshinAllowedLowRarity => {
                    return self.poison(ObserverError::UnsupportedLowRarity);
                }
                MappingClass::Unknown => return self.poison(ObserverError::UnknownMapping),
            }
        }
        if counts.observed != rows.len()
            || counts.supported != supported
            || counts.unsupported != unsupported
            || counts.observed
                != counts
                    .supported
                    .checked_add(counts.unsupported)
                    .unwrap_or(usize::MAX)
            || counts.serialized != counts.supported
            || counts.imported != counts.supported
        {
            return self.poison(ObserverError::CountMismatch);
        }

        self.snapshot = Some(counts);
        Ok(counts)
    }

    pub fn cancel(&mut self) {
        self.clear();
    }

    pub fn disconnect(&mut self) {
        self.clear();
    }

    pub const fn snapshot(&self) -> Option<SnapshotCounts> {
        self.snapshot
    }

    pub const fn is_poisoned(&self) -> bool {
        self.poisoned
    }

    pub const fn has_context(&self) -> bool {
        self.context.is_some()
    }

    fn poison(&mut self, error: ObserverError) -> Result<SnapshotCounts, ObserverError> {
        self.account = None;
        self.snapshot = None;
        self.poisoned = true;
        Err(error)
    }

    fn clear(&mut self) {
        self.account = None;
        self.snapshot = None;
        self.context = None;
        self.poisoned = false;
    }
}

fn checked_instance_ids(rows: &[SyntheticGearRow]) -> Result<Zeroizing<Vec<u64>>, ObserverError> {
    let mut ids = Zeroizing::new(rows.iter().map(|row| row.instance_id).collect::<Vec<_>>());
    if ids.contains(&0) {
        return Err(ObserverError::InvalidInstanceId);
    }
    ids.sort_unstable();
    if ids.windows(2).any(|pair| pair[0] == pair[1]) {
        return Err(ObserverError::DuplicateInstanceId);
    }
    Ok(ids)
}

#[cfg(test)]
mod tests {
    use super::*;

    const FIRST: ObservationContext = ObservationContext {
        generation: 1,
        flow: 10,
        account: 7,
    };
    const SECOND: ObservationContext = ObservationContext {
        generation: 2,
        flow: 20,
        account: 8,
    };
    const EMPTY: SnapshotCounts = SnapshotCounts {
        observed: 0,
        supported: 0,
        unsupported: 0,
        serialized: 0,
        imported: 0,
    };

    fn begin(game: Game, context: ObservationContext) -> GearObserver {
        let mut observer = GearObserver::new(game);
        observer.begin(context.generation, context.flow);
        observer
    }

    fn full(rows: &[SyntheticGearRow], counts: SnapshotCounts) -> Observation<'_> {
        Observation::FullSnapshot { rows, counts }
    }

    #[test]
    fn reset_replaces_old_state_and_stale_input_cannot_corrupt_current() {
        let row = [SyntheticGearRow {
            instance_id: 1,
            mapping: MappingClass::Supported,
        }];
        let one = SnapshotCounts {
            observed: 1,
            supported: 1,
            unsupported: 0,
            serialized: 1,
            imported: 1,
        };
        let mut observer = begin(Game::Gi, FIRST);
        assert_eq!(observer.observe(FIRST, full(&row, one)), Ok(one));

        observer.begin(SECOND.generation, SECOND.flow);
        assert_eq!(observer.snapshot(), None);
        assert_eq!(
            observer.observe(FIRST, Observation::Malformed),
            Err(ObserverError::StaleContext)
        );
        assert!(!observer.is_poisoned());
        assert_eq!(observer.observe(SECOND, full(&[], EMPTY)), Ok(EMPTY));
    }

    #[test]
    fn accepted_context_account_or_input_failure_poison_until_reset() {
        let mut observer = begin(Game::Gi, FIRST);
        let zero = ObservationContext {
            account: 0,
            ..FIRST
        };
        assert_eq!(
            observer.observe(zero, full(&[], EMPTY)),
            Err(ObserverError::InvalidAccount)
        );
        assert_eq!(
            observer.observe(FIRST, full(&[], EMPTY)),
            Err(ObserverError::Poisoned)
        );

        observer.begin(FIRST.generation, FIRST.flow);
        assert_eq!(observer.observe(FIRST, full(&[], EMPTY)), Ok(EMPTY));
        let mixed = ObservationContext {
            account: 8,
            ..FIRST
        };
        assert_eq!(
            observer.observe(mixed, full(&[], EMPTY)),
            Err(ObserverError::MixedAccount)
        );
        assert!(observer.is_poisoned());

        observer.begin(FIRST.generation, FIRST.flow);
        assert_eq!(
            observer.observe(FIRST, Observation::Malformed),
            Err(ObserverError::Malformed)
        );
        assert_eq!(
            observer.observe(FIRST, full(&[], EMPTY)),
            Err(ObserverError::Poisoned)
        );
    }

    #[test]
    fn mapping_classes_and_count_equations_are_game_specific() {
        let rows = [
            SyntheticGearRow {
                instance_id: 1,
                mapping: MappingClass::Supported,
            },
            SyntheticGearRow {
                instance_id: 2,
                mapping: MappingClass::GenshinAllowedLowRarity,
            },
        ];
        let counts = SnapshotCounts {
            observed: 2,
            supported: 1,
            unsupported: 1,
            serialized: 1,
            imported: 1,
        };
        assert_eq!(
            begin(Game::Gi, FIRST).observe(FIRST, full(&rows, counts)),
            Ok(counts)
        );
        assert_eq!(
            begin(Game::Hsr, FIRST).observe(FIRST, full(&rows, counts)),
            Err(ObserverError::UnsupportedLowRarity)
        );

        let unknown = [SyntheticGearRow {
            instance_id: 1,
            mapping: MappingClass::Unknown,
        }];
        assert_eq!(
            begin(Game::Gi, FIRST).observe(FIRST, full(&unknown, EMPTY)),
            Err(ObserverError::UnknownMapping)
        );
    }

    #[test]
    fn ids_are_nonzero_unique_bounded_and_held_in_wiped_temporary_storage() {
        fn assert_zeroizing(_: &Zeroizing<Vec<u64>>) {}

        let valid = [SyntheticGearRow {
            instance_id: 1,
            mapping: MappingClass::Supported,
        }];
        assert_zeroizing(&checked_instance_ids(&valid).unwrap());

        for (rows, error) in [
            (
                vec![SyntheticGearRow {
                    instance_id: 0,
                    mapping: MappingClass::Supported,
                }],
                ObserverError::InvalidInstanceId,
            ),
            (
                vec![
                    SyntheticGearRow {
                        instance_id: 1,
                        mapping: MappingClass::Supported,
                    },
                    SyntheticGearRow {
                        instance_id: 1,
                        mapping: MappingClass::Supported,
                    },
                ],
                ObserverError::DuplicateInstanceId,
            ),
        ] {
            let counts = SnapshotCounts {
                observed: rows.len(),
                supported: rows.len(),
                unsupported: 0,
                serialized: rows.len(),
                imported: rows.len(),
            };
            let mut observer = begin(Game::Gi, FIRST);
            assert_eq!(observer.observe(FIRST, full(&rows, counts)), Err(error));
            assert!(observer.is_poisoned());
        }

        let rows = vec![
            SyntheticGearRow {
                instance_id: 1,
                mapping: MappingClass::Supported,
            };
            MAX_GEAR_ROWS + 1
        ];
        let counts = SnapshotCounts {
            observed: rows.len(),
            supported: rows.len(),
            unsupported: 0,
            serialized: rows.len(),
            imported: rows.len(),
        };
        assert_eq!(
            begin(Game::Gi, FIRST).observe(FIRST, full(&rows, counts)),
            Err(ObserverError::TooManyRows)
        );

        let rows = (1..=MAX_GEAR_ROWS as u64)
            .map(|instance_id| SyntheticGearRow {
                instance_id,
                mapping: MappingClass::Supported,
            })
            .collect::<Vec<_>>();
        let counts = SnapshotCounts {
            observed: MAX_GEAR_ROWS,
            supported: MAX_GEAR_ROWS,
            unsupported: 0,
            serialized: MAX_GEAR_ROWS,
            imported: MAX_GEAR_ROWS,
        };
        assert_eq!(
            begin(Game::Gi, FIRST).observe(FIRST, full(&rows, counts)),
            Ok(counts)
        );
    }

    #[test]
    fn every_count_mismatch_poisons() {
        let row = [SyntheticGearRow {
            instance_id: 1,
            mapping: MappingClass::Supported,
        }];
        let valid = SnapshotCounts {
            observed: 1,
            supported: 1,
            unsupported: 0,
            serialized: 1,
            imported: 1,
        };
        for counts in [
            SnapshotCounts {
                observed: 0,
                ..valid
            },
            SnapshotCounts {
                supported: 0,
                ..valid
            },
            SnapshotCounts {
                unsupported: 1,
                ..valid
            },
            SnapshotCounts {
                serialized: 0,
                ..valid
            },
            SnapshotCounts {
                imported: 0,
                ..valid
            },
        ] {
            let mut observer = begin(Game::Gi, FIRST);
            assert_eq!(
                observer.observe(FIRST, full(&row, counts)),
                Err(ObserverError::CountMismatch)
            );
            assert!(observer.is_poisoned());
        }
    }

    #[test]
    fn unproven_mutation_events_fail_closed_and_second_snapshot_is_rejected() {
        for event in [Observation::Update, Observation::Delete, Observation::Delta] {
            let mut observer = begin(Game::Gi, FIRST);
            assert_eq!(
                observer.observe(FIRST, event),
                Err(ObserverError::UnsupportedEvent)
            );
            assert!(observer.is_poisoned());
        }

        let mut observer = begin(Game::Gi, FIRST);
        assert_eq!(observer.observe(FIRST, full(&[], EMPTY)), Ok(EMPTY));
        assert_eq!(
            observer.observe(FIRST, full(&[], EMPTY)),
            Err(ObserverError::SnapshotAlreadyAccepted)
        );
    }

    #[test]
    fn cancel_disconnect_clear_account_context_snapshot_and_poison() {
        fn assert_zeroizing(_: &Zeroizing<u64>) {}

        let mut observer = begin(Game::Gi, FIRST);
        assert_eq!(observer.observe(FIRST, full(&[], EMPTY)), Ok(EMPTY));
        assert_zeroizing(observer.account.as_ref().unwrap());
        observer.cancel();
        assert!(!observer.has_context());
        assert_eq!(observer.snapshot(), None);
        assert!(observer.account.is_none());

        observer.begin(FIRST.generation, FIRST.flow);
        assert_eq!(
            observer.observe(FIRST, Observation::Malformed),
            Err(ObserverError::Malformed)
        );
        observer.disconnect();
        assert!(!observer.has_context());
        assert!(!observer.is_poisoned());
        assert!(observer.account.is_none());
    }
}
